using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SummerCoop.Core
{
    // Unity's stripped Mono System.dll does not expose InvalidDataException.
    public sealed class ProtocolException : IOException
    {
        public ProtocolException(string message) : base(message) { }
    }
    internal sealed class JournalIdentityException : IOException
    {
        public JournalIdentityException() : base("Journal belongs to another world") { }
    }
    public enum MessageKind : byte { Hello = 1, Welcome, Reject, SaveBundle, SnapshotBegin, SnapshotChunk, SnapshotEnd, Ready, WorldDelta, Command, Result, Pose, Motion, Goodbye, Ping, Resync }
    public sealed class Packet { public MessageKind Kind; public byte[] Payload; }
    public struct PacketView { public MessageKind Kind; public byte[] Bytes; public int Offset, Length; }

    // The container is borrowed; Entity/Atom snapshots remain independently owned.
    internal sealed class DeltaReadBuffer
    {
        public readonly World World = new World();
        public int EntityCapacity;
        public void Clear()
        {
            if (World.Entities.Count != 0) World.Entities.Clear();
            if (World.Players.Count != 0) World.Players.Clear();
            World.Changes = null; World.ChangeSequence = 0;
        }
        public void EnsureCapacity(int count)
        {
            if (count <= EntityCapacity) return;
            int capacity = 16; while (capacity < count) capacity *= 2;
            EntityCapacity = Math.Min(capacity, Protocol.MaxEntities);
            World.Entities = new Dictionary<string, Entity>(EntityCapacity, StringComparer.Ordinal);
        }
    }

    public static class Codec
    {
        private const uint Magic = 0x504F4353;
        [ThreadStatic] private static byte[] textBuffer;
        [ThreadStatic] private static List<string> entityKeys;
        [ThreadStatic] private static List<string> patchedKeys;
        [ThreadStatic] private static byte[] readTextBuffer;
        [ThreadStatic] private static CachedText[] readTextCache;
        [ThreadStatic] private static Atom[] readNumbers, readBooleans, readZeroVectors, readIdentityRotations;
        [ThreadStatic] private static Atom readUnitVector;
        private sealed class CachedText { public byte[] Bytes; public int Length; public uint Hash; public string Value; }
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private sealed class WriteBuffer
        {
            public readonly MemoryStream Stream = new MemoryStream(256);
            public readonly BinaryWriter Writer;
            public WriteBuffer() { Writer = new WireWriter(Stream); }
        }
        [ThreadStatic] private static Stack<WriteBuffer> writeBuffers;
        private static void Store32(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)value; bytes[offset + 1] = (byte)(value >> 8); bytes[offset + 2] = (byte)(value >> 16); bytes[offset + 3] = (byte)(value >> 24);
        }
        public static byte[] Packet(MessageKind kind, byte[] payload)
        {
            if (payload == null || payload.Length > Protocol.MaxFrame - 15) throw new ProtocolException("Packet too large");
            byte[] bytes = new byte[payload.Length + 15]; Store32(bytes, 0, Magic);
            bytes[4] = (byte)Protocol.Version; bytes[5] = (byte)(Protocol.Version >> 8); bytes[6] = (byte)kind;
            Store32(bytes, 7, (uint)payload.Length); Store32(bytes, 11, Crc(payload)); Array.Copy(payload, 0, bytes, 15, payload.Length); return bytes;
        }
        public static Packet Open(byte[] bytes)
        {
            MessageKind kind = Validate(bytes);
            byte[] payload = new byte[bytes.Length - 15]; Array.Copy(bytes, 15, payload, 0, payload.Length);
            return new Packet { Kind = kind, Payload = payload };
        }
        // The transport owns an immutable frame until its synchronous handler returns.
        // Decode its validated payload in place instead of copying every received frame.
        public static PacketView View(byte[] bytes)
        {
            return new PacketView { Kind = Validate(bytes), Bytes = bytes, Offset = 15, Length = bytes.Length - 15 };
        }
        private static uint Load32(byte[] bytes, int offset)
        {
            return (uint)bytes[offset] | (uint)bytes[offset + 1] << 8 | (uint)bytes[offset + 2] << 16 | (uint)bytes[offset + 3] << 24;
        }
        public static MessageKind Validate(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 15 || bytes.Length > Protocol.MaxFrame) throw new ProtocolException("Invalid frame size");
            if (Load32(bytes, 0) != Magic || (bytes[4] | bytes[5] << 8) != Protocol.Version) throw new ProtocolException("Protocol mismatch");
            MessageKind kind = (MessageKind)bytes[6]; if (kind < MessageKind.Hello || kind > MessageKind.Resync) throw new ProtocolException("Unknown packet");
            if (Load32(bytes, 7) != bytes.Length - 15) throw new ProtocolException("Invalid payload length");
            if (~CrcAppend(0xffffffff, bytes, 15, bytes.Length - 15) != Load32(bytes, 11)) throw new ProtocolException("Packet checksum mismatch");
            return kind;
        }
        public static byte[] Make(Action<BinaryWriter> write)
        {
            return WriteBuffered(0, false, write);
        }
        // Encode a complete immutable packet in one owned array, without first
        // materializing and then copying a separate payload. Wire bytes are unchanged.
        public static byte[] MakeFrame(MessageKind kind, Action<BinaryWriter> write)
        {
            return WriteBuffered(kind, true, write);
        }
        private static byte[] WriteBuffered(MessageKind kind, bool framed, Action<BinaryWriter> write)
        {
            if (writeBuffers == null) writeBuffers = new Stack<WriteBuffer>();
            WriteBuffer buffer = writeBuffers.Count == 0 ? new WriteBuffer() : writeBuffers.Pop();
            try
            {
                if (framed) { buffer.Stream.SetLength(15); buffer.Stream.Position = 15; }
                write(buffer.Writer); buffer.Writer.Flush();
                if (framed && (buffer.Stream.Length < 15 || buffer.Stream.Length > Protocol.MaxFrame)) throw new ProtocolException("Packet too large");
                byte[] bytes = buffer.Stream.ToArray();
                if (framed)
                {
                    Store32(bytes, 0, Magic); bytes[4] = (byte)Protocol.Version; bytes[5] = (byte)(Protocol.Version >> 8); bytes[6] = (byte)kind;
                    Store32(bytes, 7, (uint)(bytes.Length - 15)); Store32(bytes, 11, ~CrcAppend(0xffffffff, bytes, 15, bytes.Length - 15));
                }
                return bytes;
            }
            finally
            {
                // Returned byte arrays own their storage. Reentrant and other-thread calls get separate writers.
                if (buffer.Stream.Capacity <= 1048576 && writeBuffers.Count < 4) { buffer.Stream.SetLength(0); buffer.Stream.Position = 0; writeBuffers.Push(buffer); }
                else buffer.Writer.Close();
            }
        }
        public static void Read(byte[] data, Action<BinaryReader> read)
        {
            Read(data, 0, data.Length, read);
        }
        public static void Read(PacketView packet, Action<BinaryReader> read) { Read(packet.Bytes, packet.Offset, packet.Length, read); }
        public static void Read(byte[] data, int offset, int length, Action<BinaryReader> read)
        {
            if (data == null || offset < 0 || length < 0 || offset > data.Length - length) throw new ProtocolException("Invalid payload bounds");
            using (MemoryStream s = new MemoryStream(data, offset, length, false)) using (BinaryReader r = new BinaryReader(s))
            {
                read(r); if (s.Position != s.Length) throw new ProtocolException("Trailing data");
            }
        }
        public static void Text(BinaryWriter w, string text)
        {
            text = text ?? ""; int length = Encoding.UTF8.GetByteCount(text);
            if (length > Protocol.MaxString) throw new ProtocolException("String too long");
            if (textBuffer == null || textBuffer.Length < length) textBuffer = new byte[Math.Max(256, length)];
            Encoding.UTF8.GetBytes(text, 0, text.Length, textBuffer, 0);
            w.Write(length); w.Write(textBuffer, 0, length);
        }
        public static string Text(BinaryReader r)
        {
            int n = Count(r, Protocol.MaxString);
            return ReadText(r, n);
        }
        private static string ReadText(BinaryReader r, int n)
        {
            if (n < 0 || n > Protocol.MaxString) throw new ProtocolException("Invalid string length");
            if (n == 0) return "";
            if (readTextBuffer == null || readTextBuffer.Length < n) readTextBuffer = new byte[Math.Max(256, n)];
            int offset = 0; while (offset < n) { int count = r.Read(readTextBuffer, offset, n - offset); if (count == 0) throw new EndOfStreamException(); offset += count; }
            if (n > 256) return StrictUtf8.GetString(readTextBuffer, 0, n);
            uint hash = 2166136261; for (int i = 0; i < n; i++) hash = (hash ^ readTextBuffer[i]) * 16777619;
            if (readTextCache == null) readTextCache = new CachedText[8192];
            int slot = (int)(hash & 2047) * 4; CachedText cached;
            for (int way = 0; way < 4; way++)
            {
                cached = readTextCache[slot + way];
                if (cached == null) break;
                if (cached.Hash != hash || cached.Length != n) continue;
                bool same = true; for (int i = 0; i < n; i++) if (cached.Bytes[i] != readTextBuffer[i]) { same = false; break; }
                if (!same) continue;
                for (int i = way; i > 0; i--) readTextCache[slot + i] = readTextCache[slot + i - 1];
                readTextCache[slot] = cached; return cached.Value;
            }
            string value = StrictUtf8.GetString(readTextBuffer, 0, n);
            // Four entries per bucket keep colliding hot names and IDs without
            // increasing the 8192-entry bound. Hits still compare every byte.
            cached = readTextCache[slot + 3]; if (cached == null) cached = new CachedText();
            if (cached.Bytes == null || cached.Bytes.Length < n) cached.Bytes = new byte[n];
            Array.Copy(readTextBuffer, cached.Bytes, n); cached.Length = n; cached.Hash = hash; cached.Value = value;
            for (int i = 3; i > 0; i--) readTextCache[slot + i] = readTextCache[slot + i - 1];
            readTextCache[slot] = cached; return value;
        }
        public static int Count(BinaryReader r, int maximum)
        {
            int n = r.ReadInt32(); if (n < 0 || n > maximum) throw new ProtocolException("Invalid count"); return n;
        }
        public static void Vector(BinaryWriter w, V3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        public static V3 Vector(BinaryReader r)
        {
            V3 v = new V3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); if (!v.IsFinite) throw new ProtocolException("Invalid vector"); return v;
        }
        public static void Rotation(BinaryWriter w, Q4 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write(v.W); }
        public static Q4 Rotation(BinaryReader r)
        {
            Q4 q = new Q4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (!q.IsFinite) throw new ProtocolException("Invalid rotation"); return q;
        }
        public static void Value(BinaryWriter w, Atom a)
        {
            w.Write((byte)a.Kind);
            switch (a.Kind)
            {
                case AtomKind.Number: case AtomKind.Boolean: w.Write(a.Number); break;
                case AtomKind.Text: case AtomKind.Reference: Text(w, a.Text); break;
                case AtomKind.Vector: Vector(w, a.Vector); break;
                case AtomKind.Rotation: Rotation(w, a.Rotation); break;
                default: throw new ProtocolException("Unknown value kind");
            }
        }
        public static Atom Value(BinaryReader r)
        {
            return ReadValue(r, false);
        }
        private static int ZeroSign(float value) { return 1.0f / value < 0 ? 1 : 0; }
        private static Atom ReadValue(BinaryReader r, bool immutable)
        {
            AtomKind kind = (AtomKind)r.ReadByte();
            switch (kind)
            {
                case AtomKind.Number: case AtomKind.Boolean:
                    double n = r.ReadDouble();
                    if (double.IsNaN(n) || double.IsInfinity(n)) throw new ProtocolException("Invalid number");
                    if (kind == AtomKind.Boolean && n != 0 && n != 1) throw new ProtocolException("Invalid boolean");
                    if (immutable && kind == AtomKind.Boolean)
                    {
                        if (readBooleans == null) readBooleans = new Atom[3]; int index = n == 1 ? 1 : 1.0 / n < 0 ? 2 : 0;
                        return readBooleans[index] ?? (readBooleans[index] = new Atom { Kind = kind, Number = n });
                    }
                    if (immutable && kind == AtomKind.Number && n >= -16 && n <= 256 && n == Math.Truncate(n))
                    {
                        if (readNumbers == null) readNumbers = new Atom[274]; int index = n == 0 && 1.0 / n < 0 ? 273 : (int)n + 16;
                        return readNumbers[index] ?? (readNumbers[index] = Atom.N(n));
                    }
                    return new Atom { Kind = kind, Number = n };
                case AtomKind.Text: case AtomKind.Reference: return new Atom { Kind = kind, Text = Text(r) };
                case AtomKind.Vector:
                    V3 vector = Vector(r);
                    if (immutable && vector.X == 0 && vector.Y == 0 && vector.Z == 0)
                    {
                        if (readZeroVectors == null) readZeroVectors = new Atom[8]; int index = ZeroSign(vector.X) | ZeroSign(vector.Y) << 1 | ZeroSign(vector.Z) << 2;
                        return readZeroVectors[index] ?? (readZeroVectors[index] = Atom.V(vector));
                    }
                    if (immutable && vector.X == 1 && vector.Y == 1 && vector.Z == 1) return readUnitVector ?? (readUnitVector = Atom.V(vector));
                    return Atom.V(vector);
                case AtomKind.Rotation:
                    Q4 rotation = Rotation(r);
                    if (immutable && rotation.X == 0 && rotation.Y == 0 && rotation.Z == 0 && rotation.W == 1)
                    {
                        if (readIdentityRotations == null) readIdentityRotations = new Atom[8]; int index = ZeroSign(rotation.X) | ZeroSign(rotation.Y) << 1 | ZeroSign(rotation.Z) << 2;
                        return readIdentityRotations[index] ?? (readIdentityRotations[index] = Atom.Q(rotation));
                    }
                    return Atom.Q(rotation);
                default: throw new ProtocolException("Unknown value kind");
            }
        }
        public static void Entity(BinaryWriter w, Entity e)
        {
            WriteEntity(w, e, false, null);
        }
        private static bool SameWireValue(Atom a, Atom b)
        {
            if (object.ReferenceEquals(a, b)) return true;
            if (!a.Same(b)) return false;
            if (a.Kind == AtomKind.Number || a.Kind == AtomKind.Boolean) return a.Number != 0 || 1.0 / a.Number == 1.0 / b.Number;
            if (a.Kind == AtomKind.Vector) return SameZero(a.Vector.X, b.Vector.X) && SameZero(a.Vector.Y, b.Vector.Y) && SameZero(a.Vector.Z, b.Vector.Z);
            if (a.Kind == AtomKind.Rotation) return SameZero(a.Rotation.X, b.Rotation.X) && SameZero(a.Rotation.Y, b.Rotation.Y) && SameZero(a.Rotation.Z, b.Rotation.Z) && SameZero(a.Rotation.W, b.Rotation.W);
            return true;
        }
        private static bool SameZero(float a, float b) { return a != 0 || 1.0f / a == 1.0f / b; }
        private static void WriteEntity(BinaryWriter w, Entity e, bool shareProperties, Entity baseline)
        {
            Text(w, e.Id);
            if (baseline != null && e.Key == baseline.Key && e.Prefab == baseline.Prefab && e.Parent == baseline.Parent && e.Kind == baseline.Kind) w.Write(-1);
            else { Text(w, e.Key); Text(w, e.Prefab); Text(w, e.Parent); w.Write((byte)e.Kind); }
            w.Write(e.Active);
            Vector(w, e.Position); Rotation(w, e.Rotation); Vector(w, e.Velocity); Vector(w, e.AngularVelocity);
            if (shareProperties) { w.Write(-1); return; }
            List<string> keys = entityKeys; if (keys == null) entityKeys = keys = new List<string>(); keys.Clear();
            if (baseline != null)
            {
                w.Write(-2); keys.Clear();
                foreach (KeyValuePair<string, Atom> p in e.Properties) { Atom old; if (!baseline.Properties.TryGetValue(p.Key, out old) || !SameWireValue(p.Value, old)) keys.Add(p.Key); }
                keys.Sort(StringComparer.Ordinal); w.Write(keys.Count); foreach (string key in keys) { Text(w, key); Value(w, e.Properties[key]); }
                keys.Clear(); foreach (string key in baseline.Properties.Keys) if (!e.Properties.ContainsKey(key)) keys.Add(key);
                keys.Sort(StringComparer.Ordinal); w.Write(keys.Count); foreach (string key in keys) Text(w, key); return;
            }
            keys.AddRange(e.Properties.Keys); keys.Sort(StringComparer.Ordinal);
            w.Write(keys.Count); foreach (string key in keys) { Text(w, key); Value(w, e.Properties[key]); }
        }
        public static Entity Entity(BinaryReader r)
        {
            return ReadEntity(r, null, false);
        }
        private static Entity ReadEntity(BinaryReader r, World baseline, bool immutable)
        {
            string id = Text(r), key, prefab, parent; int keyLength = r.ReadInt32(); Entity old = null; EntityKind kind;
            if (keyLength == -1)
            {
                if (baseline == null || !baseline.Entities.TryGetValue(id, out old)) throw new ProtocolException("Missing delta metadata baseline");
                id = old.Id; key = old.Key; prefab = old.Prefab; parent = old.Parent; kind = old.Kind;
            }
            else { key = ReadText(r, keyLength); prefab = Text(r); parent = Text(r); kind = (EntityKind)r.ReadByte(); }
            bool active = r.ReadBoolean();
            V3 position = Vector(r); Q4 rotation = Rotation(r); V3 velocity = Vector(r), angularVelocity = Vector(r);
            if (id == "" || kind < EntityKind.Object || kind > EntityKind.Globals) throw new ProtocolException("Invalid entity");
            int n = r.ReadInt32(); Dictionary<string, Atom> properties;
            if (n == -1 || n == -2)
            {
                if (baseline == null || key == "__deleted" || (old == null && !baseline.Entities.TryGetValue(id, out old))) throw new ProtocolException("Missing delta property baseline");
                properties = old.Properties;
                if (n == -2)
                {
                    int changes = Count(r, Protocol.MaxProperties); List<string> changed = patchedKeys;
                    if (changed == null) patchedKeys = changed = new List<string>(); changed.Clear();
                    if (changes > 0) properties = new Dictionary<string, Atom>(properties, StringComparer.Ordinal);
                    for (int i = 0; i < changes; i++) { string property = Text(r); changed.Add(property); properties[property] = ReadValue(r, immutable); }
                    changed.Sort(StringComparer.Ordinal);
                    for (int i = 1; i < changed.Count; i++) if (changed[i] == changed[i - 1]) throw new ProtocolException("Duplicate delta property");
                    int removed = Count(r, Protocol.MaxProperties);
                    if (removed > 0 && changes == 0) properties = new Dictionary<string, Atom>(properties, StringComparer.Ordinal);
                    for (int i = 0; i < removed; i++)
                    {
                        string property = Text(r);
                        if (changed.BinarySearch(property, StringComparer.Ordinal) >= 0 || !properties.Remove(property)) throw new ProtocolException("Invalid removed delta property");
                    }
                    if (properties.Count > Protocol.MaxProperties) throw new ProtocolException("Too many delta properties");
                    changed.Clear();
                }
            }
            else
            {
                if (n < 0 || n > Protocol.MaxProperties) throw new ProtocolException("Invalid count");
                properties = new Dictionary<string, Atom>(n, StringComparer.Ordinal);
            }
            Entity e = new Entity(properties) { Id = id, Key = key, Prefab = prefab, Parent = parent, Kind = kind, Active = active, Position = position, Rotation = rotation, Velocity = velocity, AngularVelocity = angularVelocity };
            for (int i = 0; i < n; i++) e.Properties.Add(Text(r), ReadValue(r, immutable));
            return e;
        }
        public static void Player(BinaryWriter w, Player p)
        {
            Text(w, p.Id); Text(w, p.Name); Text(w, p.Held); Text(w, p.Seat); Vector(w, p.Position); Rotation(w, p.Rotation); w.Write(p.Pitch); w.Write(p.SleepReady); w.Write(p.LastPose);
        }
        public static Player Player(BinaryReader r)
        {
            Player p = new Player { Id = Text(r), Name = Text(r), Held = Text(r), Seat = Text(r), Position = Vector(r), Rotation = Rotation(r), Pitch = r.ReadSingle(), SleepReady = r.ReadBoolean(), LastPose = r.ReadInt64() };
            if (p.Id == "" || !V3.Finite(p.Pitch)) throw new ProtocolException("Invalid player"); return p;
        }
        public static byte[] World(World world)
        {
            byte[] bytes = Make(delegate(BinaryWriter w) { WriteWorld(w, world); });
            if (bytes.Length > Protocol.MaxSnapshot) throw new ProtocolException("World snapshot too large"); return bytes;
        }
        public static void WriteWorld(BinaryWriter w, World world)
        {
            WriteWorld(w, world, null);
        }
        public static void WriteDelta(BinaryWriter w, World delta, World baseline)
        {
            if (baseline == null || delta.Id != baseline.Id || delta.GameHash != baseline.GameHash || delta.SchemaHash != baseline.SchemaHash || delta.Revision < baseline.Revision) throw new ProtocolException("Invalid delta baseline");
            WriteWorld(w, delta, baseline);
        }
        private static void WriteWorld(BinaryWriter w, World world, World baseline)
        {
                w.Write(Protocol.Version); Text(w, world.Id); Text(w, world.GameHash); Text(w, world.SchemaHash); w.Write(world.Revision); w.Write(world.Money); w.Write(world.SleepEpoch);
                List<string> keys = new List<string>(world.Entities.Keys); keys.Sort(StringComparer.Ordinal);
                w.Write(keys.Count); foreach (string key in keys)
                {
                    Entity e = world.Entities[key], old = null;
                    if (baseline != null && e.Key != "__deleted") baseline.Entities.TryGetValue(key, out old);
                    WriteEntity(w, e, old != null && object.ReferenceEquals(e.Properties, old.Properties), old);
                }
                keys = new List<string>(world.Players.Keys); keys.Sort(StringComparer.Ordinal);
                w.Write(keys.Count); foreach (string key in keys) Player(w, world.Players[key]);
        }
        public static World World(byte[] bytes)
        {
            return DecodeWorld(bytes, false);
        }
        // Only for callers that replace snapshots/atoms instead of mutating their fields.
        public static World ImmutableWorld(byte[] bytes)
        {
            return DecodeWorld(bytes, true);
        }
        private static World DecodeWorld(byte[] bytes, bool immutable)
        {
            if (bytes == null || bytes.Length > Protocol.MaxSnapshot) throw new ProtocolException("Snapshot too large");
            World world = null;
            Read(bytes, delegate(BinaryReader r) { world = ReadWorld(r, null, false, immutable); }); return world;
        }
        public static World ReadWorld(BinaryReader r)
        {
            return ReadWorld(r, null);
        }
        public static World ReadDelta(BinaryReader r, World baseline)
        {
            if (baseline == null) throw new ArgumentNullException("baseline");
            return ReadWorld(r, baseline);
        }
        internal static World ReadDelta(BinaryReader r, World baseline, DeltaReadBuffer buffer)
        {
            if (baseline == null) throw new ArgumentNullException("baseline");
            return ReadWorld(r, baseline, false, true, buffer);
        }
        private static World ReadWorld(BinaryReader r, World baseline)
        {
            return ReadWorld(r, baseline, false, baseline != null);
        }
        internal static World ReadJournalRecord(BinaryReader r, World baseline)
        {
            return ReadWorld(r, baseline, true, true);
        }
        private static World ReadWorld(BinaryReader r, World baseline, bool skipCheckpointed, bool immutable)
        {
            return ReadWorld(r, baseline, skipCheckpointed, immutable, null);
        }
        private static World ReadWorld(BinaryReader r, World baseline, bool skipCheckpointed, bool immutable, DeltaReadBuffer buffer)
        {
                if (r.ReadInt32() != Protocol.Version) throw new ProtocolException("Snapshot version mismatch");
                World world = buffer == null ? new World() : buffer.World;
                if (object.ReferenceEquals(world, baseline)) throw new ProtocolException("Delta container aliases its baseline");
                world.Id = Text(r); world.GameHash = Text(r); world.SchemaHash = Text(r); world.Revision = r.ReadInt64(); world.Money = r.ReadDouble(); world.SleepEpoch = r.ReadInt64();
                if (world.Id == "" || world.Revision < 0 || world.SleepEpoch < 0 || double.IsNaN(world.Money) || double.IsInfinity(world.Money)) throw new ProtocolException("Invalid world");
                if (baseline != null && (world.Id != baseline.Id || world.GameHash != baseline.GameHash || world.SchemaHash != baseline.SchemaHash || (!skipCheckpointed && world.Revision < baseline.Revision)))
                {
                    if (skipCheckpointed) throw new JournalIdentityException();
                    throw new ProtocolException("Invalid delta identity");
                }
                // CRC and identity are already checked. Older records are superseded by the
                // checkpoint; their property references need not exist in the current world.
                if (skipCheckpointed && world.Revision <= baseline.Revision) { r.BaseStream.Position = r.BaseStream.Length; return world; }
                int n = Count(r, Protocol.MaxEntities);
                // The validated count is already in the frame. Avoid allocating
                // successively larger bucket/entry arrays for every world delta.
                if (buffer != null) { buffer.Clear(); buffer.EnsureCapacity(n); }
                else if (n > 0) world.Entities = new Dictionary<string, Entity>(n, StringComparer.Ordinal);
                for (int i = 0; i < n; i++) { Entity e = ReadEntity(r, baseline, immutable); world.Entities.Add(e.Id, e); }
                n = Count(r, Protocol.MaxPlayers);
                for (int i = 0; i < n; i++) { Player p = Player(r); world.Players.Add(p.Id, p); }
                return world;
        }
        public static byte[] Command(Command c)
        {
            return Make(delegate(BinaryWriter w) { w.Write(c.Sequence); w.Write((byte)c.Kind); Text(w, c.Target); Text(w, c.Argument); w.Write(c.Number); Vector(w, c.Vector); });
        }
        public static Command Command(byte[] data)
        {
            Command c = null; Read(data, delegate(BinaryReader r) { c = ReadCommand(r); }); return c;
        }
        public static Command Command(PacketView packet) { Command c = null; Read(packet, delegate(BinaryReader r) { c = ReadCommand(r); }); return c; }
        private static Command ReadCommand(BinaryReader r)
        {
            Command c = new Command { Sequence = r.ReadInt64(), Kind = (CommandKind)r.ReadByte(), Target = Text(r), Argument = Text(r), Number = r.ReadDouble(), Vector = Vector(r) };
            if (!Enum.IsDefined(typeof(CommandKind), c.Kind) || c.Sequence <= 0 || double.IsNaN(c.Number) || double.IsInfinity(c.Number)) throw new ProtocolException("Invalid command"); return c;
        }
        public static byte[] Result(CommandResult a) { return Make(delegate(BinaryWriter w) { w.Write(a.Sequence); w.Write(a.Revision); w.Write(a.Accepted); Text(w, a.Reason); }); }
        public static CommandResult Result(byte[] data)
        {
            CommandResult a = null; Read(data, delegate(BinaryReader r) { a = ReadResult(r); }); return a;
        }
        public static CommandResult Result(PacketView packet) { CommandResult a = null; Read(packet, delegate(BinaryReader r) { a = ReadResult(r); }); return a; }
        private static CommandResult ReadResult(BinaryReader r) { return new CommandResult { Sequence = r.ReadInt64(), Revision = r.ReadInt64(), Accepted = r.ReadBoolean(), Reason = Text(r) }; }
        private static void WritePose(BinaryWriter w, Pose p) { Text(w, p.Id); w.Write(p.Sequence); Vector(w, p.Position); Rotation(w, p.Rotation); w.Write(p.Pitch); }
        public static byte[] Pose(Pose p) { return Make(delegate(BinaryWriter w) { WritePose(w, p); }); }
        public static byte[] PoseFrame(Pose p) { return MakeFrame(MessageKind.Pose, delegate(BinaryWriter w) { WritePose(w, p); }); }
        public static Pose Pose(byte[] data)
        {
            Pose p = null; Read(data, delegate(BinaryReader r) { p = ReadPose(r); }); return p;
        }
        public static Pose Pose(PacketView packet) { Pose p = null; Read(packet, delegate(BinaryReader r) { p = ReadPose(r); }); return p; }
        private static Pose ReadPose(BinaryReader r)
        {
            Pose p = new Pose { Id = Text(r), Sequence = r.ReadInt64(), Position = Vector(r), Rotation = Rotation(r), Pitch = r.ReadSingle() }; if (!V3.Finite(p.Pitch)) throw new ProtocolException("Invalid pitch"); return p;
        }
        public static byte[] Motion(MotionFrame frame)
        {
            return Make(delegate(BinaryWriter w) { WriteMotion(w, frame); });
        }
        private static void WriteMotion(BinaryWriter w, MotionFrame frame) { w.Write(frame.Sequence); w.Write(frame.Bodies.Count); foreach (BodyPose b in frame.Bodies) { Text(w, b.Id); Vector(w, b.Position); Rotation(w, b.Rotation); Vector(w, b.Velocity); } }
        public static byte[] MotionPacket(MotionFrame frame) { return MakeFrame(MessageKind.Motion, delegate(BinaryWriter w) { WriteMotion(w, frame); }); }
        public static MotionFrame Motion(byte[] data)
        {
            MotionFrame f = null; Read(data, delegate(BinaryReader r) { f = ReadMotion(r); }); return f;
        }
        public static MotionFrame Motion(PacketView packet) { MotionFrame f = null; Read(packet, delegate(BinaryReader r) { f = ReadMotion(r); }); return f; }
        private static MotionFrame ReadMotion(BinaryReader r)
        {
            long sequence = r.ReadInt64(); int n = Count(r, 16); MotionFrame f = new MotionFrame(n) { Sequence = sequence }; for (int i = 0; i < n; i++) f.Bodies.Add(new BodyPose { Id = Text(r), Position = Vector(r), Rotation = Rotation(r), Velocity = Vector(r) }); return f;
        }
        private static readonly uint[] CrcTable = MakeCrcTable();
        private static uint[] MakeCrcTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < table.Length; i++) { uint n = i; for (int k = 0; k < 8; k++) n = (n >> 1) ^ ((n & 1) != 0 ? 0xedb88320u : 0); table[i] = n; }
            return table;
        }
        public static uint Crc(byte[] bytes)
        {
            return ~CrcAppend(0xffffffff, bytes, 0, bytes.Length);
        }
        public static uint CrcAppend(uint crc, byte[] bytes, int offset, int count)
        {
            int end = offset + count; for (int i = offset; i < end; i++) crc = (crc >> 8) ^ CrcTable[(crc ^ bytes[i]) & 255]; return crc;
        }
        public static string Hash(byte[] bytes)
        {
            using (SHA256 s = SHA256.Create()) return BitConverter.ToString(s.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }

    public sealed class SnapshotReceiver
    {
        private MemoryStream stream;
        private int expected;
        private string hash;
        public void Begin(int bytes, string checksum)
        {
            if (bytes <= 0 || bytes > Protocol.MaxSnapshot || checksum == null || checksum.Length != 64) throw new ProtocolException("Invalid snapshot declaration");
            Reset(); expected = bytes; hash = checksum; stream = new MemoryStream(Math.Min(bytes, 1048576));
        }
        public void Chunk(int offset, byte[] bytes)
        {
            Chunk(offset, bytes, 0, bytes.Length);
        }
        public void Chunk(int offset, byte[] bytes, int sourceOffset, int length)
        {
            if (bytes == null || sourceOffset < 0 || length <= 0 || sourceOffset > bytes.Length - length || stream == null || offset != stream.Length || length > Protocol.SnapshotChunk || offset > expected - length) throw new ProtocolException("Unexpected snapshot chunk");
            stream.Write(bytes, sourceOffset, length);
        }
        public byte[] End()
        {
            if (stream == null || stream.Length != expected) throw new ProtocolException("Incomplete snapshot");
            byte[] bytes = stream.ToArray();
            if (Codec.Hash(bytes) != hash) { Reset(); throw new ProtocolException("Snapshot hash mismatch"); }
            Reset(); return bytes;
        }
        public void Reset() { if (stream != null) stream.Dispose(); stream = null; expected = 0; hash = null; }
    }
}
