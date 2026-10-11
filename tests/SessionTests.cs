using System;
using System.Collections.Generic;
using System.IO;
using SummerCoop.Core;

internal static class SessionTests
{
    private sealed class Adapter : ICommandAdapter
    {
        public string Validate(string player, Command c, World world) { return null; }
        public void Apply(string player, Command c, World world) { if (c.Kind == CommandKind.Purchase) world.Money -= 25; }
        public void Rollback(World world) { }
    }
    private sealed class Hub
    {
        public double Time;
        public bool Impaired;
        private Random random = new Random(516750);
        public Node Host;
        private readonly List<Delivery> deliveries = new List<Delivery>();
        private sealed class Delivery { public Node To; public TransportEvent Event; public double Due; }
        public void Send(Node from, Node to, byte[] bytes, bool reliable)
        {
            if (Impaired && !reliable && random.Next(100) < 5) return;
            double due = Time + (Impaired ? 0.15 : 0);
            // Reliable frames preserve order; datagrams may arrive in a different order.
            if (Impaired && !reliable) due += random.NextDouble() * 0.15;
            deliveries.Add(new Delivery { To = to, Due = due, Event = new TransportEvent { Kind = TransportEventKind.Data, Peer = from.IsHost ? "host" : from.Id, Reliable = reliable, Data = (byte[])bytes.Clone() } });
        }
        public void Pump(double time)
        {
            Time = time;
            for (int i = 0; i < deliveries.Count; i++) if (deliveries[i].Due <= time) { deliveries[i].To.Events.Enqueue(deliveries[i].Event); deliveries.RemoveAt(i--); }
        }
    }
    private sealed class Node : ITransport
    {
        public Hub Hub;
        public string Id;
        public bool IsHost { get; set; }
        public string HostPeer { get { return "host"; } }
        public Queue<TransportEvent> Events = new Queue<TransportEvent>();
        public Dictionary<string, Node> Links = new Dictionary<string, Node>();
        public IList<string> Peers { get { return new List<string>(Links.Keys); } }
        public bool Poll(out TransportEvent e) { e = Events.Count == 0 ? null : Events.Dequeue(); return e != null; }
        public void Send(string peer, byte[] bytes, bool reliable) { Node to; if (Links.TryGetValue(peer, out to)) Hub.Send(this, to, bytes, reliable); }
        public void Disconnect(string peer) { Node to; if (!Links.TryGetValue(peer, out to)) return; Links.Remove(peer); to.Links.Remove(IsHost ? Id : "host"); to.Events.Enqueue(new TransportEvent { Kind = TransportEventKind.Disconnected, Peer = IsHost ? "host" : Id }); }
        public void Update() { }
        public void Dispose() { foreach (string peer in new List<string>(Links.Keys)) Disconnect(peer); }
    }
    private static void Assert(bool condition, string why) { if (!condition) throw new Exception(why); }
    private static Node ClientNode(Hub hub, string id)
    {
        Node client = new Node { Hub = hub, Id = id }; client.Links.Add("host", hub.Host); hub.Host.Links.Add(id, client);
        hub.Host.Events.Enqueue(new TransportEvent { Kind = TransportEventKind.Connected, Peer = id }); client.Events.Enqueue(new TransportEvent { Kind = TransportEventKind.Connected, Peer = "host" }); return client;
    }
    private static void Tick(Hub hub, Session host, IList<Session> clients, int count)
    {
        for (int i = 0; i < count; i++) { hub.Pump(hub.Time + 0.05); host.Update(hub.Time); foreach (Session c in clients) c.Update(hub.Time); }
    }
    private sealed class Pair : IDisposable
    {
        public readonly Hub Hub = new Hub();
        public readonly Session Host, Client;
        public readonly Node ClientTransport;
        public Pair(bool reuse)
        {
            Hub.Host = new Node { Hub = Hub, Id = "host", IsHost = true };
            World world = new World { Id = "buffer-world", GameHash = "game", SchemaHash = "schema" };
            Entity part = new Entity { Id = "part", Key = "garage/part", Kind = EntityKind.Rigidbody };
            part.Properties.Add("stage", Atom.N(0)); world.Entities.Add(part.Id, part);
            Host = new Session(Hub.Host, new Authority(world, new Adapter(), null), "game", "Host", "", 0);
            Host.PrepareNativeSave = delegate { return new byte[] { 1, 2, 3 }; }; Host.SceneLoaded("schema");
            ClientTransport = ClientNode(Hub, "client");
            Client = new Session(ClientTransport, null, "game", "Client", "", 0) { ReuseDeltaContainers = reuse };
            Client.NativeSaveReceived = delegate { Client.SceneLoaded("schema"); };
            Tick(Hub, Host, new Session[] { Client }, 50);
            Assert(Client.Phase == SessionPhase.Playing, "Test client did not join");
        }
        public World Delta()
        {
            World delta = new World { Id = Client.World.Id, GameHash = Client.World.GameHash, SchemaHash = Client.World.SchemaHash, Revision = Client.World.Revision + 1, Money = Client.World.Money, SleepEpoch = Client.World.SleepEpoch };
            foreach (Player player in Client.World.Players.Values) delta.Players.Add(player.Id, player.Clone());
            return delta;
        }
        public void Enqueue(World delta, bool malformed, long baselineRevision = -1)
        {
            byte[] bytes = Codec.Make(delegate(BinaryWriter w) { Codec.WriteDelta(w, delta, Client.World); });
            byte[] payload = Codec.Make(delegate(BinaryWriter w) { w.Write(baselineRevision < 0 ? Client.World.Revision : baselineRevision); w.Write(bytes.Length); w.Write(bytes); if (malformed) w.Write((byte)123); });
            ClientTransport.Events.Enqueue(new TransportEvent { Kind = TransportEventKind.Data, Peer = "host", Reliable = true, Data = Codec.Packet(MessageKind.WorldDelta, payload) });
        }
        public void Deliver(World delta) { Enqueue(delta, false); Client.Update(Hub.Time); }
        public void Dispose() { Host.Dispose(); Client.Dispose(); }
    }
    private static Entity Stage(World world, int stage)
    {
        Entity part = world.Entities["part"].Clone(); part.Properties["stage"] = Atom.N(stage); return part;
    }
    public static void Run(Action<string, Action> test)
    {
        test("session handshake / 1.5 MB save / four players / 150 ms latency / 5% UDP loss", delegate
        {
            Hub hub = new Hub { Impaired = true }; hub.Host = new Node { Hub = hub, Id = "host", IsHost = true };
            World world = new World { Id = "session-world", GameHash = "game", SchemaHash = "schema", Money = 500 };
            world.Entities.Add("part", new Entity { Id = "part", Kind = EntityKind.Rigidbody });
            Authority authority = new Authority(world, new Adapter(), null);
            Session host = new Session(hub.Host, authority, "game", "Host", "secret", 0);
            byte[] native = new byte[1500000]; new Random(51).NextBytes(native); host.PrepareNativeSave = delegate { return native; }; host.SceneLoaded("schema");
            List<Session> clients = new List<Session>(); List<Node> nodes = new List<Node>(); int loaded = 0;
            try
            {
                for (int i = 0; i < 3; i++)
                {
                    Node node = ClientNode(hub, "peer" + i); nodes.Add(node);
                    Session client = new Session(node, null, "game", "Player" + i, "secret", 0);
                    client.ReuseDeltaContainers = i == 0;
                    client.NativeSaveReceived = delegate(byte[] bytes) { Assert(Codec.Hash(bytes) == Codec.Hash(native), "Native save corrupted"); loaded++; client.SceneLoaded("schema"); };
                    clients.Add(client);
                }
                Tick(hub, host, clients, 180);
                Assert(loaded == 3 && host.World.Players.Count == 4, "Handshake did not finish");
                foreach (Session c in clients) Assert(c.Phase == SessionPhase.Playing && c.World.Players.Count == 4 && c.LocalId != "host", "Client was not admitted");
                clients[0].Submit(new Command { Kind = CommandKind.Take, Target = "part" });
                clients[1].Submit(new Command { Kind = CommandKind.Take, Target = "part" }); Tick(hub, host, clients, 30);
                Assert(host.World.Players[clients[0].LocalId].Held == "part" && host.World.Players[clients[1].LocalId].Held == "", "Conflicting ownership");
                clients[0].Submit(new Command { Kind = CommandKind.Drop, Target = "part" });
                Command purchase = new Command { Kind = CommandKind.Purchase, Target = "part" }; clients[1].Submit(purchase);
                nodes[1].Send("host", Codec.Packet(MessageKind.Command, Codec.Command(purchase)), true); Tick(hub, host, clients, 30);
                Assert(host.World.Money == 475, "Duplicate purchase charged twice");
                for (int i = 0; i < 100; i++) { clients[0].SendPose(new Pose { Id = "forged-id", Position = new V3(i, 0, 0) }); Tick(hub, host, clients, 1); }
                Tick(hub, host, clients, 15); Assert(!host.World.Players.ContainsKey("forged-id") && host.World.Players[clients[0].LocalId].Position.X > 90, "Pose identity/loss handling failed");
                host.World.Revision++; host.Publish(); Tick(hub, host, clients, 20);
                foreach (Session c in clients) Assert(Codec.Hash(Codec.World(c.World)) == Codec.Hash(Codec.World(host.World)), "Client state diverged");
                host.Close("Host left"); Tick(hub, host, clients, 20); foreach (Session c in clients) Assert(c.Phase == SessionPhase.Closed, "Client continued without host");
            }
            finally { host.Dispose(); foreach (Session c in clients) c.Dispose(); }
        });
        test("session default deltas retain independent callback containers", delegate
        {
            using (Pair pair = new Pair(false))
            {
                World first = null, second = null;
                pair.Client.WorldReceived = delegate(World delta, bool full) { if (first == null) first = delta; else second = delta; };
                World update = pair.Delta(); update.Entities.Add("part", Stage(pair.Client.World, 1)); pair.Deliver(update);
                update = pair.Delta(); update.Entities.Add("part", Stage(pair.Client.World, 2)); pair.Deliver(update);
                Assert(!object.ReferenceEquals(first, second) && first.Entities["part"].Properties["stage"].Number == 1, "Default callback data was reused");
                Assert(second.Entities["part"].Properties["stage"].Number == 2, "Second delta lost");
            }
        });
        test("session borrowed deltas grow, shrink, delete and recreate without mutating retained snapshots", delegate
        {
            using (Pair pair = new Pair(true))
            {
                World container = null, retainedClone = null; Entity retained = null;
                int count = 0;
                pair.Client.WorldReceived = delegate(World delta, bool full)
                {
                    Assert(!full && delta.Players.Count == 2, "Borrowed callback lost players");
                    if (container == null) { container = delta; retained = delta.Entities["part"]; retainedClone = delta.Clone(); }
                    else Assert(object.ReferenceEquals(container, delta), "Delta container was not reused");
                    count++;
                };
                World update = pair.Delta(); update.Entities.Add("part", Stage(pair.Client.World, 1)); pair.Deliver(update);
                Dictionary<string, Entity> small = container.Entities;
                update = pair.Delta(); update.Entities.Add("part", Stage(pair.Client.World, 2));
                for (int i = 0; i < 256; i++) update.Entities.Add("added" + i, new Entity { Id = "added" + i });
                pair.Deliver(update); Dictionary<string, Entity> large = container.Entities;
                Assert(!object.ReferenceEquals(small, large), "Buffer did not grow for a larger delta");
                update = pair.Delta(); update.Entities.Add("part", new Entity { Id = "part", Key = "__deleted" }); pair.Deliver(update);
                Assert(!pair.Client.World.Entities.ContainsKey("part"), "Deleted part remains");
                update = pair.Delta(); Entity recreated = new Entity { Id = "part", Key = "garage/recreated", Parent = "car" }; recreated.Properties.Add("stage", Atom.N(8)); update.Entities.Add("part", recreated); pair.Deliver(update);
                update = pair.Delta(); update.Money = 3000; pair.Deliver(update);
                Assert(count == 5 && container.Entities.Count == 0 && container.Players.Count == 0, "Borrowed data was not cleared after callback");
                Assert(object.ReferenceEquals(large, container.Entities), "A smaller update replaced the grown buffer");
                Assert(retained.Properties["stage"].Number == 1 && retainedClone.Entities["part"].Properties["stage"].Number == 1, "Later packets changed a retained snapshot");
                Assert(pair.Client.World.Entities.Count == 257 && pair.Client.World.Entities["part"].Parent == "car" && pair.Client.World.Entities["part"].Properties["stage"].Number == 8 && pair.Client.World.Money == 3000, "Persistent world lost borrowed entities");
            }
        });
        test("session reentrant borrowed callback keeps its outer delta intact", delegate
        {
            using (Pair pair = new Pair(true))
            {
                World outer = null, inner = null, third = null; int count = 0;
                pair.Client.WorldReceived = delegate(World delta, bool full)
                {
                    count++;
                    if (count == 1)
                    {
                        outer = delta;
                        World nested = pair.Delta(); nested.Entities.Add("part", Stage(pair.Client.World, 2)); pair.Enqueue(nested, false); pair.Client.Update(pair.Hub.Time);
                        Assert(outer.Entities["part"].Properties["stage"].Number == 1 && outer.Players.Count == 2, "Nested processing cleared the outer delta");
                    }
                    else if (count == 2) inner = delta; else third = delta;
                };
                World update = pair.Delta(); update.Entities.Add("part", Stage(pair.Client.World, 1)); pair.Deliver(update);
                Assert(!object.ReferenceEquals(outer, inner) && inner.Entities["part"].Properties["stage"].Number == 2, "Nested delta reused a live container");
                update = pair.Delta(); update.Entities.Add("part", Stage(pair.Client.World, 3)); pair.Deliver(update);
                Assert(count == 3 && object.ReferenceEquals(outer, third) && pair.Client.World.Entities["part"].Properties["stage"].Number == 3, "Buffer was not released after nested processing");
            }
        });
        test("session malformed borrowed delta applies no partial state and releases its data", delegate
        {
            using (Pair pair = new Pair(true))
            {
                World container = null; int callbacks = 0;
                pair.Client.WorldReceived = delegate(World delta, bool full) { container = delta; callbacks++; };
                World update = pair.Delta(); update.Entities.Add("part", Stage(pair.Client.World, 1)); pair.Deliver(update);
                string before = Codec.Hash(Codec.World(pair.Client.World));
                update = pair.Delta(); update.Entities.Add("part", Stage(pair.Client.World, 7)); pair.Enqueue(update, true); pair.Client.Update(pair.Hub.Time);
                Assert(pair.Client.Phase == SessionPhase.Closed && callbacks == 1, "Malformed delta reached callback");
                Assert(Codec.Hash(Codec.World(pair.Client.World)) == before, "Malformed packet applied partial state");
                Assert(container.Entities.Count == 0 && container.Players.Count == 0, "Malformed packet retained scratch data");
            }
        });
        test("session borrowed callback failure clears scratch without losing applied entities", delegate
        {
            using (Pair pair = new Pair(true))
            {
                World container = null; Entity retained = null;
                pair.Client.WorldReceived = delegate(World delta, bool full) { container = delta; retained = delta.Entities["part"]; throw new IOException("Injected callback failure"); };
                World update = pair.Delta(); update.Entities.Add("part", Stage(pair.Client.World, 5)); pair.Deliver(update);
                Assert(pair.Client.Phase == SessionPhase.Closed && container.Entities.Count == 0 && container.Players.Count == 0, "Callback failure retained scratch data");
                Assert(object.ReferenceEquals(retained, pair.Client.World.Entities["part"]) && retained.Properties["stage"].Number == 5, "Clearing scratch lost an applied part");
            }
        });
        test("session borrowed chunked delta and baseline resync preserve full world ownership", delegate
        {
            using (Pair pair = new Pair(true))
            {
                World container = null, fullWorld = null; int deltas = 0, snapshots = 0;
                pair.Client.WorldReceived = delegate(World world, bool full)
                {
                    if (full) { fullWorld = world; snapshots++; }
                    else { if (container == null) container = world; else Assert(object.ReferenceEquals(container, world), "Chunked delta changed its borrowed container"); deltas++; }
                };
                pair.Host.World.Entities["part"] = Stage(pair.Host.World, 1); pair.Host.World.Revision++; pair.Host.Publish(); Tick(pair.Hub, pair.Host, new Session[] { pair.Client }, 10);
                for (int i = 0; i < 3200; i++) pair.Host.World.Entities.Add("large" + i, new Entity { Id = "large" + i, Key = "garage/large" + i });
                long sentBefore = pair.Host.DeltaBytesSent;
                pair.Host.World.Revision++; pair.Host.Publish(); Tick(pair.Hub, pair.Host, new Session[] { pair.Client }, 100);
                Assert(pair.Host.DeltaBytesSent - sentBefore > Protocol.MaxFrame && deltas == 2 && pair.Client.World.Entities.Count == 3201 && pair.Client.Phase == SessionPhase.Playing, "Chunked delta failed or did not exceed the frame limit");
                World invalidBaseline = pair.Delta(); invalidBaseline.Money = 9999;
                string before = Codec.Hash(Codec.World(pair.Client.World));
                pair.Enqueue(invalidBaseline, false, pair.Client.World.Revision - 1); pair.Client.Update(pair.Hub.Time);
                Assert(deltas == 2 && Codec.Hash(Codec.World(pair.Client.World)) == before, "Mismatched baseline applied a delta");
                Tick(pair.Hub, pair.Host, new Session[] { pair.Client }, 100);
                Assert(snapshots == 1 && object.ReferenceEquals(fullWorld, pair.Client.World) && !object.ReferenceEquals(fullWorld, container), "Resync reused a borrowed container for the full world");
                Entity retained = fullWorld.Entities["part"];
                pair.Host.World.Entities["part"] = Stage(pair.Host.World, 4); pair.Host.World.Revision++; pair.Host.Publish(); Tick(pair.Hub, pair.Host, new Session[] { pair.Client }, 10);
                Assert(deltas == 3 && fullWorld.Entities.Count == 3201 && container.Entities.Count == 0 && retained.Properties["stage"].Number == 1, "Later delta cleared full world or changed a retained entity");
                Assert(Codec.Hash(Codec.World(pair.Client.World)) == Codec.Hash(Codec.World(pair.Host.World)), "Chunked/resynced replica diverged");
            }
        });
        test("session rejects mismatched builds and passwords before native loading", delegate
        {
            foreach (bool mismatch in new bool[] { true, false })
            {
                Hub hub = new Hub(); hub.Host = new Node { Hub = hub, Id = "host", IsHost = true };
                Session host = new Session(hub.Host, new Authority(new World { GameHash = "game", SchemaHash = "schema" }, null, null), "game", "Host", "secret", 0);
                host.SceneLoaded("schema"); host.PrepareNativeSave = delegate { throw new Exception("Unadmitted client loaded a save"); };
                Session client = new Session(ClientNode(hub, "bad"), null, mismatch ? "other-game" : "game", "Bad", mismatch ? "secret" : "wrong", 0);
                Tick(hub, host, new Session[] { client }, 30); Assert(client.Phase == SessionPhase.Closed && host.World.Players.Count == 1, "Incompatible client admitted"); host.Dispose(); client.Dispose();
            }
        });
    }
}
