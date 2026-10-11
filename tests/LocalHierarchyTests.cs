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
        test("native menu recovery preserves world progress and the original input snapshot", delegate
        {
            var world = new World { Id = "menu-world", Revision = 24, Money = 1250 };
            Add(world, "systems", "scene/Systems[0]", EntityKind.Object, "Systems", "Untagged", "");
            Entity options = Add(world, "options", "scene/Systems[0]/Options[0]", EntityKind.Object, "Options", "Untagged", "systems");
            options.Properties["@array/0/count"] = Atom.N(2);
            options.Properties["@array/0/0"] = Atom.S("Use=Mouse0"); options.Properties["@array/0/1"] = Atom.S("Pick=Mouse1");
            Entity controls = Add(world, "controls", options.Key + "|fsm/Controls/0", EntityKind.Fsm, "", "", "options");
            controls.Properties["v/SteeringSensitivity"] = Atom.N(0.75);
            Add(world, "menu", "scene/Systems[0]/OptionsMenu[0]", EntityKind.Object, "OptionsMenu", "Untagged", "systems");
            Add(world, "button", "scene/Systems[0]/OptionsMenu[0]/Menu[0]/Close[0]", EntityKind.Object, "Close", "Untagged", "menu");
            Add(world, "button-fsm", "scene/Systems[0]/OptionsMenu[0]/Menu[0]/Close[0]|fsm/Button/0", EntityKind.Fsm, "", "", "button");
            Entity initialize = Add(world, "initialize", "scene/Systems[0]/InitializeControls[0]", EntityKind.Object, "InitializeControls", "Untagged", "systems");
            Add(world, "initialize-fsm", initialize.Key + "|fsm/Setup/0", EntityKind.Fsm, "", "", "initialize");
            Entity car = Add(world, "car", "vehicle/SATSUMA", EntityKind.Vehicle, "SATSUMA", "Untagged", "");
            car.Properties["bolt"] = Atom.N(8); car.Properties["wired"] = Atom.B(true);
            Entity shop = Add(world, "shop", "scene/STORE[0]|fsm/CashRegister/0", EntityKind.Fsm, "", "", "");
            shop.Properties["v/Purchases"] = Atom.N(3);
            Add(world, "computer", "scene/COMPUTER[0]", EntityKind.Object, "COMPUTER", "Untagged", "");
            Entity hud = Add(world, "computer-hud", "scene/COMPUTER[0]/Game[0]/HUD[0]", EntityKind.Object, "HUD", "Untagged", "computer");
            hud.Properties["v/Score"] = Atom.N(150);
            World before = world.ReferenceSnapshot();
            Check(LocalHierarchy.RemoveFromCheckpoint(world) == 7 && world.Entities.Count == 5, "Menu/control subtree was not removed completely");
            Check(world.Id == "menu-world" && world.Revision == 24 && world.Money == 1250, "Menu recovery changed shared world metadata");
            foreach (string id in new[] { "systems", "car", "shop", "computer", "computer-hud" })
                Check(object.ReferenceEquals(before.Entities[id], world.Entities[id]), "Recovery rewrote an unrelated shared entity: " + id);
            Check(world.Entities["car"].Properties["bolt"].Number == 8 && world.Entities["car"].Properties["wired"].Number == 1
                && world.Entities["shop"].Properties["v/Purchases"].Number == 3 && world.Entities["computer-hud"].Properties["v/Score"].Number == 150, "Recovery lost shared gameplay progress");
            Check(before.Entities["options"].Properties["@array/0/count"].Number == 2
                && before.Entities["options"].Properties["@array/0/0"].Text == "Use=Mouse0"
                && before.Entities["options"].Properties["@array/0/1"].Text == "Pick=Mouse1"
                && before.Entities["controls"].Properties["v/SteeringSensitivity"].Number == 0.75 && before.Entities.Count == 12, "Repair mutated copied options, bindings, or the previous snapshot");
            Check(LocalHierarchy.RemoveFromCheckpoint(world) == 0, "Repeated menu recovery changed the world");
        });
        test("obsolete menu references are removed without mutating shared snapshots", delegate
        {
            var world = new World();
            Add(world, "menu", "scene/Systems[0]/OptionsMenu[0]", EntityKind.Object, "OptionsMenu", "Untagged", "");
            Add(world, "camera", "scene/Systems[0]/OptionsMenu[0]/CAM[0]", EntityKind.Object, "CAM", "Untagged", "menu");
            Entity consumer = Add(world, "consumer", "scene/Systems[0]/Other[0]|fsm/Use/0", EntityKind.Fsm, "", "", "");
            consumer.Properties["v/Menu"] = Atom.R("menu"); consumer.Properties["v/MenuCamera"] = Atom.R("camera#UnityEngine.Transform");
            consumer.Properties["v/SharedCamera"] = Atom.R("shared-camera#UnityEngine.Transform");
            consumer.Properties["v/MenuTexture"] = Atom.R("asset/UnityEngine.Texture2D/OptionsMenu");
            consumer.Properties["v/Value"] = Atom.N(42);
            Entity item = Add(world, "item", "spawn/shop-purchase", EntityKind.Rigidbody, "beer", "ITEM", "camera"); item.Prefab = "prefab/beer";
            World before = world.ReferenceSnapshot();
            Check(LocalHierarchy.RemoveFromCheckpoint(world) == 2, "Wrong menu reference recovery size");
            Entity repaired = world.Entities["consumer"];
            Check(!repaired.Properties.ContainsKey("v/Menu") && !repaired.Properties.ContainsKey("v/MenuCamera"), "Removed menu object/component reference survived");
            Check(repaired.Properties["v/SharedCamera"].Text == "shared-camera#UnityEngine.Transform"
                && repaired.Properties["v/MenuTexture"].Text == "asset/UnityEngine.Texture2D/OptionsMenu"
                && repaired.Properties["v/Value"].Number == 42, "Reference recovery changed unrelated values or assets");
            Check(world.Entities["item"].Parent == "" && world.Entities["item"].Position.X == 1 && world.Entities["item"].Prefab == "prefab/beer", "Recovered item lost its prefab or world position");
            Check(before.Entities["consumer"].Properties.ContainsKey("v/Menu") && before.Entities["consumer"].Properties.ContainsKey("v/MenuCamera")
                && before.Entities["item"].Parent == "camera" && before.Entities.ContainsKey("menu"), "Menu repair mutated the retained snapshot");
        });
        test("similarly named gameplay objects outside the native settings roots survive", delegate
        {
            var world = new World();
            Add(world, "menu", "scene/Systems[0]/OptionsMenu[0]", EntityKind.Object, "OptionsMenu", "Untagged", "");
            Add(world, "close", "scene/Systems[0]/OptionsMenu[0]/Close[0]", EntityKind.Object, "Close", "Untagged", "menu");
            Add(world, "outside", "scene/YARD[0]/OptionsMenu[0]", EntityKind.Object, "OptionsMenu", "Untagged", "");
            Add(world, "nested", "scene/Systems[0]/Other[0]/OptionsMenu[0]", EntityKind.Object, "OptionsMenu", "Untagged", "");
            Add(world, "similar", "scene/Systems[0]/OptionsMenu[0]-sign", EntityKind.Object, "OptionsMenu", "Untagged", "");
            Add(world, "other-ordinal", "scene/Systems[0]/OptionsMenu[1]", EntityKind.Object, "OptionsMenu", "Untagged", "");
            Add(world, "outside-options", "scene/STORE[0]/Options[0]", EntityKind.Object, "Options", "Untagged", "");
            Add(world, "outside-controls", "scene/COMPUTER[0]/InitializeControls[0]", EntityKind.Object, "InitializeControls", "Untagged", "");
            World before = world.ReferenceSnapshot();
            Check(LocalHierarchy.RemoveFromCheckpoint(world) == 2 && world.Entities.Count == 6, "Settings scope deleted similarly named gameplay objects");
            foreach (Entity survivor in world.Entities.Values) Check(object.ReferenceEquals(before.Entities[survivor.Id], survivor), "Settings scope rewrote a surviving object");
            Check(LocalHierarchy.IsPersonalSystemName("Systems", "OptionsMenu") && LocalHierarchy.IsPersonalSystemName("Systems", "Options")
                && LocalHierarchy.IsPersonalSystemName("Systems", "InitializeControls") && !LocalHierarchy.IsPersonalSystemName("STORE", "OptionsMenu")
                && !LocalHierarchy.IsPersonalSystemName("Systems", "OptionsMenuSign"), "Native settings name matching extends beyond the local nodes");
        });
        test("seated first-person camera is personal while carried parts remain shared", delegate
        {
            var world = new World { Revision = 17, Money = 3000 };
            Add(world, "moped", "vehicle/JONNEZ", EntityKind.Vehicle, "JONNEZ", "Untagged", "");
            Add(world, "pivot", "vehicle/JONNEZ/CameraPivot[0]", EntityKind.Object, "CameraPivot", "Untagged", "moped");
            Add(world, "camera", "spawn/detached-camera", EntityKind.Object, "FPSCamera", "MainCamera", "pivot");
            Add(world, "hand", "spawn/detached-camera/Hand[0]", EntityKind.Object, "Hand", "Untagged", "camera");
            Add(world, "hand-fsm", "spawn/detached-camera/Hand[0]|fsm/PickUp/0", EntityKind.Fsm, "", "", "hand");
            Entity part = Add(world, "part", "spawn/detached-camera/Hand[0]/starter[0]", EntityKind.Rigidbody, "starter(Clone)", "PART", "hand"); part.Prefab = "prefab/starter";
            Entity use = Add(world, "use", "vehicle/JONNEZ/DriveTrigger[0]|fsm/PlayerTrigger/0", EntityKind.Fsm, "", "", "moped");
            use.Properties["v/Camera"] = Atom.R("camera#UnityEngine.Camera"); use.Properties["v/Gear"] = Atom.N(1);
            World before = world.ReferenceSnapshot();
            Check(LocalHierarchy.RemoveFromCheckpoint(world) == 3, "Detached first-person camera was kept in the shared world");
            Check(world.Entities.Count == 4 && world.Entities["part"].Parent == "" && world.Entities["part"].Prefab == "prefab/starter", "Camera recovery removed a held part or its identity");
            Check(!world.Entities["use"].Properties.ContainsKey("v/Camera") && world.Entities["use"].Properties["v/Gear"].Number == 1, "Camera recovery changed shared moped state");
            Check(before.Entities.Count == 7 && before.Entities["part"].Parent == "hand" && before.Entities["use"].Properties.ContainsKey("v/Camera"), "Camera recovery mutated the retained snapshot");
            Check(LocalHierarchy.IsNestedName("FPSCamera") && LocalHierarchy.IsNestedName("FPSCamera(Clone)") && !LocalHierarchy.IsNestedName("CameraPivot"), "Camera matching includes shared vehicle pivots");
        });
        test("personal death lifecycle is removed while vehicle damage and NPC deaths survive", delegate
        {
            var world = new World();
            Add(world, "death", "scene/Systems[0]/Death[0]", EntityKind.Object, "Death", "Untagged", "");
            Add(world, "death-fsm", "scene/Systems[0]/Death[0]|fsm/Activate Dead Body/0", EntityKind.Fsm, "", "", "death");
            Add(world, "screen", "scene/Systems[0]/Death[0]/GameOverScreen[0]", EntityKind.Object, "GameOverScreen", "Untagged", "death");
            Add(world, "moped", "vehicle/JONNEZ", EntityKind.Vehicle, "JONNEZ", "Untagged", "");
            Add(world, "damage", "vehicle/JONNEZ/DriverHeadPivot[0]|fsm/Death/0", EntityKind.Fsm, "", "", "moped");
            Add(world, "npc", "scene/NPC[0]/Death[0]", EntityKind.Object, "Death", "Untagged", "");
            Check(LocalHierarchy.RemoveFromCheckpoint(world) == 3 && world.Entities.Count == 3 && world.Entities.ContainsKey("damage") && world.Entities.ContainsKey("npc"), "Death repair affected shared damage or NPC state");
            Check(LocalHierarchy.IsPersonalSystemName("Systems", "Death") && !LocalHierarchy.IsPersonalSystemName("NPC", "Death"), "Personal death scope is too broad");
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
