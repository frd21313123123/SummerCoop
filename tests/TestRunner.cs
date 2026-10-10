using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using SummerCoop.Core;

internal static class TestRunner
{
    private static int passed;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws(Action a) { bool thrown = false; try { a(); } catch (Exception) { thrown = true; } Check(thrown, "Expected rejection"); }
    private static void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    private sealed class Adapter : ICommandAdapter
    {
        public int Applied, RolledBack;
        public bool Fail;
        public string Validate(string player, Command c, World w)
        {
            if (c.Kind == CommandKind.Purchase && w.Money < 25) return "Insufficient funds";
            if (c.Kind == CommandKind.Bolt && (c.Number < 0 || c.Number > 8)) return "Invalid bolt stage";
            return null;
        }
        public void Apply(string player, Command c, World w)
        {
            Applied++;
            if (c.Kind == CommandKind.Purchase) w.Money -= 25;
            if (c.Kind == CommandKind.Bolt) w.Entities[c.Target].Properties["stage"] = Atom.N(c.Number);
            if (Fail) { w.Money = -100; throw new IOException("Injected failure"); }
        }
        public void Rollback(World before) { RolledBack++; }
    }
    private sealed class FailingLog : ICommitLog { public void Commit(World w, string p, Command c) { throw new IOException("Disk full"); } }
    private sealed class CountingLog : ICommitLog { public int Writes; public void Commit(World w, string p, Command c) { Writes++; } }
    private sealed class OptimizedAdapter : ICopyOnWriteCommandAdapter, ITransientCommandAdapter
    {
        public int Inputs, Rollbacks;
        public bool Fail;
        public string Validate(string player, Command c, World world) { return null; }
        public void Apply(string player, Command c, World world)
        {
            Entity next = world.Entities[c.Target].Clone(); next.Properties["stage"] = Atom.N(c.Number); world.Entities[c.Target] = next;
            if (Fail) throw new IOException("Injected native failure");
        }
        public void ApplyTransient(string player, Command c, World world) { Inputs++; }
        public void Rollback(World before) { Rollbacks++; }
    }
    private static World Sample()
    {
        World w = new World { Id = "test-world", GameHash = "game", SchemaHash = "schema", Money = 500 };
        Entity e = new Entity { Id = "part", Key = "garage/block", Kind = EntityKind.Rigidbody, Position = new V3(1, 0, 0) };
        e.Properties["stage"] = Atom.N(0); e.Properties["fitted"] = Atom.B(false); e.Properties["name"] = Atom.S("Блок двигателя"); e.Properties["socket"] = Atom.R("car"); e.Properties["v"] = Atom.V(new V3(1, 2, 3)); e.Properties["q"] = Atom.Q(Q4.Identity);
        w.Entities.Add(e.Id, e); w.Entities.Add("seat", new Entity { Id = "seat", Kind = EntityKind.Vehicle }); return w;
    }
    private static Authority Auth(Adapter a, ICommitLog log)
    {
        Authority h = new Authority(Sample(), a, log); h.AddPlayer("A", "Alice"); h.AddPlayer("B", "Bob"); return h;
    }
    private static Command Cmd(long sequence, CommandKind kind, string target) { return new Command { Sequence = sequence, Kind = kind, Target = target }; }
    private static void OptimizationTests()
    {
        Test("allocation-free floating point writes retain all IEEE bits and the original wire format", delegate
        {
            float[] singles = { 0, BitConverter.ToSingle(new byte[] { 0, 0, 0, 128 }, 0), float.Epsilon, -float.Epsilon, float.MinValue, float.MaxValue, float.PositiveInfinity, float.NegativeInfinity, float.NaN };
            double[] doubles = { 0, BitConverter.Int64BitsToDouble(long.MinValue), double.Epsilon, -double.Epsilon, double.MinValue, double.MaxValue, double.PositiveInfinity, double.NegativeInfinity, double.NaN };
            Random random = new Random(921); byte[] data = new byte[8];
            List<float> floats = new List<float>(singles); List<double> numbers = new List<double>(doubles);
            for (int i = 0; i < 1024; i++) { random.NextBytes(data); floats.Add(BitConverter.ToSingle(data, 0)); numbers.Add(BitConverter.ToDouble(data, 0)); }
            Action<BinaryWriter> encode = delegate(BinaryWriter writer) { foreach (float value in floats) writer.Write(value); foreach (double value in numbers) writer.Write(value); };
            byte[] expected; using (MemoryStream stream = new MemoryStream()) using (BinaryWriter writer = new BinaryWriter(stream)) { encode(writer); writer.Flush(); expected = stream.ToArray(); }
            byte[] actual = Codec.Make(encode); Check(Convert.ToBase64String(actual) == Convert.ToBase64String(expected), "Primitive writer changed float/double bit patterns");
        });
        Test("pooled encoding preserves nested output, error recovery and cross-thread ownership", delegate
        {
            byte[] nested = null;
            byte[] outer = Codec.Make(delegate(BinaryWriter writer) { writer.Write(123); nested = Codec.Make(delegate(BinaryWriter inner) { Codec.Text(inner, "внутренний"); }); writer.Write(nested.Length); writer.Write(nested); writer.Write(456); });
            Codec.Read(outer, delegate(BinaryReader reader) { Check(reader.ReadInt32() == 123, "Outer prefix overwritten"); int length = reader.ReadInt32(); Codec.Read(reader.ReadBytes(length), delegate(BinaryReader inner) { Check(Codec.Text(inner) == "внутренний", "Nested bytes overwritten"); }); Check(reader.ReadInt32() == 456, "Outer suffix overwritten"); });
            byte[] retained = (byte[])outer.Clone();
            Throws(delegate { Codec.Make(delegate(BinaryWriter writer) { writer.Write(999); throw new IOException("encode failed"); }); });
            byte[] clean = Codec.Make(delegate(BinaryWriter writer) { writer.Write(7); }); Check(clean.Length == 4 && BitConverter.ToInt32(clean, 0) == 7, "Failed encoding retained old bytes");
            Exception failure = null; System.Threading.Thread worker = new System.Threading.Thread(delegate() { try { for (int i = 0; i < 1000; i++) { int value = i; byte[] bytes = Codec.Make(delegate(BinaryWriter writer) { writer.Write(value); }); Check(BitConverter.ToInt32(bytes, 0) == value, "Worker bytes overwritten"); } } catch (Exception error) { failure = error; } }); worker.Start();
            for (int i = 0; i < 1000; i++) { byte[] bytes = Codec.Make(delegate(BinaryWriter writer) { writer.Write(-1); }); Check(BitConverter.ToInt32(bytes, 0) == -1, "Main bytes overwritten"); }
            worker.Join(); if (failure != null) throw failure;
            Check(Convert.ToBase64String(outer) == Convert.ToBase64String(retained), "Returned output shared mutable storage");
        });
        Test("copy-on-write native transaction rolls back entity replacements", delegate
        {
            OptimizedAdapter adapter = new OptimizedAdapter { Fail = true };
            Authority host = new Authority(Sample(), adapter, null); host.AddPlayer("A", "Alice");
            Entity original = host.World.Entities["part"];
            Command command = Cmd(1, CommandKind.Bolt, "part"); command.Number = 4;
            Check(!host.Execute("A", command).Accepted, "Failed native action was accepted");
            Check(host.World.Entities["part"].Properties["stage"].Number == 0 && object.ReferenceEquals(original, host.World.Entities["part"]), "Replacement leaked into rollback");
            Check(adapter.Rollbacks == 1, "Native rollback was skipped");
        });
        Test("driving input is ownership-checked without checkpoint writes", delegate
        {
            OptimizedAdapter adapter = new OptimizedAdapter(); CountingLog log = new CountingLog();
            Authority host = new Authority(Sample(), adapter, log); host.AddPlayer("A", "Alice"); host.AddPlayer("B", "Bob"); host.World.Players["A"].Seat = "seat";
            for (int i = 1; i <= 100; i++) Check(host.Execute("A", Cmd(i, CommandKind.Drive, "seat")).Accepted, "Driver input rejected");
            Check(!host.Execute("B", Cmd(1, CommandKind.Drive, "seat")).Accepted, "Passenger could control another player's seat");
            Check(adapter.Inputs == 100 && log.Writes == 0 && host.World.Revision == 0, "Input created a durable world transaction");
            host.Execute("A", Cmd(100, CommandKind.Drive, "seat")); Check(adapter.Inputs == 100, "Duplicate input was replayed");
        });
        Test("immutable checkpoint baseline survives replacements and deletions", delegate
        {
            string directory = Path.Combine(Path.GetTempPath(), "summercoop-immutable-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                World world = Sample(); CheckpointStore store = new CheckpointStore(directory) { ImmutableEntities = true }; store.Checkpoint(world);
                for (int i = 1; i <= 4; i++) { Entity next = world.Entities["part"].Clone(); next.Properties["stage"] = Atom.N(i); world.Entities["part"] = next; world.Revision++; store.Commit(world, "native", Cmd(i, CommandKind.Bolt, "part")); }
                world.Entities.Remove("seat"); world.Revision++; store.Commit(world, "native", Cmd(5, CommandKind.Interact, "part"));
                World recovered = new CheckpointStore(directory).Recover();
                Check(recovered.Revision == 5 && recovered.Entities["part"].Properties["stage"].Number == 4 && !recovered.Entities.ContainsKey("seat"), "Incremental baseline lost a committed change");
            }
            finally { Directory.Delete(directory, true); }
        });
    }
    private static void ProtocolTests()
    {
        Test("canonical snapshot / all value types / Cyrillic", delegate
        {
            World w = Sample(); byte[] a = Codec.World(w), b = Codec.World(Codec.World(a));
            Check(Codec.Hash(a) == Codec.Hash(b), "Roundtrip changed world");
            Check(Codec.World(a).Entities["part"].Properties["name"].Text == "Блок двигателя", "UTF-8 failed");
        });
        Test("sleep completion survives snapshots and incremental updates", delegate
        {
            World a = Sample(), b = a.Clone(); b.SleepEpoch = 1; b.Revision++;
            CheckpointStore.ApplyDelta(a, Codec.World(Codec.World(CheckpointStore.Difference(a, b))));
            Check(a.SleepEpoch == 1 && Codec.World(Codec.World(a)).SleepEpoch == 1, "Sleep completion lost during replication");
        });
        Test("packet checksum, magic, version, truncation, trailing bytes", delegate
        {
            byte[] bytes = Codec.Packet(MessageKind.Command, Codec.Command(Cmd(1, CommandKind.Take, "part")));
            Check(Codec.Open(bytes).Kind == MessageKind.Command, "Wrong packet");
            byte[] modified = (byte[])bytes.Clone(); modified[modified.Length - 1] ^= 1; Throws(delegate { Codec.Open(modified); });
            modified = (byte[])bytes.Clone(); modified[0] = 0; Throws(delegate { Codec.Open(modified); });
            modified = (byte[])bytes.Clone(); modified[4] = 9; Throws(delegate { Codec.Open(modified); });
            for (int i = 0; i < bytes.Length; i++) { byte[] shortBytes = new byte[i]; Array.Copy(bytes, shortBytes, i); Throws(delegate { Codec.Open(shortBytes); }); }
            byte[] trailing = new byte[bytes.Length + 1]; Array.Copy(bytes, trailing, bytes.Length); Throws(delegate { Codec.Open(trailing); });
        });
        Test("NaN, infinity, invalid enum, excessive strings are rejected", delegate
        {
            Command c = Cmd(1, CommandKind.Bolt, "part"); c.Number = double.NaN; Throws(delegate { Codec.Command(Codec.Command(c)); });
            c.Number = double.PositiveInfinity; Throws(delegate { Codec.Command(Codec.Command(c)); });
            c.Number = 0; c.Kind = (CommandKind)255; Throws(delegate { Codec.Command(Codec.Command(c)); });
            c.Kind = CommandKind.Bolt; c.Argument = new string('x', Protocol.MaxString + 1); Throws(delegate { Codec.Command(c); });
        });
        Test("snapshot chunk ordering, hash, incomplete transfer and restart", delegate
        {
            byte[] bytes = Codec.World(Sample()); SnapshotReceiver r = new SnapshotReceiver();
            r.Begin(bytes.Length, Codec.Hash(bytes)); Throws(delegate { r.Chunk(1, bytes); });
            r.Begin(bytes.Length, Codec.Hash(bytes)); r.Chunk(0, bytes); Check(Codec.Hash(r.End()) == Codec.Hash(bytes), "Wrong snapshot");
            r.Begin(bytes.Length, new string('0', 64)); r.Chunk(0, bytes); Throws(delegate { r.End(); });
            r.Begin(bytes.Length, Codec.Hash(bytes)); Throws(delegate { r.End(); });
            Throws(delegate { r.Begin(Protocol.MaxSnapshot + 1, new string('a', 64)); });
        });
        Test("identity survives culture changes", delegate
        {
            string a = Identity.Stable("SATSUMA/engine/I");
            System.Globalization.CultureInfo old = Thread.CurrentThread.CurrentCulture;
            try { Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("tr-TR"); Check(a == Identity.Stable("SATSUMA/engine/I"), "Culture-dependent IDs"); }
            finally { Thread.CurrentThread.CurrentCulture = old; }
        });
    }
    private static void AuthorityTests()
    {
        Test("simultaneous take has exactly one owner", delegate
        {
            Authority h = Auth(null, null);
            Check(h.Execute("A", Cmd(1, CommandKind.Take, "part")).Accepted, "First take rejected");
            Check(!h.Execute("B", Cmd(1, CommandKind.Take, "part")).Accepted, "Two owners");
            Check(!h.Execute("B", Cmd(2, CommandKind.Drop, "part")).Accepted, "Foreign drop accepted");
            Check(h.Execute("A", Cmd(2, CommandKind.Drop, "part")).Accepted, "Drop rejected");
            Check(h.Execute("B", Cmd(3, CommandKind.Take, "part")).Accepted, "Transfer rejected");
        });
        Test("seat claim, unowned driving and disconnect cleanup", delegate
        {
            Adapter a = new Adapter(); Authority h = Auth(a, null);
            Check(!h.Execute("A", Cmd(1, CommandKind.Drive, "seat")).Accepted, "Driving without seat");
            Check(h.Execute("A", Cmd(2, CommandKind.EnterSeat, "seat")).Accepted, "Seat rejected");
            Check(!h.Execute("B", Cmd(1, CommandKind.EnterSeat, "seat")).Accepted, "Double driver");
            h.RemovePlayer("A"); Check(h.Execute("B", Cmd(2, CommandKind.EnterSeat, "seat")).Accepted, "Seat not released");
        });
        Test("purchase retry charges once / stale retries after eviction", delegate
        {
            Adapter a = new Adapter(); Authority h = Auth(a, null); Command c = Cmd(1, CommandKind.Purchase, "part");
            CommandResult first = h.Execute("A", c), second = h.Execute("A", c);
            Check(first.Accepted && second.Accepted && first.Revision == second.Revision && h.World.Money == 475 && a.Applied == 1, "Duplicate transaction");
            for (int i = 2; i < 300; i++) h.Execute("A", Cmd(i, CommandKind.Sleep, ""));
            Check(!h.Execute("A", c).Accepted && h.World.Money == 475, "Evicted retry executed");
        });
        Test("distance and unknown targets / unknown players", delegate
        {
            Authority h = Auth(null, null); h.World.Entities["part"].Position = new V3(9, 0, 0);
            Check(!h.Execute("A", Cmd(1, CommandKind.Take, "part")).Accepted, "Remote take");
            Check(!h.Execute("A", Cmd(2, CommandKind.Take, "missing")).Accepted, "Unknown target");
            Check(!h.Execute("unknown", Cmd(1, CommandKind.Take, "part")).Accepted, "Unknown player");
        });
        Test("failed adapter and failed durable commit roll back", delegate
        {
            Adapter a = new Adapter { Fail = true }; Authority h = Auth(a, null);
            Check(!h.Execute("A", Cmd(1, CommandKind.Purchase, "part")).Accepted && h.World.Money == 500 && h.World.Revision == 0 && a.RolledBack == 1, "Adapter rollback failed");
            a = new Adapter(); h = Auth(a, new FailingLog());
            Check(!h.Execute("A", Cmd(1, CommandKind.Take, "part")).Accepted && h.World.Players["A"].Held == "" && a.RolledBack == 1, "Disk failure rollback failed");
        });
        Test("sleep requires all connected players", delegate
        {
            Authority h = Auth(null, null); Check(!h.World.AllSleeping(), "Initially sleeping");
            h.Execute("A", new Command { Sequence = 1, Kind = CommandKind.Sleep, Number = 1 }); Check(!h.World.AllSleeping(), "One player can skip time");
            h.Execute("B", new Command { Sequence = 1, Kind = CommandKind.Sleep, Number = 1 }); Check(h.World.AllSleeping(), "Both ready not recognized");
        });
        Test("four-player limit and invalid/out-of-order poses", delegate
        {
            Authority h = Auth(null, null); h.AddPlayer("C", "C"); h.AddPlayer("D", "D"); Throws(delegate { h.AddPlayer("E", "E"); });
            Check(h.SetPose("A", new Pose { Sequence = 2, Position = new V3(1, 2, 3) }), "Valid pose rejected");
            Check(!h.SetPose("A", new Pose { Sequence = 1, Position = new V3(4, 5, 6) }), "Old pose applied");
            Check(!h.SetPose("A", new Pose { Sequence = 3, Position = new V3(float.NaN, 0, 0) }), "NaN pose accepted");
        });
    }
    private static void PersistenceTests()
    {
        Test("buffered UTF8 decoding remains exact through hash collisions and malformed input", delegate
        {
            string[] values = { "costarring", "liquid", "costarring", "Привет мир", "", new string('x', 512), "liquid" };
            byte[] encoded = Codec.Make(delegate(BinaryWriter writer) { foreach (string value in values) Codec.Text(writer, value); });
            Codec.Read(encoded, delegate(BinaryReader reader) { foreach (string value in values) Check(Codec.Text(reader) == value, "Text cache returned another value"); });
            byte[] invalid = Codec.Make(delegate(BinaryWriter writer) { writer.Write(2); writer.Write(new byte[] { 0xc3, 0x28 }); });
            Throws(delegate { Codec.Read(invalid, delegate(BinaryReader reader) { Codec.Text(reader); }); });
            byte[] shortText = Codec.Make(delegate(BinaryWriter writer) { writer.Write(3); writer.Write(new byte[] { 0x41 }); });
            Throws(delegate { Codec.Read(shortText, delegate(BinaryReader reader) { Codec.Text(reader); }); });
        });
        Test("background and main thread UTF8 buffers do not share mutable storage", delegate
        {
            Exception failure = null;
            Thread worker = new Thread(delegate()
            {
                try { for (int i = 0; i < 500; i++) { string expected = "background-" + i + "-ёж"; byte[] bytes = Codec.Make(delegate(BinaryWriter writer) { Codec.Text(writer, expected); }); Codec.Read(bytes, delegate(BinaryReader reader) { Check(Codec.Text(reader) == expected, "Background buffer changed"); }); } }
                catch (Exception e) { failure = e; }
            });
            worker.Start();
            for (int i = 0; i < 500; i++) { string expected = "main-" + i + "-машина"; byte[] bytes = Codec.Make(delegate(BinaryWriter writer) { Codec.Text(writer, expected); }); Codec.Read(bytes, delegate(BinaryReader reader) { Check(Codec.Text(reader) == expected, "Main buffer changed"); }); }
            worker.Join(); if (failure != null) throw failure;
        });
        Test("tracked deltas equal full comparison through coalescing, deletions and history eviction", delegate
        {
            World world = Sample(); world.EnableChangeTracking(); world.Changes = new EntityChanges(8);
            World baseline = world.ReferenceSnapshot(); Random random = new Random(516750);
            for (int step = 0; step < 200; step++)
            {
                string id = "tracked-" + random.Next(20); world.Revision++;
                if (step % 5 == 0) world.Entities.Remove(id);
                else { Entity e = new Entity { Id = id, Key = id }; e.Properties["stage"] = Atom.N(step); world.Entities[id] = e; }
                world.EntityChanged(id);
                if (step % 7 == 0)
                {
                    World expected = CheckpointStore.Difference(baseline.Clone(), world, false);
                    World actual = CheckpointStore.Difference(baseline, world, true);
                    Check(Codec.Hash(Codec.World(expected)) == Codec.Hash(Codec.World(actual)), "Tracked delta omitted a change at step " + step);
                    CheckpointStore.ApplyDelta(baseline, actual, true);
                    Check(Codec.Hash(Codec.World(baseline)) == Codec.Hash(Codec.World(world)), "Tracked baseline diverged");
                }
            }
            Check(world.Changes.PrunedThrough > 0, "History eviction was not exercised");
        });
        Test("one changed entity in a 40000-object world keeps unchanged snapshot references", delegate
        {
            World world = Sample(); for (int i = 0; i < 40000; i++) { string id = "object-" + i; world.Entities[id] = new Entity { Id = id, Key = id }; }
            world.EnableChangeTracking(); World baseline = world.ReferenceSnapshot(); SnapshotBuilder builder = new SnapshotBuilder();
            Entity probe = builder.Begin("object-123", "object-123", EntityKind.Object); probe.Properties["stage"] = builder.N(8); builder.Publish(world); world.Revision++;
            World delta = CheckpointStore.Difference(baseline, world, true);
            Check(delta.Entities.Count == 1 && delta.Entities.ContainsKey("object-123"), "Sparse delta included unrelated objects");
            CheckpointStore.ApplyDelta(baseline, delta, true);
            Check(object.ReferenceEquals(baseline.Entities["object-123"], world.Entities["object-123"]), "Baseline cloned the immutable replacement");
            Check(CheckpointStore.Difference(baseline, world, true).Entities.Count == 0, "Unchanged baseline published again");
        });
        Test("reused capture probes preserve published values and removed properties", delegate
        {
            World world = Sample(); SnapshotBuilder builder = new SnapshotBuilder();
            Entity probe = builder.Begin("captured", "native", EntityKind.Object);
            probe.Properties["stage"] = builder.N(1); probe.Properties["color"] = builder.Q(new Q4(1, 2, 3, 4)); probe.Properties["name"] = builder.S("part");
            Check(builder.Publish(world), "Initial capture missing"); Entity first = world.Entities["captured"];
            probe = builder.Begin("captured", "native", EntityKind.Object);
            probe.Properties["stage"] = builder.N(1); probe.Properties["color"] = builder.Q(new Q4(1, 2, 3, 4)); probe.Properties["name"] = builder.S("part");
            Check(!builder.Publish(world) && object.ReferenceEquals(first, world.Entities["captured"]), "Unchanged capture allocated a snapshot");
            probe = builder.Begin("captured", "native", EntityKind.Object); probe.Properties["stage"] = builder.N(2); probe.Properties["name"] = builder.S("part");
            Check(builder.Publish(world), "Changed capture missing"); Entity second = world.Entities["captured"];
            Check(first.Properties["stage"].Number == 1 && first.Properties["color"].Rotation.W == 4 && !second.Properties.ContainsKey("color"), "Probe corrupted history or retained removed values");
            Check(object.ReferenceEquals(first.Properties["name"], second.Properties["name"]), "Unchanged atom not shared");
            probe = builder.Begin("another", "native2", EntityKind.Fsm); probe.Properties["stage"] = builder.S("different"); builder.Publish(world);
            Check(first.Properties["stage"].Number == 1 && second.Properties["stage"].Number == 2, "Pool mutation escaped into published values");
        });
        Test("capture size buckets keep large arrays and small objects isolated", delegate
        {
            World world = Sample(); SnapshotBuilder builder = new SnapshotBuilder();
            Entity large = builder.Begin("large", "native-array", EntityKind.Object, 2048);
            Dictionary<string, Atom> largeBuffer = large.Properties;
            for (int i = 0; i < 2048; i++) large.Properties["array/" + i] = builder.N(i);
            builder.Publish(world); Entity published = world.Entities["large"];
            Entity small = builder.Begin("small", "native-small", EntityKind.Object, 1); Dictionary<string, Atom> smallBuffer = small.Properties;
            Check(!object.ReferenceEquals(largeBuffer, smallBuffer) && small.Properties.Count == 0, "Small capture retained large scratch storage");
            small.Properties["value"] = builder.N(7); builder.Publish(world);
            large = builder.Begin("large", "native-array", EntityKind.Object, 2048);
            Check(object.ReferenceEquals(largeBuffer, large.Properties) && large.Properties.Count == 0, "Large scratch storage was not reused or cleared");
            large.Properties["array/0"] = builder.N(99); builder.Publish(world);
            Check(published.Properties.Count == 2048 && published.Properties["array/0"].Number == 0 && world.Entities["small"].Properties["value"].Number == 7, "Bucket reuse modified published entities");
            small = builder.Begin("grown", "unknown-size", EntityKind.Object, 1);
            for (int i = 0; i < 500; i++) small.Properties["key" + i] = builder.B(true);
            builder.Publish(world);
            small = builder.Begin("small", "native-small", EntityKind.Object, 1);
            Check(!object.ReferenceEquals(smallBuffer, small.Properties), "Unknown large entity polluted the small scratch bucket");
        });
        Test("shared captured constants preserve exact wire values and deep clone isolation", delegate
        {
            World world = Sample(); SnapshotBuilder builder = new SnapshotBuilder();
            for (int index = 0; index < 2; index++)
            {
                Entity probe = builder.Begin("constant" + index, "native" + index, EntityKind.Object);
                probe.Properties["bool"] = builder.B(true); probe.Properties["n"] = builder.N(7); probe.Properties["text"] = builder.S("PART"); probe.Properties["ref"] = builder.R(""); probe.Properties["scale"] = builder.V(new V3(1, 1, 1));
                probe.Properties["precise"] = builder.N(0.123456789123); builder.Publish(world);
            }
            Entity first = world.Entities["constant0"], second = world.Entities["constant1"];
            foreach (string key in new string[] { "bool", "n", "text", "ref", "scale" }) Check(object.ReferenceEquals(first.Properties[key], second.Properties[key]), "Constant was duplicated: " + key);
            byte[] bytes = Codec.World(world); World decoded = Codec.World(bytes);
            Check(CheckpointStore.Equal(first, decoded.Entities[first.Id]) && first.Properties["precise"].Number == 0.123456789123, "Shared capture changed exact values");
            World copy = world.Clone(); copy.Entities[first.Id].Properties["n"].Number = 8;
            Check(first.Properties["n"].Number == 7 && second.Properties["n"].Number == 7, "Mutable deep clone modified shared constants");
            Entity changed = builder.Begin(first.Id, first.Key, first.Kind); changed.Properties["bool"] = builder.B(false); builder.Publish(world);
            Check(first.Properties["bool"].Number == 1 && second.Properties["bool"].Number == 1 && world.Entities[first.Id].Properties["bool"].Number == 0, "Scratch capture modified published constants");
        });
        string testRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".local"));
        string path = Path.GetFullPath(Path.Combine(testRoot, "tests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(path);
        try
        {
            Test("background native journal drains before a durable command and clean checkpoint", delegate
            {
                string queuedPath = Path.Combine(path, "queued");
                using (CheckpointStore store = new CheckpointStore(queuedPath) { ImmutableEntities = true })
                {
                    World world = Sample(); world.EnableChangeTracking(); store.Checkpoint(world);
                    for (int stage = 1; stage <= 4; stage++)
                    {
                        Entity next = world.Entities["part"].Clone(); next.Properties["stage"] = Atom.N(stage); world.Entities["part"] = next; world.EntityChanged("part"); world.Revision++;
                        store.CommitNative(world); store.FlushNative();
                    }
                    world.Money = 456; world.Revision++; store.Commit(world, "A", Cmd(1, CommandKind.Purchase, "part"));
                    World recovered = new CheckpointStore(queuedPath).Recover();
                    Check(recovered.Revision == 5 && recovered.Money == 456 && recovered.Entities["part"].Properties["stage"].Number == 4, "Durable command overtook native records");
                    store.BeginCheckpoint(world); world.Money = 432; world.Revision++; store.CommitNative(world); store.Checkpoint(world);
                    Check(new CheckpointStore(queuedPath).Recover().Money == 432 && new FileInfo(Path.Combine(queuedPath, "world.scj")).Length == 0, "Clean checkpoint lost pending native state");
                }
            });
            Test("background journal disk errors are surfaced before further durable commands", delegate
            {
                string failedPath = Path.Combine(path, "failed-background");
                CheckpointStore store = new CheckpointStore(failedPath) { ImmutableEntities = true }; World world = Sample(); store.Checkpoint(world);
                File.Delete(Path.Combine(failedPath, "world.scj")); Directory.CreateDirectory(Path.Combine(failedPath, "world.scj"));
                world.Revision++; store.CommitNative(world); Throws(delegate { store.FlushNative(); });
                Throws(delegate { store.Commit(world, "A", Cmd(1, CommandKind.Purchase, "part")); });
                Throws(delegate { store.Dispose(); });
            });
            Test("background checkpoint retains later durable commands and immutable snapshot", delegate
            {
                string backgroundPath = Path.Combine(path, "background");
                CheckpointStore store = new CheckpointStore(backgroundPath) { ImmutableEntities = true };
                World world = Sample(); store.Checkpoint(world);
                world.Revision = 1; Entity captured = world.Entities["part"].Clone(); captured.Properties["stage"] = Atom.N(2); world.Entities["part"] = captured;
                store.Commit(world, "A", Cmd(1, CommandKind.Bolt, "part"));
                Check(store.BeginCheckpoint(world), "Checkpoint did not start");
                Entity later = captured.Clone(); later.Properties["stage"] = Atom.N(3); world.Entities["part"] = later; world.Revision = 2; world.Money = 321;
                store.Commit(world, "A", Cmd(2, CommandKind.Purchase, "part")); store.FinishBackgroundCheckpoint();
                using (FileStream file = File.OpenRead(Path.Combine(backgroundPath, "world.scw"))) using (BinaryReader reader = new BinaryReader(file))
                {
                    int length = reader.ReadInt32(); uint crc = reader.ReadUInt32(); byte[] bytes = reader.ReadBytes(length);
                    World snapshot = Codec.World(bytes);
                    Check(file.Position == file.Length && Codec.Crc(bytes) == crc && snapshot.Revision == 1 && snapshot.Entities["part"].Properties["stage"].Number == 2, "Streaming checkpoint differs from captured state or has a bad checksum");
                }
                World recovered = new CheckpointStore(backgroundPath).Recover();
                Check(recovered.Revision == 2 && recovered.Money == 321 && recovered.Entities["part"].Properties["stage"].Number == 3, "Concurrent checkpoint lost later journal entries");
                store.Checkpoint(world); Check(new FileInfo(Path.Combine(backgroundPath, "world.scj")).Length == 0, "Clean checkpoint did not compact journal");
            });
            Test("journal restores committed state after incomplete append", delegate
            {
                CheckpointStore store = new CheckpointStore(path); Authority h = Auth(new Adapter(), store); store.Checkpoint(h.World);
                h.Execute("A", new Command { Sequence = 1, Kind = CommandKind.Bolt, Target = "part", Number = 6 });
                h.Execute("B", Cmd(1, CommandKind.Purchase, "part"));
                using (FileStream s = new FileStream(Path.Combine(path, "world.scj"), FileMode.Append)) s.Write(new byte[] { 255, 7, 12 }, 0, 3);
                World loaded = new CheckpointStore(path).Recover(); Check(loaded.Money == 475 && loaded.Entities["part"].Properties["stage"].Number == 6 && loaded.Revision == 2, "Committed state lost");
            });
            Test("new commits after recovery replace an interrupted journal tail", delegate
            {
                CheckpointStore store = new CheckpointStore(path); World loaded = store.Recover();
                loaded.Money = 321; loaded.Revision++; store.Commit(loaded, "A", Cmd(3, CommandKind.Purchase, "part"));
                Check(new CheckpointStore(path).Recover().Money == 321, "New commit hidden behind corrupt tail");
            });
            Test("corrupted checkpoint falls back to backup", delegate
            {
                CheckpointStore store = new CheckpointStore(path); World w = Sample(); store.Checkpoint(w); w.Money = 123; w.Revision = 1; store.Checkpoint(w);
                File.WriteAllBytes(Path.Combine(path, "world.scw"), new byte[] { 1, 2, 3 });
                Check(new CheckpointStore(path).Recover().Money == 500, "Backup unavailable");
            });
            Test("delta removes destroyed objects and disconnected players", delegate
            {
                World a = Sample(); a.Players.Add("A", new Player { Id = "A" }); World b = a.Clone(); b.Entities.Remove("part"); b.Players.Clear(); b.Revision++;
                CheckpointStore.ApplyDelta(a, Codec.World(Codec.World(CheckpointStore.Difference(a, b)))); Check(!a.Entities.ContainsKey("part") && a.Players.Count == 0, "Tombstone not applied");
            });
        }
        finally { Check(path.StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Unsafe cleanup target"); Directory.Delete(path, true); }
    }
    private static void TransportTests()
    {
        Test("actual loopback TCP/UDP with host plus three clients", delegate
        {
            int port = 30000 + (Environment.TickCount & 8191);
            using (LanTransport host = LanTransport.Host(port))
            {
                List<LanTransport> clients = new List<LanTransport>();
                try
                {
                    for (int i = 0; i < 3; i++) clients.Add(LanTransport.Join("127.0.0.1", port));
                    int reliable = 0, datagrams = 0, replies = 0;
                    HashSet<string> connected = new HashSet<string>(); Stopwatch clock = Stopwatch.StartNew();
                    while (clock.ElapsedMilliseconds < 10000 && (reliable < 3 || datagrams < 3 || replies < 3))
                    {
                        TransportEvent e;
                        for (int i = 0; i < clients.Count; i++)
                        {
                            while (clients[i].Poll(out e))
                            {
                                if (e.Kind == TransportEventKind.Error) throw new Exception(e.Error);
                                if (e.Kind == TransportEventKind.Connected)
                                {
                                    clients[i].Send("host", Codec.Packet(MessageKind.Command, Codec.Command(Cmd(i + 1, CommandKind.Take, "part"))), true);
                                    clients[i].Send("host", Codec.Packet(MessageKind.Pose, Codec.Pose(new Pose { Id = "client" + i, Sequence = 1 })), false);
                                }
                                if (e.Kind == TransportEventKind.Data) { Check(Codec.Open(e.Data).Kind == MessageKind.Ping, "Wrong reply"); replies++; }
                            }
                        }
                        while (host.Poll(out e))
                        {
                            if (e.Kind == TransportEventKind.Connected) connected.Add(e.Peer);
                            if (e.Kind == TransportEventKind.Error) throw new Exception(e.Error);
                            if (e.Kind == TransportEventKind.Data)
                            {
                                Packet p = Codec.Open(e.Data);
                                if (p.Kind == MessageKind.Command) { reliable++; Check(e.Reliable, "Command sent over UDP"); host.Send(e.Peer, Codec.Packet(MessageKind.Ping, new byte[0]), true); }
                                if (p.Kind == MessageKind.Pose) datagrams++;
                            }
                        }
                        Thread.Sleep(2);
                    }
                    Check(connected.Count == 3 && reliable == 3 && datagrams == 3 && replies == 3, "Loopback incomplete: " + reliable + "/" + datagrams + "/" + replies);
                    clients[0].Dispose(); clock.Restart(); bool removed = false;
                    while (clock.ElapsedMilliseconds < 3000 && !removed) { TransportEvent e; while (host.Poll(out e)) if (e.Kind == TransportEventKind.Disconnected) removed = true; Thread.Sleep(2); }
                    Check(removed && host.Peers.Count == 2, "Disconnect not detected");
                }
                finally { foreach (LanTransport c in clients) c.Dispose(); }
            }
        });
        Test("seeded four-player replay / duplicates / UDP loss and reordering", delegate
        {
            Random random = new Random(516750); Adapter a = new Adapter(); Authority h = Auth(a, null); h.AddPlayer("C", "C"); h.AddPlayer("D", "D");
            World[] replicas = new World[3]; for (int i = 0; i < replicas.Length; i++) replicas[i] = Codec.World(Codec.World(h.World));
            long[] sequence = new long[4]; string[] names = { "A", "B", "C", "D" };
            int lost = 0;
            for (int i = 0; i < 1000; i++)
            {
                int actor = random.Next(4); Command c = new Command { Sequence = ++sequence[actor], Kind = CommandKind.Bolt, Target = "part", Number = random.Next(9) };
                CommandResult r = h.Execute(names[actor], Codec.Command(Codec.Command(c)));
                Check(r.Accepted, "Valid bolt rejected");
                if (random.Next(4) == 0) Check(h.Execute(names[actor], c).Revision == r.Revision, "Duplicate advanced revision");
                for (int j = 0; j < 3; j++) { World delta = CheckpointStore.Difference(replicas[j], h.World); CheckpointStore.ApplyDelta(replicas[j], Codec.World(Codec.World(delta))); }
                if (random.Next(100) < 5) lost++; else h.SetPose(names[actor], new Pose { Sequence = i + 2, Position = new V3(0, 0, 0) });
                h.SetPose(names[actor], new Pose { Sequence = 1, Position = new V3(100, 0, 0) });
            }
            foreach (World replica in replicas) { replica.Players.Clear(); foreach (Player p in h.World.Players.Values) replica.Players[p.Id] = p.Clone(); Check(Codec.Hash(Codec.World(replica)) == Codec.Hash(Codec.World(h.World)), "State diverged"); }
            Check(lost > 0 && a.Applied == 1000, "Loss/duplicate test did not exercise conditions");
        });
    }
    public static int Main()
    {
        try { ProtocolTests(); AuthorityTests(); OptimizationTests(); PersistenceTests(); TransportTests(); SessionTests.Run(Test); Console.WriteLine("ALL " + passed + " TESTS PASSED"); return 0; }
        catch (Exception e) { Console.Error.WriteLine("FAIL after " + passed + " tests: " + e); return 1; }
    }
}
