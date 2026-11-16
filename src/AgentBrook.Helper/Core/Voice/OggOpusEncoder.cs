using System.Text;
using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;

namespace AgentBrook.Helper.Core.Voice;

/// <summary>
/// Ogg-Opus 编码（S0-A spike 实测通过：Concentus 纯托管 OpusEncoder + OpusOggWriteStream，
/// 写流内置 inputSampleRate→48k 重采样；10s 音频编码 306ms、解码时长零偏差）。
/// 输入：24kHz 单声道 PCM16 容器字节（TTS 纯合成产物）；输出：ogg 容器字节（Telegram sendVoice）。
/// </summary>
internal static class OggOpusEncoder
{
    public static byte[] Encode(byte[] wav24k)
    {
        var (rate, samples16) = ReadWavPcm16(wav24k);
        var encoder = new OpusEncoder(rate, 1, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = 48000;   // 单声道语音 48kbps
        var tags = new OpusTags();

        using var oggFs = File.Create(Path.Combine(Path.GetTempPath(), $"ab-voice-{Guid.NewGuid():N}.ogg"));
        var writer = new OpusOggWriteStream(encoder, oggFs, tags, rate, 10, leaveOpen: false);
        int chunk = rate / 50;   // 20ms
        for (int off = 0; off < samples16.Length; off += chunk)
        {
            var n = Math.Min(chunk, samples16.Length - off);
            writer.WriteSamples(samples16, off, n);
        }
        writer.Finish();
        oggFs.Close();
        return File.ReadAllBytes(oggFs.Name);
    }

    private static (int Rate, short[] Samples) ReadWavPcm16(byte[] bytes)
    {
        int pos = 12, rate = 24000;
        while (pos + 8 <= bytes.Length)
        {
            string id = Encoding.ASCII.GetString(bytes, pos, 4);
            int size = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "fmt ")
            {
                rate = BitConverter.ToInt32(bytes, pos + 12);
            }
            else if (id == "data")
            {
                int n = size / 2;
                var samples = new short[n];
                for (int i = 0; i < n; i++)
                    samples[i] = BitConverter.ToInt16(bytes, pos + 8 + i * 2);
                return (rate, samples);
            }
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException("data chunk not found");
    }
}
