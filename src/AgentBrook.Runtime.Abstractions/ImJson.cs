using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBrook.Runtime.Abstractions;

/// <summary>
/// 契约 DTO 序列化策略（C01 冻结）：
/// camelCase 属性名；枚举一律 camelCase 字符串且 <b>拒绝整数与未知值</b>（allowIntegerValues=false，
/// 未知字符串抛 JsonException —— 未知值默认拒绝，不做静默映射）；
/// 时间一律 DateTimeOffset（UTC 语义，ISO 8601 round-trip）。
/// </summary>
public static class ImJson
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        NumberHandling = JsonNumberHandling.Strict,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JsonException("契约 DTO 反序列化结果为 null。");
}
