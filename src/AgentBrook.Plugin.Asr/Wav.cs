using System.Text;

namespace AgentBrook.Plugin.Asr;

/// <summary>最小 WAV(RIFF/PCM16) 解析：取 data chunk → (采样率, float[-1..1])。自检样例/单测用。</summary>
public static class Wav
{
    public static (int SampleRate, float[] Samples) ReadPcm16(byte[] bytes)
    {
        if (bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF")
            throw new InvalidDataException("not a RIFF file");
        int pos = 12, sampleRate = 16000, channels = 1;
        short bitsPerSample = 16;
        while (pos + 8 <= bytes.Length)
        {
            string id = Encoding.ASCII.GetString(bytes, pos, 4);
            int size = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(bytes, pos + 10);
                sampleRate = BitConverter.ToInt32(bytes, pos + 12);
                bitsPerSample = BitConverter.ToInt16(bytes, pos + 22);
            }
            else if (id == "data")
            {
                if (bitsPerSample != 16 || channels != 1)
                    throw new InvalidDataException($"unsupported wav: {channels}ch {bitsPerSample}bit");
                int n = size / 2;
                var samples = new float[n];
                for (int i = 0; i < n; i++)
                    samples[i] = BitConverter.ToInt16(bytes, pos + 8 + i * 2) / 32768f;
                return (sampleRate, samples);
            }
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException("data chunk not found");
    }
}
