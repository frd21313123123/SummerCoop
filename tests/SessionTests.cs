using System;
using System.Collections.Generic;
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
