using System;
using SummerCoop.Core;

internal static class TransferEncodingTests
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    public static void Run(Action<string, Action> test)
    {
        test("compressed scene seed and world preserve every byte and own their input", delegate
        {
            foreach (TransferKind kind in new[] { TransferKind.NativeSave, TransferKind.World, TransferKind.Delta })
            {
                byte[] bytes = new byte[2000000]; for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 7);
                EncodedTransfer encoded = TransferEncoding.Encode(kind, bytes); bytes[0] = 99;
                Check(TransferEncoding.Compressed(encoded.Kind) && encoded.Bytes.Length < 20000, "Repeated snapshot was not compressed");
                byte[] decoded = TransferEncoding.Decode(encoded.Kind, encoded.Bytes);
                Check(decoded.Length == bytes.Length && decoded[0] == 0 && decoded[bytes.Length - 1] == (byte)((bytes.Length - 1) % 7), "Compressed snapshot lost ownership or content");
                Check(TransferEncoding.Plain(encoded.Kind) == kind && Codec.Hash(encoded.Bytes) == encoded.Hash, "Transfer kind/hash mismatch");
            }
        });
        test("small and incompressible transfers remain bounded owned plain payloads", delegate
        {
            byte[] small = { 1, 2, 3 }; EncodedTransfer encoded = TransferEncoding.Encode(TransferKind.NativeSave, small); small[0] = 9;
            Check(encoded.Kind == TransferKind.NativeSave && encoded.Bytes[0] == 1, "Small input retained mutable caller data");
            byte[] random = new byte[50000]; new Random(4185).NextBytes(random);
            encoded = TransferEncoding.Encode(TransferKind.World, random);
            Check(encoded.Kind == TransferKind.World && Codec.Hash(encoded.Bytes) == Codec.Hash(random), "Incompressible transfer changed bytes or grew");
        });
        test("compressed transfers reject forged lengths, truncation and expansion overflow", delegate
        {
            EncodedTransfer encoded = TransferEncoding.Encode(TransferKind.World, new byte[10000]);
            foreach (int length in new[] { -1, 0, Protocol.MaxSnapshot + 1, 9999, 10001 })
            {
                byte[] forged = (byte[])encoded.Bytes.Clone(); Array.Copy(BitConverter.GetBytes(length), forged, 4);
                bool failed = false; try { TransferEncoding.Decode(encoded.Kind, forged); } catch { failed = true; }
                Check(failed, "Forged expanded length accepted: " + length);
            }
            bool truncated = false; try { TransferEncoding.Decode(encoded.Kind, new byte[] { 1, 2, 3 }); } catch { truncated = true; }
            Check(truncated, "Truncated compression envelope accepted");
        });
        test("scene readiness uses two actual ready frames without a ten-second timer", delegate
        {
            SceneReadiness ready = new SceneReadiness();
            Check(!ready.Observe(1, false, true, true) && !ready.Observe(2, true, false, true), "Scene admitted without native player");
            Check(!ready.Observe(3, true, true, true) && !ready.Observe(3, true, true, true), "Repeated observation counted a new frame");
            Check(ready.Observe(4, true, true, true), "Ready scene retained an artificial wait");
            Check(!ready.Observe(5, true, true, false) && !ready.Observe(6, true, true, true) && ready.Observe(7, true, true, true), "Readiness did not reset after lost camera");
        });
    }
}
