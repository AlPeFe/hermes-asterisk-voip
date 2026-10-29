using System.Buffers.Binary;
using System.Text;

namespace HermesAsteriskVoip;

/// <summary>
/// Lector mínimo de cabeceras WAV (RIFF). Evita depender de NAudio o de
/// SoundPlayer (que no existe en Linux) solo para saber duración y formato.
/// </summary>
public static class Wav
{
    public readonly record struct WavInfo(double Seconds, int Rate, int Channels, int Bits);

    public static WavInfo Info(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            return new WavInfo(0, 0, 0, 0);

        int rate = 0, channels = 0, bits = 0;
        long dataSize = 0;
        var pos = 12;

        while (pos + 8 <= bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes, pos, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos + 4, 4));
            if (size < 0) break;

            if (id == "fmt " && pos + 8 + 16 <= bytes.Length)
            {
                channels = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(pos + 10, 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos + 12, 4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(pos + 22, 2));
            }
            else if (id == "data")
            {
                dataSize = Math.Min(size, bytes.Length - (pos + 8));
                break;
            }

            pos += 8 + size + (size & 1); // los chunks van alineados a 2 bytes
        }

        var bytesPerSecond = rate * channels * (bits / 8);
        var seconds = bytesPerSecond > 0 ? (double)dataSize / bytesPerSecond : 0;
        return new WavInfo(seconds, rate, channels, bits);
    }
}
