using System.Text;
using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;

namespace AgentBrook.Helper.Core.Voice;

/// <summary>
/// 音频前端（doc/Voice D9）：宿主统一做解码/重采样归一化，转写引擎（本地插件/云端降级）只吃 16kHz 单声道。
/// Telegram voice = Ogg 封装的 Opus（Concentus.OggFile 解封装 + Concentus 解码，纯 C# 零 native 依赖）。
/// 语音字节不落盘、不进日志（脱敏红线）。
/// </summary>
public static class AudioFrontend
{
    public const int TargetSampleRate = 16000;

    /// <summary>是否为可本地解码的格式（ogg/opus、wav；mp3/m4a 等不支持——云端直传）。</summary>
    public static bool IsDecodable(string? mediaType, string? fileName = null)
    {
        if (string.IsNullOrWhiteSpace(mediaType)) return false;
        var ext = ExtensionOf(mediaType, fileName);
        return ext is "ogg" or "opus" or "wav";
    }

    /// <summary>媒体类型/文件名 → 上传扩展名（云端端点按文件名后缀嗅探）。</summary>
    public static string ExtensionOf(string? mediaType, string? fileName = null)
    {
        var fromName = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
        if (fromName.Length is > 0 and <= 5) return fromName;
        return (mediaType ?? "").ToLowerInvariant() switch
        {
            "audio/ogg" or "application/ogg" or "audio/opus" => "ogg",
            "audio/wav" or "audio/x-wav" or "audio/wave" => "wav",
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/mp4" or "audio/m4a" => "m4a",
            "audio/flac" => "flac",
            "audio/webm" => "webm",
            _ => "wav",
        };
    }

    /// <summary>
    /// 解码任意可解格式 → 16kHz 单声道 16bit PCM。ogg/opus 解码后线性重采样；wav 原生多采样率解析。
    /// 不可解格式返回 null（调用方走云端直传，不降级）。
    /// </summary>
    public static byte[]? ToPcm16k(byte[] audio, string? mediaType, string? fileName = null)
    {
        var ext = ExtensionOf(mediaType, fileName);
        try
        {
            switch (ext)
            {
                case "ogg" or "opus":
                {
                    var (rate, samples) = DecodeOggOpus(audio);
                    if (samples.Length == 0) return null;
                    var resampled = rate == TargetSampleRate ? samples : Resample(samples, rate, TargetSampleRate);
                    return FloatsToPcm16(resampled);
                }
                case "wav":
                {
                    var (rate, channels, bits, data) = ParseWav(audio);
                    if (bits != 16) return null;
                    var mono = DownmixToMono(data, channels);
                    var resampled = rate == TargetSampleRate ? mono : Resample(mono, rate, TargetSampleRate);
                    return FloatsToPcm16(resampled);
                }
                default:
                    return null;   // mp3/m4a/flac… 本地不可解
            }
        }
        catch
        {
            return null;           // 解码失败：调用方按原格式直传云端
        }
    }

    /// <summary>16bit PCM → WAV 容器（RIFF 头 + data），供不支持 ogg 的云端供应商降级上传。</summary>
    public static byte[] WrapWav(byte[] pcm16, int sampleRate = TargetSampleRate)
    {
        var header = new byte[44];
        var dataLen = pcm16.Length;
        // RIFF/WAVE/fmt(16, PCM, mono)/data
        "RIFF"u8.CopyTo(header.AsSpan(0));
        PutInt(header, 4, 36 + dataLen);
        "WAVE"u8.CopyTo(header.AsSpan(8));
        "fmt "u8.CopyTo(header.AsSpan(12));
        PutInt(header, 16, 16);                       // fmt chunk size
        PutShort(header, 20, 1);                      // PCM
        PutShort(header, 22, 1);                      // mono
        PutInt(header, 24, sampleRate);
        PutInt(header, 28, sampleRate * 2);           // byte rate
        PutShort(header, 32, 2);                      // block align
        PutShort(header, 34, 16);                     // bits
        "data"u8.CopyTo(header.AsSpan(36));
        PutInt(header, 40, dataLen);
        var wav = new byte[44 + dataLen];
        Buffer.BlockCopy(header, 0, wav, 0, 44);
        Buffer.BlockCopy(pcm16, 0, wav, 44, dataLen);
        return wav;
    }

    /// <summary>Concentus：Ogg 解封装 + Opus 解码（48kHz 原生）。</summary>
    private static (int Rate, float[] Samples) DecodeOggOpus(byte[] ogg)
    {
        var decoder = new OpusDecoder(48000, 1);
        using var ms = new MemoryStream(ogg);
        var stream = new OpusOggReadStream(decoder, ms);
        var pcm = new List<short>(48000);
        while (stream.HasNextPacket)
        {
            var packet = stream.DecodeNextPacket();
            if (packet is null) continue;              // 丢包/坏帧跳过（语音可容忍）
            pcm.AddRange(packet);
        }
        var samples = new float[pcm.Count];
        for (int i = 0; i < samples.Length; i++) samples[i] = pcm[i] / 32768f;
        return (48000, samples);
    }

    private static (int Rate, int Channels, short Bits, byte[] Data) ParseWav(byte[] bytes)
    {
        if (bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF")
            throw new InvalidDataException("not a RIFF file");
        int pos = 12, rate = 16000, channels = 1;
        short bits = 16;
        while (pos + 8 <= bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes, pos, 4);
            var size = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(bytes, pos + 10);
                rate = BitConverter.ToInt32(bytes, pos + 12);
                bits = BitConverter.ToInt16(bytes, pos + 22);
            }
            else if (id == "data")
            {
                var data = new byte[size];
                Buffer.BlockCopy(bytes, pos + 8, data, 0, size);
                return (rate, channels, bits, data);
            }
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException("data chunk not found");
    }

    /// <summary>交错多声道 → 单声道（均值）。</summary>
    private static float[] DownmixToMono(byte[] pcm16, int channels)
    {
        int frames = pcm16.Length / 2 / Math.Max(1, channels);
        var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            int acc = 0;
            for (int c = 0; c < channels; c++)
                acc += BitConverter.ToInt16(pcm16, (i * channels + c) * 2);
            mono[i] = acc / channels / 32768f;
        }
        return mono;
    }

    /// <summary>线性插值重采样（ASR 前端可接受；非音频制作用途）。</summary>
    internal static float[] Resample(float[] input, int fromRate, int toRate)
    {
        if (input.Length == 0 || fromRate == toRate) return input;
        var ratio = (double)fromRate / toRate;
        var outLen = (int)(input.Length / ratio);
        var output = new float[outLen];
        for (int i = 0; i < outLen; i++)
        {
            var src = i * ratio;
            var i0 = (int)src;
            var i1 = Math.Min(i0 + 1, input.Length - 1);
            var frac = src - i0;
            output[i] = (float)(input[i0] * (1 - frac) + input[i1] * frac);
        }
        return output;
    }

    private static byte[] FloatsToPcm16(float[] samples)
    {
        var pcm = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
            BitConverter.GetBytes((short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue))
                .CopyTo(pcm, i * 2);
        return pcm;
    }

    private static void PutInt(byte[] b, int at, int v) => BitConverter.GetBytes(v).CopyTo(b, at);
    private static void PutShort(byte[] b, int at, short v) => BitConverter.GetBytes(v).CopyTo(b, at);
}
