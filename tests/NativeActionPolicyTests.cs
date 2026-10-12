using System;
using System.Collections.Generic;
using SummerCoop.Core;

internal static class NativeActionPolicyTests
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static NativeActionInfo Action(string name, bool personal, bool writes) { return new NativeActionInfo { Name = name, PersonalTarget = personal, WritesPersonal = writes }; }
    public static void Run(Action<string, Action> test)
    {
        test("synchronous bolt replay and delayed beer replay release the next native action", delegate
        {
            NativeReplayTracker<string> replay = new NativeReplayTracker<string>();
            for (int scroll = 0; scroll < 3; scroll++)
            {
                Check(!replay.Contains("bolt"), "Next scroll was still a personal replay");
                replay.Transition("bolt", "Screw 2", ""); replay.Transition("bolt", "Stages 2", "");
                Check(replay.Contains("bolt"), "Effect chain lost its actor scope");
                replay.Transition("bolt", "Idle", "");
            }
            replay.Transition("beer", "Remove bottle", "Wait player"); replay.Transition("beer", "Play anim", "Wait player");
            Check(replay.Contains("beer"), "Asynchronous drink lost its actor scope before completion");
            replay.Transition("beer", "Check bottles", "Wait player"); replay.Transition("beer", "Wait player", "Wait player");
            Check(!replay.Contains("beer") && !replay.Contains("bolt"), "Actor marker survived terminal transition");
        });
        test("moped seat, helmet and gear display stay local without making kickstart or vehicle physics personal", delegate
        {
            foreach (string name in new[] { "PlayerTrigger", "HelmetSim", "GearIndicator", "BeeDeath", "HeadForce" }) Check(NativeActionPolicy.PersonalFsm(name), "Local driving FSM filtered: " + name);
            foreach (string name in new[] { "Use", "Kickstart", "Engine", "Drivetrain", "Screw" }) Check(!NativeActionPolicy.PersonalFsm(name), "Shared action became local: " + name);
        });
        test("native beer effect boundary changes stock once and never disables the host actor", delegate
        {
            // Functional action signatures from native Remove bottle / Play anim.
            NativeActionInfo[] remove = { Action("IntAdd", false, false), Action("SetBoolValue", false, true), Action("GameObjectHasChildren", false, false), Action("GetRandomChild", false, false), Action("DestroyObject", false, false) };
            NativeActionInfo[] animate = { Action("EnableFSM", true, false), Action("GetChild", true, false), Action("SendEvent", true, false), Action("Wait", false, false) };
            int hostBottles = 24, guestAnimations = 0; bool hostControls = true;
            foreach (NativeActionInfo action in remove)
                if (NativeActionPolicy.Allowed(action, NativeActionScope.SharedActor) && action.Name == "IntAdd") hostBottles--;
            foreach (NativeActionInfo action in animate)
                if (NativeActionPolicy.Allowed(action, NativeActionScope.SharedActor) && action.Name == "EnableFSM") hostControls = false;
            foreach (NativeActionInfo action in remove)
                Check(action.Name != "IntAdd" || !NativeActionPolicy.Allowed(action, NativeActionScope.PersonalReplay), "Guest applied the shared bottle count a second time");
            foreach (NativeActionInfo action in animate)
                if (NativeActionPolicy.Allowed(action, NativeActionScope.PersonalReplay) && action.Name == "SendEvent") guestAnimations++;
            Check(hostBottles == 23 && hostControls && guestAnimations == 1, "Actor and shared consumable effects were mixed");
            Check(NativeActionPolicy.Pollable(animate) && !NativeActionPolicy.Pollable(remove), "Beer animation or host boundary classification incorrect");
        });
        test("remote actor cannot write host stop, drink, menu or driving globals", delegate
        {
            foreach (string variable in new[] { "PlayerStop", "PlayerDrinkOn", "PlayerCarControl", "PlayerSeated", "GUIuse" })
            {
                NativeActionInfo action = Action("SetBoolValue", false, NativeActionPolicy.PersonalVariable(variable));
                Check(!NativeActionPolicy.Allowed(action, NativeActionScope.SharedActor) && NativeActionPolicy.Allowed(action, NativeActionScope.PersonalReplay), "Personal global leaked: " + variable);
            }
            Check(!NativeActionPolicy.PersonalVariable("PlayerMoney") && !NativeActionPolicy.PersonalVariable("PlayerKeyGarage"), "Shared wallet or keys became personal");
        });
        test("native bolt actions remain host effects while input and actor feedback remain local", delegate
        {
            NativeActionInfo[] tighten = { Action("IntAdd", false, false), Action("IntCompare", false, false) };
            NativeActionInfo[] publish = { Action("MasterAudioPlaySound", false, false), Action("AddFsmFloat", false, false), Action("ArrayListSet", false, false) };
            Check(!NativeActionPolicy.Pollable(tighten), "Bolt effect executed as client polling");
            foreach (NativeActionInfo action in publish) Check(NativeActionPolicy.Allowed(action, NativeActionScope.SharedActor) && !NativeActionPolicy.Allowed(action, NativeActionScope.PersonalReplay), "Shared bolt stage/list modified by guest");
            Check(NativeActionPolicy.Pollable(new[] { Action("GetAxis", false, false), Action("SetMaterialColor", false, false) }), "Mixed native scroll input blocked");
            Check(NativeActionPolicy.ReplayFinished("Idle", "") && NativeActionPolicy.ReplayFinished("Wait player", "Wait player") && !NativeActionPolicy.ReplayFinished("Wait 4", ""), "Bolt replay did not release the next scroll request");
        });
        test("pending native action waits for its matching host revision and completes once", delegate
        {
            PendingNativeActions pending = new PendingNativeActions();
            Command command = new Command { Sequence = 7, Kind = CommandKind.Interact, Target = "beer", Argument = "Remove bottle" }; pending.Add(command);
            Check(pending.ContainsTarget("beer") && pending.Ready(100).Count == 0, "Unconfirmed action replayed");
            pending.Complete(new CommandResult { Sequence = 999, Accepted = true, Revision = 4 });
            pending.Complete(new CommandResult { Sequence = 7, Accepted = true, Revision = 12 });
            Check(pending.Ready(11).Count == 0, "Result overtook shared delta and replayed early");
            List<PendingNativeAction> ready = pending.Ready(12);
            Check(ready.Count == 1 && ready[0].State == "Remove bottle" && pending.Ready(50).Count == 0 && !pending.ContainsTarget("beer"), "Native result replayed twice or lost its state");
            pending.Add(new Command { Sequence = 8, Target = "beer" }); pending.Complete(new CommandResult { Sequence = 8, Accepted = false });
            Check(!pending.ContainsTarget("beer") && pending.Ready(100).Count == 0, "Rejected action played actor effects");
        });
        test("beer slots fill only missing positions without overlapping surviving bottles", delegate
        {
            List<V3> templates = new List<V3>(); for (int i = 0; i < 24; i++) templates.Add(new V3(i % 6, 0, i / 6));
            List<V3> native = new List<V3>(); foreach (int slot in new[] { 23, 0, 7, 12, 1, 8, 13, 2, 9, 14 }) native.Add(templates[slot]);
            for (int consumed = 0; consumed <= 24; consumed++)
            {
                int remaining = ConsumableStock.Remaining(24, consumed); int[] assigned = ConsumableStock.AssignSlots(native, templates, remaining);
                HashSet<int> reused = new HashSet<int>();
                Check(assigned.Length == remaining, "Visual count does not match authoritative stock");
                for (int slot = 0; slot < assigned.Length; slot++)
                    if (assigned[slot] >= 0) Check(reused.Add(assigned[slot]) && native[assigned[slot]].Distance(templates[slot]) == 0, "A native bottle was duplicated or drawn in a different slot");
                Check(remaining < 3 || assigned[0] == 1 && assigned[1] == 4 && assigned[2] == 7, "Survivor order replaced slot identity");
            }
            Check(ConsumableStock.Remaining(24, int.MinValue) == 24 && ConsumableStock.Remaining(24, int.MaxValue) == 0, "Invalid native stock overflowed");
        });
    }
}
