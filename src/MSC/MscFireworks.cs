using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    public static class MscFireworks
    {
        public const int Request = 14, State = 15, Pose = 16;
        sealed class Rocket
        {
            public string Id; public GameObject Root; public Rigidbody Body; public PlayMakerFSM Use;
            public AddForce Thrust;
            public int Phase, Revision, PoseRevision; public float Next, PoseAt; public Vector3 Position, Velocity; public Quaternion Rotation;
        }
        sealed class Hook : ModHook
        {
            public PlayMakerFSM Fsm; public FsmState State; public string Id;
            public override string Module { get { return "msc-fireworks"; } }
            public override void OnEnter()
            {
                if (!Session.Active || Replay.Depth > 0 || applying) { Finish(); return; }
                queue.Add(new Edit { Id = Id, Sequence = ++sequence, Frame = Time.frameCount });
                FireTools.Cancel(Fsm, State, this, "Wait player");
            }
        }
        sealed class Edit { public string Id; public int Sequence, Frame; }
        static readonly Dictionary<string, Rocket> rockets = new Dictionary<string, Rocket>();
        static readonly List<Edit> queue = new List<Edit>();
        static MscEditClock clock = new MscEditClock();
        static PlayMakerFSM bag;
        static GameObject prefab;
        static int sequence, born;
        static float scan, nextPose;
        static bool applying;
        static bool bound;
        public static bool Flying(GameObject go)
        {
            if (go == null) return false;
            Rocket rocket; return rockets.TryGetValue(Props.ItemId(go), out rocket) && rocket.Phase > 0 && rocket.Phase <= 3;
        }
        public static bool Owns(PlayMakerFSM f)
        {
            return f != null && (Recon.Path(f.transform).StartsWith("MISC/fireworks bag(itemx)", StringComparison.Ordinal)
                || f.transform.root.name.StartsWith("rocket", StringComparison.Ordinal));
        }
        public static void Reset() { rockets.Clear(); queue.Clear(); bag = null; prefab = null; clock = new MscEditClock(); sequence = born = 0; scan = nextPose = 0; }
        public static void Update()
        {
            if (!Session.Active || !PlayerSync.InGame) return;
            if (!bound) { bound = true; Session.PlayerLeft += delegate(PlayerInfo p) { if (p != null) clock.ForgetActor(p.Id); }; }
            if (bag == null && Time.realtimeSinceStartup >= scan)
            {
                scan = Time.realtimeSinceStartup + 2;
                GameObject go = Game.FindAny("MISC/fireworks bag(itemx)"); bag = go != null ? Game.FsmOn(go, "Open") : null;
                if (bag != null)
                {
                    FsmState st = bag.Fsm.GetState("Play anim");
                    foreach (FsmStateAction a in FireTools.Actions(bag, st)) if (a is CreateObject) prefab = ((CreateObject)a).gameObject.Value;
                    HookState(bag, st, "");
                }
            }
            for (int i = 0; i < queue.Count; i++)
            {
                Edit e = queue[i]; if (Time.frameCount <= e.Frame) continue; queue.RemoveAt(i--);
                if (Session.IsHost) Apply(0, e.Sequence, e.Id);
                else Session.SendAll(new NetWriter(Msg.Home).U8(Session.LocalId).U8(Request).I32(e.Sequence).Str(e.Id), true);
            }
            bool pose = Time.realtimeSinceStartup >= nextPose; if (pose) nextPose = Time.realtimeSinceStartup + .1f;
            foreach (Rocket rocket in rockets.Values)
            {
                if (rocket.Root == null) continue;
                if (Session.IsHost)
                {
                    int phase = PhaseOf(rocket.Use);
                    if (rocket.Phase != phase || Time.realtimeSinceStartup >= rocket.Next)
                    { rocket.Phase = phase; rocket.Next = Time.realtimeSinceStartup + 2; Publish(rocket); }
                    if (pose && phase > 0 && phase <= 3)
                        Session.SendAll(new NetWriter(Msg.Home).U8(0).U8(Pose).Str(rocket.Id).I32(++rocket.PoseRevision).Vec(rocket.Body.position).Quat(rocket.Body.rotation).Vec(rocket.Body.velocity), false);
                }
                else if (rocket.Phase > 0 && rocket.Phase <= 3)
                {
                    rocket.Body.isKinematic = true;
                    float blend = 1 - Mathf.Exp(-Time.unscaledDeltaTime * 24);
                    Vector3 predicted = rocket.Position + rocket.Velocity * Mathf.Min(Time.realtimeSinceStartup - rocket.PoseAt, .2f);
                    rocket.Body.position = Vector3.Lerp(rocket.Body.position, predicted, blend);
                    rocket.Body.rotation = Quaternion.Slerp(rocket.Body.rotation, rocket.Rotation, blend);
                }
            }
        }
        static int PhaseOf(PlayMakerFSM f)
        {
            string state = f.ActiveStateName;
            if (state == "ON 3") return 1;
            if (state == "ON") return 2;
            if (state == "Distance?") return 2;
            if (state == "ON 2") return 5;
            if (state == "State 2") return 4;
            if (state == "Wait player" || state == "Wait button" || state == "State 4") return 0;
            return 3;
        }
        public static void FixedUpdate()
        {
            if (!Session.Active || !Session.IsHost || !PlayerSync.InGame) return;
            foreach (Rocket rocket in rockets.Values)
                if (rocket.Root != null && rocket.Thrust != null && rocket.Use.ActiveStateName == "ON")
                {
                    AddForce action = rocket.Thrust;
                    Vector3 force = action.vector.IsNone ? Vector3.zero : action.vector.Value;
                    if (!action.x.IsNone) force.x = action.x.Value;
                    if (!action.y.IsNone) force.y = action.y.Value;
                    if (!action.z.IsNone) force.z = action.z.Value;
                    if (action.space == Space.Self) rocket.Body.AddRelativeForce(force, action.forceMode);
                    else if (!action.atPosition.IsNone) rocket.Body.AddForceAtPosition(force, action.atPosition.Value, action.forceMode);
                    else rocket.Body.AddForce(force, action.forceMode);
                }
        }
        static void HookState(PlayMakerFSM f, FsmState st, string id)
        {
            if (f == null || st == null) return;
            foreach (FsmStateAction action in FireTools.Actions(f, st)) if (action is Hook) return;
            FireTools.Insert(f, st, new Hook { Fsm = f, State = st, Id = id }, false); Replay.Claim(f, "msc-fireworks");
        }
        static Rocket Register(GameObject go, string id)
        {
            var result = new Rocket { Id = id, Root = go, Body = go.GetComponent<Rigidbody>(), Use = Game.FsmOn(go.transform.Find("Trigger").gameObject, "Use"), Position = go.transform.position, Rotation = go.transform.rotation };
            var identity = go.AddComponent<PlayMakerFSM>(); identity.FsmName = "MWCoopId";
            identity.FsmVariables.StringVariables = new[] { new FsmString("ID") { Value = id } };
            rockets[id] = result; Props.Adopt(go, id); HookState(result.Use, result.Use.Fsm.GetState("ON 3"), id);
            if (string.IsNullOrEmpty(result.Use.ActiveStateName)) { applying = true; try { Game.SetState(result.Use, "Wait player"); } finally { applying = false; } }
            // Dynamic FSMs in this MSC build do not reliably forward fixed
            // callbacks. Apply its original thrust parameters to the owned
            // body once per coop fixed step; observers follow the pose.
            foreach (FsmStateAction action in FireTools.Actions(result.Use, result.Use.Fsm.GetState("ON")))
            {
                var thrust = action as AddForce; if (thrust == null) continue;
                thrust.gameObject = new FsmOwnerDefault { OwnerOption = OwnerDefaultOption.SpecifyGameObject, GameObject = new FsmGameObject { Value = go } };
                thrust.Enabled = false; result.Thrust = thrust;
            }
            if (!Session.IsHost)
            {
                PlayMakerFSM automatic = Game.FsmOn(go, "Explosion"); if (automatic != null) automatic.enabled = false;
                foreach (FsmState st in result.Use.Fsm.States)
                    foreach (FsmStateAction action in FireTools.Actions(result.Use, st))
                    {
                        if (action is AddForce) action.Enabled = false;
                        var random = action as SendRandomEvent;
                        if (random != null) for (int i = 0; i < random.events.Length; i++) random.weights[i].Value = random.events[i].Name == "FINISHED" ? 1 : 0;
                    }
            }
            return result;
        }
        static void Apply(int actor, int seq, string id)
        {
            if (!clock.AcceptRequest(actor, seq) || bag == null) return;
            Rocket rocket;
            applying = true; Replay.Depth++;
            try
            {
                if (id.Length == 0)
                {
                    FireTools.Resume(bag); Game.SetState(bag, "Play anim");
                    GameObject go = bag.FsmVariables.GetFsmGameObject("Rocket").Value;
                    if (go == null) return;
                    rocket = Register(go, "msc-rocket-" + (++born));
                }
                else
                {
                    if (!rockets.TryGetValue(id, out rocket) || rocket.Root == null || PhaseOf(rocket.Use) != 0) return;
                    Props.TakePhysics(rocket.Root);
                    FireTools.Resume(rocket.Use); Game.SetState(rocket.Use, "ON 3"); rocket.Phase = 1;
                }
            }
            finally { applying = false; Replay.Depth--; }
            Publish(rocket);
        }
        static void Publish(Rocket rocket)
        {
            Session.SendAll(new NetWriter(Msg.Home).U8(0).U8(State).Str(rocket.Id).I32(++rocket.Revision).U8(rocket.Phase)
                .Vec(rocket.Body.position).Quat(rocket.Body.rotation).Vec(rocket.Body.velocity), true);
        }
        public static void OnMessage(Peer from, int kind, NetReader r)
        {
            if (kind == Request)
            {
                int seq = r.I32(); string key = r.Str();
                if (Session.IsHost && from != null && key.Length < 100) Apply(from.Id, seq, key); return;
            }
            string id = r.Str(); int revision = r.I32(); int phase = kind == State ? r.U8() : -1;
            Vector3 pos = r.Vec(); Quaternion rot = r.Quat(); Vector3 velocity = r.Vec();
            if (Session.IsHost || from == null || from.Id != 0 || id.Length > 100 || prefab == null || phase > 5) return;
            Rocket rocket;
            bool created = false;
            if (!rockets.TryGetValue(id, out rocket))
            {
                if (kind == Pose) return;
                var go = (GameObject)UnityEngine.Object.Instantiate(prefab, pos, rot);
                go.SetActive(true); var f = Game.FsmOn(go, "Explosion"); if (f != null) f.FsmVariables.GetFsmGameObject("Bag").Value = bag.gameObject;
                rocket = Register(go, id); created = true;
            }
            if (rocket.Root == null) return;
            if (kind == Pose)
            {
                if (revision <= rocket.PoseRevision) return;
                rocket.PoseRevision = revision; rocket.Position = pos; rocket.Rotation = rot; rocket.Velocity = velocity; rocket.PoseAt = Time.realtimeSinceStartup; return;
            }
            if (revision <= rocket.Revision) return;
            rocket.Revision = revision; rocket.Position = pos; rocket.Rotation = rot; rocket.Velocity = velocity; rocket.PoseAt = Time.realtimeSinceStartup;
            if (created || rocket.Phase != phase)
            {
                rocket.Body.position = pos; rocket.Body.rotation = rot;
                rocket.Body.isKinematic = phase > 0 && phase <= 3;
                if (!rocket.Body.isKinematic) rocket.Body.velocity = velocity;
                applying = true; Replay.Depth++;
                try
                {
                    FireTools.Resume(rocket.Use);
                    // A late join sees the spent shell directly, without
                    // replaying the explosion or leaving its fuse/trail alive.
                    if (phase == 5)
                    {
                        foreach (string path in new[] { "Trails", "Firewire", "Fireworks", "ExplosionClose" })
                        { Transform part = rocket.Root.transform.Find(path); if (part != null) UnityEngine.Object.Destroy(part.gameObject); }
                        Transform cap = rocket.Root.transform.Find("mesh/cap"); if (cap != null) cap.gameObject.SetActive(false);
                    }
                    string target = phase == 1 ? "ON 3" : phase == 2 ? "ON" : phase == 4 ? "State 2" : phase == 3 ? "State 1" : phase == 5 ? "ON 2" : "Wait player";
                    if (PhaseOf(rocket.Use) != phase) Game.SetState(rocket.Use, target);
                }
                finally { applying = false; Replay.Depth--; }
                rocket.Phase = phase;
            }
        }
    }
}
