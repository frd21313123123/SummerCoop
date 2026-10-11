using System;
using System.IO;
using SummerCoop.Core;

internal static class LocalHierarchyTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Entity Add(World world, string id, string key, EntityKind kind, string name, string tag, string parent)
    {
        var entity = new Entity { Id = id, Key = key, Kind = kind, Parent = parent, Position = new V3(1, 2, 3) };
        entity.Properties["@name"] = Atom.S(name); entity.Properties["@tag"] = Atom.S(tag);
        world.Entities.Add(id, entity); return entity;
    }
    public static void Run(Action<string, Action> test)
    {
        test("local hierarchy names survive seating and native clone names", delegate
        {
            foreach (string name in new[] { "PLAYER", "PLAYER(Clone)", "GUI", "HUD", "Steam", "MSCLoaderUI", "SummerCoop.Avatar.1" })
                Check(LocalHierarchy.IsRootName(name), "Local root was not recognized: " + name);
            foreach (string name in new[] { "SATSUMA(557kg, 248)", "PlayerKey", "PLAYER seat", "Spanner", "Summer car", "" })
                Check(!LocalHierarchy.IsRootName(name), "Shared name was excluded: " + name);
            Check(LocalHierarchy.IsNestedName("PLAYER") && !LocalHierarchy.IsNestedName("Steam") && !LocalHierarchy.IsNestedName("HUD"), "Nested sauna vapour/computer HUD was classified as a local root");
        });
        test("checkpoint recovery removes personal trees and preserves exact shared state", delegate
        {
            var world = new World { Id = "existing-world", Revision = 17, Money = 3000, SleepEpoch = 2, GameHash = "game", SchemaHash = "schema" };
            Add(world, "player", "spawn/player", EntityKind.Object, "PLAYER", "Player", "");
            Add(world, "pivot", "spawn/player/Pivot[0]", EntityKind.Object, "Pivot", "Untagged", "player");
            Add(world, "hand", "spawn/player/Pivot[0]|fsm/Take/0", EntityKind.Fsm, "", "", "pivot");
            Add(world, "gui", "scene/GUI[0]", EntityKind.Object, "GUI", "Untagged", "");
            Add(world, "label", "scene/GUI[0]/Text[0]", EntityKind.Object, "Text", "Untagged", "gui");
            Entity car = Add(world, "car", "vehicle/SATSUMA", EntityKind.Vehicle, "SATSUMA", "Untagged", "");
            car.Properties["bolt"] = Atom.N(7); car.Properties["wire"] = Atom.B(true); car.Velocity = new V3(10, 0, 0);
            string carHash = Codec.Hash(Codec.World(new World { Id = "car", Entities = new System.Collections.Generic.Dictionary<string, Entity> { { car.Id, car } } }));
            Entity look = Add(world, "npc", "scene/NPC[0]|fsm/Look/0", EntityKind.Fsm, "", "", "car");
            look.Properties["v/Target"] = Atom.R("pivot#UnityEngine.Transform");
            look.Properties["v/Asset"] = Atom.R("asset/UnityEngine.Texture2D/player");
            look.Properties["v/Car"] = Atom.R("car");
            World before = world.ReferenceSnapshot();
            Check(LocalHierarchy.RemoveFromCheckpoint(world) == 5, "Wrong personal subtree size");
            Check(world.Entities.Count == 2 && world.Id == "existing-world" && world.Revision == 17 && world.Money == 3000 && world.SleepEpoch == 2 && world.GameHash == "game" && world.SchemaHash == "schema", "Shared metadata changed");
            Check(carHash == Codec.Hash(Codec.World(new World { Id = "car", Entities = new System.Collections.Generic.Dictionary<string, Entity> { { car.Id, world.Entities[car.Id] } } })), "Car state changed");
            Check(!world.Entities["npc"].Properties.ContainsKey("v/Target") && world.Entities["npc"].Properties["v/Asset"].Text == "asset/UnityEngine.Texture2D/player" && world.Entities["npc"].Properties["v/Car"].Text == "car", "Reference filtering changed shared references");
            Check(before.Entities["npc"].Properties.ContainsKey("v/Target") && before.Entities.Count == 7, "Recovery mutated an immutable snapshot");
            Check(LocalHierarchy.RemoveFromCheckpoint(world) == 0, "Recovery is not idempotent");
        });
        test("held parts and purchased items survive personal hierarchy repair", delegate
        {
            var world = new World();
            Add(world, "player", "scene/vehicle/PLAYER[0]", EntityKind.Object, "PLAYER", "Player", "");
            Add(world, "pivot", "scene/vehicle/PLAYER[0]/ItemPivot[0]", EntityKind.Object, "ItemPivot", "Untagged", "player");
            Entity part = Add(world, "part", "part/Database/starter", EntityKind.Rigidbody, "starter", "PART", "pivot");
            part.Properties["fitted"] = Atom.B(false); part.Properties["@loose"] = Atom.B(true);
            Entity item = Add(world, "item", "scene/vehicle/PLAYER[0]/beer[0]", EntityKind.Rigidbody, "beer", "ITEM", "pivot");
            item.Prefab = "prefab/beer";
            Add(world, "mesh", item.Key + "/Mesh[0]", EntityKind.Object, "Mesh", "Untagged", "item");
            Add(world, "fsm", item.Key + "|fsm/Drink/0", EntityKind.Fsm, "", "", "item");
            Add(world, "other", "scene/vehicle/PLAYER[0]-other", EntityKind.Object, "other", "Untagged", "");
            Check(LocalHierarchy.RemoveFromCheckpoint(world) == 2 && world.Entities.Count == 5, "Shared held subtree was removed");
            Check(world.Entities["part"].Parent == "" && world.Entities["item"].Parent == "" && world.Entities["mesh"].Parent == "item" && world.Entities["fsm"].Parent == "item", "Held object parent was not recovered");
            Check(world.Entities["part"].Position.X == 1 && world.Entities["item"].Position.Z == 3 && world.Entities["item"].Prefab == "prefab/beer" && !world.Entities["part"].Properties["fitted"].Number.Equals(1.0), "Held object state changed");
        });
        test("ordinary objects named PLAYER and unrelated missing parents remain visible to validation", delegate
        {
            var world = new World();
            Add(world, "ordinary", "spawn/ordinary", EntityKind.Object, "PLAYER", "Untagged", "missing-parent");
            Entity prefab = Add(world, "prefab", "spawn/prefab", EntityKind.Rigidbody, "PLAYER", "Player", ""); prefab.Prefab = "prefab/custom-player-item";
            string before = Codec.Hash(Codec.World(world));
            Check(LocalHierarchy.RemoveFromCheckpoint(world) == 0 && Codec.Hash(Codec.World(world)) == before, "Recovery masked an unrelated broken parent or deleted an item");
        });
        test("sauna steam and computer HUD survive alongside a contaminated player tree", delegate
        {
            var world = new World();
            Add(world, "player", "spawn/player", EntityKind.Object, "PLAYER", "Player", "");
            Add(world, "sauna", "scene/SAUNA[0]", EntityKind.Object, "SAUNA", "Untagged", "");
            Add(world, "steam", "scene/SAUNA[0]/Steam[0]", EntityKind.Object, "Steam", "Untagged", "sauna");
            Add(world, "hud", "scene/SAUNA[0]/HUD[0]", EntityKind.Object, "HUD", "Untagged", "sauna");
            Check(LocalHierarchy.RemoveFromCheckpoint(world) == 1 && world.Entities.ContainsKey("steam") && world.Entities.ContainsKey("hud"), "Recovery removed world objects with local root names");
        });
        test("repaired checkpoint restarts with purchases, bolts and wiring intact", delegate
        {
            string path = Path.Combine(Path.GetTempPath(), "SummerCoop-restore-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var world = new World { Money = 425, Revision = 9 };
                Add(world, "player", "spawn/player", EntityKind.Object, "PLAYER", "Player", "");
                Entity part = Add(world, "part", "part/starter", EntityKind.Rigidbody, "starter", "PART", "");
                part.Properties["bolt"] = Atom.N(8); part.Properties["wired"] = Atom.B(true);
                Entity purchase = Add(world, "purchase", "item/beer17", EntityKind.Rigidbody, "beer17", "ITEM", ""); purchase.Prefab = "prefab/beer";
                using (var store = new CheckpointStore(path)) store.Checkpoint(world);
                using (var store = new CheckpointStore(path)) { World restored = store.Recover(); Check(LocalHierarchy.RemoveFromCheckpoint(restored) == 1, "Stored personal root was not recovered"); store.Checkpoint(restored); }
                using (var store = new CheckpointStore(path))
                {
                    World restored = store.Recover();
                    Check(restored.Entities.Count == 2 && restored.Money == 425 && restored.Revision == 9 && restored.Entities["part"].Properties["bolt"].Number == 8 && restored.Entities["part"].Properties["wired"].Number == 1 && restored.Entities["purchase"].Prefab == "prefab/beer", "Restart lost shared progress");
                }
            }
            finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
        });
    }
}
