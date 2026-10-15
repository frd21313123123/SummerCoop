using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MWCoop.Net;

class MscAdapterTests
{
    static int passed;
    static void Assert(bool value, string reason) { if (!value) throw new Exception(reason); }
    static void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    static void Reject(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Malformed packet accepted"); }
    sealed class Simulated : ILink
    {
        public static readonly Dictionary<string, Simulated> Nodes = new Dictionary<string, Simulated>();
        public static float Time;
        static Random random = new Random(516750);
        sealed class Item { public string From; public byte[] Bytes; public float Due; }
        readonly List<Item> packets = new List<Item>();
        public string Id;
        public Simulated(string id) { Id = id; Nodes[id] = this; }
        public bool Send(object to, byte[] data, int length)
        {
            if (random.Next(100) < 5) return true;
            byte[] owned = new byte[length]; Buffer.BlockCopy(data, 0, owned, 0, length);
            Nodes[(string)to].packets.Add(new Item { From = Id, Bytes = owned, Due = Time + .15f + (float)random.NextDouble() * .08f }); return true;
        }
        public bool Receive(byte[] buffer, out int length, out object from)
        {
            for (int i = packets.Count - 1; i >= 0; i--) if (packets[i].Due <= Time) { Item item = packets[i]; packets.RemoveAt(i); from = item.From; length = item.Bytes.Length; Buffer.BlockCopy(item.Bytes, 0, buffer, 0, length); return true; }
            from = null; length = 0; return false;
        }
        public string KeyOf(object value) { return (string)value; }
        public string Describe(object value) { return (string)value; }
        public void Forget(object value) { }
        public void Close() { packets.Clear(); }
    }
    static Transport Create() { return new Transport { NetVersion = 1044 }; }
    static int Main(string[] args)
    {
        try
        {
#if !MWCOOP_NETWORK_ONLY
            Test("MSC assembly loads MWCoop runtime without the legacy SummerCoop runtime", delegate {
                var assembly = typeof(MWCoop.MscRuntime).Assembly;
                Assert(assembly.GetType("MWCoop.Core") != null && assembly.GetType("SummerCoop.Game.SummerCoopMod") != null, "New runtime missing");
                Assert(assembly.GetType("SummerCoop.Game.CoopRuntime") == null, "Two multiplayer runtimes would run");
                Assert(Session.NetVersion != 41 && MWCoop.Version.Text == SummerCoop.Core.Protocol.ModVersion, "Build identities disagree");
            });
#endif
            Test("MWCoop codec retains Cyrillic, exact numbers and bounded owned slices", delegate {
                byte[] bytes = new NetWriter(Msg.Chat).Str("Привет, MSC 🚗").I32(123).F32(1.25f).Bool(true).Bytes(new byte[] { 4, 5, 6 }, 1, 2).ToArray();
                NetReader reader = new NetReader(bytes, 1, bytes.Length - 1);
                Assert(reader.Str() == "Привет, MSC 🚗" && reader.I32() == 123 && reader.F32() == 1.25f && reader.Bool(), "Codec changed values");
                Assert(reader.Bytes()[1] == 6 && !reader.More, "Byte slice changed");
            });
            Test("MWCoop codec rejects invalid ranges, truncation and UTF8", delegate {
                Reject(delegate { new NetReader(new byte[8], int.MaxValue, 4); }); Reject(delegate { new NetReader(new byte[8], 0, -1); });
                Reject(delegate { new NetReader(new byte[] { 3, 0, 65 }, 0, 3).Str(); }); Reject(delegate { new NetReader(new byte[] { 1, 0, 255 }, 0, 3).Str(); });
                Reject(delegate { new NetWriter(Msg.Chat).Str(new string('x', 16385)); }); Reject(delegate { new NetWriter(Msg.Chat).Bytes(new byte[4], 0, 65536); });
            });
            Test("MWCoop rejects NaN, infinity and invalid booleans before game state changes", delegate {
                foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity }) Reject(delegate { new NetReader(BitConverter.GetBytes(value), 0, 4).F32(); });
                Reject(delegate { new NetReader(BitConverter.GetBytes(double.NaN), 0, 8).F64(); }); Reject(delegate { new NetReader(new byte[] { 2 }, 0, 1).Bool(); });
            });
#if !MWCOOP_NETWORK_ONLY
            Test("MSC edits reject duplicate/reordered requests and follow the host's canonical revision", delegate {
                var hostClock = new MWCoop.MscEditClock();
                float hostValue = 0;
                int hostRevision = 0;
                foreach (int sequence in new[] { 3, 1, 2, 3 })
                    if (hostClock.AcceptRequest(1, sequence)) { hostValue = sequence * .3f; hostRevision = hostClock.Publish(true); }
                Assert(Math.Abs(hostValue - .9f) < .0001f && hostRevision == 1, "Older actor packet overwrote the final adjustment");
                Assert(hostClock.AcceptRequest(2, 1), "A different player's first edit was rejected");
                hostValue = .6f; hostRevision = hostClock.Publish(true);
                var observer = new MWCoop.MscEditClock(); float observed = 0;
                if (observer.AcceptSnapshot(hostRevision)) observed = hostValue;
                if (observer.AcceptSnapshot(1)) observed = .9f;
                if (observer.AcceptSnapshot(hostRevision)) observed = 5;
                Assert(observed == .6f, "Duplicate or stale host snapshot overwrote the latest value");
                var lateJoiner = new MWCoop.MscEditClock();
                Assert(lateJoiner.AcceptSnapshot(hostRevision), "Late joiner rejected the current host snapshot");
                Assert(!hostClock.AcceptRequest(2, 0) && !lateJoiner.AcceptSnapshot(0), "Unversioned edits were accepted");
                hostClock.ForgetActor(2);
                Assert(hostClock.AcceptRequest(2, 1) && hostClock.HostRevision == hostRevision, "A rejoining player's reset request sequence was rejected or changed the host revision");
            });
            Test("MSC policy excludes winter terrain, MWC shops and native DXR while retaining major coop modules", delegate {
                foreach (Type type in new[] { typeof(MWCoop.Clearing), typeof(MWCoop.Kauppa), typeof(MWCoop.Frost), typeof(MWCoop.RayTracing) }) Assert(!MWCoop.MscRuntime.Supports(type), "Winter-only module enabled: " + type);
                foreach (Type type in new[] { typeof(MWCoop.PlayerSync), typeof(MWCoop.VehicleSync), typeof(MWCoop.Parts), typeof(MWCoop.Chat), typeof(MWCoop.WorldFsms), typeof(MWCoop.Wallet), typeof(SaveTransfer) }) Assert(MWCoop.MscRuntime.Supports(type), "Core MWCoop module removed: " + type);
            });
            Test("MSC mount adapter preserves variant HandChild slots and reads native Part parenting actions", delegate {
                var names = new[] { new HutongGames.PlayMaker.FsmString { Name = "HandChild2", Value = "starter(Clone)" }, new HutongGames.PlayMaker.FsmString { Name = "Other", Value = "block(Clone)" } };
                Assert(SummerCoop.Game.NativeAssembly.Accepts(names, "starter(Clone)") && !SummerCoop.Game.NativeAssembly.Accepts(names, "block(Clone)"), "MSC mount compatibility changed");
                var action = new HutongGames.PlayMaker.Actions.SetParent { gameObject = new HutongGames.PlayMaker.FsmOwnerDefault { OwnerOption = HutongGames.PlayMaker.OwnerDefaultOption.SpecifyGameObject, GameObject = new HutongGames.PlayMaker.FsmGameObject { Name = "Part" } } };
                Assert(object.ReferenceEquals(SummerCoop.Game.NativeAssembly.MountAction(new HutongGames.PlayMaker.FsmStateAction[] { action }), action), "Native Part parent was ignored");
            });
            Test("MSC respawn offers only the church and parents' house", delegate {
                var destinations = (Array)typeof(MWCoop.Respawn).GetField("places", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);
                Assert(destinations.Length == 2, "Unexpected respawn destinations");
                string first = (string)destinations.GetValue(0).GetType().GetField("NameEn").GetValue(destinations.GetValue(0));
                string second = (string)destinations.GetValue(1).GetType().GetField("NameEn").GetValue(destinations.GetValue(1));
                Assert(first == "the church" && second == "the parents' house", "MWC apartment respawn survived");
            });
#endif
            Test("standing player support poses retain local feet, head and yaw and detach without stale attachment data", delegate {
                var pose = new SupportPose { Vehicle = 5, Feet = new UnityEngine.Vector3(.2f, 1.4f, -2.3f), Head = new UnityEngine.Vector3(.3f, 3f, -2.2f), Yaw = -45f };
                var writer = new NetWriter(Msg.PlayerState); pose.Write(writer);
                byte[] bytes = writer.ToArray();
                var decoded = SupportPose.Read(new NetReader(bytes, 1, bytes.Length - 1));
                Assert(decoded.Attached && decoded.VehicleIndex == 4 && decoded.Feet.x == .2f && decoded.Head.y == 3f && decoded.Yaw == -45f, "Relative player pose changed on the wire");
                writer = new NetWriter(Msg.PlayerState); new SupportPose().Write(writer); bytes = writer.ToArray();
                decoded = SupportPose.Read(new NetReader(bytes, 1, bytes.Length - 1));
                Assert(!decoded.Attached && decoded.VehicleIndex == -1 && bytes.Length == 2, "Leaving a platform retained the old vehicle or coordinates");
            });
            Test("kilju snapshots retain persistent recipe values, fermentation progress and lid state", delegate {
                var recipe = new BrewState { Sugar = 6, Yeast = 1, Water = 30, Alcohol = .14f, Sweetness = .2f, Time = 681, Vinegar = .0004f, Finished = true, LidOn = true };
                var writer = new NetWriter(Msg.Home); recipe.Write(writer); byte[] bytes = writer.ToArray();
                var reader = new NetReader(bytes, 1, bytes.Length - 1); BrewState decoded = BrewState.Read(reader);
                Assert(!reader.More && recipe.Equals(decoded), "Persistent kilju contents changed on the wire");
                writer = new NetWriter(Msg.Home); new BrewState().Write(writer); bytes = writer.ToArray();
                decoded = BrewState.Read(new NetReader(bytes, 1, bytes.Length - 1));
                Assert(decoded.Water == 0 && decoded.Sugar == 0 && !decoded.Finished && !decoded.LidOn, "An emptied bucket retained the previous recipe");
            });
            Test("invalid kilju quantities, nonfinite quality, truncated frames and invalid flags are rejected", delegate {
                foreach (float value in new[] { float.NaN, float.PositiveInfinity, -1, 100 })
                    Reject(delegate { new BrewState { Water = value }.Write(new NetWriter(Msg.Home)); });
                Reject(delegate { new BrewState { Alcohol = .5f }.Write(new NetWriter(Msg.Home)); });
                Reject(delegate { BrewState.Read(new NetReader(new byte[27], 0, 27)); });
                byte[] bad = new byte[30]; bad[28] = 2;
                Reject(delegate { BrewState.Read(new NetReader(bad, 0, bad.Length)); });
                bad = new byte[30]; Buffer.BlockCopy(BitConverter.GetBytes(float.NaN), 0, bad, 0, 4);
                Reject(delegate { BrewState.Read(new NetReader(bad, 0, bad.Length)); });
            });
            Test("invalid, truncated and excessive player support poses are refused before applying an avatar", delegate {
                Reject(delegate { new SupportPose { Vehicle = 256 }.Write(new NetWriter(Msg.PlayerState)); });
                Reject(delegate { new SupportPose { Vehicle = -1 }.Write(new NetWriter(Msg.PlayerState)); });
                Reject(delegate { new SupportPose { Vehicle = 1, Feet = new UnityEngine.Vector3(33f, 0, 0) }.Write(new NetWriter(Msg.PlayerState)); });
                Reject(delegate { SupportPose.Read(new NetReader(new byte[] { 1 }, 0, 1)); });
                Reject(delegate { SupportPose.Read(new NetReader(new byte[] { 0, 1 }, 0, 2)); });
                foreach (float value in new[] { float.NaN, float.PositiveInfinity, 33f }) {
                    byte[] bad = new NetWriter(Msg.PlayerState).U8(1).Vec(new UnityEngine.Vector3(value, 0, 0)).Vec(new UnityEngine.Vector3()).F32(0).ToArray();
                    Reject(delegate { SupportPose.Read(new NetReader(bad, 1, bad.Length - 1)); });
                }
            });
            Test("moving MSC vehicles without cabin heat survive the production motion decoder", delegate {
                foreach (int mode in new[] { 1, 2 })
                {
                    var writer = new NetWriter(Msg.Vehicle).U8(1).U8(0).U8(mode)
                        .Vec(new UnityEngine.Vector3(10f, 2f, 30f)).Quat(new UnityEngine.Quaternion(0f, 0f, 0f, 1f))
                        .Vec(new UnityEngine.Vector3(3f, 0f, 0f)).Vec(new UnityEngine.Vector3())
                        .F32(1400f).F32(.5f).F32(.1f).F32(float.NaN).U16(0);
                    if (mode == 1) writer.Vec(new UnityEngine.Vector3(0f, 1f, 0f));
                    byte[] packet = writer.F32(12f).ToArray();
                    var reader = new NetReader(packet, 4, packet.Length - 4);
                    VehicleSnapshot state = VehicleSnapshot.Read(reader, mode);
                    Assert(!reader.More && float.IsNaN(state.Heat) && state.Rpm == 1400f && state.Position.x == 10f && state.Velocity.x == 3f && state.Timestamp == 12f, "Driving packet lost motion or optional heat");
                }
                // Tow.SendCar deliberately has no timestamp or heater.
                byte[] tow = new NetWriter(Msg.Vehicle).Vec(new UnityEngine.Vector3(2f, 0f, 0f))
                    .Quat(new UnityEngine.Quaternion(0f, 0f, 0f, 1f)).Vec(new UnityEngine.Vector3()).Vec(new UnityEngine.Vector3())
                    .F32(0f).F32(0f).F32(0f).F32(float.NaN).U16(0).ToArray();
                VehicleSnapshot towed = VehicleSnapshot.Read(new NetReader(tow, 1, tow.Length - 1), 2);
                Assert(towed.Timestamp == -1f && float.IsNaN(towed.Heat) && towed.Position.x == 2f, "Upstream towing packet no longer decodes");
                byte[] parked = new NetWriter(Msg.Vehicle).Vec(new UnityEngine.Vector3(5f, 0f, 0f))
                    .Quat(new UnityEngine.Quaternion(0f, 0f, 0f, 1f)).Vec(new UnityEngine.Vector3()).Vec(new UnityEngine.Vector3()).ToArray();
                Assert(VehicleSnapshot.Read(new NetReader(parked, 1, parked.Length - 1), 0).Position.x == 5f, "Parked packet changed");
            });
            Test("optional cabin heat cannot weaken validation of vehicle motion", delegate {
                Assert(float.IsNaN(new NetReader(BitConverter.GetBytes(float.NaN), 0, 4).OptionalF32()), "Missing heat not retained");
                foreach (float v in new[] { float.PositiveInfinity, float.NegativeInfinity }) Reject(delegate { new NetReader(BitConverter.GetBytes(v), 0, 4).OptionalF32(); });
                foreach (int mode in new[] { 1, 2 })
                foreach (int badField in new[] { 0, 13, 16 })
                {
                    // 13 pose/velocity floats, then rpm/throttle/steering/heat.
                    var w = new NetWriter(Msg.Vehicle);
                    for (int i = 0; i < 17; i++) w.F32(i == badField ? (badField == 16 ? float.PositiveInfinity : float.NaN) : 0f);
                    byte[] packet = w.U16(0).ToArray();
                    Reject(delegate { VehicleSnapshot.Read(new NetReader(packet, 1, packet.Length - 1), mode); });
                }
                Reject(delegate { VehicleSnapshot.Read(new NetReader(new byte[0], 0, 0), 3); });
            });
            Test("active vehicle owner outranks delayed parked samples and yields to the next driver", delegate {
                Assert(!VehicleSnapshot.AcceptMotion(0, 0, 1), "Host parking sample overrode guest driver");
                Assert(!VehicleSnapshot.AcceptMotion(2, 1, 2), "Previous owner's coast sample overrode next driver");
                Assert(VehicleSnapshot.AcceptMotion(0, 1, 1), "Current driver cannot release vehicle");
                Assert(VehicleSnapshot.AcceptMotion(1, 2, 1), "Next driver cannot take over");
                Assert(VehicleSnapshot.AcceptMotion(0, 0, -1), "Host cannot sync parked vehicle");
            });
            Test("MWCoop reliable UDP protocol preserves three clients' ordering through loss and reordering", delegate {
                Simulated.Time = 0; Simulated.Nodes.Clear(); Transport host = Create(); host.Host(new Simulated("host")); List<Transport> clients = new List<Transport>();
                Dictionary<int, int> received = new Dictionary<int, int>();
                host.OnMessage += delegate(Peer peer, byte[] data, int offset, int length) { int value = BitConverter.ToInt32(data, offset); int expected; received.TryGetValue(peer.Id, out expected); Assert(value == expected, "Reliable message duplicated or reordered"); received[peer.Id] = expected + 1; };
                try {
                    for (int i = 0; i < 3; i++) { Transport client = Create(); clients.Add(client); client.Connect(new Simulated("client" + i), "host"); }
                    bool sent = false;
                    for (int step = 0; step < 4000; step++) {
                        Simulated.Time = step * .02f; host.Update(Simulated.Time); foreach (Transport client in clients) client.Update(Simulated.Time);
                        if (!sent && host.Peers.Count == 3 && clients.TrueForAll(c => c.LocalId > 0)) { foreach (Transport client in clients) for (int n = 0; n < 600; n++) client.SendReliable(client.Peers[0], BitConverter.GetBytes(n)); sent = true; }
                        if (sent && received.Count == 3 && new List<int>(received.Values).TrueForAll(n => n == 600)) break;
                    }
                    Assert(received.Count == 3 && new List<int>(received.Values).TrueForAll(n => n == 600), "Reliable transfer failed under 150ms latency and 5% loss");
                } finally { foreach (Transport client in clients) client.Close(); host.Close(); }
            });
            if (Array.IndexOf(args, "--no-sockets") < 0) Test("MWCoop transport connects a host and three actual UDP clients on localhost", delegate {
                int port; using (Socket reserve = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)) { reserve.Bind(new IPEndPoint(IPAddress.Loopback, 0)); port = ((IPEndPoint)reserve.LocalEndPoint).Port; }
                Transport host = Create(); List<Transport> clients = new List<Transport>(); int received = 0; host.OnMessage += delegate { received++; };
                try {
                    host.Host(port); for (int i = 0; i < 3; i++) { Transport client = Create(); clients.Add(client); client.Connect("127.0.0.1", port); }
                    bool sent = false;
                    for (int step = 0; step < 600; step++) { float time = step * .01f; host.Update(time); foreach (Transport client in clients) client.Update(time); if (!sent && clients.TrueForAll(c => c.LocalId > 0)) { foreach (Transport client in clients) client.SendReliable(client.Peers[0], new byte[] { 1, 2, 3 }); sent = true; } if (received == 3) break; Thread.Sleep(5); }
                    Assert(received == 3 && host.Peers.Count == 3, "Actual UDP transport failed");
                } finally { foreach (Transport client in clients) client.Close(); host.Close(); }
            });
            Console.WriteLine("ALL " + passed + " MSC ADAPTER TESTS PASSED"); return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL after " + passed + ": " + e); return 1; }
    }
}
