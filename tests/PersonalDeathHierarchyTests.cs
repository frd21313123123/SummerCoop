using System;
using System.IO;
using SummerCoop.Core;

internal static class PersonalDeathHierarchyTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Entity Add(World world, string key, string parent, EntityKind kind, string name)
    {
        var entity = new Entity { Id = Identity.Stable(key), Key = key, Parent = parent, Kind = kind };
        entity.Properties["@name"] = Atom.S(name); entity.Properties["@tag"] = Atom.S("Untagged");
        entity.Properties["@empty"] = Atom.B(kind == EntityKind.Object); entity.Properties["@loose"] = Atom.B(false);
        world.Entities.Add(entity.Id, entity); return entity;
    }
    private static Entity Body(World world, string key, bool owner, bool active)
    {
        Entity body = Add(world, key, "", EntityKind.Rigidbody, "DeadBody");
        body.Active = active; body.Properties["@body"] = Atom.B(true); body.Properties["@mass"] = Atom.N(10);
        Add(world, key + "/CameraPivot[0]", body.Id, EntityKind.Object, "CameraPivot");
        for (int i = 0; i < 3; i++)
        {
            Entity coll = Add(world, key + "/Coll[" + i + "]", body.Id, EntityKind.Object, "Coll");
            coll.Properties["@empty"] = Atom.B(false); coll.Properties["@collider/0"] = Atom.B(true);
        }
        if (owner)
        {
            Entity fsm = Add(world, key + "|fsm/Owner/0", body.Id, EntityKind.Fsm, "");
            fsm.Properties["v/Owner"] = Atom.R(body.Id); fsm.Properties["@state"] = Atom.S("");
        }
        return body;
    }
    public static void Run(Action<string, Action> test)
    {
        test("detached native death body removes CameraPivot without losing shared progress", delegate
        {
            var world = new World { Money = 3000, Revision = 11299 };
            Entity body = Body(world, "spawn/death-body", true, false);
            Entity car = Add(world, "vehicle/SATSUMA", "", EntityKind.Vehicle, "SATSUMA");
            car.Properties["bolt"] = Atom.N(8); car.Properties["wired"] = Atom.B(true);
            Entity use = Add(world, "vehicle/JONNEZ|fsm/PlayerTrigger/0", "", EntityKind.Fsm, "");
            use.Properties["v/DeadBody"] = Atom.R(body.Id); use.Properties["v/Camera"] = Atom.R(Identity.Stable(body.Key + "/CameraPivot[0]") + "#UnityEngine.Transform");
            use.Properties["v/Gear"] = Atom.N(2);
            World before = world.ReferenceSnapshot(); world.EnableChangeTracking();
            Check(LocalHierarchy.RemoveFromCheckpoint(world) == 6, "Detached body or its CameraPivot survived");
            Check(world.Entities.Count == 2 && world.Revision == 11299 && world.Money == 3000, "Shared progress changed");
            Check(object.ReferenceEquals(world.Entities[car.Id], before.Entities[car.Id]), "Shared vehicle changed");
            Check(world.Entities[use.Id].Properties.Count == before.Entities[use.Id].Properties.Count - 2
                && world.Entities[use.Id].Properties["v/Gear"].Number == 2, "Shared references or gear changed");
            Check(before.Entities.Count == 8 && before.Entities[use.Id].Properties.ContainsKey("v/Camera"), "Immutable snapshot changed");
            Check(world.ChangeSequence > 0 && LocalHierarchy.RemoveFromCheckpoint(world) == 0, "Repair is untracked or not idempotent");
        });
        test("vehicle crash bodies and unrelated CameraPivot parents remain shared", delegate
        {
            var world = new World();
            Body(world, "vehicle/SATSUMA/DeadBody[0]", false, false);
            Body(world, "scene/NPC/DeadBody[0]", false, true);
            Entity unknown = Body(world, "spawn/unknown", true, false);
            world.Entities[Identity.Stable(unknown.Key + "|fsm/Owner/0")].Properties["v/Owner"] = Atom.R("another-owner");
            Add(world, "spawn/unrelated/CameraPivot[0]", "missing-parent", EntityKind.Object, "CameraPivot");
            string before = Codec.Hash(Codec.World(world));
            Check(LocalHierarchy.RemoveFromCheckpoint(world) == 0 && Codec.Hash(Codec.World(world)) == before, "Repair masked unrelated missing parents or removed shared deaths");
        });
        test("native death identity requires both Owner FSM and camera pivot", delegate
        {
            Check(LocalHierarchy.IsPersonalDeathBody("DeadBody", true, true) && LocalHierarchy.IsPersonalDeathBody("DeadBody(Clone)", true, true), "Native body or clone not recognized");
            Check(!LocalHierarchy.IsPersonalDeathBody("DeadBody", false, true) && !LocalHierarchy.IsPersonalDeathBody("DeadBody", true, false)
                && !LocalHierarchy.IsPersonalDeathBody("CameraPivot", true, true) && !LocalHierarchy.IsNestedName("DeadBody"), "Shared body or camera matched by name alone");
        });
        test("active local death body preserves held purchases and assembled parts", delegate
        {
            var world = new World(); Entity body = Body(world, "spawn/active-death", true, true);
            Entity part = Add(world, body.Key + "/starter[0]", body.Id, EntityKind.Rigidbody, "starter");
            part.Properties["@loose"] = Atom.B(true); part.Properties["bolt"] = Atom.N(4);
            Entity item = Add(world, body.Key + "/beer[0]", body.Id, EntityKind.Rigidbody, "beer"); item.Prefab = "prefab/beer";
            Entity mesh = Add(world, item.Key + "/Mesh[0]", item.Id, EntityKind.Object, "Mesh");
            World before = world.ReferenceSnapshot();
            Check(LocalHierarchy.RemoveFromCheckpoint(world) == 6 && world.Entities.Count == 3, "Repair removed held objects");
            Check(world.Entities[part.Id].Parent == "" && world.Entities[item.Id].Parent == "" && world.Entities[mesh.Id].Parent == item.Id
                && world.Entities[part.Id].Properties["bolt"].Number == 4 && world.Entities[item.Id].Prefab == "prefab/beer", "Repair changed held gameplay state");
            Check(before.Entities[part.Id].Parent == body.Id && before.Entities.Count == 9, "Repair mutated previous body hierarchy");
        });
        test("detached personal electric shock is filtered without changing world lightning", delegate
        {
            var world = new World(); Entity shock = Add(world, "spawn/player-shock", "", EntityKind.Object, "PlayerElecShock");
            Add(world, shock.Key + "/Spotlight[0]", shock.Id, EntityKind.Object, "Spotlight");
            Add(world, shock.Key + "|fsm/Closer/0", shock.Id, EntityKind.Fsm, "");
            Entity lightning = Add(world, "scene/Weather/Lightning[0]", "", EntityKind.Object, "Lightning");
            Check(LocalHierarchy.RemoveFromCheckpoint(world) == 3 && world.Entities.Count == 1 && world.Entities.ContainsKey(lightning.Id), "Repair altered shared weather");
            Check(LocalHierarchy.IsRootName("PlayerElecShock(Clone)") && LocalHierarchy.IsNestedName("PlayerElecShock"), "Detached personal effect can be captured again");
        });
        test("death repair survives checkpoint restart and retains journal progress", delegate
        {
            string directory = Path.Combine(Path.GetTempPath(), "SummerCoop-death-" + Guid.NewGuid().ToString("N"));
            try
            {
                var world = new World { Money = 3000, Revision = 2 };
                Body(world, "spawn/death", true, false);
                Entity car = Add(world, "vehicle/SATSUMA", "", EntityKind.Vehicle, "SATSUMA"); car.Properties["bolt"] = Atom.N(4);
                using (var store = new CheckpointStore(directory))
                {
                    store.Checkpoint(world); world.Revision++; world.Money = 2500;
                    Entity next = car.Clone(); next.Properties["bolt"] = Atom.N(8); world.Entities[car.Id] = next;
                    store.Commit(world, "host", new Command { Sequence = 1, Kind = CommandKind.Bolt, Target = car.Id, Number = 8 });
                }
                using (var store = new CheckpointStore(directory))
                {
                    World recovered = store.Recover(); Check(LocalHierarchy.RemoveFromCheckpoint(recovered) == 6, "Journal replay prevented death repair");
                    store.Checkpoint(recovered);
                }
                using (var store = new CheckpointStore(directory))
                {
                    World recovered = store.Recover();
                    Check(recovered.Entities.Count == 1 && recovered.Money == 2500 && recovered.Revision == 3
                        && recovered.Entities[car.Id].Properties["bolt"].Number == 8 && LocalHierarchy.RemoveFromCheckpoint(recovered) == 0, "Restart lost journal progress or restored the broken body");
                }
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
    }
}
