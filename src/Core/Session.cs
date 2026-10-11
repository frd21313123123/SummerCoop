using System;
using System.Collections.Generic;
using System.IO;

namespace SummerCoop.Core
{
    public enum SessionPhase { Connecting, ReceivingSave, Loading, ReceivingWorld, Playing, Closed }
    public enum TransferKind : byte { NativeSave, World, Delta }

    // All methods run on the Unity main thread. Transports only enqueue immutable frames.
    public sealed class Session : IDisposable
    {
        private sealed class Peer
        {
            public string Id, Name;
            public bool Admitted, Playing, Loading, AwaitingAck;
            public double Seen, CommandWindow;
            public int Commands;
            public long SentRevision = -1;
            public World Baseline;
            public Queue<Transfer> Transfers = new Queue<Transfer>();
        }
        private sealed class Transfer
        {
            public TransferKind Kind;
            public byte[] Bytes;
            public int Offset;
            public bool Started;
        }
        private readonly ITransport transport;
        private readonly Authority authority;
        private readonly Dictionary<string, Peer> peers = new Dictionary<string, Peer>(StringComparer.Ordinal);
        private readonly SnapshotReceiver receiver = new SnapshotReceiver();
        private readonly string gameHash, name, secret;
        private TransferKind receiving;
        private bool transferActive, disposed, sceneReady;
        private double now, lastPing, opened;
        private long commandSequence, poseSequence;
        private DeltaReadBuffer deltaReadBuffer;
        private bool deltaBufferInUse;
        public bool IsHost { get { return transport.IsHost; } }
        public ITransport Transport { get { return transport; } }
        public string LocalId { get; private set; }
        public SessionPhase Phase { get; private set; }
        public World World { get; private set; }
        public int LastSnapshotBytes { get; private set; }
        public long DeltaBytesSent { get; private set; }
        public Action<string> Status;
        public Action<byte[]> NativeSaveReceived;
        public Action<World, bool> WorldReceived;
        // Opt in only when the delta container is consumed during WorldReceived.
        // With this option its dictionaries are valid until that callback returns;
        // retain individual immutable entities or clone the delta to keep it.
        public bool ReuseDeltaContainers;
        public Action<MotionFrame> MotionReceived;
        public Action<Pose> PoseReceived;
        public Action<CommandResult> CommandCompleted;
        public Action<string> Ended;
        public Func<byte[]> PrepareNativeSave;
        public Action<bool> JoinBarrier;

        public Session(ITransport transport, Authority authority, string gameHash, string name, string secret, double time)
        {
            if (transport == null || gameHash == null || name == null) throw new ArgumentNullException();
            if (transport.IsHost && authority == null) throw new ArgumentException("Host authority missing");
            this.transport = transport; this.authority = authority; this.gameHash = gameHash;
            this.name = name.Length > 48 ? name.Substring(0, 48) : name;
            this.secret = secret ?? ""; now = opened = time;
            LocalId = IsHost ? "host" : ""; Phase = SessionPhase.Connecting;
            if (IsHost) { World = authority.World; authority.AddPlayer(LocalId, this.name); }
        }
        public void SceneLoaded(string schemaHash)
        {
            sceneReady = true;
            if (IsHost)
            {
                World = authority.World; World.SchemaHash = schemaHash;
                Phase = SessionPhase.Playing;
            }
            else
            {
                if (Phase != SessionPhase.Loading) throw new InvalidOperationException("Unexpected scene load");
                Send(transport.HostPeer, MessageKind.Ready, Codec.Make(delegate(BinaryWriter w) { w.Write((byte)1); Codec.Text(w, schemaHash); }), true);
                Phase = SessionPhase.ReceivingWorld;
            }
        }
        public void Update(double time)
        {
            if (disposed) return; now = time;
            transport.Update(); TransportEvent item;
            for (int n = 0; n < 256 && transport.Poll(out item); n++)
            {
                try { Handle(item); }
                catch (Exception e)
                {
                    if (Status != null) Status("Protocol: " + e.Message);
                    if (IsHost) Reject(item.Peer, "Invalid request"); else { Close("Invalid host data: " + e.Message); return; }
                }
                if (disposed) return;
            }
            foreach (Peer p in new List<Peer>(peers.Values))
            {
                if (now - p.Seen > (p.Playing ? 25 : 120)) { transport.Disconnect(p.Id); Remove(p.Id, "Connection timed out"); continue; }
                Pump(p);
            }
            if (!IsHost && peers.Count == 0 && now - opened > 20) { Close("Host connection timed out"); return; }
            if (now - lastPing >= 2)
            {
                lastPing = now;
                foreach (Peer p in peers.Values) Send(p.Id, MessageKind.Ping, new byte[0], true);
            }
        }
        private void Handle(TransportEvent item)
        {
            if (item.Kind == TransportEventKind.Error) { Close(item.Error); return; }
            if (item.Kind == TransportEventKind.Connected)
            {
                if (peers.ContainsKey(item.Peer)) return;
                Peer p = new Peer { Id = item.Peer, Seen = now }; peers.Add(p.Id, p);
                if (!IsHost)
                {
                    if (item.Peer != transport.HostPeer || peers.Count != 1) throw new ProtocolException("Unexpected host");
                    Send(p.Id, MessageKind.Hello, Codec.Make(delegate(BinaryWriter w) { Codec.Text(w, Protocol.ModVersion); Codec.Text(w, gameHash); Codec.Text(w, name); Codec.Text(w, secret); }), true);
                }
                return;
            }
            if (item.Kind == TransportEventKind.Disconnected) { Remove(item.Peer, item.Error); return; }
            Peer peer; if (!peers.TryGetValue(item.Peer, out peer)) throw new ProtocolException("Unadmitted connection");
            PacketView packet = Codec.View(item.Data); peer.Seen = now;
            if (!item.Reliable && packet.Kind != MessageKind.Pose && packet.Kind != MessageKind.Motion) throw new ProtocolException("Unreliable control message");
            if (packet.Kind == MessageKind.Ping) { if (packet.Length != 0) throw new ProtocolException("Invalid ping"); return; }
            if (packet.Kind == MessageKind.Goodbye) { Remove(peer.Id, ReadText(packet)); transport.Disconnect(peer.Id); return; }
            if (IsHost) HandleHost(peer, packet); else HandleClient(peer, packet);
        }
        private void HandleHost(Peer p, PacketView packet)
        {
            if (packet.Kind == MessageKind.Hello)
            {
                if (p.Admitted || !sceneReady) { Reject(p.Id, "Host world is not ready"); return; }
                string mod = "", hash = "", playerName = "", password = "";
                Codec.Read(packet, delegate(BinaryReader r) { mod = Codec.Text(r); hash = Codec.Text(r); playerName = Codec.Text(r); password = Codec.Text(r); });
                if (mod != Protocol.ModVersion || hash != gameHash) { Reject(p.Id, "Different game or mod build"); return; }
                if (password != secret) { Reject(p.Id, "Incorrect session password"); return; }
                if (playerName.Length == 0 || playerName.Length > 48 || World.Players.Count >= Protocol.MaxPlayers) { Reject(p.Id, "Invalid player or session full"); return; }
                p.Admitted = true; p.Name = playerName; p.Loading = true;
                authority.AddPlayer(p.Id, playerName); World = authority.World; World.Revision++;
                if (JoinBarrier != null) JoinBarrier(true);
                Send(p.Id, MessageKind.Welcome, Codec.Make(delegate(BinaryWriter w) { Codec.Text(w, p.Id); Codec.Text(w, World.Id); Codec.Text(w, World.SchemaHash); }), true);
                if (PrepareNativeSave == null) throw new InvalidOperationException("Native save adapter missing");
                Queue(p, TransferKind.NativeSave, PrepareNativeSave());
                return;
            }
            if (!p.Admitted) throw new ProtocolException("Handshake required");
            if (packet.Kind == MessageKind.Ready)
            {
                byte stage = 0; string schema = ""; long revision = -1;
                Codec.Read(packet, delegate(BinaryReader r) { stage = r.ReadByte(); if (stage == 1) schema = Codec.Text(r); else if (stage == 2) revision = r.ReadInt64(); else throw new ProtocolException("Unknown ready stage"); });
                if (stage == 1)
                {
                    if (!p.Loading || p.AwaitingAck || schema != World.SchemaHash) { Reject(p.Id, "Scene schema mismatch"); return; }
                    QueueWorld(p); p.AwaitingAck = true;
                }
                else
                {
                    if (!p.AwaitingAck || revision != p.SentRevision) throw new ProtocolException("Snapshot acknowledgement mismatch");
                    p.AwaitingAck = false; p.Loading = false; p.Playing = true;
                    if (Status != null) Status(p.Name + " connected");
                    UpdateBarrier(); Publish();
                }
                return;
            }
            if (!p.Playing && packet.Kind == MessageKind.Pose) return; // UDP can overtake Ready on the reliable channel.
            if (!p.Playing) throw new ProtocolException("World not acknowledged");
            switch (packet.Kind)
            {
                case MessageKind.Command:
                    if (now - p.CommandWindow >= 1) { p.CommandWindow = now; p.Commands = 0; }
                    if (++p.Commands > 64) throw new ProtocolException("Command rate exceeded");
                    Command c = Codec.Command(packet);
                    CommandResult result = authority.Execute(p.Id, c); World = authority.World;
                    Send(p.Id, MessageKind.Result, Codec.Result(result), true); Publish(); break;
                case MessageKind.Pose:
                    Pose pose = Codec.Pose(packet); pose.Id = p.Id;
                    if (authority.SetPose(p.Id, pose)) { if (PoseReceived != null) PoseReceived(pose); Broadcast(MessageKind.Pose, Codec.Pose(pose), false, p.Id); }
                    break;
                case MessageKind.Resync:
                    if (packet.Length != 0 || p.Transfers.Count != 0) break;
                    QueueWorld(p); break;
                default: throw new ProtocolException("Unexpected client message");
            }
        }
        private void HandleClient(Peer p, PacketView packet)
        {
            if (p.Id != transport.HostPeer) throw new ProtocolException("Host identity mismatch");
            if (packet.Kind == MessageKind.Reject) { Close(ReadText(packet)); return; }
            if (packet.Kind == MessageKind.Welcome)
            {
                if (LocalId != "") throw new ProtocolException("Duplicate welcome");
                string worldId = "", schema = "";
                Codec.Read(packet, delegate(BinaryReader r) { LocalId = Codec.Text(r); worldId = Codec.Text(r); schema = Codec.Text(r); });
                if (LocalId == "" || LocalId == "host" || worldId == "") throw new ProtocolException("Invalid assigned identity");
                World = new World { Id = worldId, GameHash = gameHash, SchemaHash = schema }; p.Admitted = true; Phase = SessionPhase.ReceivingSave; return;
            }
            if (!p.Admitted) throw new ProtocolException("Welcome required");
            if (packet.Kind == MessageKind.SnapshotBegin)
            {
                if (transferActive) throw new ProtocolException("Overlapping transfers");
                Codec.Read(packet, delegate(BinaryReader r) { receiving = (TransferKind)r.ReadByte(); if (!Enum.IsDefined(typeof(TransferKind), receiving)) throw new ProtocolException("Unknown transfer"); receiver.Begin(r.ReadInt32(), Codec.Text(r)); });
                if (receiving == TransferKind.NativeSave && Phase != SessionPhase.ReceivingSave) throw new ProtocolException("Unexpected native save");
                if (receiving != TransferKind.NativeSave && !sceneReady) throw new ProtocolException("Scene not ready");
                transferActive = true; return;
            }
            if (packet.Kind == MessageKind.SnapshotChunk)
            {
                if (!transferActive) throw new ProtocolException("No transfer");
                Codec.Read(packet, delegate(BinaryReader r)
                {
                    int offset = r.ReadInt32(), length = Codec.Count(r, Protocol.SnapshotChunk);
                    if (length > r.BaseStream.Length - r.BaseStream.Position) throw new EndOfStreamException();
                    receiver.Chunk(offset, packet.Bytes, packet.Offset + checked((int)r.BaseStream.Position), length); r.BaseStream.Position += length;
                }); return;
            }
            if (packet.Kind == MessageKind.SnapshotEnd)
            {
                if (!transferActive || packet.Length != 0) throw new ProtocolException("Unexpected transfer end");
                byte[] bytes = receiver.End(); transferActive = false;
                if (receiving == TransferKind.NativeSave)
                {
                    Phase = SessionPhase.Loading; if (NativeSaveReceived == null) throw new InvalidOperationException("Native load adapter missing"); NativeSaveReceived(bytes);
                }
                else if (receiving == TransferKind.World) { LastSnapshotBytes = bytes.Length; AcceptWorld(Codec.ImmutableWorld(bytes)); }
                else AcceptDelta(bytes);
                return;
            }
            if (packet.Kind == MessageKind.WorldDelta) { AcceptDelta(packet); return; }
            if (Phase != SessionPhase.Playing) return; // Motion may race the last reliable chunk.
            switch (packet.Kind)
            {
                case MessageKind.Pose:
                    Pose pose = Codec.Pose(packet); Player player;
                    if (pose.Id == LocalId || !World.Players.TryGetValue(pose.Id, out player) || pose.Sequence <= player.LastPose) break;
                    player.Position = pose.Position; player.Rotation = pose.Rotation; player.Pitch = pose.Pitch; player.LastPose = pose.Sequence;
                    if (PoseReceived != null) PoseReceived(pose); break;
                case MessageKind.Motion: if (MotionReceived != null) MotionReceived(Codec.Motion(packet)); break;
                case MessageKind.Result: if (CommandCompleted != null) CommandCompleted(Codec.Result(packet)); break;
                default: throw new ProtocolException("Unexpected host message");
            }
        }
        private void AcceptWorld(World world)
        {
            if (world.Id != World.Id || world.GameHash != gameHash || world.SchemaHash != World.SchemaHash || !world.Players.ContainsKey(LocalId)) throw new ProtocolException("World identity mismatch");
            World = world; if (WorldReceived != null) WorldReceived(world, true);
            if (Phase != SessionPhase.Playing) Send(transport.HostPeer, MessageKind.Ready, Codec.Make(delegate(BinaryWriter w) { w.Write((byte)2); w.Write(world.Revision); }), true);
            Phase = SessionPhase.Playing;
        }
        private void AcceptDelta(byte[] bytes) { AcceptDelta(new PacketView { Bytes = bytes, Offset = 0, Length = bytes.Length }); }
        private void AcceptDelta(PacketView packet)
        {
            if (Phase != SessionPhase.Playing) throw new ProtocolException("Unexpected delta");
            DeltaReadBuffer buffer = null;
            if (ReuseDeltaContainers && !deltaBufferInUse) { buffer = deltaReadBuffer ?? (deltaReadBuffer = new DeltaReadBuffer()); deltaBufferInUse = true; }
            try
            {
                long baseline = -1; World delta = null;
                Codec.Read(packet, delegate(BinaryReader r) { baseline = r.ReadInt64(); int n = Codec.Count(r, Protocol.MaxSnapshot); long began = r.BaseStream.Position; if (n > r.BaseStream.Length - began) throw new EndOfStreamException(); if (baseline != World.Revision) { r.BaseStream.Position += n; return; } delta = buffer == null ? Codec.ReadDelta(r, World) : Codec.ReadDelta(r, World, buffer); if (r.BaseStream.Position - began != n) throw new ProtocolException("Invalid delta length"); });
                if (baseline != World.Revision) { Send(transport.HostPeer, MessageKind.Resync, new byte[0], true); return; }
                if (delta.Id != World.Id || delta.GameHash != gameHash || delta.SchemaHash != World.SchemaHash) throw new ProtocolException("Delta identity mismatch");
                CheckpointStore.ApplyDelta(World, delta, true); if (WorldReceived != null) WorldReceived(delta, false);
            }
            finally { if (buffer != null) { buffer.Clear(); deltaBufferInUse = false; } }
        }
        public void Publish()
        {
            if (!IsHost || disposed) return; World = authority.World;
            foreach (Peer p in peers.Values)
            {
                if (!p.Playing || p.Transfers.Count != 0 || p.Baseline == null || p.SentRevision == World.Revision) continue;
                World delta = CheckpointStore.Difference(p.Baseline, World, authority.ImmutableEntities);
                byte[] payload = Codec.Make(delegate(BinaryWriter w)
                {
                    w.Write(p.SentRevision); long lengthAt = w.BaseStream.Position; w.Write(0); long began = w.BaseStream.Position;
                    if (authority.ImmutableEntities) Codec.WriteDelta(w, delta, p.Baseline); else Codec.WriteWorld(w, delta);
                    long end = w.BaseStream.Position; w.BaseStream.Position = lengthAt; w.Write(checked((int)(end - began))); w.BaseStream.Position = end;
                });
                DeltaBytesSent += payload.Length;
                if (payload.Length <= Protocol.MaxFrame - 15) Send(p.Id, MessageKind.WorldDelta, payload, true); else Queue(p, TransferKind.Delta, payload);
                p.SentRevision = World.Revision;
                CheckpointStore.ApplyDelta(p.Baseline, delta, authority.ImmutableEntities);
            }
        }
        private void QueueWorld(Peer p)
        {
            World = authority.World; byte[] bytes = Codec.World(World); LastSnapshotBytes = bytes.Length; Queue(p, TransferKind.World, bytes);
            p.SentRevision = World.Revision; p.Baseline = authority.ImmutableEntities ? World.ReferenceSnapshot() : World.Clone();
        }
        private void Queue(Peer p, TransferKind kind, byte[] bytes)
        {
            if (bytes.Length == 0 || bytes.Length > Protocol.MaxSnapshot || p.Transfers.Count >= 2) throw new ProtocolException("Transfer budget exceeded");
            p.Transfers.Enqueue(new Transfer { Kind = kind, Bytes = bytes });
        }
        private void Pump(Peer p)
        {
            if (p.Transfers.Count == 0) return;
            Transfer t = p.Transfers.Peek();
            if (!t.Started)
            {
                Send(p.Id, MessageKind.SnapshotBegin, Codec.Make(delegate(BinaryWriter w) { w.Write((byte)t.Kind); w.Write(t.Bytes.Length); Codec.Text(w, Codec.Hash(t.Bytes)); }), true); t.Started = true;
            }
            // Limit main-thread work and transport queue occupancy, even for a large native save.
            for (int i = 0; i < 2 && t.Offset < t.Bytes.Length; i++)
            {
                int count = Math.Min(Protocol.SnapshotChunk, t.Bytes.Length - t.Offset);
                Send(p.Id, MessageKind.SnapshotChunk, Codec.Make(delegate(BinaryWriter w) { w.Write(t.Offset); w.Write(count); w.Write(t.Bytes, t.Offset, count); }), true); t.Offset += count;
            }
            if (t.Offset == t.Bytes.Length) { Send(p.Id, MessageKind.SnapshotEnd, new byte[0], true); p.Transfers.Dequeue(); }
        }
        public CommandResult Submit(Command command)
        {
            if (disposed || Phase != SessionPhase.Playing) return new CommandResult { Reason = "Session is not ready" };
            command.Sequence = ++commandSequence;
            if (IsHost)
            {
                CommandResult result = authority.Execute(LocalId, command); World = authority.World; Publish(); if (CommandCompleted != null) CommandCompleted(result); return result;
            }
            Send(transport.HostPeer, MessageKind.Command, Codec.Command(command), true); return null;
        }
        public void SendPose(Pose pose)
        {
            if (disposed || Phase != SessionPhase.Playing) return;
            pose.Id = LocalId; pose.Sequence = ++poseSequence;
            if (IsHost) { authority.SetPose(LocalId, pose); Broadcast(MessageKind.Pose, Codec.Pose(pose), false, null); }
            else Send(transport.HostPeer, MessageKind.Pose, Codec.Pose(pose), false);
        }
        public void SendMotion(MotionFrame frame)
        {
            if (!IsHost || disposed) return; Broadcast(MessageKind.Motion, Codec.Motion(frame), false, null);
        }
        private void Broadcast(MessageKind kind, byte[] bytes, bool reliable, string except)
        {
            foreach (Peer p in peers.Values) if (p.Playing && p.Id != except) Send(p.Id, kind, bytes, reliable);
        }
        private void Send(string peer, MessageKind kind, byte[] bytes, bool reliable) { transport.Send(peer, Codec.Packet(kind, bytes), reliable); }
        private static string ReadText(PacketView packet) { string text = ""; Codec.Read(packet, delegate(BinaryReader r) { text = Codec.Text(r); }); return text; }
        private void Reject(string id, string reason)
        {
            Send(id, MessageKind.Reject, Codec.Make(delegate(BinaryWriter w) { Codec.Text(w, reason); }), true);
            // A reliable rejection can be delivered before the peer closes its own transport.
            Peer p; if (peers.TryGetValue(id, out p)) p.Seen = now - 119;
        }
        private void Remove(string id, string reason)
        {
            Peer p; if (!peers.TryGetValue(id, out p)) return; peers.Remove(id);
            if (!IsHost) { Close(reason == "" ? "Host left. Session ended." : reason); return; }
            if (p.Admitted) { authority.RemovePlayer(id); World = authority.World; World.Revision++; UpdateBarrier(); Publish(); }
            if (Status != null) Status(p.Name + " disconnected: " + reason);
        }
        private void UpdateBarrier()
        {
            bool pending = false; foreach (Peer p in peers.Values) if (p.Admitted && p.Loading) pending = true;
            if (JoinBarrier != null) JoinBarrier(pending);
        }
        public void Close(string reason)
        {
            if (disposed) return;
            foreach (Peer p in peers.Values) { try { Send(p.Id, MessageKind.Goodbye, Codec.Make(delegate(BinaryWriter w) { Codec.Text(w, reason); }), true); } catch (IOException) { } }
            Dispose(); if (Ended != null) Ended(reason);
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true; Phase = SessionPhase.Closed;
            receiver.Reset(); transport.Dispose(); peers.Clear(); if (JoinBarrier != null) JoinBarrier(false);
        }
    }
}
