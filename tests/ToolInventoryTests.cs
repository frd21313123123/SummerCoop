using System;
using SummerCoop.Core;

internal static class ToolInventoryTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static Entity Object(World world, string id, string name, string parent, string key)
    {
        Entity entity = new Entity { Id = id, Parent = parent, Key = key, Kind = EntityKind.Object };
        entity.Properties["@name"] = Atom.S(name); world.Entities.Add(id, entity); return entity;
    }
    public static void Run(Action<string, Action> test)
    {
        test("native wrench and ratchet inventories are private while NPC tools and shared cases remain shared", delegate
        {
            foreach (string name in new[] { "spanner set(itemx)", "spanner set(Clone)", "ratchet set(Clone)", "ratchet set" })
                Check(ToolInventory.IsContainer("Tools", name), "Owned case tools were not recognized");
            foreach (string name in new[] { "ToolCart", "SATSUMA", "spanner set invoice", "ratchet settings", "beer case(itemx)", "" })
                Check(!ToolInventory.IsContainer("Tools", name), "Unrelated tools became personal");
            Check(!ToolInventory.IsContainer("Pivot", "spanner set(itemx)"), "Shared lid became a private inventory");
        });
        test("checkpoint repair restores scene tool slots without losing the case, mounts or world progress", delegate
        {
            World world = new World { Id = "tools", Revision = 77, Money = 3100 };
            Entity box = Object(world, "box", "spanner set(itemx)", "", "scene/ITEMS[0]/spanner set(itemx)[0]");
            Entity slots = Object(world, "slots", "Tools", box.Id, box.Key + "/Tools[0]");
            Entity tool = Object(world, "wrench", "spanner(11)(Clone)", slots.Id, slots.Key + "/spanner(11)(Clone)[0]");
            tool.Active = false;
            Object(world, "lid", "Pivot", box.Id, box.Key + "/Pivot[0]");
            Entity engine = Object(world, "engine", "starter(Clone)", "", "part/engine"); engine.Properties["bolt"] = Atom.N(8);
            Entity use = new Entity { Id = "use", Key = box.Key + "|fsm/Use/0", Parent = box.Id, Kind = EntityKind.Fsm };
            use.Properties["v/Tools"] = Atom.R(slots.Id); use.Properties["v/Open"] = Atom.B(true); world.Entities.Add(use.Id, use);
            World before = world.Clone();
            Check(LocalHierarchy.RemoveFromCheckpoint(world) == 2, "Lost slots remain authoritative");
            Check(!world.Entities.ContainsKey(slots.Id) && !world.Entities.ContainsKey(tool.Id), "Host inventory can hide another player's tools");
            Check(CheckpointStore.Equal(before.Entities[box.Id], world.Entities[box.Id]) && world.Entities.ContainsKey("lid")
                && CheckpointStore.Equal(before.Entities[engine.Id], world.Entities[engine.Id]), "Recovery changed shared parts or case");
            Check(!world.Entities[use.Id].Properties.ContainsKey("v/Tools") && world.Entities[use.Id].Properties["v/Open"].Number == 1,
                "Recovery broke the lid or overwrote a local Tools reference with null");
            Check(world.Id == "tools" && world.Revision == 77 && world.Money == 3100 && LocalHierarchy.RemoveFromCheckpoint(world) == 0,
                "Tool repair changed progression or is not idempotent");
        });
        test("purchased case inventory repair survives the wire and preserves NPC tool carts", delegate
        {
            World world = new World { Revision = 19, Money = 2500 };
            Entity box = Object(world, "box", "ratchet set(Clone)", "", "spawn/ratchet");
            Entity slots = Object(world, "slots", "Tools", box.Id, box.Key + "/Tools[0]");
            Object(world, "socket", "ratchet(Clon1)", slots.Id, slots.Key + "/ratchet(Clon1)[0]");
            Entity npc = Object(world, "npc", "ToolCart", "", "scene/REPAIRSHOP[0]/ToolCart[0]");
            Entity npcTools = Object(world, "npc-slots", "Tools", npc.Id, npc.Key + "/Tools[0]");
            Object(world, "npc-wrench", "spanner(11)(Clone)", npcTools.Id, npcTools.Key + "/spanner(11)(Clone)[0]");
            World repaired = Codec.World(Codec.World(world));
            Check(LocalHierarchy.RemoveFromCheckpoint(repaired) == 2 && repaired.Entities.Count == 4, "Removed NPC tools or failed to repair purchased case");
            World reopened = Codec.World(Codec.World(repaired));
            Check(LocalHierarchy.RemoveFromCheckpoint(reopened) == 0 && reopened.Entities.ContainsKey("npc-wrench")
                && reopened.Money == 2500 && reopened.Revision == 19, "Tool repair did not survive reopening");
        });
    }
}
