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
        Test("immutable decoding shares bounded constants, preserves IEEE bits and isolates mutable callers", delegate
        {
            World source = Sample(); Entity part = source.Entities["part"];
            double minusZero = BitConverter.Int64BitsToDouble(long.MinValue); float negativeZero = BitConverter.ToSingle(new byte[] { 0, 0, 0, 128 }, 0);
            for (int i = -16; i <= 256; i++) { part.Properties["n/" + i] = Atom.N(i); part.Properties["copy/" + i] = Atom.N(i); }
            part.Properties["negative-zero"] = Atom.N(minusZero); part.Properties["bool-negative-zero"] = new Atom { Kind = AtomKind.Boolean, Number = minusZero };
            part.Properties["unit"] = Atom.V(new V3(1, 1, 1));
            for (int signs = 0; signs < 8; signs++)
            {
                V3 zero = new V3((signs & 1) == 0 ? 0 : negativeZero, (signs & 2) == 0 ? 0 : negativeZero, (signs & 4) == 0 ? 0 : negativeZero);
                part.Properties["v/" + signs] = Atom.V(zero); part.Properties["q/" + signs] = Atom.Q(new Q4(zero.X, zero.Y, zero.Z, 1));
            }
            byte[] bytes = Codec.World(source); World first = Codec.ImmutableWorld(bytes), second = Codec.ImmutableWorld(bytes);
            Check(Codec.Hash(bytes) == Codec.Hash(Codec.World(first)), "Cached constants changed exact wire bits");
            Check(object.ReferenceEquals(first.Entities["part"].Properties["n/0"], first.Entities["part"].Properties["copy/0"]), "Common constants were allocated twice");
            foreach (string key in part.Properties.Keys) if (key != "name" && key != "socket" && key != "v")
                Check(object.ReferenceEquals(first.Entities["part"].Properties[key], second.Entities["part"].Properties[key]), "Constant cache missed " + key);
            World mutable = Codec.World(bytes), another = Codec.World(bytes), clone = first.Clone();
            mutable.Entities["part"].Properties["n/0"].Number = 123; clone.Entities["part"].Properties["n/0"].Number = 456;
            Check(another.Entities["part"].Properties["n/0"].Number == 0 && first.Entities["part"].Properties["n/0"].Number == 0 && mutable.Entities["part"].Properties["copy/0"].Number == 0, "Mutable decode or clone changed a shared constant");
            World otherThread = null; Exception failure = null; Thread thread = new Thread(delegate() { try { otherThread = Codec.ImmutableWorld(bytes); } catch (Exception e) { failure = e; } }); thread.Start(); thread.Join();
            if (failure != null) throw failure;
            Check(!object.ReferenceEquals(first.Entities["part"].Properties["n/0"], otherThread.Entities["part"].Properties["n/0"]), "Threads shared mutable constant storage");
            Check(Codec.Hash(bytes) == Codec.Hash(Codec.World(otherThread)), "Other-thread decode changed exact values");
        });
        Test("streamed journal records preserve canonical bytes, headers and append boundaries", delegate
        {
            World world = Sample(); Entity entity = world.Entities["part"];
            for (int i = 0; i < 128; i++) entity.Properties["mesh/" + i] = Atom.S(new string((char)('a' + i % 26), 12000));
            byte[] expected = Codec.World(world);
            using (MemoryStream stream = new MemoryStream())
            {
                stream.WriteByte(77); AtomicFile.WriteWorldRecord(stream, world); long firstEnd = stream.Position;
                world.Revision++; world.Money = 456; AtomicFile.WriteWorldRecord(stream, world);
                long end = stream.Position; stream.Position = 0;
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    Check(reader.ReadByte() == 77, "Streamed header overwrote existing records");
                    int length = reader.ReadInt32(); uint crc = reader.ReadUInt32(); byte[] bytes = reader.ReadBytes(length);
                    Check(length == expected.Length && crc == Codec.Crc(expected) && Convert.ToBase64String(bytes) == Convert.ToBase64String(expected) && stream.Position == firstEnd, "First streamed record differs from the previous format");
                    length = reader.ReadInt32(); crc = reader.ReadUInt32(); bytes = reader.ReadBytes(length);
                    Check(Codec.Crc(bytes) == crc && Codec.World(bytes).Money == 456 && stream.Position == end, "Second record has a bad boundary or checksum");
                }
            }
        });
        Test("moving captures share immutable properties without changing old snapshots", delegate
        {
            World world = Sample(); world.EnableChangeTracking(); SnapshotBuilder builder = new SnapshotBuilder();
            Entity probe = builder.Begin("moving", "native-moving", EntityKind.Rigidbody);
            probe.Properties["stage"] = builder.N(1); probe.Properties["name"] = builder.S("starter"); builder.Publish(world);
            Entity first = world.Entities["moving"]; World baseline = world.ReferenceSnapshot();
            probe = builder.Begin("moving", "native-moving", EntityKind.Rigidbody); probe.Position = new V3(0.1234567f, 2, 3); probe.Velocity = new V3(4, 5, 6);
            probe.Properties["name"] = builder.S("starter"); probe.Properties["stage"] = builder.N(1); builder.Publish(world); world.Revision++;
            Entity moved = world.Entities["moving"];
            Check(object.ReferenceEquals(first.Properties, moved.Properties), "Movement copied unchanged property storage");
            Check(first.Position.X == 0 && moved.Position.X == 0.1234567f && moved.Velocity.Z == 6, "Movement changed history or precision");
            World delta = Codec.World(Codec.World(CheckpointStore.Difference(baseline, world, true)));
            Check(delta.Entities.Count == 1 && CheckpointStore.Equal(delta.Entities["moving"], moved), "Shared properties changed delta bytes");
            probe = builder.Begin("moving", "native-moving", EntityKind.Rigidbody); probe.Properties["stage"] = builder.N(2); builder.Publish(world);
            Check(!object.ReferenceEquals(moved.Properties, world.Entities["moving"].Properties) && first.Properties["stage"].Number == 1 && first.Properties.ContainsKey("name"), "Changed properties mutated shared history");
            World copy = baseline.Clone(); copy.Entities["moving"].Properties["stage"].Number = 9;
            Check(first.Properties["stage"].Number == 1 && moved.Properties["stage"].Number == 1, "Deep clone exposed shared mutable properties");
        });
        Test("large text streams retain exact values through bounded cache replacement", delegate
        {
            byte[] bytes = Codec.Make(delegate(BinaryWriter writer)
            {
                for (int i = 0; i < 24000; i++) { Codec.Text(writer, "scene/объект/" + i); Codec.Text(writer, "@body"); Codec.Text(writer, i % 2 == 0 ? "costarring" : "liquid"); }
            });
            Codec.Read(bytes, delegate(BinaryReader reader)
            {
                for (int i = 0; i < 24000; i++)
                {
                    Check(Codec.Text(reader) == "scene/объект/" + i, "High-cardinality text aliased another value");
                    string hot = Codec.Text(reader); Check(hot == "@body", "Recurring property name was corrupted");
                    Check(Codec.Text(reader) == (i % 2 == 0 ? "costarring" : "liquid"), "Collision returned another string");
                }
            });
            Array cache = (Array)typeof(Codec).GetField("readTextCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
            Check(cache.Length == 8192, "Text cache exceeded its fixed storage bound");
        });
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
            string testRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".local"));
            string directory = Path.GetFullPath(Path.Combine(testRoot, "summercoop-immutable-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(directory);
            try
            {
                World world = Sample(); CheckpointStore store = new CheckpointStore(directory) { ImmutableEntities = true }; store.Checkpoint(world);
                for (int i = 1; i <= 4; i++) { Entity next = world.Entities["part"].Clone(); next.Properties["stage"] = Atom.N(i); world.Entities["part"] = next; world.Revision++; store.Commit(world, "native", Cmd(i, CommandKind.Bolt, "part")); }
                world.Entities.Remove("seat"); world.Revision++; store.Commit(world, "native", Cmd(5, CommandKind.Interact, "part"));
                World recovered = new CheckpointStore(directory).Recover();
                Check(recovered.Revision == 5 && recovered.Entities["part"].Properties["stage"].Number == 4 && !recovered.Entities.ContainsKey("seat"), "Incremental baseline lost a committed change");
            }
            finally { Check(directory.StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Unsafe cleanup target"); Directory.Delete(directory, true); }
        });
    }
    private static void ProtocolTests()
    {
        Test("direct motion and pose packets preserve wire bytes, ownership, nesting and failed writer recovery", delegate
        {
            Pose pose = new Pose { Id = "тест", Sequence = 321, Pitch = -0.0f, Position = new V3(1.125f, -3.75f, 0.1f), Rotation = new Q4(0.25f, -0.5f, 0.125f, 1) };
            Check(Codec.Hash(Codec.PoseFrame(pose)) == Codec.Hash(Codec.Packet(MessageKind.Pose, Codec.Pose(pose))), "Direct pose frame changed wire bytes");
            MotionFrame motion = new MotionFrame { Sequence = 91 };
            for (int i = 0; i < 13; i++) motion.Bodies.Add(new BodyPose { Id = "part-" + i, Position = new V3(i * 0.125f, -0.0f, 0.1f), Velocity = new V3(0, i * 0.01f, 0), Rotation = new Q4(0.25f, -0.5f, 0.125f, 1) });
            byte[] packet = Codec.MotionPacket(motion); string hash = Codec.Hash(packet);
            Check(hash == Codec.Hash(Codec.Packet(MessageKind.Motion, Codec.Motion(motion))), "Direct motion frame changed wire bytes");
            MotionFrame first = Codec.Motion(Codec.View(packet));
            byte[] nested = null;
            byte[] outer = Codec.MakeFrame(MessageKind.Ping, delegate(BinaryWriter w) { w.Write(7); nested = Codec.MotionPacket(motion); w.Write(9); });
            Check(Codec.Hash(nested) == hash && Codec.Hash(outer) == Codec.Hash(Codec.Packet(MessageKind.Ping, Codec.Make(delegate(BinaryWriter w) { w.Write(7); w.Write(9); }))), "Nested writer overwrote a live frame");
            Throws(delegate { Codec.MakeFrame(MessageKind.Motion, delegate(BinaryWriter w) { w.Write(99); throw new IOException("writer failed"); }); });
            Check(Codec.Hash(Codec.MotionPacket(motion)) == hash, "Failed writer retained stale prefix or payload");
            motion.Bodies[0].Position = new V3(100, 200, 300); Codec.MotionPacket(motion); MotionFrame second = Codec.Motion(Codec.View(Codec.MotionPacket(motion)));
            Check(Codec.Hash(packet) == hash && first.Bodies.Count == 13 && first.Bodies[0].Position.X == 0 && second.Bodies[0].Position.X == 100, "Returned packet or retained decoded body was reused");
            Throws(delegate { Codec.MakeFrame(MessageKind.Motion, delegate(BinaryWriter w) { w.Write(new byte[Protocol.MaxFrame]); }); });
            Check(Codec.Hash(Codec.PoseFrame(pose)) == Codec.Hash(Codec.Packet(MessageKind.Pose, Codec.Pose(pose))), "Oversized frame poisoned the reusable writer");
        });
        Test("colliding hot UTF8 strings retain independent exact values in the bounded cache", delegate
        {
            // These different ASCII strings have the same 32-bit FNV-1a hash.
            byte[] bytes = Codec.Make(delegate(BinaryWriter w) { foreach (string s in new string[] { "costarring", "liquid", "costarring", "liquid", "дверь 🚗", "дверь 🚗" }) Codec.Text(w, s); });
            Codec.Read(bytes, delegate(BinaryReader r)
            {
                string a = Codec.Text(r), b = Codec.Text(r);
                Check(a == "costarring" && b == "liquid", "Hash collision aliased different strings");
                Check(object.ReferenceEquals(a, Codec.Text(r)) && object.ReferenceEquals(b, Codec.Text(r)), "Colliding hot values allocated new strings");
                string unicode = Codec.Text(r); Check(unicode == "дверь 🚗" && object.ReferenceEquals(unicode, Codec.Text(r)), "UTF8 cache changed a multi-byte value");
            });
        });
        Test("packet views decode bounded immutable slices and snapshot chunks without copying", delegate
        {
            Command command = Cmd(17, CommandKind.Attach, "part"); command.Argument = "Assemble";
            byte[] frame = Codec.Packet(MessageKind.Command, Codec.Command(command)); string hash = Codec.Hash(frame); PacketView view = Codec.View(frame);
            Check(object.ReferenceEquals(view.Bytes, frame) && view.Offset == 15 && view.Length == frame.Length - 15, "View copied its payload");
            Check(Codec.Hash(Codec.Command(Codec.Command(view))) == Codec.Hash(Codec.Command(command)) && Codec.Hash(frame) == hash, "Slice decoding changed a command or its immutable frame");
            Pose pose = new Pose { Id = "A", Sequence = 42, Position = new V3(1, 2, 3), Pitch = 16 };
            Check(Codec.Pose(Codec.View(Codec.Packet(MessageKind.Pose, Codec.Pose(pose)))).Sequence == 42, "Pose slice failed");
            CommandResult result = new CommandResult { Sequence = 8, Revision = 9, Accepted = true, Reason = "готово" };
            Check(Codec.Result(Codec.View(Codec.Packet(MessageKind.Result, Codec.Result(result)))).Reason == result.Reason, "Result slice failed");
            MotionFrame motion = new MotionFrame { Sequence = 4 }; motion.Bodies.Add(new BodyPose { Id = "part", Position = new V3(3, 2, 1) });
            Check(Codec.Hash(Codec.Motion(Codec.Motion(Codec.View(Codec.Packet(MessageKind.Motion, Codec.Motion(motion)))))) == Codec.Hash(Codec.Motion(motion)), "Motion slice changed values");
            SnapshotReceiver receiver = new SnapshotReceiver(); byte[] chunk = new byte[] { 1, 2, 3, 4 }, framed = new byte[] { 99, 1, 2, 3, 4, 88 };
            receiver.Begin(chunk.Length, Codec.Hash(chunk)); receiver.Chunk(0, framed, 1, 4); Check(Codec.Hash(receiver.End()) == Codec.Hash(chunk), "Chunk included bytes outside its bounds");
            Throws(delegate { Codec.Read(view, delegate(BinaryReader r) { r.ReadByte(); }); });
            Throws(delegate { Codec.Read(frame, 14, frame.Length, delegate(BinaryReader r) { }); });
            byte[] corrupt = (byte[])frame.Clone(); corrupt[corrupt.Length - 1] ^= 1; Throws(delegate { Codec.View(corrupt); });
            byte[] truncated = new byte[frame.Length - 1]; Array.Copy(frame, truncated, truncated.Length); Throws(delegate { Codec.View(truncated); });
            receiver.Begin(4, Codec.Hash(chunk)); Throws(delegate { receiver.Chunk(0, framed, 3, 4); }); receiver.Reset();
        });
        Test("canonical snapshot / all value types / Cyrillic", delegate
        {
            World w = Sample(); byte[] a = Codec.World(w), b = Codec.World(Codec.World(a));
            Check(Codec.Hash(a) == Codec.Hash(b), "Roundtrip changed world");
            Check(Codec.World(a).Entities["part"].Properties["name"].Text == "Блок двигателя", "UTF-8 failed");
        });
        Test("compact movement deltas retain every property and exact transform without copying the property map", delegate
        {
            World world = new World { Id = "compact", GameHash = "build", SchemaHash = "schema" }; world.EnableChangeTracking(); SnapshotBuilder builder = new SnapshotBuilder();
            Entity probe = builder.Begin("moving", "native/moving", EntityKind.Rigidbody, 2048);
            for (int i = 0; i < 2048; i++) probe.Properties["vertex/" + i] = builder.V(new V3(i, i / 3.0f, -i));
            builder.Publish(world); World baseline = world.ReferenceSnapshot(), replica = world.Clone(); Entity old = world.Entities["moving"];
            probe = builder.Begin(old.Id, old.Key, old.Kind, 2048); foreach (KeyValuePair<string, Atom> property in old.Properties) probe.Properties[property.Key] = property.Value;
            probe.Parent = "parent"; probe.Active = false; probe.Position = new V3(1.234567f, float.Epsilon, -98.76f); probe.Rotation = new Q4(.1f, .2f, .3f, .9f); probe.Velocity = new V3(13, -4, 5); probe.AngularVelocity = new V3(6, 7, 8);
            builder.Publish(world); world.Revision++;
            World delta = CheckpointStore.Difference(baseline, world, true);
            byte[] bytes = Codec.Make(delegate(BinaryWriter w) { Codec.WriteDelta(w, delta, baseline); }); World decoded = null;
            Codec.Read(bytes, delegate(BinaryReader r) { decoded = Codec.ReadDelta(r, replica); });
            Check(bytes.Length < Codec.World(delta).Length / 20, "Movement repeated the large property map");
            Check(object.ReferenceEquals(decoded.Entities["moving"].Properties, replica.Entities["moving"].Properties), "Movement allocated a replacement property map");
            CheckpointStore.ApplyDelta(replica, decoded, true);
            Check(Codec.Hash(Codec.World(world)) == Codec.Hash(Codec.World(replica)), "Compact movement lost exact world values");
            Throws(delegate { Codec.World(bytes); });
            World missing = baseline.Clone(); missing.Entities.Clear(); Throws(delegate { Codec.Read(bytes, delegate(BinaryReader r) { Codec.ReadDelta(r, missing); }); });
            World wrong = baseline.Clone(); wrong.GameHash = "different"; Throws(delegate { Codec.Read(bytes, delegate(BinaryReader r) { Codec.ReadDelta(r, wrong); }); });
        });
        Test("sparse properties retain signed zero, removed keys and unchanged deformation without mutating the baseline", delegate
        {
            World before = Sample(); Entity part = before.Entities["part"];
            for (int i = 0; i < 2048; i++) part.Properties["vertex/" + i] = Atom.V(new V3(i, i / 7.0f, -i));
            double minusZero = BitConverter.Int64BitsToDouble(long.MinValue); float negativeZero = BitConverter.ToSingle(new byte[] { 0, 0, 0, 128 }, 0);
            part.Properties["zero"] = Atom.N(minusZero); part.Properties["vector-zero"] = Atom.V(new V3(negativeZero, 1, 2));
            World after = before.Clone(); after.Revision++; after.Entities["part"].Properties["zero"] = Atom.N(0); after.Entities["part"].Properties["vector-zero"] = Atom.V(new V3(0, 1, 2));
            after.Entities["part"].Properties["stage"] = Atom.N(3); after.Entities["part"].Properties.Remove("name"); after.Entities["part"].Properties["added"] = Atom.S("проводка");
            World delta = CheckpointStore.Difference(before, after, true), decoded = null;
            byte[] bytes = Codec.Make(delegate(BinaryWriter w) { Codec.WriteDelta(w, delta, before); });
            Codec.Read(bytes, delegate(BinaryReader r) { decoded = Codec.ReadDelta(r, before); });
            Check(bytes.Length < Codec.World(delta).Length / 20, "A small property change repeated the deformation map");
            Check(before.Entities["part"].Properties.ContainsKey("name") && before.Entities["part"].Properties["stage"].Number != 3, "Sparse decode mutated the baseline");
            World replica = before.Clone(); CheckpointStore.ApplyDelta(replica, decoded, true);
            Check(Codec.Hash(Codec.World(replica)) == Codec.Hash(Codec.World(after)), "Sparse properties lost exact wire values or a removal");
            for (int step = 0; step < 4; step++)
            {
                World previous = replica.ReferenceSnapshot(); after = replica.Clone(); after.Revision++;
                Entity changed = after.Entities["part"];
                if (step == 0) changed.Parent = "seat";
                if (step == 1) changed.Prefab = "prefab/changed";
                if (step == 2) changed.Kind = EntityKind.Vehicle;
                if (step == 3) changed.Key = "native/changed";
                delta = CheckpointStore.Difference(previous, after, true);
                bytes = Codec.Make(delegate(BinaryWriter w) { Codec.WriteDelta(w, delta, previous); });
                Codec.Read(bytes, delegate(BinaryReader r) { decoded = Codec.ReadDelta(r, replica); }); CheckpointStore.ApplyDelta(replica, decoded, true);
                Check(Codec.Hash(Codec.World(replica)) == Codec.Hash(Codec.World(after)), "Compact metadata omitted changed hierarchy, prefab, kind or key");
            }
        });
        Test("compact deltas reproduce seeded replacement, removal, deletion and recreation sequences", delegate
        {
            World world = new World { Id = "compact-replay", GameHash = "build", SchemaHash = "schema" }; world.EnableChangeTracking(); SnapshotBuilder builder = new SnapshotBuilder(); Random random = new Random(76439);
            World baseline = world.ReferenceSnapshot(), replica = world.Clone();
            for (int step = 0; step < 240; step++)
            {
                string id = "entity-" + random.Next(12); Entity old; world.Entities.TryGetValue(id, out old);
                if (old != null && step % 7 == 0) { world.Entities.Remove(id); world.EntityChanged(id); }
                else
                {
                    Entity probe = builder.Begin(id, "native/" + id, EntityKind.Object);
                    if (old != null) foreach (KeyValuePair<string, Atom> p in old.Properties) probe.Properties[p.Key] = p.Value;
                    if (old == null || step % 3 == 0) probe.Properties["value"] = builder.N(step + .123456789);
                    if (step % 5 == 0) probe.Properties.Remove("value");
                    probe.Position = new V3(step / 7.0f, -step, float.Epsilon); probe.Active = step % 2 == 0; builder.Publish(world);
                }
                world.Revision++; world.Money = step / 3.0; world.SleepEpoch = step / 20;
                World delta = CheckpointStore.Difference(baseline, world, true), decoded = null;
                byte[] bytes = Codec.Make(delegate(BinaryWriter w) { Codec.WriteDelta(w, delta, baseline); });
                Codec.Read(bytes, delegate(BinaryReader r) { decoded = Codec.ReadDelta(r, replica); }); CheckpointStore.ApplyDelta(replica, decoded, true); CheckpointStore.ApplyDelta(baseline, delta, true);
                Check(Codec.Hash(Codec.World(world)) == Codec.Hash(Codec.World(replica)), "Compact replica diverged at step " + step);
            }
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
            Test("slow native journal coalesces unwritten state without losing deletions or durable commands", delegate
            {
                string slowPath = Path.Combine(path, "slow-background");
                using (CheckpointStore store = new CheckpointStore(slowPath) { ImmutableEntities = true })
                {
                    World world = Sample(); world.EnableChangeTracking(); store.Checkpoint(world);
                    object queue = typeof(CheckpointStore).GetField("pendingNative", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(store);
                    lock (queue)
                    {
                        for (int stage = 1; stage <= 12; stage++)
                        {
                            Entity next = world.Entities["part"].Clone(); next.Properties["stage"] = Atom.N(stage); world.Entities["part"] = next; world.EntityChanged("part");
                            if (stage == 5) { Entity added = next.Clone(); added.Id = "temporary"; world.Entities[added.Id] = added; world.EntityChanged(added.Id); }
                            if (stage == 6) { world.Entities.Remove("temporary"); world.EntityChanged("temporary"); }
                            world.Revision++; store.CommitNative(world);
                        }
                        Check((int)queue.GetType().GetProperty("Count").GetValue(queue, null) == 4, "Native journal queue grew beyond its bound");
                    }
                    world.Money = 321; world.Revision++; store.Commit(world, "A", Cmd(1, CommandKind.Purchase, "part"));
                    World recovered = new CheckpointStore(slowPath).Recover();
                    Check(recovered.Revision == 13 && recovered.Money == 321 && recovered.Entities["part"].Properties["stage"].Number == 12 && !recovered.Entities.ContainsKey("temporary"), "Coalesced journal lost state or reordered a durable command");
                }
            });
            Test("compact journal restores exact movement and survives a checkpoint that supersedes removed baselines", delegate
            {
                string compactPath = Path.Combine(path, "compact-journal"); World world = new World { Id = "journal-compact", GameHash = "build", SchemaHash = "schema" }; world.EnableChangeTracking(); SnapshotBuilder builder = new SnapshotBuilder();
                Entity probe = builder.Begin("moving", "native/moving", EntityKind.Rigidbody, 2048);
                for (int i = 0; i < 2048; i++) probe.Properties["vertex/" + i] = builder.V(new V3(i, i / 3.0f, -i));
                builder.Publish(world);
                using (CheckpointStore store = new CheckpointStore(compactPath) { ImmutableEntities = true })
                {
                    store.Checkpoint(world); long fullBytes = Codec.World(world).Length;
                    for (int i = 1; i <= 10; i++)
                    {
                        Entity old = world.Entities["moving"]; probe = builder.Begin(old.Id, old.Key, old.Kind, 2048);
                        foreach (KeyValuePair<string, Atom> p in old.Properties) probe.Properties[p.Key] = p.Value;
                        probe.Position = new V3(i / 7.0f, -i, float.Epsilon); builder.Publish(world); world.Revision++; store.CommitNative(world); store.FlushNative();
                    }
                    World recovered = new CheckpointStore(compactPath).Recover();
                    Check(Codec.Hash(Codec.World(world)) == Codec.Hash(Codec.World(recovered)), "Compact journal lost exact property or movement values");
                    Check(new FileInfo(Path.Combine(compactPath, "world.scj")).Length < fullBytes, "Movement journal kept repeating the property map");
                    world.Entities.Remove("moving"); world.EntityChanged("moving"); world.Revision++; store.CommitNative(world); store.FlushNative();
                    store.BeginCheckpoint(world); store.FinishBackgroundCheckpoint();
                    world.Money = 123; world.Revision++; store.CommitNative(world); store.FlushNative();
                    recovered = new CheckpointStore(compactPath).Recover();
                    Check(recovered.Revision == 12 && recovered.Money == 123 && recovered.Entities.Count == 0, "Superseded compact records broke checkpoint recovery");
                    World foreign = world.Clone(); foreign.Id = "foreign";
                    using (FileStream file = new FileStream(Path.Combine(compactPath, "world.scj"), FileMode.Append)) AtomicFile.WriteWorldRecord(file, foreign);
                    Throws(delegate { new CheckpointStore(compactPath).Recover(); });
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
    private static void TransportTests(bool sockets)
    {
        if (sockets) Test("actual loopback TCP/UDP with host plus three clients", delegate
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
                    // Retain both directions' packets while later datagrams reuse the
                    // receiver's scratch buffer. Each event must keep its own bytes.
                    List<byte[]> sent = new List<byte[]>(), retainedHost = new List<byte[]>(), retainedClient = new List<byte[]>();
                    for (int n = 0; n < 8; n++)
                    {
                        byte[] packet = Codec.Packet(MessageKind.Pose, Codec.Pose(new Pose { Id = "buffer-proof-" + new string('x', n * 9), Sequence = n + 100, Position = new V3(n, -n, n * 0.25f) }));
                        sent.Add(packet); byte[] outbound = (byte[])packet.Clone(); clients[1].Send("host", outbound, false);
                        // UDP Send owns an envelope before returning, even when the
                        // operating-system send happens later on the background writer.
                        Array.Clear(outbound, 0, outbound.Length);
                    }
                    clock.Restart();
                    while (clock.ElapsedMilliseconds < 3000 && (retainedHost.Count < 8 || retainedClient.Count < 8))
                    {
                        TransportEvent e;
                        while (host.Poll(out e)) if (e.Kind == TransportEventKind.Data && !e.Reliable)
                        {
                            retainedHost.Add(e.Data); byte[] outbound = (byte[])e.Data.Clone(); host.Send(e.Peer, outbound, false); Array.Clear(outbound, 0, outbound.Length);
                        }
                        while (clients[1].Poll(out e)) if (e.Kind == TransportEventKind.Data && !e.Reliable) retainedClient.Add(e.Data);
                        Thread.Sleep(2);
                    }
                    Check(retainedHost.Count == 8 && retainedClient.Count == 8, "UDP buffer proof incomplete");
                    foreach (List<byte[]> retained in new List<byte[]>[] { retainedHost, retainedClient })
                    {
                        HashSet<int> seen = new HashSet<int>();
                        foreach (byte[] packet in retained)
                        {
                            Pose pose = Codec.Pose(Codec.Open(packet).Payload); int n = checked((int)pose.Sequence - 100);
                            Check(n >= 0 && n < sent.Count && seen.Add(n) && Codec.Hash(packet) == Codec.Hash(sent[n]), "Later datagram changed retained packet bytes");
                        }
                    }
                    clients[0].Dispose(); clock.Restart(); bool removed = false;
                    while (clock.ElapsedMilliseconds < 3000 && !removed) { TransportEvent e; while (host.Poll(out e)) if (e.Kind == TransportEventKind.Disconnected) removed = true; Thread.Sleep(2); }
                    Check(removed && host.Peers.Count == 2, "Disconnect not detected");
                }
                finally { foreach (LanTransport c in clients) c.Dispose(); }
            }
        });
        else Console.WriteLine("SKIP actual loopback TCP/UDP: --no-sockets requested");
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
    public static int Main(string[] args)
    {
        if (args.Length > 1 || (args.Length == 1 && args[0] != "--no-sockets")) { Console.Error.WriteLine("Usage: SummerCoop.Tests.exe [--no-sockets]"); return 2; }
        bool sockets = args.Length == 0;
        try { ProtocolTests(); AuthorityTests(); OptimizationTests(); PerformanceTests.Run(Test); PersistenceTests(); LocalHierarchyTests.Run(Test); ToolInventoryTests.Run(Test); PersonalDeathHierarchyTests.Run(Test); ClientInteractionRegressionTests.Run(Test); TransferEncodingTests.Run(Test); NativeActionPolicyTests.Run(Test); PersonalInteractionTests.Run(Test); LobbyCreationTests.Run(Test); SandboxTests.Run(Test); NativeSaveBundleTests.Run(Test); MscSaveProfileTests.Run(Test); TransportTests(sockets); SessionTests.Run(Test); Console.WriteLine((sockets ? "ALL " : "PARTIAL (1 socket test skipped): ") + passed + " TESTS PASSED"); return 0; }
        catch (Exception e) { Console.Error.WriteLine("FAIL after " + passed + " tests: " + e); return 1; }
    }
}
