using System;
using System.IO;

namespace SummerCoop.Core
{
    // Small, fully managed transfer codec for Unity 5's stripped Mono runtime.
    // Envelope: expanded size (4), format version (1), then literals/references.
    // Literal token 0..127 stores 1..128 bytes. Reference token 128..255 stores
    // length 4..131 and a 16-bit distance; length 131 has a 16-bit extension.
    internal static class PortableLz
    {
        private const int MaxDistance = 65535, MaxMatch = 131 + 65535;
        private static int Hash(byte[] bytes, int offset)
        {
            uint value = (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);
            return (int)(unchecked(value * 2654435761u) >> 16);
        }
        private static bool Literals(MemoryStream stream, byte[] bytes, int start, int end, int limit)
        {
            while (start < end)
            {
                int count = Math.Min(128, end - start);
                if (stream.Length + count + 1 >= limit) return false;
                stream.WriteByte((byte)(count - 1)); stream.Write(bytes, start, count); start += count;
            }
            return true;
        }
        public static byte[] Compress(byte[] bytes)
        {
            int limit = bytes.Length - 64;
            int[] positions = new int[65536];
            using (MemoryStream stream = new MemoryStream())
            {
                byte[] size = BitConverter.GetBytes(bytes.Length); stream.Write(size, 0, 4); stream.WriteByte(1);
                int offset = 0, literal = 0;
                while (offset + 4 <= bytes.Length)
                {
                    int hash = Hash(bytes, offset), candidate = positions[hash] - 1; positions[hash] = offset + 1;
                    if (candidate < 0 || offset - candidate > MaxDistance || bytes[candidate] != bytes[offset]
                        || bytes[candidate + 1] != bytes[offset + 1] || bytes[candidate + 2] != bytes[offset + 2] || bytes[candidate + 3] != bytes[offset + 3]) { offset++; continue; }
                    int count = 4, maximum = Math.Min(MaxMatch, bytes.Length - offset);
                    while (count < maximum && bytes[candidate + count] == bytes[offset + count]) count++;
                    if (!Literals(stream, bytes, literal, offset, limit)) return null;
                    int distance = offset - candidate;
                    stream.WriteByte((byte)(128 | Math.Min(127, count - 4)));
                    stream.WriteByte((byte)distance); stream.WriteByte((byte)(distance >> 8));
                    if (count >= 131) { int extra = count - 131; stream.WriteByte((byte)extra); stream.WriteByte((byte)(extra >> 8)); }
                    if (stream.Length >= limit) return null;
                    int end = offset + count;
                    // Remember the end of a match without rescanning its entire run.
                    for (int tail = Math.Max(offset + 1, end - 8); tail < end && tail + 4 <= bytes.Length; tail++) positions[Hash(bytes, tail)] = tail + 1;
                    offset = literal = end;
                }
                if (!Literals(stream, bytes, literal, bytes.Length, limit)) return null;
                return stream.Length < limit ? stream.ToArray() : null;
            }
        }
        public static byte[] Decompress(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 6) throw new ProtocolException("Truncated compressed transfer");
            int size = BitConverter.ToInt32(bytes, 0);
            if (size < 1 || size > Protocol.MaxSnapshot) throw new ProtocolException("Expanded transfer budget exceeded");
            if (bytes[4] != 1) throw new ProtocolException("Unsupported managed transfer format");
            byte[] output = new byte[size]; int input = 5, offset = 0;
            while (input < bytes.Length)
            {
                int token = bytes[input++];
                if (token < 128)
                {
                    int count = token + 1;
                    if (count > bytes.Length - input || count > size - offset) throw new ProtocolException("Invalid compressed literal");
                    Array.Copy(bytes, input, output, offset, count); input += count; offset += count;
                }
                else
                {
                    if (bytes.Length - input < 2) throw new ProtocolException("Truncated compressed reference");
                    int distance = bytes[input] | bytes[input + 1] << 8; input += 2;
                    int count = (token & 127) + 4;
                    if (count == 131)
                    {
                        if (bytes.Length - input < 2) throw new ProtocolException("Truncated compressed run");
                        count += bytes[input] | bytes[input + 1] << 8; input += 2;
                    }
                    if (distance < 1 || distance > offset || count > size - offset) throw new ProtocolException("Invalid compressed reference");
                    // Forward copying intentionally supports overlapping runs.
                    for (int i = 0; i < count; i++) output[offset + i] = output[offset + i - distance];
                    offset += count;
                }
            }
            if (offset != size) throw new ProtocolException("Expanded transfer size mismatch");
            return output;
        }
    }
}
