using System;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace SummerCoop.Core
{
    public sealed class EncodedTransfer
    {
        public TransferKind Kind;
        public byte[] Bytes;
        public string Hash;
        public int OriginalLength;
    }
    public static class TransferEncoding
    {
        public static bool Compressed(TransferKind kind) { return kind == TransferKind.CompressedSave || kind == TransferKind.CompressedWorld || kind == TransferKind.CompressedDelta; }
        public static TransferKind Plain(TransferKind kind)
        {
            if (kind == TransferKind.CompressedSave) return TransferKind.NativeSave;
            if (kind == TransferKind.CompressedWorld) return TransferKind.World;
            if (kind == TransferKind.CompressedDelta) return TransferKind.Delta;
            return kind;
        }
        public static EncodedTransfer Encode(TransferKind kind, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > Protocol.MaxSnapshot || Compressed(kind)) throw new ProtocolException("Invalid transfer input");
            byte[] owned = (byte[])bytes.Clone();
            if (bytes.Length >= 4096)
            {
                using (MemoryStream stream = new MemoryStream())
                {
                    byte[] size = BitConverter.GetBytes(bytes.Length); stream.Write(size, 0, size.Length);
                    using (DeflateStream deflate = new DeflateStream(stream, CompressionMode.Compress, true)) deflate.Write(bytes, 0, bytes.Length);
                    if (stream.Length + 64 < bytes.Length)
                    {
                        owned = stream.ToArray();
                        kind = kind == TransferKind.NativeSave ? TransferKind.CompressedSave : kind == TransferKind.World ? TransferKind.CompressedWorld : TransferKind.CompressedDelta;
                    }
                }
            }
            return new EncodedTransfer { Kind = kind, Bytes = owned, Hash = Codec.Hash(owned), OriginalLength = bytes.Length };
        }
        public static byte[] Decode(TransferKind kind, byte[] bytes)
        {
            if (!Compressed(kind)) return bytes;
            if (bytes == null || bytes.Length < 5) throw new ProtocolException("Truncated compressed transfer");
            int size = BitConverter.ToInt32(bytes, 0);
            if (size < 1 || size > Protocol.MaxSnapshot) throw new ProtocolException("Expanded transfer budget exceeded");
            byte[] decoded = new byte[size];
            using (MemoryStream stream = new MemoryStream(bytes, 4, bytes.Length - 4, false))
            using (DeflateStream deflate = new DeflateStream(stream, CompressionMode.Decompress))
            {
                int offset = 0, count;
                while (offset < size && (count = deflate.Read(decoded, offset, size - offset)) > 0) offset += count;
                if (offset != size || deflate.ReadByte() != -1) throw new ProtocolException("Expanded transfer size mismatch");
            }
            return decoded;
        }
    }
    // Serialization uses only an owned, immutable model snapshot, never Unity objects.
    internal sealed class WorldTransferJob
    {
        private int completed;
        public EncodedTransfer Result;
        public Exception Error;
        public bool Completed { get { return Interlocked.CompareExchange(ref completed, 0, 0) != 0; } }
        public WorldTransferJob(World snapshot)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { Result = TransferEncoding.Encode(TransferKind.World, Codec.World(snapshot)); }
                catch (Exception e) { Error = e; }
                finally { Interlocked.Exchange(ref completed, 1); }
            });
        }
    }
}
