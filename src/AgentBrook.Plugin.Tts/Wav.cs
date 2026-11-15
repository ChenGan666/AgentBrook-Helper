namespace AgentBrook.Plugin.Tts;

/// <summary>最小 WAV(RIFF/PCM16) 写出：float[-1..1] → 16bit 单声道 wav 文件（播放路径的短暂物化）。</summary>
public static class Wav
{
    public static void WritePcm16(string path, float[] samples, int sampleRate)
    {
        using var bw = new BinaryWriter(File.Create(path));
        int n = samples.Length;
        bw.Write("RIFF"u8);
        bw.Write(36 + n * 2);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);              // PCM
        bw.Write((short)1);              // mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);        // byte rate
        bw.Write((short)2);              // block align
        bw.Write((short)16);             // bits
        bw.Write("data"u8);
        bw.Write(n * 2);
        for (int i = 0; i < n; i++)
        {
            bw.Write((short)Math.Clamp((int)Math.Round(samples[i] * 32767f), short.MinValue, short.MaxValue));
        }
    }
}
