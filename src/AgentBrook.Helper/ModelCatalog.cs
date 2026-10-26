using System.Net.Http;
using System.Text.Json;
using AgentBrook.Agent.Infrastructure;

namespace AgentBrook.Helper;

internal static class ModelCatalog
{
    internal sealed record Entry(string Id, string? Name, long? ContextWindow, int? MaxOutput, int ModelType, string Endpoints, string Params);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    internal static string EffectiveApiBase(ProviderConfig p)
    {
        var url = (p.BaseUrl ?? "").TrimEnd('/');
        return p.Name == "AgentBrook" && !url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? url + "/v1" : url;
    }

    internal static ProviderConfig Clone(ProviderConfig p) => JsonSerializer.Deserialize<ProviderConfig>(Fingerprint(p))!;
    internal static string Fingerprint(ProviderConfig p)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(p));
        return CanonicalJson(doc.RootElement);
    }

    internal static async Task<List<Entry>> FetchAsync(ProviderConfig p)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{EffectiveApiBase(p)}/models?_={DateTimeOffset.Now.ToUnixTimeMilliseconds()}");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", p.ApiKey);
        req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var resp = await Http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {(text.Length > 120 ? text[..120] : text)}");
        return Parse(text);
    }

    internal static List<Entry> Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var list = new List<Entry>();
        foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var id = m.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id)) continue;
            var endpoints = m.TryGetProperty("endpoints", out var ep) && ep.ValueKind == JsonValueKind.Array
                ? string.Join(",", ep.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrEmpty(s))) : "";
            list.Add(new Entry(id,
                m.TryGetProperty("name", out var n) ? n.GetString() : null,
                m.TryGetProperty("context_window", out var cw) && cw.ValueKind == JsonValueKind.Number ? cw.GetInt64() : null,
                m.TryGetProperty("max_output_tokens", out var mo) && mo.ValueKind == JsonValueKind.Number ? mo.GetInt32() : null,
                m.TryGetProperty("model_type", out var mt) && mt.ValueKind == JsonValueKind.Number ? mt.GetInt32() : 1,
                endpoints, m.TryGetProperty("parameters", out var pp) && pp.ValueKind == JsonValueKind.Object ? CanonicalJson(pp) : ""));
        }
        return list.DistinctBy(m => m.Id).ToList();
    }

    private static string CanonicalJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + CanonicalJson(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(CanonicalJson)) + "]",
        _ => value.GetRawText()
    };

    internal static bool Apply(ProviderConfig p, IReadOnlyList<Entry> entries)
    {
        if (entries.Count == 0) return false;
        var previous = Clone(p);
        foreach (var key in previous.ModelParams.Keys.ToList())
        {
            try
            {
                using var doc = JsonDocument.Parse(previous.ModelParams[key]);
                previous.ModelParams[key] = CanonicalJson(doc.RootElement);
            }
            catch (JsonException) { }
        }
        var before = Fingerprint(previous);
        var ids = entries.Select(m => m.Id).Distinct().ToList();
        if (p.Models.Count != ids.Count || !p.Models.ToHashSet().SetEquals(ids)) p.Models = ids;
        p.ModelTypes = entries.ToDictionary(m => m.Id, m => m.ModelType);
        p.ModelNames = entries.Where(m => !string.IsNullOrWhiteSpace(m.Name)).ToDictionary(m => m.Id, m => m.Name!);
        p.ModelEndpoints = entries.Where(m => !string.IsNullOrWhiteSpace(m.Endpoints)).ToDictionary(m => m.Id, m => m.Endpoints);
        p.ModelParams = entries.Where(m => !string.IsNullOrWhiteSpace(m.Params)).ToDictionary(m => m.Id, m => m.Params);
        if (!p.Models.Contains(p.ActiveModel) || !p.IsChatModel(p.ActiveModel))
            p.ActiveModel = p.Models.FirstOrDefault(p.IsChatModel) ?? "";
        if (!p.Models.Contains(p.ActiveMediaModel) || p.IsChatModel(p.ActiveMediaModel)) p.ActiveMediaModel = "";
        return before != Fingerprint(p);
    }
}
