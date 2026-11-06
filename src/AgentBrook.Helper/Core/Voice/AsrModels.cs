namespace AgentBrook.Helper.Core.Voice;

/// <summary>转写请求：音频字节 + 媒体类型（audio/ogg、audio/wav…）+ 可选语言提示。</summary>
public sealed record AsrRequest(byte[] Audio, string MediaType, string? LanguageHint = null);

/// <summary>转写结果：Error 为 null 即成功（引擎名/时长供遥测；错误码分类见 CloudAsrProvider.ClassifyError）。</summary>
public sealed record AsrResult(string Text, string Engine, TimeSpan Duration, int? ErrorCode = null, string? Error = null)
{
    public bool Success => Error is null;

    public static AsrResult Ok(string text, string engine, TimeSpan duration) => new(text, engine, duration);

    public static AsrResult Fail(string engine, string error, int? errorCode = null) =>
        new("", engine, TimeSpan.Zero, errorCode, error);
}
