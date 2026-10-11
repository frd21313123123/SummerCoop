using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using SummerCoop.Core;

// Offline allocation comparison. The game and its transport are not loaded.
public static class MotionPacketBenchmark
{
    private static long sink;
    private static void Encode(MotionFrame motion, int peers, bool direct)
    {
        byte[] payload = direct ? null : Codec.Motion(motion);
        byte[] shared = direct ? Codec.MotionPacket(motion) : null;
        for (int peer = 0; peer < peers; peer++)
        {
            byte[] packet = direct ? shared : Codec.Packet(MessageKind.Motion, payload);
            // Both paths retain the LAN token envelope and an independently owned array.
            byte[] datagram = new byte[16 + packet.Length]; Array.Copy(packet, 0, datagram, 16, packet.Length);
            sink += datagram.Length + datagram[16];
        }
    }
    public static string Run(int frames, int rounds)
    {
        MethodInfo counter = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Static | BindingFlags.Public);
        if (counter == null) throw new Exception("Use modern PowerShell/.NET for the allocation counter.");
        Func<long> allocated = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), counter);
        MotionFrame motion = new MotionFrame(13) { Sequence = 77 };
        for (int i = 0; i < 13; i++) motion.Bodies.Add(new BodyPose { Id = Identity.Stable("part-" + i), Position = new V3(i, 0.1f, -3.75f), Velocity = new V3(0, i * 0.01f, 0) });
        string hash = Codec.Hash(Codec.Packet(MessageKind.Motion, Codec.Motion(motion)));
        if (Codec.Hash(Codec.MotionPacket(motion)) != hash) throw new Exception("Wire mismatch.");
        StringBuilder output = new StringBuilder();
        output.AppendLine("Offline CLR allocation replay; not Unity/Mono FPS proof");
        output.AppendLine("Runtime=" + Environment.Version + ", frames=" + frames + ", bodies=13, rounds=" + rounds + ", packetSHA256=" + hash);
        foreach (int peers in new int[] { 1, 3 })
        {
            long[] totals = new long[2];
            for (int mode = 0; mode < 2; mode++) for (int i = 0; i < 100; i++) Encode(motion, peers, mode == 1);
            for (int round = 0; round < rounds; round++) for (int order = 0; order < 2; order++)
            {
                int mode = (round + order) % 2;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Stopwatch clock = new Stopwatch(); long begin = allocated(); clock.Start();
                for (int i = 0; i < frames; i++) Encode(motion, peers, mode == 1);
                clock.Stop(); long bytes = allocated() - begin; totals[mode] += bytes;
                output.AppendLine("round=" + round + " peers=" + peers + " mode=" + mode + " allocated=" + bytes + " elapsedMs=" + clock.Elapsed.TotalMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            }
            for (int mode = 0; mode < 2; mode++) output.AppendLine("MEAN peers=" + peers + " mode=" + mode + " allocated=" + (totals[mode] / rounds));
        }
        output.AppendLine("Modes: 0=payload + per-peer packet, 1=one shared immutable packet; both include independent LAN token envelopes. sink=" + sink);
        return output.ToString();
    }
}
