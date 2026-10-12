using System;
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
                byte[] compressed = PortableLz.Compress(bytes);
                if (compressed != null)
                {
                    owned = compressed;
                    kind = kind == TransferKind.NativeSave ? TransferKind.CompressedSave : kind == TransferKind.World ? TransferKind.CompressedWorld : TransferKind.CompressedDelta;
                }
            }
            return new EncodedTransfer { Kind = kind, Bytes = owned, Hash = Codec.Hash(owned), OriginalLength = bytes.Length };
        }
        public static byte[] Decode(TransferKind kind, byte[] bytes)
        {
            if (!Compressed(kind)) return bytes;
            return PortableLz.Decompress(bytes);
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
