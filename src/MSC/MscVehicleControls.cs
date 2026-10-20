using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace MWCoop
{
    internal static class MscVehicleControls
    {
        static readonly HashSet<PlayMakerFSM> prepared = new HashSet<PlayMakerFSM>();
        static PlayMakerFSM ignition;
        static MousePickEvent ignitionPick;
        internal static bool PlayerObject(Transform transform)
        {
            for (Transform t = transform; t != null; t = t.parent)
                if (t.name == "PLAYER") return true;
            return false;
        }
        internal static void Reset() { prepared.Clear(); ignition = null; ignitionPick = null; }
        internal static bool IgnitionTargeted()
        {
            return ignition != null && ignition.enabled && ignition.gameObject.activeInHierarchy && ignitionPick != null
                && ActionHelpers.IsMouseOver(ignition.gameObject, ignitionPick.rayDistance.Value,
                    ActionHelpers.LayerArrayToLayerMask(ignitionPick.layerMask, ignitionPick.invertMask.Value));
        }
        sealed class HornPick : MousePickEvent
        {
            bool Blocked()
            {
                // Ignition uses layer 13, the wheel uses layer 19. Native picks
                // can therefore hit both along the very same mouse ray.
                if (!IgnitionTargeted()) return false;
                if (mouseOff != null && !string.IsNullOrEmpty(mouseOff.Name)) Fsm.Event(mouseOff);
                return true;
            }
            public override void OnEnter() { if (!Blocked()) base.OnEnter(); }
            public override void OnUpdate() { if (!Blocked()) base.OnUpdate(); }
        }
        internal static bool IsIgnition(PlayMakerFSM f)
        {
            return f != null && f.FsmName == "UseNew" && f.gameObject.name == "IgnitionSatsuma"
                && f.Fsm.GetState("Motor starting") != null && f.Fsm.GetState("Motor OFF") != null;
        }
        internal static bool IsHorn(PlayMakerFSM f)
        {
            return f != null && f.FsmName == "Horn" && (f.gameObject.name.StartsWith("stock steering wheel")
                || f.gameObject.name.StartsWith("gt steering wheel"));
        }
        internal static void Prepare(PlayMakerFSM f)
        {
            if (f == null || !f.Fsm.Initialized || prepared.Contains(f) || !(IsHorn(f) || IsIgnition(f))) return;
            bool horn = IsHorn(f);
            foreach (FsmState state in f.FsmStates)
            {
                MousePickEvent pick = null;
                GetMouseButtonDown click = null;
                foreach (FsmStateAction action in state.Actions)
                {
                    if (action is MousePickEvent) pick = (MousePickEvent)action;
                    if (action is GetMouseButtonDown) click = (GetMouseButtonDown)action;
                }
                if (!horn && pick != null) { ignition = f; ignitionPick = pick; }
                if (horn && pick != null)
                {
                    var guard = new HornPick { GameObject = pick.GameObject, rayDistance = pick.rayDistance,
                        mouseOver = pick.mouseOver, mouseDown = pick.mouseDown, mouseUp = pick.mouseUp, mouseOff = pick.mouseOff,
                        layerMask = pick.layerMask, invertMask = pick.invertMask, everyFrame = pick.everyFrame, Enabled = pick.Enabled };
                    guard.Init(state);
                    var actions = (FsmStateAction[])state.Actions.Clone();
                    for (int i = 0; i < actions.Length; i++) if (actions[i] == pick) actions[i] = guard;
                    state.Actions = actions; pick = guard;
                }
                if (pick == null || click == null || (int)click.button != 0 || !click.Enabled || click.sendEvent == null) continue;
                // The separate raw LMB action can still fire in a stale hover
                // state while another control handles the same click. Let the
                // native pick action test the target and send the click together.
                if (pick.mouseDown == null || string.IsNullOrEmpty(pick.mouseDown.Name))
                { pick.mouseDown = click.sendEvent; click.Enabled = false; }
            }
            prepared.Add(f);
        }
    }
}
