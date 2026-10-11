using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using SummerCoop.Core;

// Offline CLR replay only. This helper and the instrumented Codec never ship.
public static class DeltaContainerBenchmark
{
    public static bool Growing;
    private static byte[] Record(BinaryReader reader)
    {
        int count = reader.ReadInt32(); uint crc = reader.ReadUInt32();
        if (count < 0 || count > Protocol.MaxSnapshot) throw new Exception("Invalid record size");
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count || Codec.Crc(bytes) != crc) throw new Exception("Invalid record CRC");
        return bytes;
    }
    public static string Run(string directory, int rounds)
    {
        World baseline;
        using (BinaryReader reader = new BinaryReader(File.OpenRead(Path.Combine(directory, "world.scw")))) baseline = Codec.ImmutableWorld(Record(reader));
        World loaded = baseline.ReferenceSnapshot(); List<byte[]> records = new List<byte[]>(); long entities = 0; int peak = 0;
        using (BinaryReader reader = new BinaryReader(File.OpenRead(Path.Combine(directory, "world.scj"))))
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                byte[] bytes = Record(reader); World delta = null;
                Codec.Read(bytes, delegate(BinaryReader r) { delta = Codec.ReadJournalRecord(r, loaded); });
                if (delta.Revision <= loaded.Revision) continue;
                records.Add(bytes); entities += delta.Entities.Count; peak = Math.Max(peak, delta.Entities.Count);
                CheckpointStore.ApplyDelta(loaded, delta, true);
            }
        string expected = Codec.Hash(Codec.World(loaded));
        MethodInfo counter = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Static | BindingFlags.Public);
        if (counter == null) throw new Exception("Run this replay in modern PowerShell/.NET: allocation counter unavailable");
        Func<long> allocated = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), counter);
        StringBuilder output = new StringBuilder();
        output.AppendLine("Offline CLR replay; not Unity/Mono FPS proof");
        output.AppendLine("Runtime=" + Environment.Version + ", revision=" + baseline.Revision + "->" + loaded.Revision + ", records=" + records.Count + ", entities=" + entities + ", peakDelta=" + peak);
        output.AppendLine("Expected final SHA256=" + expected);
        DeltaReadBuffer reused = new DeltaReadBuffer();
        long[] totals = new long[3]; double[] times = new double[3];
        for (int round = -2; round < rounds; round++)
            for (int order = 0; order < 3; order++)
            {
                int mode = round < 0 ? order : (order + round) % 3;
                Growing = mode == 0; World world = baseline.ReferenceSnapshot();
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Stopwatch clock = new Stopwatch(); long begin = allocated(); int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2); clock.Start();
                foreach (byte[] bytes in records)
                {
                    World delta = null;
                    Codec.Read(bytes, delegate(BinaryReader r) { delta = mode == 2 ? Codec.ReadDelta(r, world, reused) : Codec.ReadDelta(r, world); });
                    CheckpointStore.ApplyDelta(world, delta, true);
                    if (mode == 2) reused.Clear();
                }
                clock.Stop(); long bytesAllocated = allocated() - begin;
                int collections0 = GC.CollectionCount(0) - gc0, collections1 = GC.CollectionCount(1) - gc1, collections2 = GC.CollectionCount(2) - gc2;
                // Serialize and hash outside both measured allocation and elapsed time.
                string actual = Codec.Hash(Codec.World(world));
                if (actual != expected) throw new Exception("Replay state mismatch for mode " + mode);
                if (round >= 0)
                {
                    totals[mode] += bytesAllocated; times[mode] += clock.Elapsed.TotalMilliseconds;
                    output.AppendLine("round=" + round + " mode=" + mode + " allocated=" + bytesAllocated + " elapsedMs=" + clock.Elapsed.TotalMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " GC=" + collections0 + "/" + collections1 + "/" + collections2 + " state=PASS");
                }
            }
        for (int mode = 0; mode < 3; mode++) output.AppendLine("MEAN mode=" + mode + " allocated=" + (totals[mode] / rounds) + " elapsedMs=" + (times[mode] / rounds).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        output.AppendLine("Modes: 0=growing dictionary (previous release), 1=pre-sized dictionary, 2=reused delta container");
        return output.ToString();
    }
}
