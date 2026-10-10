using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SummerCoop.Core
{
    public static class AtomicFile
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushFileBuffers(IntPtr file);
        public static void Flush(FileStream file)
        {
            file.Flush();
#pragma warning disable 618
            if (Environment.OSVersion.Platform == PlatformID.Win32NT && !FlushFileBuffers(file.Handle)) throw new IOException("Disk flush failed");
#pragma warning restore 618
        }
        public static void Write(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temp = path + ".pending";
            using (FileStream file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, false)) { file.Write(bytes, 0, bytes.Length); Flush(file); }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
        }
        private sealed class RecordStream : Stream
        {
            private readonly Stream output;
            [ThreadStatic] private static byte[] writeBuffer;
            private readonly byte[] buffer;
            private int buffered;
            public uint Checksum = 0xffffffff;
            public long Count;
            public RecordStream(Stream output) { this.output = output; buffer = writeBuffer ?? (writeBuffer = new byte[65536]); }
            private void Drain()
            {
                if (buffered == 0) return;
                Checksum = Codec.CrcAppend(Checksum, buffer, 0, buffered);
                output.Write(buffer, 0, buffered); buffered = 0;
            }
            public override void Write(byte[] bytes, int offset, int count)
            {
                Count += count; if (Count > Protocol.MaxSnapshot) throw new ProtocolException("World snapshot too large");
                while (count > 0)
                {
                    int n = Math.Min(count, buffer.Length - buffered);
                    Array.Copy(bytes, offset, buffer, buffered, n); buffered += n; offset += n; count -= n;
                    if (buffered == buffer.Length) Drain();
                }
            }
            public override void WriteByte(byte value)
            {
                if (++Count > Protocol.MaxSnapshot) throw new ProtocolException("World snapshot too large");
                buffer[buffered++] = value; if (buffered == buffer.Length) Drain();
            }
            public override void Flush() { Drain(); output.Flush(); }
            public override bool CanRead { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override long Length { get { return Count; } }
            public override long Position { get { return Count; } set { throw new NotSupportedException(); } }
            public override int Read(byte[] b, int o, int c) { throw new NotSupportedException(); }
            public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); }
            public override void SetLength(long n) { throw new NotSupportedException(); }
        }
        public static void WriteWorld(string path, World world)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))); string temp = path + ".pending";
            using (FileStream file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, false))
            {
                WriteWorldRecord(file, world); Flush(file);
            }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
        }
        public static void WriteWorldRecord(Stream output, World world)
        {
            WriteWorldRecord(output, world, null);
        }
        internal static void WriteWorldRecord(Stream output, World world, World baseline)
        {
            long start = output.Position;
            output.Write(new byte[8], 0, 8);
            RecordStream record = new RecordStream(output); BinaryWriter writer = new WireWriter(record);
            if (baseline == null) Codec.WriteWorld(writer, world); else Codec.WriteDelta(writer, world, baseline); writer.Flush();
            long end = output.Position; output.Position = start;
            BinaryWriter header = new BinaryWriter(output); header.Write(checked((int)record.Count)); header.Write(~record.Checksum); header.Flush();
            output.Position = end;
        }
    }
    public sealed class CheckpointStore : ICommitLog, IDisposable
    {
        private readonly string directory;
        private World baseline;
        private World journalBaseline;
        private long journalEnd = -1;
        private Thread checkpointWorker;
        private volatile Exception checkpointError;
        private readonly Queue<World> pendingNative = new Queue<World>();
        private World pendingTail;
        private readonly AutoResetEvent journalWake = new AutoResetEvent(false);
        private readonly ManualResetEvent journalIdle = new ManualResetEvent(true);
        private Thread journalWorker;
        private volatile Exception journalError;
        private volatile bool journalStopping;
        public bool ImmutableEntities { get; set; }
        public CheckpointStore(string directory) { this.directory = directory; Directory.CreateDirectory(directory); }
        public void Checkpoint(World world)
        {
            FinishBackgroundCheckpoint();
            FlushNative();
            AtomicFile.WriteWorld(Path.Combine(directory, "world.scw"), world);
            // A journal containing old records is safe: recovery ignores <= checkpoint revision.
            AtomicFile.Write(Path.Combine(directory, "world.scj"), new byte[0]);
            baseline = ImmutableEntities ? world.ReferenceSnapshot() : world.Clone();
            journalBaseline = ImmutableEntities ? baseline.ReferenceSnapshot() : null;
            journalEnd = -1;
        }
        public bool BeginCheckpoint(World world)
        {
            CheckBackground();
            if (!ImmutableEntities) throw new InvalidOperationException("Background checkpoint requires immutable entities");
            if (checkpointWorker != null && checkpointWorker.IsAlive) return false;
            World captured = world.ReferenceSnapshot();
            checkpointWorker = new Thread(delegate()
            {
                try
                {
                    // Keep the append-only journal: records newer than this snapshot remain durable.
                    // A normal checkpoint on clean exit compacts it after this writer finishes.
                    AtomicFile.WriteWorld(Path.Combine(directory, "world.scw"), captured);
                }
                catch (Exception e) { checkpointError = e; }
            });
            checkpointWorker.IsBackground = true; checkpointWorker.Start(); return true;
        }
        public void CheckBackground()
        {
            Thread worker = checkpointWorker;
            if (worker != null && !worker.IsAlive) { worker.Join(); checkpointWorker = null; }
            if (checkpointError != null) throw new IOException("Background checkpoint failed", checkpointError);
            if (journalError != null) throw new IOException("Background journal failed", journalError);
        }
        public void FinishBackgroundCheckpoint()
        {
            Thread worker = checkpointWorker; if (worker != null) worker.Join();
            CheckBackground();
        }
        public void Commit(World world, string player, Command command)
        {
            CheckBackground();
            FlushNative();
            if (baseline == null) { Checkpoint(world); return; }
            World delta = Difference(baseline, world, ImmutableEntities);
            Append(delta);
            ApplyDelta(baseline, delta, ImmutableEntities);
        }
        public void CommitNative(World world)
        {
            CheckBackground();
            if (!ImmutableEntities || baseline == null || journalEnd >= 0) throw new InvalidOperationException("Native journal requires an initialized immutable checkpoint");
            World delta = Difference(baseline, world, true);
            lock (pendingNative)
            {
                if (journalStopping) throw new IOException("Native journal is closed");
                journalIdle.Reset();
                // Native captures describe state, not actions. Merge only unwritten captures;
                // durable commands still drain all native state before their own append.
                if (pendingNative.Count >= 4) ApplyDelta(pendingTail, delta, true);
                else { pendingNative.Enqueue(delta); pendingTail = delta; }
                if (journalWorker == null) { journalWorker = new Thread(WriteNative); journalWorker.IsBackground = true; journalWorker.Start(); }
            }
            ApplyDelta(baseline, delta, true); journalWake.Set();
        }
        private void WriteNative()
        {
            try
            {
                while (true)
                {
                    World delta = null;
                    lock (pendingNative) { if (pendingNative.Count > 0) { delta = pendingNative.Dequeue(); if (pendingNative.Count == 0) pendingTail = null; } else journalIdle.Set(); }
                    if (delta != null) { Append(delta); continue; }
                    if (journalStopping) return;
                    journalWake.WaitOne();
                }
            }
            catch (Exception e) { journalError = e; journalIdle.Set(); }
        }
        public void FlushNative()
        {
            journalIdle.WaitOne(); CheckBackground();
        }
        private void Append(World delta)
        {
            using (FileStream file = new FileStream(Path.Combine(directory, "world.scj"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 65536, false))
            {
                if (journalEnd >= 0 && file.Length > journalEnd) file.SetLength(journalEnd);
                file.Position = file.Length; AtomicFile.WriteWorldRecord(file, delta, journalBaseline); AtomicFile.Flush(file);
            }
            if (journalBaseline != null) ApplyDelta(journalBaseline, delta, true);
            journalEnd = -1;
        }
        public void Dispose()
        {
            try { FlushNative(); FinishBackgroundCheckpoint(); }
            finally { journalStopping = true; journalWake.Set(); if (journalWorker != null) journalWorker.Join(); journalWake.Close(); journalIdle.Close(); }
        }
        public World Recover()
        {
            World world = LoadCheckpoint(Path.Combine(directory, "world.scw"), ImmutableEntities);
            if (world == null) world = LoadCheckpoint(Path.Combine(directory, "world.scw.bak"), ImmutableEntities);
            if (world == null) return null;
            string journal = Path.Combine(directory, "world.scj");
            if (File.Exists(journal))
            {
                journalEnd = 0;
                using (FileStream file = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (BinaryReader r = new BinaryReader(file))
                {
                    while (file.Position < file.Length)
                    {
                        byte[] bytes;
                        if (!TryRecord(r, out bytes)) break; // An interrupted final append is discarded.
                        World next;
                        try { next = null; Codec.Read(bytes, delegate(BinaryReader reader) { next = Codec.ReadJournalRecord(reader, world); }); }
                        catch (JournalIdentityException) { throw; }
                        catch (Exception) { break; }
                        if (next.Id != world.Id || next.GameHash != world.GameHash) throw new ProtocolException("Journal belongs to another world");
                        if (next.Revision > world.Revision) ApplyDelta(world, next);
                        journalEnd = file.Position;
                    }
                }
            }
            baseline = ImmutableEntities ? world.ReferenceSnapshot() : world.Clone(); journalBaseline = ImmutableEntities ? baseline.ReferenceSnapshot() : null; return world;
        }
        private static World LoadCheckpoint(string path, bool immutable)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using (FileStream file = File.OpenRead(path)) using (BinaryReader r = new BinaryReader(file))
                {
                    byte[] bytes; if (!TryRecord(r, out bytes) || file.Position != file.Length) return null; return immutable ? Codec.ImmutableWorld(bytes) : Codec.World(bytes);
                }
            }
            catch (Exception e) { if (e is IOException || e is ArgumentException) return null; throw; }
        }
        private static bool TryRecord(BinaryReader r, out byte[] bytes)
        {
            bytes = null;
            try
            {
                int n = Codec.Count(r, Protocol.MaxSnapshot); uint crc = r.ReadUInt32(); bytes = r.ReadBytes(n);
                return bytes.Length == n && Codec.Crc(bytes) == crc;
            }
            catch (EndOfStreamException) { return false; }
            catch (ProtocolException) { return false; }
        }
        public static World Difference(World before, World after)
        {
            return Difference(before, after, false);
        }
        public static World Difference(World before, World after, bool immutableEntities)
        {
            World delta = new World { Id = after.Id, GameHash = after.GameHash, SchemaHash = after.SchemaHash, Revision = after.Revision, Money = after.Money, SleepEpoch = after.SleepEpoch, Changes = after.Changes, ChangeSequence = after.ChangeSequence };
            if (immutableEntities && after.Changes != null && object.ReferenceEquals(before.Changes, after.Changes) && before.ChangeSequence >= after.Changes.PrunedThrough)
            {
                foreach (string id in after.Changes.Since(before.ChangeSequence))
                {
                    Entity e, old;
                    if (after.Entities.TryGetValue(id, out e)) { if (!before.Entities.TryGetValue(id, out old) || !Equal(old, e)) delta.Entities.Add(id, e); }
                    else if (before.Entities.ContainsKey(id)) delta.Entities.Add(id, new Entity { Id = id, Key = "__deleted" });
                }
            }
            else
            {
                foreach (Entity e in after.Entities.Values)
                {
                    Entity old;
                    if (!before.Entities.TryGetValue(e.Id, out old) || !Equal(old, e)) delta.Entities.Add(e.Id, immutableEntities ? e : e.Clone());
                }
                foreach (string id in before.Entities.Keys) if (!after.Entities.ContainsKey(id)) delta.Entities.Add(id, new Entity { Id = id, Key = "__deleted" });
            }
            foreach (Player p in after.Players.Values) delta.Players.Add(p.Id, p.Clone());
            return delta;
        }
        public static bool Equal(Entity a, Entity b)
        {
            if (object.ReferenceEquals(a, b)) return true;
            if (a.Active != b.Active || a.Parent != b.Parent || a.Kind != b.Kind || a.Key != b.Key || a.Prefab != b.Prefab || a.Properties.Count != b.Properties.Count) return false;
            if (a.Position.X != b.Position.X || a.Position.Y != b.Position.Y || a.Position.Z != b.Position.Z || a.Rotation.X != b.Rotation.X || a.Rotation.Y != b.Rotation.Y || a.Rotation.Z != b.Rotation.Z || a.Rotation.W != b.Rotation.W) return false;
            if (a.Velocity.X != b.Velocity.X || a.Velocity.Y != b.Velocity.Y || a.Velocity.Z != b.Velocity.Z || a.AngularVelocity.X != b.AngularVelocity.X || a.AngularVelocity.Y != b.AngularVelocity.Y || a.AngularVelocity.Z != b.AngularVelocity.Z) return false;
            foreach (KeyValuePair<string, Atom> p in a.Properties) { Atom other; if (!b.Properties.TryGetValue(p.Key, out other) || !p.Value.Same(other)) return false; }
            return true;
        }
        public static void ApplyDelta(World world, World delta)
        {
            ApplyDelta(world, delta, false);
        }
        public static void ApplyDelta(World world, World delta, bool immutableEntities)
        {
            if (world.Id != delta.Id || delta.Revision < world.Revision) throw new ProtocolException("Invalid delta revision");
            foreach (Entity e in delta.Entities.Values) { if (e.Key == "__deleted") world.Entities.Remove(e.Id); else world.Entities[e.Id] = immutableEntities ? e : e.Clone(); }
            world.Players.Clear(); foreach (Player p in delta.Players.Values) world.Players[p.Id] = p.Clone();
            world.Revision = delta.Revision; world.Money = delta.Money; world.SleepEpoch = delta.SleepEpoch;
            world.Changes = delta.Changes; world.ChangeSequence = delta.ChangeSequence;
        }
    }
}
