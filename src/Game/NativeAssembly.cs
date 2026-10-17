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
        public static bool Accepts(PlayMakerFSM f, GameObject part)
        {
            if (part == null) return false;
            if (Accepts(f.FsmVariables.StringVariables, part.name)) return true;
            // Variant slots select Part1/2/... by object reference; the subframe
            // instead uses a literal GetChild name and has no HandChild variable.
            foreach (FsmGameObject variable in f.FsmVariables.GameObjectVariables)
                if (variable.Name.StartsWith("Part", StringComparison.Ordinal) && variable.Name != "Part" && variable.Value == part) return true;
            foreach (FsmState state in f.FsmStates) foreach (FsmStateAction action in state.Actions)
            {
                GetChild child = action as GetChild;
                if (child != null && child.storeResult != null && child.storeResult.Name == "Part" && child.childName.Value == part.name) return true;
            }
            return false;
        }
        public static PlayMakerFSM Database(PlayMakerFSM f, FsmState state)
        {
            foreach (FsmStateAction action in state.Actions)
            {
                SetFsmBool set = action as SetFsmBool;
                if (set == null || set.fsmName.Value != "Data" || set.variableName.Value != "Installed" || !set.setValue.Value) continue;
                GameObject go = set.gameObject.OwnerOption == OwnerDefaultOption.UseOwner ? f.gameObject : set.gameObject.GameObject.Value;
                if (go != null) return PlayMakerFSM.FindFsmOnGameObject(go, "Data");
            }
            return null;
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
        public static SetParent MountAction(FsmStateAction[] actions)
        {
            for (int i = 0; i < actions.Length; i++)
            {
                SetParent mount = actions[i] as SetParent;
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
        public static void PrepareJoint(PlayMakerFSM f, string state)
        {
            FsmState mount = f.Fsm.GetState(state);
            if (mount == null || MountAction(mount.Actions) == null) return;
            // Follow only this mount's incoming native FINISHED chain. Hood
            // variants share a trigger but require different joint types.
            Queue<FsmState> incoming = new Queue<FsmState>(); incoming.Enqueue(mount);
            HashSet<string> seen = new HashSet<string>();
            while (incoming.Count > 0 && seen.Count < 32)
            {
                FsmState setup = incoming.Dequeue();
                if (!seen.Add(setup.Name)) continue;
                foreach (FsmState candidate in f.FsmStates)
                    foreach (FsmTransition transition in candidate.Transitions)
                        if (transition.EventName == "FINISHED" && transition.ToState == setup.Name)
                        {
                            bool jointSetup = false;
                            foreach (FsmStateAction action in candidate.Actions)
                            {
                                AddComponent add = action as AddComponent;
                                if (add != null && add.component.Value.EndsWith("Joint", StringComparison.Ordinal)) jointSetup = true;
                            }
                            if (MountAction(candidate.Actions) == null || jointSetup) incoming.Enqueue(candidate);
                        }
                if (setup == mount) continue;
                bool found = false, missing = false;
                foreach (FsmStateAction action in setup.Actions)
                {
                    AddComponent add = action as AddComponent;
                    if (add == null || !add.component.Value.EndsWith("Joint", StringComparison.Ordinal)) continue;
                    found = true;
                    GameObject go = add.gameObject.OwnerOption == OwnerDefaultOption.UseOwner ? f.gameObject : add.gameObject.GameObject.Value;
                    Component joint = go == null ? null : go.GetComponent(add.component.Value);
                    if (joint == null) missing = true;
                    else if (add.storeComponent != null) add.storeComponent.Value = joint;
                }
                if (missing) foreach (FsmStateAction action in setup.Actions)
                    if (action is AddComponent || action is SetProperty || action is SetHingeJointLimits || action is SetHingeJointProperties) action.OnEnter();
                if (found) return;
            }
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
            Complete(binding, state, part, new string[8]);
        }
        public static Vector3 LocalOffset(FsmStateAction[] actions, Vector3 value)
        {
            for (int i = 0; i < actions.Length; i++)
            {
                SetPosition position = actions[i] as SetPosition;
                if (position == null || position.gameObject.OwnerOption != OwnerDefaultOption.SpecifyGameObject
                    || position.gameObject.GameObject.Name != "Part" || position.space != Space.Self) continue;
                if (!position.vector.IsNone) value = position.vector.Value;
                if (!position.x.IsNone) value.x = position.x.Value;
                if (!position.y.IsNone) value.y = position.y.Value;
                if (!position.z.IsNone) value.z = position.z.Value;
            }
            return value;
        }
        public static void Complete(FsmBinding binding, string state, GameObject part, string[] seen)
        {
            if (seen == null || seen.Length < 8) throw new ArgumentException("Native mount scratch space requires eight states", "seen");
            SetParent action = MountAction(binding.Component.Fsm.GetState(state).Actions);
            if (action == null || action.parent.Value == null || part == null) throw new InvalidOperationException("Native mount parent is unavailable");
            Transform parent = action.parent.Value.transform;
            if (WorldRegistry.PersonalObject(parent.gameObject) || parent == part.transform || parent.IsChildOf(part.transform)) throw new InvalidOperationException("Invalid native mount parent");
            part.transform.parent = parent;
            if (action.resetLocalPosition.Value) part.transform.localPosition = Vector3.zero;
            // A late held/loose pose can tilt a part after native SetParent has
            // already attached it. Match the native reset even for the same parent,
            // just as we do for position; otherwise repair preserves that tilt.
            if (action.resetLocalRotation.Value) part.transform.localRotation = Quaternion.identity;
            // Several native End states apply a local offset after SetParent (engine
            // internals included). Read that offset from the game, not a mod table.
            FsmState current = binding.Component.Fsm.GetState(state);
            int count = 0;
            while (current != null && count < 8)
            {
                bool repeated = false;
                for (int i = 0; i < count; i++) if (seen[i] == current.Name) { repeated = true; break; }
                if (repeated) break;
                seen[count++] = current.Name;
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
            if (part == null) return false;
            PlayMakerFSM use = PlayMakerFSM.FindFsmOnGameObject(part, "Use");
            FsmString corner = use == null ? null : use.FsmVariables.FindFsmString("Corner");
            if (corner != null) { value = corner.Value.Length > 0 || part.transform.parent != null && part.GetComponent<Rigidbody>() == null; return true; }
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
