using System;
using System.IO;
using System.Text;
namespace CustomSoundtrack
{
    public static class WavHeader
    {
        // Validate container bounds before entering Unity's native decoder. Never modify input.
        public static string Check(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream, Encoding.ASCII))
            {
                if (stream.Length < 12) return "Header is truncated";
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") return "Expected a RIFF WAV container";
                uint size = reader.ReadUInt32();
                if (size == uint.MaxValue || (long)size + 8 != stream.Length) return "RIFF size does not match file length";
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") return "Missing WAVE signature";
                bool format = false, data = false;
                int alignment = 0;
                long dataBytes = 0;
                while (stream.Position < stream.Length)
                {
                    if (stream.Length - stream.Position < 8) return "Truncated chunk header";
                    string tag = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    uint length = reader.ReadUInt32();
                    if (length == uint.MaxValue || (long)length > stream.Length - stream.Position) return "Invalid size for chunk " + tag;
                    long end = stream.Position + length;
                    if (tag == "fmt ")
                    {
                        if (length < 16) return "Truncated format chunk";
                        ushort encoding = reader.ReadUInt16(), channels = reader.ReadUInt16();
                        uint rate = reader.ReadUInt32(), byteRate = reader.ReadUInt32();
                        alignment = reader.ReadUInt16(); ushort bits = reader.ReadUInt16();
                        if (channels == 0 || rate == 0 || byteRate == 0 || alignment == 0) return "Invalid audio format fields";
                        if ((encoding == 1 || encoding == 3) && (bits == 0 || bits % 8 != 0 || alignment != channels * (bits / 8) || byteRate != (long)rate * alignment)) return "Inconsistent PCM format fields";
                        format = true;
                    }
                    else if (tag == "data") { data = true; dataBytes += length; }
                    stream.Position = end;
                    if ((length & 1) != 0)
                    {
                        if (stream.Position == stream.Length) return "Missing chunk padding";
                        stream.Position++;
                    }
                }
                if (!format || !data || dataBytes == 0) return "Missing format or audio data";
                if (dataBytes % alignment != 0) return "Audio data is not frame aligned";
                return null;
            }
        }
    }
}
