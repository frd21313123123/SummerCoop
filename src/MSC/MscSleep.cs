using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // MSC beds skip discrete hours; they do not use MWC's TimeScaleSleep.
    public static class MscSleep
    {
        public const int Request = 10, Response = 11;
        sealed class Bed
        {
            public PlayMakerFSM Fsm;
            public FsmState State;
            public FsmStateAction[] Actions;
            public readonly List<FsmStateAction> ClockActions = new List<FsmStateAction>();
        }
        sealed class Gate : ModHook
        {
            public Bed Bed;
            public override string Module { get { return "msc-sleep"; } }
            public override void OnEnter() { Waiting(Bed); }
        }
        sealed class Ask { public int Sequence, Hours; public float At; }
        static readonly Dictionary<PlayMakerFSM, Bed> beds = new Dictionary<PlayMakerFSM, Bed>();
        static readonly Dictionary<int, Ask> requests = new Dictionary<int, Ask>();
        static readonly Dictionary<int, int> done = new Dictionary<int, int>();
        static Bed waiting;
        static int sequence;
        static float scan;
        static bool bound;
        public static bool Owns(PlayMakerFSM f)
        {
            if (f == null) return false;
            return f.gameObject.name == "SleepTrigger" && f.FsmName == "Activate"
                || Recon.Path(f.transform).StartsWith("MAP/SUN/", StringComparison.Ordinal);
        }
        public static void Reset() { beds.Clear(); requests.Clear(); done.Clear(); waiting = null; sequence = 0; scan = 0; }
        public static void Update()
        {
            if (!PlayerSync.InGame) return;
            if (!bound) { bound = true; Session.PlayerLeft += delegate(PlayerInfo p) { if (p != null) { requests.Remove(p.Id); done.Remove(p.Id); } }; }
            if (!Session.Active)
            {
                foreach (Bed b in beds.Values) Restore(b);
                Reset(); return;
            }
            if (Time.realtimeSinceStartup >= scan)
            {
                scan = Time.realtimeSinceStartup + 3;
                foreach (UnityEngine.Object item in Game.AllFsms())
                {
                    PlayMakerFSM f = item as PlayMakerFSM;
                    if (f == null || f.hideFlags != HideFlags.None || f.gameObject.name != "SleepTrigger" || f.FsmName != "Activate" || beds.ContainsKey(f)) continue;
                    FsmState st = f.Fsm.GetState("Check time of day");
                    if (st == null || !st.IsInitialized) continue;
                    var b = new Bed { Fsm = f, State = st, Actions = st.Actions };
                    foreach (FsmState state in f.Fsm.States)
                        foreach (FsmStateAction action in state.Actions)
                        {
                            if (action == null || !action.Enabled) continue;
                            string description = Recon.Describe(action);
                            if (description.Contains("MAP/SUN/") || description.Contains("MAP/CloudSystem/")
                                || description.Contains("{GlobalDay}") || description.Contains("{GlobalDaysPassed}") || description.Contains("{GlobalWeeksPassed}"))
                            { action.Enabled = false; b.ClockActions.Add(action); }
                        }
                    var gate = new Gate { Bed = b }; gate.Init(st);
                    st.Actions = new FsmStateAction[] { gate }; beds[f] = b; Replay.Claim(f, "msc-sleep");
                }
            }
            if (Session.IsHost) Decide();
        }
        static void Restore(Bed b)
        {
            if (b.Fsm == null) return;
            b.State.Actions = b.Actions;
            foreach (FsmStateAction a in b.ClockActions) a.Enabled = true;
            if (b == waiting) Game.SetState(b.Fsm, "Check time of day");
        }
        static void Waiting(Bed b)
        {
            waiting = b;
            int hours = b.Fsm.FsmVariables.GetFsmInt("SleepTime").Value;
            int seq = ++sequence;
            if (Session.IsHost) Add(0, seq, hours);
            else Session.SendAll(new NetWriter(Msg.Home).U8(Session.LocalId).U8(Request).I32(seq).I32(hours), true);
        }
        static void Add(int who, int seq, int hours)
        {
            int previous;
            if (seq <= 0 || hours < 0 || hours > 24 || done.TryGetValue(who, out previous) && seq <= previous) return;
            Ask current;
            if (requests.TryGetValue(who, out current) && seq <= current.Sequence) return;
            requests[who] = new Ask { Sequence = seq, Hours = hours, At = Time.realtimeSinceStartup };
        }
        static void Decide()
        {
            if (requests.Count == 0) return;
            bool allSleeping = true, allReady = true;
            int hours = 0;
            var present = new HashSet<int>();
            foreach (PlayerInfo p in Session.Players.Values)
            {
                if (p.Level != 1) continue;
                present.Add(p.Id);
                bool asleep = p.Local ? Game.GlobalBool("PlayerSleeps") : Time.realtimeSinceStartup - p.StateTime < 3 && (p.State.Flags & PlayerSync.F_Sleep) != 0;
                if (!asleep && !requests.ContainsKey(p.Id)) allSleeping = false;
                if (!requests.ContainsKey(p.Id)) allReady = false;
            }
            foreach (Ask ask in requests.Values) hours = Math.Max(hours, ask.Hours);
            if (allSleeping && !allReady)
            {
                bool expired = false;
                foreach (Ask ask in requests.Values) if (Time.realtimeSinceStartup - ask.At > 12) expired = true;
                if (!expired) return;
                allSleeping = false;
            }
            if (allSleeping && allReady) Advance(hours);
            foreach (KeyValuePair<int, Ask> request in requests)
            {
                done[request.Key] = request.Value.Sequence;
                Session.SendAll(new NetWriter(Msg.Home).U8(0).U8(Response).U8(request.Key).I32(request.Value.Sequence), true);
                if (request.Key == 0) Wake(request.Value.Sequence);
            }
            requests.Clear();
        }
        static void Advance(int hours)
        {
            GameObject go = Game.FindAny("MAP/SUN/Pivot/SUN");
            PlayMakerFSM f = go != null ? Game.FsmOn(go, "Color") : null;
            if (f == null) return;
            int now = f.FsmVariables.GetFsmInt("Time").Value % 24;
            int next = now + hours;
            for (int i = 0; i < next / 24; i++)
            {
                FsmInt day = FsmVariables.GlobalVariables.FindFsmInt("GlobalDay");
                if (day != null) day.Value = day.Value % 7 + 1;
                foreach (string name in new[] { "GlobalDaysPassed", "GlobalWeeksPassed" })
                {
                    FsmInt value = FsmVariables.GlobalVariables.FindFsmInt(name);
                    if (value != null && (name != "GlobalWeeksPassed" || day != null && day.Value == 1)) value.Value++;
                }
            }
            int hour = next % 24; hour = hour / 2 * 2; if (hour == 0) hour = 24;
            f.FsmVariables.GetFsmInt("Time").Value = hour;
            f.SendEvent("WAKEUP");
            Log.Info("MSC sleep: everyone rested; shared clock advanced " + hours + " hours to " + hour);
        }
        static void Wake(int seq)
        {
            if (waiting == null || seq != sequence || waiting.Fsm == null) return;
            Bed b = waiting; waiting = null;
            Game.SetState(b.Fsm, b.Fsm.Fsm.GetState("Check wanted") != null ? "Check wanted" : "Wake up");
        }
        public static void OnMessage(Peer from, int kind, NetReader r)
        {
            if (kind == Request) { int seq = r.I32(), hours = r.I32(); if (Session.IsHost && from != null) Add(from.Id, seq, hours); }
            else { int actor = r.U8(), seq = r.I32(); if (!Session.IsHost && from != null && from.Id == 0 && actor == Session.LocalId) Wake(seq); }
        }
        public static void Revive()
        {
            if (waiting != null && waiting.Fsm != null)
            {
                Game.SetState(waiting.Fsm, "State 5"); waiting = null;
            }
        }
    }
}
