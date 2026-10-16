using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MWCoop.Net;
using ProtocolException = SummerCoop.Core.ProtocolException;
using UnityEngine;

namespace MWCoop
{
    // MSC has eight separate Alignment screws on one rocker shaft, plus
    // Alignment on the carburettor/steering/dampers and HandRotate.Rotation.
    // Address the actual FSM, including duplicate sibling ranks, rather than
    // just (part ID, FSM name), which aliases the eight valve adjustments.
    public static class MscTuning
    {
        sealed class Binding
        {
            public string Key;
            public PlayMakerFSM Fsm;
            public FsmFloat Value;
            public int PendingFrame;
            public float PendingAt;
            public bool Changed, Pending;
            public readonly MscEditClock Clock = new MscEditClock();
        }
        sealed class Pending { public int Who, Revision; public float Value; }
        sealed class Hook : ModHook
        {
            public Binding Binding;
            public override string Module { get { return "msc-tuning"; } }
            public override void OnEnter()
            {
                if (Session.Active && PlayerSync.InGame && Replay.Depth == 0)
                { Binding.Pending = true; Binding.PendingFrame = Time.frameCount + 2; Binding.PendingAt = Time.realtimeSinceStartup + .1f; }
                Finish();
            }
        }
        static readonly Dictionary<string, Binding> bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
        static readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>(StringComparer.Ordinal);
        static readonly HashSet<PlayMakerFSM> hooked = new HashSet<PlayMakerFSM>();
        static readonly List<string> completed = new List<string>();
        static float nextScan, nextSnapshot;
        static bool leftHooked;
        public static int Count { get { return bindings.Count; } }
        public static bool Owns(PlayMakerFSM f)
        {
            if (f == null || Game.UnderPlayer(f.transform)) return false;
            if (f.FsmName == "Screw") return !MscParts.IsBolt(f) && f.FsmVariables.FindFsmFloat("Alignment") != null;
            return f.FsmName == "HandRotate" && f.FsmVariables.FindFsmFloat("Rotation") != null || IsTiming(f);
        }
        internal static bool IsTiming(PlayMakerFSM f)
        {
            return f != null && f.FsmName == "BoltCheck" && f.FsmVariables.FindFsmFloat("Angle") != null
                && f.Fsm.GetState("Adjust") != null && f.Fsm.GetState("Set angle") != null;
        }
        internal static string ValueName(PlayMakerFSM f) { return IsTiming(f) ? "Angle" : f.FsmName == "HandRotate" ? "Rotation" : "Alignment"; }
        public static void Reset() { bindings.Clear(); pending.Clear(); hooked.Clear(); completed.Clear(); nextScan = nextSnapshot = 0; }
        static bool Mutation(string state, PlayMakerFSM f)
        {
            return IsTiming(f) ? state == "Adjust" : f.FsmName == "HandRotate" ? state == "Clockwise" || state == "Counterwise"
                : state == "Screw" || state == "Unscrew" || state == "Screw 2" || state == "Unscrew 2";
        }
        static void Scan()
        {
            foreach (UnityEngine.Object item in Game.AllFsms())
            {
                PlayMakerFSM f = item as PlayMakerFSM;
                if (f == null || f.hideFlags != HideFlags.None || hooked.Contains(f) || !Owns(f)) continue;
                if (!f.Fsm.Initialized)
                {
                    string path = Recon.Path(f.transform);
                    if (!path.StartsWith("SATSUMA", StringComparison.Ordinal) && !path.StartsWith("CARPARTS/", StringComparison.Ordinal)) continue;
                    f.Fsm.InitData();
                }
                if (!Replay.Claim(f, "msc-tuning")) continue;
                string key = MscParts.Key(f);
                Binding previous;
                if (bindings.TryGetValue(key, out previous) && previous.Fsm != null && previous.Fsm != f) { Log.Warn("MSC ambiguous tuning FSM: " + key); continue; }
                Binding b = new Binding { Key = key, Fsm = f, Value = f.FsmVariables.FindFsmFloat(ValueName(f)) };
                bool installed = false;
                foreach (FsmState state in f.FsmStates)
                {
                    if (!state.IsInitialized || !Mutation(state.Name, f)) continue;
                    List<FsmStateAction> actions = new List<FsmStateAction>(state.Actions);
                    actions.Insert(0, new Hook { Binding = b }); state.Actions = actions.ToArray(); installed = true;
                }
                if (installed) { bindings[key] = b; hooked.Add(f); }
            }
        }
        public static void Update()
        {
            if (!PlayerSync.InGame || !Session.Active) return;
            if (!leftHooked) { leftHooked = true; Session.PlayerLeft += player => { foreach (Binding b in bindings.Values) b.Clock.ForgetActor(player.Id); }; }
            float now = Time.realtimeSinceStartup;
            if (now >= nextScan) { nextScan = now + 2f; Scan(); }
            completed.Clear();
            foreach (KeyValuePair<string, Pending> item in pending)
            {
                Binding b;
                if (!bindings.TryGetValue(item.Key, out b) || b.Fsm == null) continue;
                Receive(b, item.Value.Who, item.Value.Revision, item.Value.Value); completed.Add(item.Key);
            }
            foreach (string key in completed) pending.Remove(key);
            foreach (Binding b in bindings.Values)
            {
                if (b.Fsm == null || !b.Pending || Time.frameCount < b.PendingFrame || now < b.PendingAt) continue;
                b.Pending = false; b.Changed = true;
                int revision = b.Clock.Publish(Session.IsHost);
                Session.SendAll(Packet(b, Session.LocalId, revision), true);
            }
            if (Session.IsHost && now >= nextSnapshot)
            {
                nextSnapshot = now + 2f;
                foreach (Binding b in bindings.Values) if (b.Fsm != null && b.Changed) Session.Broadcast(Packet(b, Session.LocalId, b.Clock.HostRevision), true);
            }
        }
        static NetWriter Packet(Binding b, int who, int revision)
        { return new NetWriter(Msg.Setting).U8(who).Str(b.Key).I32(revision).F32(b.Value.Value); }
        public static void OnMessage(Peer from, NetReader reader)
        {
            int who = reader.U8(); string key = reader.Str(); int revision = reader.I32(); float value = reader.F32();
            if (Session.IsHost) who = from.Id;
            if (revision <= 0 || reader.More) throw new ProtocolException("Invalid MSC tuning revision");
            Binding b;
            if (!bindings.TryGetValue(key, out b) || b.Fsm == null)
            {
                Pending old;
                if (!pending.TryGetValue(key, out old) || Session.IsHost && old.Who != who || revision > old.Revision) pending[key] = new Pending { Who = who, Revision = revision, Value = value };
                return;
            }
            Receive(b, who, revision, value);
        }
        static void Receive(Binding b, int who, int revision, float value)
        {
            if (Session.IsHost)
            {
                if (!b.Clock.AcceptRequest(who, revision)) return;
            }
            else if (!b.Clock.AcceptSnapshot(revision)) return;
            Apply(b, value);
            b.Changed = true;
            if (Session.IsHost) Session.Broadcast(Packet(b, who, b.Clock.Publish(true)), true);
        }
        static void Apply(Binding b, float value)
        {
            Replay.Depth++;
            try
            {
                b.Value.Value = value;
                if (IsTiming(b.Fsm))
                {
                    // Adjust rotates by a delta. Store the received absolute angle
                    // first, then let Set angle restore the native timing mark.
                    foreach (FsmStateAction action in b.Fsm.Fsm.GetState("Adjust").Actions)
                        if (action is SetFsmFloat) action.OnEnter();
                    Commit(b.Fsm.Fsm.GetState("Set angle"), false);
                }
                else if (b.Fsm.FsmName == "HandRotate")
                {
                    // Reuse the game's clamp, pose and database write without
                    // adding another scroll step or entering a local input loop.
                    Commit(b.Fsm.Fsm.GetState("Clockwise"), true);
                    Commit(b.Fsm.Fsm.GetState("Wait"), false);
                }
                else
                {
                    FsmState setup = b.Fsm.Fsm.GetState("Setup") ?? b.Fsm.Fsm.GetState("Wait1");
                    Commit(setup, false);
                    Commit(b.Fsm.Fsm.GetState("Idle"), false);
                }
            }
            finally { Replay.Depth--; }
        }
        internal static void RestoreTiming(GameObject part, bool pose)
        {
            if (part == null) return;
            PlayMakerFSM f = Game.FsmOn(part, "BoltCheck");
            if (!IsTiming(f)) return;
            FsmGameObject db = f.FsmVariables.FindFsmGameObject("db_ThisPart");
            PlayMakerFSM data = db == null || db.Value == null ? null : Game.FsmOn(db.Value, "Data");
            FsmFloat angle = data == null ? null : data.FsmVariables.FindFsmFloat("Angle");
            if (angle == null) return;
            f.FsmVariables.FindFsmFloat("Angle").Value = angle.Value;
            if (!pose) return;
            Replay.Depth++;
            try { Commit(f.Fsm.GetState("Set angle"), false); }
            finally { Replay.Depth--; }
        }
        static void Commit(FsmState state, bool rotationOnly)
        {
            if (state == null) return;
            foreach (FsmStateAction action in state.Actions)
            {
                if (action == null || action is ModHook) continue;
                if (rotationOnly)
                {
                    if (action is FloatClamp || action is SetRotation) action.OnEnter();
                }
                else if (action is FloatClamp || action is FloatOperator || action is GetFsmFloat || action is SetFsmFloat || action is SetRotation || action is SetProperty) action.OnEnter();
            }
        }
    }
}
