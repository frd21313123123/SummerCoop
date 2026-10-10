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
    public enum MessageKind : byte { Hello = 1, Welcome, Reject, SaveBundle, SnapshotBegin, SnapshotChunk, SnapshotEnd, Ready, WorldDelta, Command, Result, Pose, Motion, Goodbye, Ping, Resync }
    public sealed class Packet { public MessageKind Kind; public byte[] Payload; }

    public static class Codec
    {
        private const uint Magic = 0x504F4353;
        [ThreadStatic] private static byte[] textBuffer;
        [ThreadStatic] private static List<string> entityKeys;
        [ThreadStatic] private static byte[] readTextBuffer;
        [ThreadStatic] private static Dictionary<uint, CachedText> readTextCache;
        private sealed class CachedText { public byte[] Bytes; public string Value; }
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        public static byte[] Packet(MessageKind kind, byte[] payload)
        {
            if (payload == null || payload.Length > Protocol.MaxFrame - 15) throw new ProtocolException("Packet too large");
            using (MemoryStream s = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(s))
            {
                w.Write(Magic); w.Write((ushort)Protocol.Version); w.Write((byte)kind);
                w.Write(payload.Length); w.Write(Crc(payload)); w.Write(payload); return s.ToArray();
            }
        }
        public static Packet Open(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 15 || bytes.Length > Protocol.MaxFrame) throw new ProtocolException("Invalid frame size");
            using (MemoryStream s = new MemoryStream(bytes))
            using (BinaryReader r = new BinaryReader(s))
            {
                if (r.ReadUInt32() != Magic || r.ReadUInt16() != Protocol.Version) throw new ProtocolException("Protocol mismatch");
                MessageKind kind = (MessageKind)r.ReadByte();
                if (!Enum.IsDefined(typeof(MessageKind), kind)) throw new ProtocolException("Unknown packet");
                int n = r.ReadInt32(); uint crc = r.ReadUInt32();
                if (n != bytes.Length - 15) throw new ProtocolException("Invalid payload length");
                byte[] payload = r.ReadBytes(n);
                if (Crc(payload) != crc) throw new ProtocolException("Packet checksum mismatch");
                return new Packet { Kind = kind, Payload = payload };
            }
        }
        public static byte[] Make(Action<BinaryWriter> write)
        {
            using (MemoryStream s = new MemoryStream()) using (BinaryWriter w = new BinaryWriter(s)) { write(w); return s.ToArray(); }
        }
        public static void Read(byte[] data, Action<BinaryReader> read)
        {
            using (MemoryStream s = new MemoryStream(data)) using (BinaryReader r = new BinaryReader(s))
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
            if (n == 0) return "";
            if (readTextBuffer == null || readTextBuffer.Length < n) readTextBuffer = new byte[Math.Max(256, n)];
            int offset = 0; while (offset < n) { int count = r.Read(readTextBuffer, offset, n - offset); if (count == 0) throw new EndOfStreamException(); offset += count; }
            if (n > 256) return StrictUtf8.GetString(readTextBuffer, 0, n);
            uint hash = 2166136261; for (int i = 0; i < n; i++) hash = (hash ^ readTextBuffer[i]) * 16777619;
            if (readTextCache == null) readTextCache = new Dictionary<uint, CachedText>();
            CachedText cached;
            if (readTextCache.TryGetValue(hash, out cached) && cached.Bytes.Length == n)
            {
                bool same = true; for (int i = 0; i < n; i++) if (cached.Bytes[i] != readTextBuffer[i]) { same = false; break; }
                if (same) return cached.Value;
            }
            string value = StrictUtf8.GetString(readTextBuffer, 0, n);
            if (readTextCache.Count >= 8192) readTextCache.Clear();
            byte[] bytes = new byte[n]; Array.Copy(readTextBuffer, bytes, n); readTextCache[hash] = new CachedText { Bytes = bytes, Value = value }; return value;
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
            AtomKind kind = (AtomKind)r.ReadByte();
            switch (kind)
            {
                case AtomKind.Number: case AtomKind.Boolean:
                    double n = r.ReadDouble();
                    if (double.IsNaN(n) || double.IsInfinity(n)) throw new ProtocolException("Invalid number");
                    if (kind == AtomKind.Boolean && n != 0 && n != 1) throw new ProtocolException("Invalid boolean");
                    return new Atom { Kind = kind, Number = n };
                case AtomKind.Text: case AtomKind.Reference: return new Atom { Kind = kind, Text = Text(r) };
                case AtomKind.Vector: return Atom.V(Vector(r));
                case AtomKind.Rotation: return Atom.Q(Rotation(r));
                default: throw new ProtocolException("Unknown value kind");
            }
        }
        public static void Entity(BinaryWriter w, Entity e)
        {
            Text(w, e.Id); Text(w, e.Key); Text(w, e.Prefab); Text(w, e.Parent); w.Write((byte)e.Kind); w.Write(e.Active);
            Vector(w, e.Position); Rotation(w, e.Rotation); Vector(w, e.Velocity); Vector(w, e.AngularVelocity);
            List<string> keys = entityKeys; if (keys == null) entityKeys = keys = new List<string>(); keys.Clear(); keys.AddRange(e.Properties.Keys); keys.Sort(StringComparer.Ordinal);
            w.Write(keys.Count); foreach (string key in keys) { Text(w, key); Value(w, e.Properties[key]); }
        }
        public static Entity Entity(BinaryReader r)
        {
            Entity e = new Entity { Id = Text(r), Key = Text(r), Prefab = Text(r), Parent = Text(r), Kind = (EntityKind)r.ReadByte(), Active = r.ReadBoolean(), Position = Vector(r), Rotation = Rotation(r), Velocity = Vector(r), AngularVelocity = Vector(r) };
            if (e.Id == "" || !Enum.IsDefined(typeof(EntityKind), e.Kind)) throw new ProtocolException("Invalid entity");
            int n = Count(r, Protocol.MaxProperties);
            for (int i = 0; i < n; i++) e.Properties.Add(Text(r), Value(r));
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
                w.Write(Protocol.Version); Text(w, world.Id); Text(w, world.GameHash); Text(w, world.SchemaHash); w.Write(world.Revision); w.Write(world.Money); w.Write(world.SleepEpoch);
                List<string> keys = new List<string>(world.Entities.Keys); keys.Sort(StringComparer.Ordinal);
                w.Write(keys.Count); foreach (string key in keys) Entity(w, world.Entities[key]);
                keys = new List<string>(world.Players.Keys); keys.Sort(StringComparer.Ordinal);
                w.Write(keys.Count); foreach (string key in keys) Player(w, world.Players[key]);
        }
        public static World World(byte[] bytes)
        {
            if (bytes == null || bytes.Length > Protocol.MaxSnapshot) throw new ProtocolException("Snapshot too large");
            World world = null;
            Read(bytes, delegate(BinaryReader r)
            {
                if (r.ReadInt32() != Protocol.Version) throw new ProtocolException("Snapshot version mismatch");
                world = new World { Id = Text(r), GameHash = Text(r), SchemaHash = Text(r), Revision = r.ReadInt64(), Money = r.ReadDouble(), SleepEpoch = r.ReadInt64() };
                if (world.Id == "" || world.Revision < 0 || world.SleepEpoch < 0 || double.IsNaN(world.Money) || double.IsInfinity(world.Money)) throw new ProtocolException("Invalid world");
                int n = Count(r, Protocol.MaxEntities);
                for (int i = 0; i < n; i++) { Entity e = Entity(r); world.Entities.Add(e.Id, e); }
                n = Count(r, Protocol.MaxPlayers);
                for (int i = 0; i < n; i++) { Player p = Player(r); world.Players.Add(p.Id, p); }
            }); return world;
        }
        public static byte[] Command(Command c)
        {
            return Make(delegate(BinaryWriter w) { w.Write(c.Sequence); w.Write((byte)c.Kind); Text(w, c.Target); Text(w, c.Argument); w.Write(c.Number); Vector(w, c.Vector); });
        }
        public static Command Command(byte[] data)
        {
            Command c = null;
            Read(data, delegate(BinaryReader r)
            {
                c = new Command { Sequence = r.ReadInt64(), Kind = (CommandKind)r.ReadByte(), Target = Text(r), Argument = Text(r), Number = r.ReadDouble(), Vector = Vector(r) };
                if (!Enum.IsDefined(typeof(CommandKind), c.Kind) || c.Sequence <= 0 || double.IsNaN(c.Number) || double.IsInfinity(c.Number)) throw new ProtocolException("Invalid command");
            }); return c;
        }
        public static byte[] Result(CommandResult a) { return Make(delegate(BinaryWriter w) { w.Write(a.Sequence); w.Write(a.Revision); w.Write(a.Accepted); Text(w, a.Reason); }); }
        public static CommandResult Result(byte[] data)
        {
            CommandResult a = null; Read(data, delegate(BinaryReader r) { a = new CommandResult { Sequence = r.ReadInt64(), Revision = r.ReadInt64(), Accepted = r.ReadBoolean(), Reason = Text(r) }; }); return a;
        }
        public static byte[] Pose(Pose p) { return Make(delegate(BinaryWriter w) { Text(w, p.Id); w.Write(p.Sequence); Vector(w, p.Position); Rotation(w, p.Rotation); w.Write(p.Pitch); }); }
        public static Pose Pose(byte[] data)
        {
            Pose p = null; Read(data, delegate(BinaryReader r) { p = new Pose { Id = Text(r), Sequence = r.ReadInt64(), Position = Vector(r), Rotation = Rotation(r), Pitch = r.ReadSingle() }; if (!V3.Finite(p.Pitch)) throw new ProtocolException("Invalid pitch"); }); return p;
        }
        public static byte[] Motion(MotionFrame frame)
        {
            return Make(delegate(BinaryWriter w) { w.Write(frame.Sequence); w.Write(frame.Bodies.Count); foreach (BodyPose b in frame.Bodies) { Text(w, b.Id); Vector(w, b.Position); Rotation(w, b.Rotation); Vector(w, b.Velocity); } });
        }
        public static MotionFrame Motion(byte[] data)
        {
            MotionFrame f = new MotionFrame(); Read(data, delegate(BinaryReader r) { f.Sequence = r.ReadInt64(); int n = Count(r, 16); for (int i = 0; i < n; i++) f.Bodies.Add(new BodyPose { Id = Text(r), Position = Vector(r), Rotation = Rotation(r), Velocity = Vector(r) }); }); return f;
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
            if (stream == null || offset != stream.Length || bytes.Length == 0 || bytes.Length > Protocol.SnapshotChunk || offset > expected - bytes.Length) throw new ProtocolException("Unexpected snapshot chunk");
            stream.Write(bytes, 0, bytes.Length);
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
