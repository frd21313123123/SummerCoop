using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace SummerCoop.Game
{
    // Route the configured Use button through the native checks and effect boundary.
    // A client's input FSM can otherwise be left in the host's load/idle state.
    public static class NativeUseInput
    {
        public static string Destination(FsmState state)
        {
            return Destination(state.Actions, state.Transitions);
        }
        public static string Destination(System.Collections.Generic.IEnumerable<FsmStateAction> actions, System.Collections.Generic.IEnumerable<FsmTransition> transitions)
        {
            foreach (FsmStateAction action in actions)
            {
                GetButtonDown button = action as GetButtonDown;
                if (button == null || !button.Enabled || button.buttonName.Value != "Use" || button.sendEvent == null) continue;
                foreach (FsmTransition transition in transitions) if (transition.EventName == button.sendEvent.Name) return transition.ToState;
            }
            return "";
        }
        public static bool Activate(FsmBinding binding)
        {
            if (binding == null || binding.Personal || binding.Component == null || !binding.Component.enabled || !binding.Component.gameObject.activeInHierarchy) return false;
            foreach (FsmState state in binding.Component.FsmStates)
            {
                string destination = Destination(state);
                if (destination == "" || !binding.Requests.Contains(destination)) continue;
                NativeAdapter.ExecuteState(binding, destination); return true;
            }
            return false;
        }
        public static void FromView(WorldRegistry registry, Camera camera)
        {
            if (camera == null) camera = Camera.main;
            if (camera == null) return;
            RaycastHit[] hits = Physics.RaycastAll(camera.transform.position, camera.transform.forward, 1.05f);
            System.Array.Sort(hits, delegate(RaycastHit a, RaycastHit b) { return a.distance.CompareTo(b.distance); });
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider == null) continue;
                for (Transform cursor = hit.collider.transform; cursor != null; cursor = cursor.parent)
                    foreach (PlayMakerFSM fsm in cursor.GetComponents<PlayMakerFSM>())
                        if (Activate(registry.Binding(fsm.Fsm))) return;
                // An opaque foreground object must not allow using an item through it.
                if (!WorldRegistry.PersonalObject(hit.collider.gameObject) && !hit.collider.isTrigger) return;
            }
        }
    }
}
