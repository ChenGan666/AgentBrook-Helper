using System.Text;
using System.Text.Json;
using AgentBrook.Plugin.Hosting.Abstractions;

namespace AgentBrook.Plugin.Im.Weixin;

/// <summary>
/// iLink（智联 / ClawBot）协议常量与请求构造（协议基线 @tencent-weixin/openclaw-weixin 2.4.6）。
/// 纯 HTTP/JSON：登录后所有 CGI 拼到 baseurl；头部固定四项 + 登录后的 Bearer token（凭据注入）。
/// </summary>
internal static class WeixinProtocol
{
    /// <summary>协议基线版本（base_info.channel_version；升级需对照官方发布包复核）。</summary>
    internal const string ChannelVersion = "2.4.6";

    /// <summary>iLink-App-ClientVersion = (major&lt;&lt;16)|(minor&lt;&lt;8)|patch → 2.4.6 = 132102。</summary>
    internal const int AppClientVersion = 132102;

    /// <summary>bot_agent 标识（服务端统计用）。</summary>
    internal const string BotAgent = "AgentBrook-Helper/weixin-channel";

    internal const string PathGetUpdates = "/ilink/bot/getupdates";
    internal const string PathSendMessage = "/ilink/bot/sendmessage";
    internal const string PathGetUploadUrl = "/ilink/bot/getuploadurl";
    internal const string PathGetConfig = "/ilink/bot/getconfig";
    internal const string PathSendTyping = "/ilink/bot/sendtyping";
    internal const string PathNotifyStart = "/ilink/bot/msg/notifystart";
    internal const string PathNotifyStop = "/ilink/bot/msg/notifystop";

    /// <summary>token 失效（官方 2.4.5+ 明确）：需用户重新扫码登录。</summary>
    internal const int ErrTokenInvalid = -14;

    /// <summary>X-WECHAT-UIN：随机 uint32 的十进制字符串再 base64（每次 POST 重新生成）。</summary>
    internal static string NextWechatUin()
    {
        var value = (uint)Random.Shared.NextInt64(0, (long)uint.MaxValue + 1);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(
            value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>POST 固定头（Authorization 由凭据注入 BearerHeader，不在此处明文拼装）。</summary>
    internal static List<HttpHeader> PostHeaders() =>
    [
        new("Content-Type", "application/json"),
        new("AuthorizationType", "ilink_bot_token"),
        new("X-WECHAT-UIN", NextWechatUin()),
        new("iLink-App-Id", "bot"),
        new("iLink-App-ClientVersion", AppClientVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
    ];

    /// <summary>GET 请求头（二维码状态轮询：官方最小集合只带公共应用头）。</summary>
    internal static List<HttpHeader> GetHeaders() =>
    [
        new("iLink-App-Id", "bot"),
        new("iLink-App-ClientVersion", AppClientVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
    ];

    /// <summary>业务 POST 请求体统一追加 base_info（channel_version + bot_agent）。</summary>
    internal static string WithBaseInfo(object payload)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                writer.WritePropertyName(p.Name);
                p.Value.WriteTo(writer);
            }
            writer.WritePropertyName("base_info");
            writer.WriteStartObject();
            writer.WriteString("channel_version", ChannelVersion);
            writer.WriteString("bot_agent", BotAgent);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>解析 ret/errcode：缺省视为 0（成功）；返回第一个非零值。</summary>
    internal static int ReadRet(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return 0;
        if (root.TryGetProperty("ret", out var ret) && ret.ValueKind == JsonValueKind.Number && ret.GetInt32() != 0)
            return ret.GetInt32();
        if (root.TryGetProperty("errcode", out var err) && err.ValueKind == JsonValueKind.Number && err.GetInt32() != 0)
            return err.GetInt32();
        return 0;
    }
}
