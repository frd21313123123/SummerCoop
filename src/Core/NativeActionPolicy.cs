using System;
using System.Collections.Generic;

namespace SummerCoop.Core
{
    public enum NativeActionScope { InputPolling, SharedActor, PersonalReplay }
    public sealed class NativeActionInfo
    {
        public string Name;
        public bool PersonalTarget, WritesPersonal;
        public bool ReadOnly { get { return NativeActionPolicy.ReadOnly(Name); } }
    }
    public static class NativeActionPolicy
    {
        private static readonly HashSet<string> Reads = new HashSet<string>(StringComparer.Ordinal)
        {
            "MousePickEvent", "MousePick", "Raycast", "GetMouseButton", "GetMouseButtonDown", "GetMouseButtonUp", "GetButton", "GetButtonDown", "GetButtonUp", "GetKey", "GetKeyDown", "GetAxis", "GetAxisVector",
            "GetDistance", "GetPosition", "GetRotation", "GetScale", "GetVelocity", "GetSpeed", "GetName", "GetOwner", "GetChild", "GetParent", "GetComponent", "GetStringLength", "GetSubstring", "GetStringRight", "BuildString",
            "GetFsmBool", "GetFsmFloat", "GetFsmInt", "GetFsmString", "GetFsmGameObject", "GetRandomChild", "GameObjectHasChildren", "GameObjectIsNull", "GameObjectCompare", "GameObjectCompareTag", "GameObjectIsChildOf", "HasComponent",
            "FloatCompare", "FloatOperator", "FloatClamp", "FloatMultiply", "FloatAbs", "IntCompare", "IntOperator", "IntClamp", "BoolTest", "BoolAllTrue", "BoolAnyTrue", "BoolNoneTrue", "StringCompare", "TriggerEvent", "Wait",
            "SetBoolValue", "SetStringValue", "SetFloatValue", "SetIntValue", "SetGameObject", "SetVector3Value", "ArrayListGet", "ConvertStringToInt", "ConvertIntToString"
        };
        private static readonly HashSet<string> Input = new HashSet<string>(StringComparer.Ordinal)
        { "MousePickEvent", "MousePick", "GetMouseButton", "GetMouseButtonDown", "GetMouseButtonUp", "GetButton", "GetButtonDown", "GetButtonUp", "GetKeyDown", "GetAxis", "TriggerEvent" };
        private static readonly HashSet<string> Setup = new HashSet<string>(StringComparer.Ordinal)
        { "ActivateGameObject", "EnableFSM", "SetProperty", "SetRotation", "SetMaterialColor" };
        public static bool ReadOnly(string name) { return Reads.Contains(name); }
        public static bool ReplayFinished(string state, string waiting) { return state == "Idle" || waiting != "" && state == waiting; }
        public static bool PersonalFsm(string name)
        {
            return name == "PlayerTrigger" || name == "Death" || name == "BeeDeath" || name == "HeadForce" || name == "HelmetSim" || name == "GearIndicator";
        }
        public static bool Allowed(NativeActionInfo action, NativeActionScope scope)
        {
            if (scope == NativeActionScope.SharedActor) return !action.WritesPersonal && (action.ReadOnly || !action.PersonalTarget);
            return action.ReadOnly || action.PersonalTarget || action.WritesPersonal;
        }
        public static bool Pollable(IEnumerable<NativeActionInfo> actions)
        {
            bool input = false, blocked = false;
            foreach (NativeActionInfo action in actions)
            {
                if (Input.Contains(action.Name)) input = true;
                if (Allowed(action, NativeActionScope.InputPolling)) continue;
                if (!Setup.Contains(action.Name)) return false;
                blocked = true;
            }
            return !blocked || input;
        }
        public static bool PersonalVariable(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith("Player", StringComparison.Ordinal)) return name != "PlayerMoney" && name != "PlayerNetIncome" && name != "PlayerFines" && name != "PlayerFinesDay" && !name.StartsWith("PlayerKey", StringComparison.Ordinal);
            return name.StartsWith("GUI", StringComparison.Ordinal) || name.StartsWith("Options", StringComparison.Ordinal) || name.StartsWith("Settings", StringComparison.Ordinal) || name.StartsWith("WWW", StringComparison.Ordinal)
                || name == "POV" || name == "SavePlayer" || name == "SavePlayerCam" || name == "ItemInHand" || name == "ItemPivot" || name == "PickedPart" || name == "LookAtTarget" || name.StartsWith("Tool", StringComparison.Ordinal)
                || name == "SaveSlot" || name == "SaveCarparts" || name == "GameVolume" || name == "InterfaceMute" || name.StartsWith("SoundAmbience", StringComparison.Ordinal);
        }
    }
    // A replay can finish synchronously inside the command context. Release it
    // at the terminal transition too, so the next input becomes a new request.
    public sealed class NativeReplayTracker<T>
    {
        private readonly HashSet<T> active = new HashSet<T>();
        public bool Contains(T key) { return active.Contains(key); }
        public void Transition(T key, string state, string waiting)
        {
            if (NativeActionPolicy.ReplayFinished(state, waiting)) active.Remove(key);
            else active.Add(key);
        }
        public void Clear() { active.Clear(); }
    }
    public sealed class PendingNativeAction
    {
        public long Sequence, Revision;
        public string Target, State;
        public CommandKind Kind;
        public bool Accepted;
    }
    public sealed class PendingNativeActions
    {
        private readonly Dictionary<long, PendingNativeAction> actions = new Dictionary<long, PendingNativeAction>();
        public bool ContainsTarget(string target) { foreach (PendingNativeAction action in actions.Values) if (action.Target == target) return true; return false; }
        public void Add(Command command) { actions.Add(command.Sequence, new PendingNativeAction { Sequence = command.Sequence, Target = command.Target, State = command.Argument, Kind = command.Kind }); }
        public void Complete(CommandResult result)
        {
            PendingNativeAction action; if (!actions.TryGetValue(result.Sequence, out action)) return;
            if (!result.Accepted) { actions.Remove(result.Sequence); return; }
            action.Accepted = true; action.Revision = result.Revision;
        }
        public List<PendingNativeAction> Ready(long revision)
        {
            List<PendingNativeAction> ready = new List<PendingNativeAction>();
            foreach (PendingNativeAction action in actions.Values) if (action.Accepted && action.Revision <= revision) ready.Add(action);
            foreach (PendingNativeAction action in ready) actions.Remove(action.Sequence);
            return ready;
        }
        public void Clear() { actions.Clear(); }
    }
    public static class ConsumableStock
    {
        public static int Remaining(int capacity, int consumed) { capacity = Math.Max(0, capacity); return capacity - Math.Max(0, Math.Min(capacity, consumed)); }
        public static bool SlotVisible(int slot, int remaining) { return slot >= 0 && slot < remaining; }
        public static int[] AssignSlots(IList<V3> native, IList<V3> template, int remaining)
        {
            int[] slots = new int[Math.Max(0, Math.Min(template.Count, remaining))];
            bool[] used = new bool[native.Count];
            for (int slot = 0; slot < slots.Length; slot++)
            {
                slots[slot] = -1;
                for (int bottle = 0; bottle < native.Count; bottle++)
                    if (!used[bottle] && native[bottle].Distance(template[slot]) < 0.002f) { slots[slot] = bottle; used[bottle] = true; break; }
            }
            return slots;
        }
    }
}
