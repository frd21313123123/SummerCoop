using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;
using MWCoop.Net;

namespace MWCoop
{
    // Native engine simulation runs on the motion owner, including an outside
    // operator cranking the engine. Copies receive phases and native audio/FX;
    // they must not run battery/fuel/input checks of their own while cranking.
    public static class MscEngineSync
    {
        sealed class Engine
        {
            public Transform Root;
            public string Key, Applied = "";
            public PlayMakerFSM Starter, Sound, Simulation, Jank, GearControl;
            public FsmBool Starting, Accessory, ShutOff, BoatRunning;
            public FsmFloat Rpm, Thrust, Gear, Charge;
            public bool Copy, StarterWas, SimulationWas;
            public float Next;
            public EngineSnapshot Sent;
            public readonly EngineOrder Order = new EngineOrder();
            public int Pulls;
        }
        static readonly Dictionary<Transform, Engine> engines = new Dictionary<Transform, Engine>();
        static int sequence;
        static readonly HashSet<string> effects = new HashSet<string> {
            "ActivateGameObject", "MasterAudioPlaySound", "MasterAudioStopAllOfSound", "AudioPlay", "AudioStop",
            "PlayAnimation", "StopAnimation", "SetAudioPitch", "SetAudioVolume" };
        public static void Reset() { engines.Clear(); sequence = 0; }
        public static void PlayerLeft(int id) { foreach (Engine e in engines.Values) e.Order.Forget(id); }
        static bool Value(FsmBool b) { return b != null && b.Value; }
        static float Value(FsmFloat f) { return f != null ? f.Value : 0; }
        static void Set(FsmBool b, bool v) { if (b != null) b.Value = v; }
        static void Set(FsmFloat f, float v) { if (f != null) f.Value = v; }
        static Engine Bind(Transform root)
        {
            if (root == null) return null;
            Engine e; if (engines.TryGetValue(root, out e)) return e;
            e = new Engine { Root = root, Key = VehicleSync.KeyOf(root) };
            foreach (PlayMakerFSM f in root.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (MscVehicleControls.PlayerObject(f.transform)) continue;
                if (f.FsmName == "Starter" && (f.Fsm.GetState("Running") != null || f.Fsm.GetState("Motor running") != null)) e.Starter = f;
                if (root.name != "BOAT") continue;
                if (f.FsmName == "Sound Engine") e.Sound = f;
                if (f.FsmName == "Simulation" && f.name == "Engine") e.Simulation = f;
                if (f.FsmName == "Jank" && f.name == "Ignition") e.Jank = f;
                if (f.FsmName == "Use" && f.name == "Gear") e.GearControl = f;
            }
            if (e.Starter != null)
            {
                e.Starting = e.Starter.FsmVariables.FindFsmBool("Starting");
                e.Accessory = e.Starter.FsmVariables.FindFsmBool("ACC");
                e.ShutOff = e.Starter.FsmVariables.FindFsmBool("ShutOff");
                FsmGameObject battery = e.Starter.FsmVariables.FindFsmGameObject("db_Battery");
                PlayMakerFSM data = battery != null && battery.Value != null ? Game.FsmOn(battery.Value, "Data") : null;
                if (data != null) e.Charge = data.FsmVariables.FindFsmFloat("Charge");
            }
            if (e.Sound != null) { e.BoatRunning = e.Sound.FsmVariables.FindFsmBool("Start"); e.Rpm = e.Sound.FsmVariables.FindFsmFloat("RPM"); }
            if (e.Simulation != null) { e.Thrust = e.Simulation.FsmVariables.FindFsmFloat("Thrust"); e.Gear = e.Simulation.FsmVariables.FindFsmFloat("Gear"); }
            engines[root] = e;
            if (e.Starter != null)
            {
                if (!e.Starter.Fsm.Initialized) e.Starter.Fsm.Init(e.Starter);
                foreach (FsmState state in e.Starter.FsmStates)
                {
                    var actions = new List<FsmStateAction>(state.Actions);
                    actions.Insert(0, new Phase { Root = root, State = state.Name });
                    state.Actions = actions.ToArray();
                }
            }
            if (e.Jank != null)
            {
                Hook(e.Jank, "State 1", root, EngineSnapshot.Pull);
                Hook(e.Jank, "Start", root, EngineSnapshot.Start);
                Transform stop = root.Find("GFX/Motor/Pivot/ShutOff");
                if (stop != null) Hook(Game.FsmOn(stop.gameObject, "Use"), "Shut Off", root, EngineSnapshot.Stop);
                foreach (string state in new[] { "First", "Neutral", "Reverse" }) Hook(e.GearControl, state, root, EngineSnapshot.Shift);
            }
            return e;
        }
        sealed class BoatAction : ModHook
        {
            public Transform Root; public int Kind; public string State;
            public override string Module { get { return "msc-engine"; } }
            public override void OnEnter()
            {
                try
                {
                    if (Session.Active && Replay.Depth == 0)
                    {
                        Engine e = Bind(Root);
                        if (Kind == EngineSnapshot.Pull) e.Pulls++;
                        if (Kind == EngineSnapshot.Pull || Kind == EngineSnapshot.Start) VehicleSync.ClaimEngine(Root);
                        var s = new EngineSnapshot { Vehicle = e.Key, Sequence = ++sequence, Kind = Kind,
                            Gear = State == "First" ? -10 : State == "Reverse" ? 64 : 10000 };
                        Send(s, true);
                    }
                }
                catch (Exception ex) { Replay.HookError(ex); }
                Finish();
            }
        }
        static void Hook(PlayMakerFSM f, string state, Transform root, int kind)
        {
            if (f == null) return;
            if (!f.Fsm.Initialized) f.Fsm.Init(f);
            FsmState s = f.Fsm.GetState(state); if (s == null) return;
            var actions = new List<FsmStateAction>(s.Actions);
            actions.Insert(0, new BoatAction { Root = root, Kind = kind, State = state });
            s.Actions = actions.ToArray();
        }
        sealed class Phase : ModHook
        {
            public Transform Root; public string State;
            public override string Module { get { return "msc-engine"; } }
            public override void OnEnter()
            {
                try
                {
                    Engine e = Bind(Root);
                    if (Session.Active && Replay.Depth == 0 && !e.Copy && VehicleSync.Authority(Root.GetComponent<Rigidbody>()) == Session.LocalId)
                    {
                        EngineSnapshot s = Capture(e); s.NativeState = State; s.Sequence = ++sequence; Send(s, true);
                    }
                }
                catch (Exception ex) { Replay.HookError(ex); }
                Finish();
            }
        }
        static EngineSnapshot Capture(Engine e)
        {
            return new EngineSnapshot { Vehicle = e.Key, NativeState = e.Starter != null ? e.Starter.ActiveStateName : e.Sound.ActiveStateName,
                Starting = Value(e.Starting), Accessory = Value(e.Accessory), ShutOff = Value(e.ShutOff), Running = Value(e.BoatRunning),
                Rpm = Value(e.Rpm), Thrust = Value(e.Thrust), Gear = Value(e.Gear), BatteryCharge = e.Charge != null ? e.Charge.Value : -1 };
        }
        public static void SetCopy(Transform root, bool copy)
        {
            Engine e = Bind(root); if (e == null || e.Copy == copy) return;
            string resume = e.Applied;
            e.Copy = copy; e.Applied = ""; e.Sent = null; e.Next = 0;
            if (e.Starter != null)
            {
                if (copy) { e.StarterWas = e.Starter.enabled; e.Starter.enabled = false; }
                else
                {
                    e.Starter.enabled = e.StarterWas;
                    // Its simulation was paused while audio/FX followed the
                    // previous owner. Resume that phase on a real handoff.
                    if (e.StarterWas && resume.Length > 0 && e.Starter.Fsm.GetState(resume) != null)
                    {
                        Replay.Depth++;
                        try { Game.SetState(e.Starter, resume); }
                        finally { Replay.Depth--; }
                    }
                }
            }
            if (e.Simulation != null)
            {
                if (copy) { e.SimulationWas = e.Simulation.enabled; e.Simulation.enabled = false; }
                else e.Simulation.enabled = e.SimulationWas;
            }
        }
        public static bool BoatRunning(Transform root) { Engine e = Bind(root); return e != null && Value(e.BoatRunning); }
        public static float BoatRpm(Transform root) { Engine e = Bind(root); return e != null ? Value(e.Rpm) : 0; }
        public static bool Cranking(Transform root)
        {
            Engine e = Bind(root); return e != null && !e.Copy && Value(e.Starting) && !Value(e.ShutOff);
        }
        public static string AppliedState(Transform root) { Engine e = Bind(root); return e != null ? e.Applied : ""; }
        public static int PullCount(Transform root) { Engine e = Bind(root); return e != null ? e.Pulls : 0; }
        public static void FollowMotion(Transform root, float rpm, bool running)
        {
            Engine e = Bind(root); if (e == null || !e.Copy || e.Sound == null) return;
            Set(e.BoatRunning, running); Set(e.Rpm, rpm);
            if (e.Simulation != null)
            {
                e.Simulation.enabled = false;
                Set(e.Simulation.FsmVariables.FindFsmFloat("RPM"), running ? rpm : 0);
            }
        }
        public static void Update()
        {
            if (!Session.Active || !PlayerSync.InGame) return;
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < VehicleSync.LocalCount; i++)
            {
                Rigidbody body = VehicleSync.LocalBody(i); if (body == null) continue;
                Engine e = Bind(body.transform);
                if (e.Starter == null && e.Sound == null || !body.gameObject.activeInHierarchy) continue;
                if (VehicleSync.Authority(body) != Session.LocalId) continue;
                // A passenger/outside operator releases the replicated key on
                // this owner, which never receives that player's mouse-up.
                if (e.Starter != null && !Value(e.Starting))
                {
                    string state = e.Starter.ActiveStateName;
                    if (state == "Fuel Mixture" || state == "Turn key" || state == "Broken starter") e.Starter.SendEvent("KEYUP");
                    else if (state == "Start or not") e.Starter.SendEvent("FINISHED");
                }
                var s = Capture(e);
                bool changed = !s.SamePhase(e.Sent);
                if (!changed && now < e.Next) continue;
                e.Next = now + .5f; e.Sent = s; s.Sequence = ++sequence;
                Send(s, true);
            }
        }
        static void Send(EngineSnapshot s, bool reliable)
        {
            var w = new NetWriter(Msg.Job).U8(Session.LocalId).Str("@engine"); s.Write(w); Session.SendAll(w, reliable);
        }
        public static void PublishFinal(Transform root)
        {
            Engine e = Bind(root); if (e == null || e.Starter == null && e.Sound == null) return;
            EngineSnapshot s = Capture(e); s.Sequence = ++sequence; Send(s, true);
        }
        static void ApplyGear(Engine e, float ratio)
        {
            Set(e.Gear, ratio);
            if (e.GearControl == null) return;
            int selected = ratio == -10 ? 1 : ratio == 64 ? -1 : 0;
            FsmInt gear = e.GearControl.FsmVariables.FindFsmInt("SelectedGear");
            if (gear != null) gear.Value = selected;
            FsmGameObject mesh = e.GearControl.FsmVariables.FindFsmGameObject("Mesh");
            if (mesh != null && mesh.Value != null) mesh.Value.transform.localRotation = Quaternion.Euler(selected * -20f, 0, 0);
            if (VehicleSync.LocalDrivingRoot == e.Root)
            {
                FsmString label = FsmVariables.GlobalVariables.FindFsmString("GUIgear");
                if (label != null) label.Value = selected == 1 ? "1" : selected == -1 ? "R" : "N";
            }
        }
        public static void OnRemote(int who, NetReader reader)
        {
            EngineSnapshot s = EngineSnapshot.Read(reader);
            Rigidbody body = VehicleSync.Body(s.Vehicle); if (body == null) return;
            Engine e = Bind(body.transform);
            if (s.Kind == EngineSnapshot.State && VehicleSync.Authority(body) != who) return;
            if (s.Kind != EngineSnapshot.State && e.Sound == null) return;
            if (!e.Order.Accept(who, s.Sequence)) return;
            if (Session.IsHost) { var w = new NetWriter(Msg.Job).U8(who).Str("@engine"); s.Write(w); Session.Broadcast(w, true, who); }
            if (s.Kind != EngineSnapshot.State)
            {
                if (s.Kind == EngineSnapshot.Pull) PlayPull(e);
                else if (VehicleSync.Authority(body) == Session.LocalId)
                {
                    if (s.Kind == EngineSnapshot.Start) { VehicleSync.ClaimEngine(e.Root); Set(e.BoatRunning, true); }
                    if (s.Kind == EngineSnapshot.Stop) Set(e.BoatRunning, false);
                    if (s.Kind == EngineSnapshot.Shift) ApplyGear(e, s.Gear);
                }
                return;
            }
            SetCopy(e.Root, true);
            Set(e.Starting, s.Starting); Set(e.Accessory, s.Accessory); Set(e.ShutOff, s.ShutOff);
            if (s.BatteryCharge >= 0) Set(e.Charge, s.BatteryCharge);
            if (e.Starter != null && s.BatteryCharge >= 0)
                Set(e.Starter.FsmVariables.FindFsmFloat("StarterPitch"), Mathf.Clamp(s.BatteryCharge / 120f, .4f, 1f));
            if (e.Sound != null) { Set(e.BoatRunning, s.Running); Set(e.Rpm, s.Rpm); Set(e.Thrust, s.Thrust); ApplyGear(e, s.Gear); }
            if (e.Starter != null && e.Applied != s.NativeState)
            {
                FsmState state = e.Starter.Fsm.GetState(s.NativeState);
                if (state != null)
                {
                    Replay.Depth++;
                    try { foreach (FsmStateAction a in state.Actions) if (a != null && a.Enabled && effects.Contains(a.GetType().Name)) a.OnEnter(); }
                    finally { Replay.Depth--; }
                }
            }
            e.Applied = s.NativeState;
        }
        static void PlayPull(Engine e)
        {
            if (e.Jank == null) return;
            FsmState state = e.Jank.Fsm.GetState("State 1"); if (state == null) return;
            foreach (FsmStateAction a in state.Actions)
                if (a != null && a.GetType().Name == "AudioPlay")
                {
                    var field = a.GetType().GetField("oneShotClip");
                    object clip = field != null ? field.GetValue(a) : null;
                    FsmObject variable = clip as FsmObject;
                    AudioClip audio = (variable != null ? variable.Value : clip) as AudioClip;
                    AudioSource source = e.Sound.GetComponent<AudioSource>();
                    if (source != null && audio != null) { source.PlayOneShot(audio); e.Pulls++; }
                }
        }
    }
}
