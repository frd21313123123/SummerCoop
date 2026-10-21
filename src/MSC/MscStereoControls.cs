using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Stereo commands belong to MscStereo/MscCd, regardless of who drives.
    // The native one-metre pick was designed for the driver's camera; a
    // seated passenger must be able to aim at the same buttons from the cabin.
    internal static class MscStereoControls
    {
        sealed class Pick { public PlayMakerFSM Fsm; public MousePickEvent Action; public float Distance; }
        static readonly List<Pick> picks = new List<Pick>();
        static readonly HashSet<PlayMakerFSM> prepared = new HashSet<PlayMakerFSM>();
        internal static bool OwnsVisual(Transform t)
        {
            for (; t != null; t = t.parent)
                if (t.name == "cd player(Clone)" || t.name == "radio(Clone)" || t.name == "RadioPivot"
                    || t.name == "CDPlayer" || t.name == "RadioChannels") return true;
            return false;
        }
        internal static void Prepare(PlayMakerFSM f)
        {
            if (!prepared.Add(f)) return;
            foreach (FsmState state in f.FsmStates)
                foreach (FsmStateAction action in FireTools.Actions(f, state) ?? new FsmStateAction[0])
                {
                    MousePickEvent pick = action as MousePickEvent;
                    if (pick != null) picks.Add(new Pick { Fsm = f, Action = pick, Distance = pick.rayDistance.Value });
                }
        }
        internal static void Update()
        {
            Transform seated = Session.Active && PlayerSync.InGame ? Seats.SeatedCar ?? VehicleSync.LocalDrivingRoot : null;
            foreach (Pick pick in picks)
            {
                if (pick.Fsm == null) continue;
                bool cabin = seated != null && pick.Fsm.transform.IsChildOf(seated);
                pick.Action.rayDistance.Value = cabin ? Mathf.Max(pick.Distance, 2.5f) : pick.Distance;
            }
        }
        internal static void Reset()
        {
            foreach (Pick pick in picks) if (pick.Fsm != null) pick.Action.rayDistance.Value = pick.Distance;
            picks.Clear(); prepared.Clear();
        }
    }
}
