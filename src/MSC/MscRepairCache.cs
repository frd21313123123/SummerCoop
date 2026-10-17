using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace MWCoop
{
    // Repeated chassis repair must still observe native variables and hierarchy
    // changes, but need not reapply an already correct activation/component.
    internal static class MscRepairCache
    {
        sealed class Target { public GameObject Object; public string Name; public Behaviour Component; }
        static readonly Dictionary<EnableBehaviour, Target> targets = new Dictionary<EnableBehaviour, Target>();
        public static void Clear() { targets.Clear(); }
        public static void Forget(EnableBehaviour action) { targets.Remove(action); }
        static bool ActivationMatches(Transform root, bool value)
        {
            if (root.gameObject.activeSelf != value) return false;
            // Native ActivateGameObject recursively allocates a Transform
            // enumerator for every node. Indexed reads also detect new children.
            for (int i = 0; i < root.childCount; i++) if (!ActivationMatches(root.GetChild(i), value)) return false;
            return true;
        }
        internal static bool AlreadyApplied(ActivateGameObject action, GameObject go)
        {
            // Preserve the action's private OnExit bookkeeping when requested.
            if (go == null || action.resetOnExit || action.activate == null || action.recursive == null) return false;
            return action.recursive.Value ? ActivationMatches(go.transform, action.activate.Value) : go.activeSelf == action.activate.Value;
        }
        internal static bool AlreadyApplied(EnableBehaviour action, GameObject go)
        {
            if (go == null || action.resetOnExit == null || action.resetOnExit.Value || action.enable == null) return false;
            Behaviour component = action.component as Behaviour;
            if (action.component == null)
            {
                if (action.behaviour == null) return false;
                string name = action.behaviour.Value;
                Target target;
                if (!targets.TryGetValue(action, out target)) targets[action] = target = new Target();
                if (target.Object != go || target.Name != name || target.Component == null)
                {
                    target.Object = go; target.Name = name;
                    target.Component = go.GetComponent(name) as Behaviour;
                }
                component = target.Component;
            }
            return component != null && component.enabled == action.enable.Value;
        }
        public static void Apply(FsmStateAction action)
        {
            ActivateGameObject activate = action as ActivateGameObject;
            if (activate != null)
            {
                GameObject go = activate.Fsm.GetOwnerDefaultTarget(activate.gameObject);
                if (!AlreadyApplied(activate, go)) activate.OnEnter();
                else if (!activate.everyFrame) activate.Finish();
                return;
            }
            EnableBehaviour enable = action as EnableBehaviour;
            if (enable != null)
            {
                GameObject go = enable.Fsm.GetOwnerDefaultTarget(enable.gameObject);
                if (!AlreadyApplied(enable, go)) enable.OnEnter();
                else enable.Finish();
            }
        }
    }
}
