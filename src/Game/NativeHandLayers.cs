using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace SummerCoop.Game
{
    public sealed class NativeHandLayers
    {
        public int Held, Dropped;
        public static NativeHandLayers Read(IEnumerable<FsmState> states)
        {
            IEnumerable<FsmStateAction> held = null, dropped = null;
            foreach (FsmState state in states)
            {
                if (state.Name == "Part picked") held = state.Actions;
                if (state.Name == "Drop part") dropped = state.Actions;
            }
            return Read(held, dropped);
        }
        private static int Layer(IEnumerable<FsmStateAction> actions)
        {
            int result = -1;
            if (actions != null) foreach (FsmStateAction action in actions)
            { SetLayer layer = action as SetLayer; if (layer != null && layer.Enabled) result = layer.layer; }
            return result;
        }
        public static NativeHandLayers Read(IEnumerable<FsmStateAction> heldActions, IEnumerable<FsmStateAction> dropActions)
        {
            int held = Layer(heldActions), dropped = Layer(dropActions);
            if (held < 0 || held > 31 || dropped < 0 || dropped > 31 || held == dropped)
                throw new InvalidOperationException("Native pickup collision layers are unavailable");
            return new NativeHandLayers { Held = held, Dropped = dropped };
        }
        public static NativeHandLayers Discover()
        {
            PlayMakerFSM fsm = CurrentHand();
            if (fsm == null) throw new InvalidOperationException("Native player hand is unavailable");
            if (!fsm.Fsm.Initialized) fsm.Fsm.Init(fsm);
            return Read(fsm.FsmStates);
        }
        public static PlayMakerFSM CurrentHand()
        {
            // Resources also contains inactive PLAYER prefabs whose state actions
            // have no initialized FSM. Only inspect the current scene actor.
            GameObject player = GameObject.Find("PLAYER");
            if (player != null) foreach (PlayMakerFSM fsm in player.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (fsm.FsmName != "PickUp" || !IsCarryHand(fsm.FsmStates)) continue;
                return fsm;
            }
            return null;
        }
        public static bool IsCarryHand(IEnumerable<FsmState> states)
        {
            bool held = false, dropped = false;
            foreach (FsmState state in states)
            {
                if (state.Name == "Part picked") held = true;
                if (state.Name == "Drop part") dropped = true;
            }
            return held && dropped;
        }
    }
}
