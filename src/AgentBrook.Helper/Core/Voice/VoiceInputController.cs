namespace AgentBrook.Helper.Core.Voice;

/// <summary>
/// 气泡语音输入（doc/Voice/06）：门卫矩阵 + 转写管线 + 草稿合并，纯静态可测。
/// 门卫严格认本地插件（用户决策 2026-11-15）：云端转写模型不作为气泡语音入口依据；
/// 转写时刻 AsrService 内部既有插件→云端回落保持不变。
/// </summary>
public static class VoiceInput
{
    /// <summary>门卫判定结果（气泡侧按此分流提示）。</summary>
    public enum Gate
    {
        Ready,              // 插件就绪，可录音
        AgentNotReady,      // Agent/插件管理器未就绪（启动中或失败）
        PluginNotInstalled, // 未安装 asr 插件 → 引导自动安装
        PluginDisabled,     // 已安装但未启用/未加载 → 引导去设置启用
        ModelMissing,       // 已启用但语音模型未下载 → 引导下载模型
    }

    /// <summary>语音识别插件 id（index/安装态一致使用）。</summary>
    public const string AsrPluginId = "asr";

    /// <summary>过短丢弃门限（秒）：长按阈值本身吃掉 ~450ms 按压时长，0.5s 音频门槛过苛，放宽到 0.3s。</summary>
    public const double MinDurationSeconds = 0.3;

    /// <summary>静音判定门限（int16 峰值）：被拒权限/静音设备通常峰值≈0，正常语音远高于此。</summary>
    public const short SilencePeakThreshold = 100;

    /// <summary>
    /// 门卫判定。capability 非空看 IsReady（模型缺失）；null 且已安装 → 停用；null 且未装 → 引导安装。
    /// </summary>
    public static Gate EvaluateGate(bool agentReady, bool pluginInstalled, AgentBrook.Plugin.Abstractions.IAsrCapability? capability)
    {
        if (!agentReady) return Gate.AgentNotReady;
        if (capability is null) return pluginInstalled ? Gate.PluginDisabled : Gate.PluginNotInstalled;
        return capability.IsReady ? Gate.Ready : Gate.ModelMissing;
    }

    /// <summary>从运行中的应用状态取门卫输入并判定（UI 层便捷入口；单测用 EvaluateGate 注入）。</summary>
    public static Gate EvaluateFromApp()
    {
        var controller = App.Controller;
        var manager = controller?.Agent?.PluginManager;
        AgentBrook.Plugin.Abstractions.IAsrCapability? capability = null;
        try
        {
            capability = manager?.Registry.GetCapability<AgentBrook.Plugin.Abstractions.IAsrCapability>();
        }
        catch
        {
            capability = null;   // Agent/插件层异常 → 按停用归类（不阻塞 UI）
        }
        var installed = manager is not null && manager.ListInstalled().ContainsKey(AsrPluginId);
        return EvaluateGate(controller?.Ready == true && manager is not null, installed, capability);
    }

    /// <summary>录音 PCM → 内存 WAV（按实际采集采样率，AsrService 支持任意采样率）→ AsrService 转写（与 IM 语音同一门面；失败打包进结果，不抛）。</summary>
    public static async Task<AsrResult> TranscribeAsync(byte[] pcm, int sampleRate, CancellationToken ct = default)
    {
        var service = Im.ImComposition.Instance?.AsrService;
        if (service is null)
        {
            return AsrResult.Fail("voice-input", "not-configured");
        }
        var wav = AudioFrontend.WrapWav(pcm, sampleRate);
        return await service.TranscribeAsync(new AsrRequest(wav, "audio/wav"), ct);
    }

    // ── 草稿合并 / 时长门卫 ─────────────────────────

    /// <summary>识别文本追加到输入框草稿（03 §1/D8 + 06 决策2）：空格衔接保留已有内容；识别为空原样返回。</summary>
    public static string MergeDraft(string? existing, string recognized)
    {
        var text = recognized.Trim();
        if (text.Length == 0) return existing ?? "";
        var draft = existing?.TrimEnd();
        if (string.IsNullOrEmpty(draft)) return text;
        return draft.EndsWith(' ') ? draft + text : draft + " " + text;
    }

    public static double DurationSeconds(long pcmBytes, int sampleRate = 16000) => pcmBytes / 2.0 / sampleRate;

    /// <summary>过短丢弃门卫（&lt;0.3s 多为误触/杂音）。</summary>
    public static bool TooShort(long pcmBytes, int sampleRate = 16000) => DurationSeconds(pcmBytes, sampleRate) < MinDurationSeconds;

    /// <summary>int16 峰值振幅（静音/死设备检测；只统计不外泄内容）。</summary>
    public static short PeakAmplitude(byte[] pcm16)
    {
        short peak = 0;
        for (int i = 0; i + 1 < pcm16.Length; i += 2)
        {
            var v = Math.Abs(BitConverter.ToInt16(pcm16, i));
            if (v > peak) peak = v;
        }
        return peak;
    }

    /// <summary>整段近乎零振幅 → 未采集到声音（权限被拒后的静默流/静音设备）。</summary>
    public static bool IsSilence(byte[] pcm16) => PeakAmplitude(pcm16) < SilencePeakThreshold;

    /// <summary>截断到 60s 硬上限（03 §2）。</summary>
    public static byte[] TruncateToMaxDuration(byte[] pcm, int sampleRate = 16000)
    {
        var maxBytes = sampleRate * 2 * MicRecorder.MaxDurationSeconds;
        return pcm.Length <= maxBytes ? pcm : pcm[..maxBytes];
    }
}
