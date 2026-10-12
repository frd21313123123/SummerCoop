using System;
using System.Collections.Generic;
using System.IO;
using SummerCoop.Core;

internal static class PersonalInteractionTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run(Action<string, Action> test)
    {
        test("personal keys survive reopening and two players cannot overwrite each other's inventory", delegate
        {
            string directory = Path.Combine(Path.GetTempPath(), "summercoop-keys-" + Guid.NewGuid().ToString("N"));
            try
            {
                var initial = new Dictionary<string, int> { { "PlayerKeyHome", 1 } };
                PersonalKeys host = new PersonalKeys(directory, "world", "host", initial), guest = new PersonalKeys(directory, "world", "guest", initial);
                host.Set("PlayerKeyGifu", 2); guest.Set("PlayerKeyHayosiko", 2);
                Check(host.Save() && guest.Save() && !guest.Save(), "Key writes were not independently persisted/coalesced");
                host = new PersonalKeys(directory, "world", "host", initial); guest = new PersonalKeys(directory, "world", "guest", initial);
                Check(host.Get("PlayerKeyGifu") == 2 && host.Get("PlayerKeyHayosiko") == 0 && guest.Get("PlayerKeyGifu") == 0 && guest.Get("PlayerKeyHayosiko") == 2, "Keys leaked between participants or disappeared");
                Check(new PersonalKeys(directory, "another-world", "guest", initial).Get("PlayerKeyHayosiko") == 0, "Keys leaked between worlds");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        test("old checkpoints discard shared key ownership and pickups while preserving money and engine parts", delegate
        {
            World world = new World { Money = 3000, Revision = 91 };
            Entity global = new Entity { Id = "globals", Key = "globals", Kind = EntityKind.Globals }; global.Properties["v/PlayerKeyGifu"] = Atom.N(0); global.Properties["v/PlayerMoney"] = Atom.N(3000); world.Entities.Add(global.Id, global);
            Entity keys = new Entity { Id = "keys", Key = "scene/YARD[0]/UNCLE[0]/GifuKeys[0]", Parent = "uncle" }; keys.Properties["@name"] = Atom.S("GifuKeys"); world.Entities.Add(keys.Id, keys);
            Entity part = new Entity { Id = "part", Key = "part/engine", Kind = EntityKind.Rigidbody }; part.Properties["@name"] = Atom.S("starter(Clone)"); world.Entities.Add(part.Id, part);
            LocalHierarchy.RemoveFromCheckpoint(world);
            Check(!world.Entities.ContainsKey("keys") && !world.Entities["globals"].Properties.ContainsKey("v/PlayerKeyGifu"), "Shared checkpoint can still erase personal keys");
            Check(world.Entities.ContainsKey("part") && world.Money == 3000 && world.Revision == 91, "Shared progress changed during key repair");
        });
        test("owned hand position and rotation survive pose frames and unowned or remote transforms are rejected", delegate
        {
            World world = new World(); Entity part = new Entity { Id = "part", Kind = EntityKind.Rigidbody }; world.Entities.Add(part.Id, part);
            Authority authority = new Authority(world, null, null); authority.AddPlayer("guest", "Guest");
            Check(authority.Execute("guest", new Command { Sequence = 1, Kind = CommandKind.Take, Target = "part" }).Accepted, "Take failed");
            Pose pose = new Pose { Sequence = 1, Held = "part", HeldPosition = new V3(.4f,1.2f,.7f), HeldRotation = new Q4(0,.70710677f,0,.70710677f) };
            Pose decoded = Codec.Pose(Codec.View(Codec.PoseFrame(pose)));
            Check(authority.SetPose("guest", decoded) && world.Players["guest"].HasHeldPose && world.Players["guest"].HeldPosition.X == .4f && world.Players["guest"].HeldRotation.Y == pose.HeldRotation.Y, "Actual hand transform lost");
            decoded.Sequence++; decoded.Held = "someone-elses-part"; Check(!authority.SetPose("guest", decoded), "Unowned object transform accepted");
            decoded.Held = "part"; decoded.HeldPosition = new V3(100,0,0); Check(!authority.SetPose("guest", decoded), "Remote teleport accepted");
            decoded.HeldPosition = new V3(float.NaN,0,0); Check(!authority.SetPose("guest", decoded), "Nonfinite transform accepted");
            authority.Execute("guest", new Command { Sequence = 2, Kind = CommandKind.Drop, Target = "part" });
            Check(!world.Players["guest"].HasHeldPose, "Dropped object retained input lease");
        });
    }
}
