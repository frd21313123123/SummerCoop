using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using SummerCoop.Core;

// Diagnostic network peer only: it cannot validate Unity rendering or native interactions.
internal static class CorePeer
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 3) throw new ArgumentException("Usage: CorePeer host-profile port playing-seconds");
            string checkpoint = Path.Combine(Path.GetFullPath(args[0]), "world\\world.scw");
            Stopwatch clock = Stopwatch.StartNew();
            while (!File.Exists(checkpoint) && clock.Elapsed.TotalSeconds < 30) Thread.Sleep(100);
            string gameHash;
            using (FileStream file = File.OpenRead(checkpoint)) using (BinaryReader reader = new BinaryReader(file))
            {
                int size = Codec.Count(reader, Protocol.MaxSnapshot); reader.ReadUInt32();
                if (size != file.Length - 8 || reader.ReadInt32() != Protocol.Version) throw new ProtocolException("Invalid host checkpoint header");
                Codec.Text(reader); gameHash = Codec.Text(reader);
            }
            double duration = double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), playingAt = -1, nextPose = 0;
            bool ended = false; string reason = "";
            using (Session session = new Session(LanTransport.Join("127.0.0.1", int.Parse(args[1])), null, gameHash, "DiagnosticCorePeer", "", clock.Elapsed.TotalSeconds))
            {
                session.NativeSaveReceived = delegate(byte[] ignored) { session.SceneLoaded(session.World.SchemaHash); };
                session.Ended = delegate(string message) { ended = true; reason = message; };
                while (!ended)
                {
                    double now = clock.Elapsed.TotalSeconds; session.Update(now);
                    if (session.Phase == SessionPhase.Playing)
                    {
                        if (playingAt < 0) { playingAt = now; Console.WriteLine("PLAYING entities=" + session.World.Entities.Count); }
                        if (now >= nextPose) { session.SendPose(new Pose { Position = new V3(1565.742f, 4.848f, 720.557f), Rotation = Q4.Identity }); nextPose = now + .05; }
                        if (now - playingAt >= duration) { Console.WriteLine("COMPLETE revision=" + session.World.Revision); return 0; }
                    }
                    else if (now > 120) throw new TimeoutException("Core peer did not reach Playing");
                    Thread.Sleep(2);
                }
                throw new IOException("Core peer disconnected: " + reason);
            }
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
