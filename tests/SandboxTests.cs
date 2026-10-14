using System;
using System.IO;
using SummerCoop.Core;

internal static class SandboxTests
{
    private static void Check(bool condition, string why) { if (!condition) throw new Exception(why); }
    private sealed class SpawnAdapter : ICopyOnWriteCommandAdapter
    {
        public int Created, RolledBack;
        public string Validate(string player, Command c, World world) { return c.Target == "prefab/beer case" ? null : "Unknown prefab"; }
        public void Apply(string player, Command c, World world)
        {
            Entity item = new Entity { Id = "spawn-" + (++Created), Key = "spawn/" + Created, Prefab = c.Target, Kind = EntityKind.Rigidbody, Position = world.Players[player].Position };
            item.Properties["@body"] = Atom.B(true); item.Properties["@sandbox/condition"] = Atom.N(c.Number);
            world.Entities.Add(item.Id, item); world.EntityChanged(item.Id);
        }
        public void Rollback(World world) { RolledBack++; }
    }
    public static void Run(Action<string, Action> test)
    {
        test("all adjustable needs and reset effects belong to the local player and percentages are bounded", delegate
        {
            foreach (string name in PlayerControls.Needs) Check(NativeActionPolicy.PersonalVariable(name), "Shared need " + name);
            foreach (string name in PlayerControls.Effects) Check(NativeActionPolicy.PersonalVariable(name), "Shared effect " + name);
            Check(PlayerControls.Needs.Length == PlayerControls.NeedLabels.Length && PlayerControls.ClampPercent(-10) == 0 && PlayerControls.ClampPercent(200) == 100 && PlayerControls.ClampPercent(37.5f) == 37.5f, "Percent range or labels");
            bool rejected = false; try { PlayerControls.ClampPercent(float.NaN); } catch (ArgumentException) { rejected = true; } Check(rejected, "NaN accepted");
        });
        test("host validates a catalog spawn without requiring an existing scene entity and deduplicates retries", delegate
        {
            SpawnAdapter adapter = new SpawnAdapter(); Authority authority = new Authority(new World(), adapter, null); authority.AddPlayer("guest", "Guest");
            authority.World.Players["guest"].Position = new V3(500,2,400);
            Command request = new Command { Sequence = 1, Kind = CommandKind.Spawn, Target = "prefab/beer case", Number = 25 };
            Check(authority.Execute("guest", request).Accepted && authority.Execute("guest", request).Accepted && adapter.Created == 1, "Spawn retried or incorrectly distance-limited");
            Check(authority.World.Entities["spawn-1"].Position.X == 500 && authority.World.Entities["spawn-1"].Properties["@sandbox/condition"].Number == 25, "Actor position or condition lost");
        });
        test("invalid spawn catalog, condition and occupied hands reject before mutation or rollback", delegate
        {
            SpawnAdapter adapter = new SpawnAdapter(); Authority authority = new Authority(new World(), adapter, null); authority.AddPlayer("guest", "Guest");
            Check(!authority.Execute("guest", new Command { Sequence = 1, Kind = CommandKind.Spawn, Target = "unknown", Number = 100 }).Accepted, "Unknown catalog accepted");
            long sequence = 2;
            foreach (double value in new[] { -1d, 101d, double.NaN, double.PositiveInfinity })
                Check(!authority.Execute("guest", new Command { Sequence = sequence++, Kind = CommandKind.Spawn, Target = "prefab/beer case", Number = value }).Accepted, "Bad condition accepted");
            authority.World.Players["guest"].Held = "owned";
            Check(!authority.Execute("guest", new Command { Sequence = 10, Kind = CommandKind.Spawn, Target = "prefab/beer case", Number = 100 }).Accepted && adapter.Created == 0 && adapter.RolledBack == 0 && authority.World.Revision == 0, "Rejected spawn mutated world");
        });
        test("spawn metadata reaches replicas and survives checkpoint reload with independent item identities", delegate
        {
            string directory = Path.Combine(Path.GetTempPath(), "summercoop-spawn-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            try
            {
                World initial = new World { Id = "sandbox", GameHash = "game", SchemaHash = "schema" };
                using (CheckpointStore store = new CheckpointStore(directory))
                {
                    store.Checkpoint(initial); SpawnAdapter adapter = new SpawnAdapter(); Authority authority = new Authority(initial, adapter, store); authority.AddPlayer("guest", "Guest");
                    World before = initial.Clone();
                    Check(authority.Execute("guest", new Command { Sequence = 1, Kind = CommandKind.Spawn, Target = "prefab/beer case", Number = 100 }).Accepted, "First spawn failed");
                    Check(authority.Execute("guest", new Command { Sequence = 2, Kind = CommandKind.Spawn, Target = "prefab/beer case", Number = 25 }).Accepted, "Second spawn failed");
                    World replica = before.Clone(); CheckpointStore.ApplyDelta(replica, Codec.World(Codec.World(CheckpointStore.Difference(before, authority.World))));
                    Check(replica.Entities.Count == 2 && replica.Entities["spawn-1"].Properties["@sandbox/condition"].Number == 100 && replica.Entities["spawn-2"].Properties["@sandbox/condition"].Number == 25, "Replica metadata or independent IDs lost");
                    World restored = store.Recover(); Check(restored.Entities.Count == 2 && restored.Entities["spawn-2"].Prefab == "prefab/beer case", "Journal did not preserve spawned native prefab");
                }
            }
            finally { Directory.Delete(directory, true); }
        });
        test("installed nonphysical parts discard stale motion while native jointed doors retain physics", delegate
        {
            Check(MountedPose.MustSnap(true, true, false), "Parent change kept stale motion");
            Check(MountedPose.MustSnap(false, false, true) && !MountedPose.AcceptMotion(false, true), "Mounted crankshaft accepts held pose");
            Check(!MountedPose.MustSnap(false, true, true) && MountedPose.AcceptMotion(true, true), "Jointed body welded by mod");
            Check(!MountedPose.MustSnap(false, true, false) && MountedPose.AcceptMotion(true, false), "Loose body lost interpolation");
        });
        test("native mounting reference and local pose survive delta and checkpoint serialization", delegate
        {
            World before = new World { Id = "mount-test" }; before.Entities.Add("pivot", new Entity { Id = "pivot" });
            Entity part = new Entity { Id = "crankshaft", Key = "part/crankshaft", Kind = EntityKind.Rigidbody }; part.Properties["@body"] = Atom.B(true); before.Entities.Add(part.Id, part);
            World mounted = before.Clone(); Entity next = mounted.Entities[part.Id]; next.Parent = "pivot"; next.Properties.Remove("@body");
            next.Properties["@mount/parent"] = Atom.R("pivot"); next.Properties["@mount/position"] = Atom.V(new V3(0,.125f,0)); next.Properties["@mount/rotation"] = Atom.Q(Q4.Identity); mounted.Revision = 1;
            World client = before.Clone(); CheckpointStore.ApplyDelta(client, Codec.World(Codec.World(CheckpointStore.Difference(before, mounted))));
            Entity received = Codec.World(Codec.World(client)).Entities[part.Id];
            Check(received.Parent == "pivot" && !received.Properties.ContainsKey("@body") && received.Properties["@mount/parent"].Text == "pivot" && received.Properties["@mount/position"].Vector.Y == .125f, "Mount did not survive network/save encoding");
            Check(before.Entities[part.Id].Parent == "" && before.Entities[part.Id].Properties.ContainsKey("@body"), "Original snapshot mutated");
        });
    }
}
