using System;
using SummerCoop.Core;

internal static class ClientInteractionRegressionTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run(Action<string, Action> test)
    {
        test("assembly wins over native drop in either FSM update order", delegate
        {
            foreach (bool dropFirst in new[] { true, false })
            {
                var release = new DeferredHandRelease<object>(); var pending = new PendingNativeActions(); object hand = new object();
                if (dropFirst) release.Queue(hand, "Drop part", "starter", 20);
                pending.Add(new Command { Sequence = 1, Kind = CommandKind.Attach, Target = "slot", Argument = "Assemble" }); release.Cancel();
                if (!dropFirst && !pending.ContainsKind(CommandKind.Attach)) release.Queue(hand, "Drop part", "starter", 20);
                object actual; string state, item;
                Check(!release.Take(21, "starter", pending.ContainsKind(CommandKind.Attach), out actual, out state, out item), "Same click dropped the part before attachment");
                pending.Complete(new CommandResult { Sequence = 1, Accepted = false });
                Check(!release.Take(22, "starter", pending.ContainsKind(CommandKind.Attach), out actual, out state, out item), "Rejected attachment released the hand");
            }
        });
        test("ordinary drop waits one frame and runs exactly once", delegate
        {
            var release = new DeferredHandRelease<object>(); object hand = new object(); object actual; string state, item;
            release.Queue(hand, "Drop part 2", "part", 12); release.Queue(hand, "Drop part", "part", 12);
            Check(!release.Take(12, "part", false, out actual, out state, out item), "Drop overtook same-frame assembly input");
            Check(release.Take(13, "part", false, out actual, out state, out item) && actual == hand && state == "Drop part 2" && item == "part", "Native throw/drop request changed");
            Check(!release.Take(14, "part", false, out actual, out state, out item), "Drop executed twice");
        });
        test("pending hand release cannot drop a replacement item or survive session cleanup", delegate
        {
            var release = new DeferredHandRelease<object>(); object actual; string state, item;
            release.Queue(new object(), "Drop part", "old-part", 1);
            Check(!release.Take(2, "new-part", false, out actual, out state, out item) && !release.Pending, "Old request dropped a different part");
            release.Queue(new object(), "Drop part", "new-part", 3); release.Cancel();
            Check(!release.Take(4, "new-part", false, out actual, out state, out item), "Disconnected hand request survived");
        });
        test("remote assembly cannot replace its Part with the host hand", delegate
        {
            var readHand = new NativeActionInfo { Name = "GetChild", PersonalTarget = true };
            var readPart = new NativeActionInfo { Name = "GetName", PersonalTarget = false };
            Check(!NativeActionPolicy.Allowed(readHand, NativeActionScope.SharedActor)
                && NativeActionPolicy.Allowed(readHand, NativeActionScope.PersonalReplay)
                && NativeActionPolicy.Allowed(readPart, NativeActionScope.SharedActor), "Actor read crossed into the host hand");
            foreach (string name in new[] { "Part", "Item", "PickedObject", "RaycastHitObject" })
                Check(NativeActionPolicy.SharedHandReference(name), "Carried object was inferred as a personal hand: " + name);
            Check(!NativeActionPolicy.SharedHandReference("Hand") && !NativeActionPolicy.SharedHandReference("FPSCamera"), "Actor animation references became shared");
        });
        test("held collision layers replicate without changing prior snapshots or mounted state", delegate
        {
            var world = new World(); var part = new Entity { Id = "part", Key = "part/starter", Kind = EntityKind.Rigidbody };
            part.Properties["@layer"] = Atom.N(19); world.Entities.Add(part.Id, part);
            World loose = world.ReferenceSnapshot(), replica = world.Clone();
            Entity held = part.Clone(); held.Properties["@layer"] = Atom.N(16); world.Entities[part.Id] = held; world.Revision++;
            CheckpointStore.ApplyDelta(replica, Codec.World(Codec.World(CheckpointStore.Difference(loose, world))));
            Check(replica.Entities[part.Id].Properties["@layer"].Number == 16 && loose.Entities[part.Id].Properties["@layer"].Number == 19, "Held physics layer did not converge or mutated loose state");
            World before = world.ReferenceSnapshot(); Entity mounted = held.Clone(); mounted.Properties["@layer"] = Atom.N(13); mounted.Parent = "mount";
            world.Entities[part.Id] = mounted; world.Revision++;
            CheckpointStore.ApplyDelta(replica, Codec.World(Codec.World(CheckpointStore.Difference(before, world))));
            Check(replica.Entities[part.Id].Properties["@layer"].Number == 13 && replica.Entities[part.Id].Parent == "mount"
                && before.Entities[part.Id].Properties["@layer"].Number == 16, "Attachment reused the held layer or detached the mount");
        });
        test("host can reclaim beer input without taking actor scope from another case", delegate
        {
            var replay = new NativeReplayTracker<string>();
            replay.Transition("shared-case", "Remove bottle", "Wait player"); replay.Transition("other-case", "Play anim", "Wait player");
            replay.Release("shared-case");
            Check(!replay.Contains("shared-case") && replay.Contains("other-case"), "Host use kept the guest marker or reset unrelated drinking");
            replay.Transition("shared-case", "Play anim", "Wait player"); replay.Transition("shared-case", "Wait player", "Wait player");
            Check(!replay.Contains("shared-case"), "Beer case required host MousePick before releasing guest scope");
        });
    }
}
