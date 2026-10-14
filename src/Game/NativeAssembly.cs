using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace SummerCoop.Game
{
    public static class NativeAssembly
    {
        // Wheel, trim and other variant slots have HandChild2/3/... too.
        public static bool Accepts(IEnumerable<FsmString> variables, string partName)
        {
            foreach (FsmString variable in variables)
                if (variable.Name.StartsWith("HandChild", StringComparison.Ordinal) && variable.Value == partName) return true;
            return false;
        }
        public static SetParent MountAction(IEnumerable<FsmStateAction> actions)
        {
            foreach (FsmStateAction action in actions)
            {
                SetParent mount = action as SetParent;
                if (mount != null && mount.gameObject.OwnerOption == OwnerDefaultOption.SpecifyGameObject
                    && mount.gameObject.GameObject.Name == "Part") return mount;
            }
            return null;
        }
        public static GameObject MountParent(FsmBinding binding, string state)
        {
            FsmState native = binding.Component.Fsm.GetState(state);
            SetParent action = native == null ? null : MountAction(native.Actions);
            return action == null || action.parent == null ? null : action.parent.Value;
        }
        public static Vector3 LocalOffset(IEnumerable<FsmStateAction> actions, Vector3 value)
        {
            foreach (FsmStateAction action in actions)
            {
                SetPosition position = action as SetPosition;
                if (position == null || position.gameObject.OwnerOption != OwnerDefaultOption.SpecifyGameObject
                    || position.gameObject.GameObject.Name != "Part" || position.space != Space.Self) continue;
                if (!position.vector.IsNone) value = position.vector.Value;
                if (!position.x.IsNone) value.x = position.x.Value;
                if (!position.y.IsNone) value.y = position.y.Value;
                if (!position.z.IsNone) value.z = position.z.Value;
            }
            return value;
        }
        public static void Complete(FsmBinding binding, string state, GameObject part)
        {
            SetParent action = MountAction(binding.Component.Fsm.GetState(state).Actions);
            if (action == null || action.parent.Value == null || part == null) throw new InvalidOperationException("Native mount parent is unavailable");
            Transform parent = action.parent.Value.transform;
            if (WorldRegistry.PersonalObject(parent.gameObject) || parent == part.transform || parent.IsChildOf(part.transform)) throw new InvalidOperationException("Invalid native mount parent");
            part.transform.parent = parent;
            if (action.resetLocalPosition.Value) part.transform.localPosition = Vector3.zero;
            if (action.resetLocalRotation.Value) part.transform.localRotation = Quaternion.identity;
            // Several native End states apply a local offset after SetParent (engine
            // internals included). Read that offset from the game, not a mod table.
            FsmState current = binding.Component.Fsm.GetState(state);
            HashSet<string> seen = new HashSet<string>();
            for (int hop = 0; current != null && hop < 8 && seen.Add(current.Name); hop++)
            {
                part.transform.localPosition = LocalOffset(current.Actions, part.transform.localPosition);
                string next = null; foreach (FsmTransition t in current.Transitions) if (t.EventName == "FINISHED") { next = t.ToState; break; }
                current = next == null ? null : binding.Component.Fsm.GetState(next);
            }
            if (part.transform.parent != parent) throw new InvalidOperationException("Native assembly did not retain its mount");
        }
        public static bool Installed(GameObject part)
        {
            bool installed; return InstallationState(part, out installed) && installed;
        }
        public static bool InstallationState(GameObject part, out bool value)
        {
            value = false;
            foreach (PlayMakerFSM removal in part.GetComponents<PlayMakerFSM>())
            {
                if (removal.FsmName != "Removal") continue;
                FsmGameObject db = removal.FsmVariables.FindFsmGameObject("db_ThisPart");
                PlayMakerFSM data = db == null || db.Value == null ? null : PlayMakerFSM.FindFsmOnGameObject(db.Value, "Data");
                FsmBool installed = data == null ? null : data.FsmVariables.FindFsmBool("Installed");
                if (installed != null) { value = installed.Value; return true; }
            }
            return false;
        }
        public static bool BelongsToSlot(FsmBinding binding, GameObject part)
        {
            foreach (FsmGameObject database in binding.Component.FsmVariables.GameObjectVariables)
            {
                if (!database.Name.StartsWith("db_ThisPart", StringComparison.Ordinal) || database.Value == null) continue;
                PlayMakerFSM data = PlayMakerFSM.FindFsmOnGameObject(database.Value, "Data");
                FsmGameObject nativePart = data == null ? null : data.FsmVariables.FindFsmGameObject("ThisPart");
                FsmBool installed = data == null ? null : data.FsmVariables.FindFsmBool("Installed");
                if (nativePart != null && nativePart.Value == part && installed != null && installed.Value) return true;
            }
            return false;
        }
    }
}
