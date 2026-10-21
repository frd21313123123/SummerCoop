using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Only a wheel step is sent. Hover states, Scroll, native power checks
    // and pre-action copies of Volume must never be replayed as user input.
    public static class MscStereo
    {
        sealed class Knob
        {
            public string Key, Device; public PlayMakerFSM Fsm; public FsmFloat Value;
            public bool Tuner, Cd; public int Received, Actor, Sequence;
            public readonly MscStereoRequests Requests = new MscStereoRequests();
        }
        sealed class Turn { public Knob Knob; public float Delta; public int Sequence, Frame; }
        sealed class Hook : ModHook
        {
            public Knob Knob; public float Delta;
            public override string Module { get { return "msc-stereo"; } }
            public override void OnEnter()
            {
                if (!Session.Active || applying || Replay.Depth > 0) { Finish(); return; }
                turns.Add(new Turn { Knob = Knob, Delta = Delta, Sequence = ++sequence, Frame = Time.frameCount });
                FireTools.Cancel(Knob.Fsm, State, this, "Mouse off 2");
            }
        }
        static readonly Dictionary<string, Knob> knobs = new Dictionary<string, Knob>();
        static readonly HashSet<PlayMakerFSM> known = new HashSet<PlayMakerFSM>();
        static readonly List<Turn> turns = new List<Turn>();
        static float scan, publish;
        static int sequence, revision;
        static bool applying, bound;
        public static bool Owns(PlayMakerFSM f)
        { return f != null && f.FsmName == "Knob" && (f.name == "RadioVolume" || f.name == "RadioTuner"); }
        public static void Reset() { knobs.Clear(); known.Clear(); turns.Clear(); MscStereoControls.Reset(); scan = publish = 0; sequence = revision = 0; applying = false; }
        public static void Resynchronize() { scan = publish = 0; foreach (Knob k in knobs.Values) k.Received = 0; }
        static string Device(PlayMakerFSM f)
        {
            for (Transform t = f.transform.parent; t != null; t = t.parent)
                if (t.name == "cd player(Clone)") return "msc:cd";
            for (Transform t = f.transform.parent; t != null; t = t.parent)
            { string id = MscParts.IdOf(t.gameObject); if (id.Length > 0) return id; }
            return VehicleSync.KeyOf(f.transform) ?? Recon.Path(f.transform.parent);
        }
        static void Scan()
        {
            scan = Time.realtimeSinceStartup + 2;
            foreach (UnityEngine.Object obj in Game.AllFsms())
            {
                PlayMakerFSM f = obj as PlayMakerFSM;
                if (!Owns(f) || f.hideFlags != HideFlags.None || known.Contains(f)) continue;
                if (!f.Fsm.Initialized) f.Fsm.InitData();
                MscStereoControls.Prepare(f);
                var k = new Knob { Fsm = f, Tuner = f.name == "RadioTuner", Device = Device(f) };
                k.Cd = k.Device == "msc:cd"; k.Key = k.Device + "/" + f.name;
                k.Value = f.FsmVariables.FindFsmFloat(k.Tuner ? "Tune" : "Volume");
                if (k.Value == null || !Replay.Claim(f, "msc-stereo")) continue;
                foreach (FsmState state in f.FsmStates)
                {
                    if (state.Name != "Increase" && state.Name != "Decrease" && !(k.Cd && state.Name == "On")) continue;
                    float step = (state.Name == "Decrease" ? -1 : 1) * (k.Tuner ? .001f : .1f);
                    FireTools.Insert(f, state, new Hook { Knob = k, Delta = step }, false);
                }
                known.Add(f); knobs[k.Key] = k;
            }
        }
        static NetWriter Snapshot(Knob k, int kind)
        { return new NetWriter(Msg.Stereo).U8(kind).I32(++revision).U8(k.Actor).I32(k.Sequence).Str(k.Key).F32(k.Value.Value); }
        public static void Update()
        {
            MscStereoControls.Update();
            if (!Session.Active || !PlayerSync.InGame) return;
            if (!bound)
            {
                bound = true;
                Session.PlayerLeft += delegate(PlayerInfo player) {
                    if (player != null) foreach (Knob k in knobs.Values) k.Requests.ForgetActor(player.Id);
                };
            }
            if (Time.realtimeSinceStartup >= scan) Scan();
            for (int i = 0; i < turns.Count; i++)
            {
                Turn turn = turns[i]; if (Time.frameCount <= turn.Frame) continue;
                turns.RemoveAt(i--);
                if (Session.IsHost) Accept(turn.Knob, 0, turn.Sequence, turn.Delta);
                else Session.SendToHost(new NetWriter(Msg.Stereo).U8(0).I32(turn.Sequence).Str(turn.Knob.Key).F32(turn.Delta), true);
            }
            if (Session.IsHost && Session.RemoteCount > 0 && Time.realtimeSinceStartup >= publish)
            {
                publish = Time.realtimeSinceStartup + 2;
                foreach (Knob k in knobs.Values) if (k.Fsm != null) Session.Broadcast(Snapshot(k, 2), true);
            }
        }
        static void Accept(Knob k, int actor, int seq, float delta)
        {
            if (!k.Requests.Accept(actor, seq)) return;
            k.Actor = actor; k.Sequence = seq;
            Apply(k, k.Value.Value + delta, actor, false);
            Session.Broadcast(Snapshot(k, 1), true);
        }
        internal static string TestSet(PlayMakerFSM f, float value)
        {
            if (MscRuntime.Argument("--summercoop-test-report").Length == 0) return "requires isolated QA";
            Scan(); Knob target = null; foreach (Knob k in knobs.Values) if (k.Fsm == f) target = k;
            if (target == null) return "untracked stereo control";
            float step = target.Tuner ? .001f : .1f;
            int down = target.Tuner ? 0 : Mathf.RoundToInt(target.Value.Value / step);
            int change = Mathf.RoundToInt((value - (target.Tuner ? target.Value.Value : 0)) / step);
            for (int i = 0; i < down + Mathf.Abs(change); i++)
                turns.Add(new Turn { Knob = target, Delta = i < down || change < 0 ? -step : step, Sequence = ++sequence, Frame = Time.frameCount });
            return target.Key + " native-sized turns=" + (down + Mathf.Abs(change));
        }
        static void Actions(PlayMakerFSM f, string state, bool display)
        {
            FsmState s = f.Fsm.GetState(state); if (s == null) return;
            foreach (FsmStateAction action in FireTools.Actions(f, s) ?? new FsmStateAction[0])
            {
                if (!(action is FloatClamp || action is FloatOperator || action is SetRotation || action is SetPosition || action is SetFsmFloat
                    || display && (action is SetProperty || action is ConvertFloatToString || action is BuildStringFast))) continue;
                if (action.Enabled) { action.OnEnter(); action.OnLateUpdate(); }
            }
        }
        static void State(Knob k, string state)
        {
            FsmState s = k.Fsm.Fsm.GetState(state); if (s == null) return;
            var disabled = new List<FsmStateAction>();
            // The value is already the host's result; native On must not add
            // another 0.1 while applying that result on each peer.
            foreach (FsmStateAction a in FireTools.Actions(k.Fsm, s) ?? new FsmStateAction[0])
                if (a is FloatAdd && a.Enabled) { a.Enabled = false; disabled.Add(a); }
            try { k.Fsm.enabled = true; Game.SetState(k.Fsm, state); }
            finally { foreach (FsmStateAction a in disabled) a.Enabled = true; }
        }
        static void Apply(Knob k, float value, int actor, bool force)
        {
            float previous = k.Value.Value;
            value = Mathf.Clamp(value, k.Tuner ? .05f : 0, k.Tuner ? .11f : 1);
            if (!k.Tuner && value < .00001f) value = 0;
            if (!force && Mathf.Abs(previous - value) < .00001f) return;
            FireTools.Resume(k.Fsm);
            applying = true; Replay.Depth++;
            bool routing = k.Tuner || force || value <= .00001f || previous <= .00001f;
            bool remote = actor != Session.LocalId && routing;
            if (routing && !remote) Radio.BeforeLocal();
            if (remote) Radio.BeforeRemote();
            string state = "";
            try
            {
                k.Value.Value = value;
                Actions(k.Fsm, k.Tuner || k.Cd ? "Wait1" : "Rotate knob", false);
                if (k.Tuner)
                {
                    foreach (Knob volume in knobs.Values)
                        if (!volume.Tuner && volume.Device == k.Device && volume.Value.Value > 0) { State(volume, "Check tune"); break; }
                }
                else if (value <= .00001f) { state = "Off"; State(k, state); }
                else if (force || previous <= .00001f)
                { state = k.Cd ? "On" : "Check tune"; State(k, state); }
                else if (k.Cd) Actions(k.Fsm, "State 1", true);
            }
            finally
            {
                if (remote) Radio.AfterRemote(k.Fsm.transform, actor, state);
                Replay.Depth--; applying = false;
            }
        }
        public static void OnMessage(Peer from, NetReader r)
        {
            int kind = r.U8();
            int rev = r.I32(), actor = kind == 0 ? (from == null ? -1 : from.Id) : r.U8();
            int seq = kind == 0 ? rev : r.I32(); string key = r.Str(); float value = r.F32();
            if (kind > 2 || seq < 0 || key.Length > 256 || float.IsNaN(value) || float.IsInfinity(value) || r.More)
                throw new System.IO.IOException("Invalid stereo control");
            if (Time.realtimeSinceStartup >= scan) Scan();
            Knob k; if (!knobs.TryGetValue(key, out k) || k.Fsm == null) return;
            if (Session.IsHost && kind == 0 && from != null)
            {
                float step = k.Tuner ? .001f : .1f;
                if (Mathf.Abs(Mathf.Abs(value) - step) > .000001f) throw new System.IO.IOException("Invalid stereo step");
                Accept(k, actor, seq, value);
            }
            else if (!Session.IsHost && kind != 0 && from != null && from.Id == 0 && rev > k.Received)
            { bool force = k.Received == 0; k.Received = rev; k.Actor = actor; k.Sequence = seq; Apply(k, value, actor, force); }
        }
    }
}
