using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Native disc identity is Player.ThisCD, independent of hierarchy and of
    // the rigidbody destroyed/recreated by insertion/ejection. Only the host
    // advances the shared player; clients follow its audio and physical slots.
    public static class MscCd
    {
        sealed class Node { public string Key, Back; public PlayMakerFSM Fsm; public float SuppressUntil; public readonly HashSet<string> Actions = new HashSet<string>(); }
        sealed class Edit { public string Key, State, Disc; public int Sequence, Frame, Actor; }
        sealed class Hook : ModHook
        {
            public Node Node;
            public override string Module { get { return "msc-cd"; } }
            public override void OnEnter()
            {
                if (!Session.Active || applying || Replay.Depth > 0) { Finish(); return; }
                // PlayMaker may finish a queued transition after a forced
                // acknowledgement has already executed that same button.
                if (Time.realtimeSinceStartup < Node.SuppressUntil) { FireTools.Cancel(Node.Fsm, State, this, Node.Back); return; }
                FsmGameObject part = Node.Fsm.FsmVariables.FindFsmGameObject("Part");
                string disc = Node.Fsm.FsmName == "Removal" ? Props.CdKey(Node.Fsm.gameObject) ?? "" : part == null || part.Value == null ? "" : Props.CdKey(part.Value) ?? "";
                queue.Add(new Edit { Key = Node.Key, State = State.Name, Disc = disc, Sequence = ++sequence, Frame = Time.frameCount });
                FireTools.Cancel(Node.Fsm, State, this, Node.Back);
            }
        }
        static readonly Dictionary<string, Node> nodes = new Dictionary<string, Node>();
        static readonly HashSet<PlayMakerFSM> known = new HashSet<PlayMakerFSM>();
        static readonly HashSet<GameObject> initializedPlayers = new HashSet<GameObject>();
        static readonly List<Edit> queue = new List<Edit>();
        static MscStereoRequests clock = new MscStereoRequests();
        static int sequence, revision, received, lastActor;
        static float scan, send, snapshotAt, desiredTime, desiredVolume;
        static string desiredKey;
        static GameObject ejecting;
        static float ejectingUntil;
        static bool applying, desiredPlaying, bound;
        static PlayMakerFSM player;
        static AudioSource audio;
        static MscAudioStream stream;
        static readonly float[] correcting = new float[3];
        static readonly int[] desiredLocations = { -1, -1, -1 };
        static readonly GameObject[] discs = new GameObject[3];
        public static void Resynchronize() { scan = send = 0; }
        public static GameObject DiscAt(int index) { return index >= 1 && index <= 3 ? discs[index - 1] : null; }
        public static void Reset()
        {
            nodes.Clear(); known.Clear(); initializedPlayers.Clear(); queue.Clear(); clock = new MscStereoRequests();
            sequence = revision = received = lastActor = 0; scan = send = 0; applying = false; player = null; audio = null;
            if (stream != null) stream.Dispose(); stream = null; desiredKey = null;
            ejecting = null; ejectingUntil = 0;
            for (int i = 0; i < 3; i++) { discs[i] = null; correcting[i] = 0; desiredLocations[i] = -1; }
        }
        static Transform CaseRoot(Transform t)
        { for (; t != null; t = t.parent) if (t.name.StartsWith("cd case(item", StringComparison.Ordinal)) return t; return null; }
        static Transform DeviceRoot(Transform t)
        { for (; t != null; t = t.parent) if (t.name == "cd player(Clone)") return t; return null; }
        static string CaseNumber(Transform root)
        { if (root == null) return ""; string n = root.name; return n.Substring("cd case(item".Length, 1); }
        public static bool Owns(PlayMakerFSM f)
        {
            if (f == null) return false;
            string name = f.name, kind = f.FsmName;
            if (name == "CDPlayer" && (kind == "Update" || kind == "Fetch")) return true;
            if ((kind == "Player" || kind == "Removal") && name.StartsWith("cd(item", StringComparison.Ordinal)) return Props.CdKey(f.gameObject) != null;
            if ((kind == "Open" || kind == "Assembly" || kind == "Use") && (name.StartsWith("cd case(item", StringComparison.Ordinal) || name == "trigger_cd") && CaseRoot(f.transform) != null) return true;
            return (name == "trigger_disc" && kind == "Assembly"
                || f.name == "Eject" && f.FsmName == "Use" || f.name == "RadioCDSwitch" && f.FsmName == "Use"
                || f.name == "TrackChannelSwitch" && (f.FsmName == "ChangeTrack" || f.FsmName == "ChangeChannel")) && DeviceRoot(f.transform) != null;
        }
        static string Key(PlayMakerFSM f)
        {
            if (f.FsmName == "Removal") return Props.CdKey(f.gameObject) + "::Removal";
            Transform root = CaseRoot(f.transform);
            if (root != null) return "case" + CaseNumber(root) + "/" + (f.transform == root ? "" : f.name) + "::" + f.FsmName;
            return "player/" + f.name + "::" + f.FsmName;
        }
        static bool ActionState(PlayMakerFSM f, string state)
        {
            if (f.FsmName == "Assembly") return state == "Assemble";
            if (f.FsmName == "Removal") return state == "Remove part";
            if (f.FsmName == "Open") return state == "Bool test";
            if (f.FsmName == "ChangeTrack") return state == "State 1";
            if (f.FsmName == "ChangeChannel") return state == "Check status";
            return f.FsmName == "Use" && state == "Switch";
        }
        static void Scan()
        {
            scan = Time.realtimeSinceStartup + 2;
            foreach (UnityEngine.Object value in Game.AllFsms())
            {
                PlayMakerFSM f = value as PlayMakerFSM;
                if (f == null || f.hideFlags != HideFlags.None) continue;
                if (f.FsmName == "Player")
                {
                    FsmString id = f.FsmVariables.FindFsmString("ThisCD");
                    if (id != null) for (int i = 1; i <= 3; i++) if (id.Value == "CD" + i) discs[i - 1] = f.gameObject;
                }
                if (f.name == "CDPlayer" && f.FsmName == "Update")
                {
                    player = f; audio = f.GetComponent<AudioSource>();
                    if (!Session.IsHost)
                    {
                        f.enabled = false;
                        // SendEventByName can enter a disabled FSM. Native
                        // local power/track events must not overwrite the
                        // receiver's independently owned host audio stream.
                        foreach (FsmState state in f.FsmStates)
                            foreach (FsmStateAction action in FireTools.Actions(f, state) ?? new FsmStateAction[0])
                                if (action is AudioPlay || action is AudioStop || action is SetAudioClip) action.Enabled = false;
                    }
                }
                if (known.Contains(f) || !Owns(f) || f.FsmName == "Player") continue;
                known.Add(f);
                if (!f.Fsm.Initialized) f.Fsm.InitData();
                if (DeviceRoot(f.transform) != null) MscStereoControls.Prepare(f);
                var node = new Node { Fsm = f, Key = Key(f), Back = f.FsmName == "Assembly" ? "Wait for assembly" : f.FsmName == "Removal" ? "Mouse off" : "Wait player" };
                foreach (FsmState state in f.FsmStates) if (ActionState(f, state.Name))
                { node.Actions.Add(state.Name); FireTools.Insert(f, state, new Hook { Node = node }, false); }
                Replay.Claim(f, "msc-cd");
                if (node.Actions.Count > 0) nodes[node.Key] = node;
            }
        }
        public static void Update()
        {
            if (!Session.Active || !PlayerSync.InGame) return;
            if (!bound) { bound = true; Session.PlayerLeft += delegate(PlayerInfo p) { if (p != null) clock.ForgetActor(p.Id); }; }
            if (Time.realtimeSinceStartup >= scan) Scan();
            for (int i = 0; i < queue.Count; i++)
            {
                Edit e = queue[i]; if (Time.frameCount <= e.Frame) continue; queue.RemoveAt(i--);
                if (Session.IsHost) Apply(0, e);
                else Session.SendToHost(Packet(0, e), true);
            }
            if (Session.IsHost && Time.realtimeSinceStartup >= send)
            { send = Time.realtimeSinceStartup + .5f; Publish(); }
            if (!Session.IsHost) { ReconcileDiscs(); FollowAudio(); }
        }
        static NetWriter Packet(int kind, Edit edit)
        {
            var w = new NetWriter(Msg.Cd).U8(kind); if (kind == 1) w.I32(++revision).U8(edit.Actor);
            return w.I32(edit.Sequence).Str(edit.Key).Str(edit.State).Str(edit.Disc);
        }
        static GameObject Disc(string id)
        { for (int i = 1; i <= 3; i++) if (id == "w:cd:CD" + i) return DiscAt(i); return null; }
        static bool Execute(Edit edit)
        {
            Node node;
            if (!nodes.TryGetValue(edit.Key, out node) || node.Fsm == null || !node.Actions.Contains(edit.State)) return false;
            PlayMakerFSM f = node.Fsm; FireTools.Resume(f);
            GameObject disc = Disc(edit.Disc);
            GameObject ejected = null;
            if (f.name == "Eject") for (int i = 1; i <= 3; i++) if (Location(DiscAt(i)) == 2) ejected = DiscAt(i);
            if (f.FsmName == "Removal" && (disc != f.gameObject || Location(disc) != 1)) return false;
            if (f.FsmName == "Assembly")
            {
                if (disc == null) return false;
                FsmGameObject target = f.FsmVariables.FindFsmGameObject("Target");
                if (target != null && target.Value != null && target.Value != disc) return false;
                FsmBool occupied = f.FsmVariables.FindFsmBool("CDin");
                FsmGameObject part = f.FsmVariables.FindFsmGameObject("Part");
                if (part == null || occupied != null && occupied.Value && part.Value != disc) return false;
                Props.DropForSimulation(disc); part.Value = disc;
            }
            applying = true; Replay.Depth++;
            bool remoteRadio = edit.Actor != Session.LocalId && (f.name == "RadioCDSwitch" || f.FsmName == "ChangeChannel");
            if (remoteRadio) Radio.BeforeRemote();
            else if (f.name == "RadioCDSwitch" || f.FsmName == "ChangeChannel") Radio.BeforeLocal();
            try {
                node.SuppressUntil = Time.realtimeSinceStartup + .15f;
                f.gameObject.SetActive(true); f.enabled = true;
                bool result = Game.SetState(f, edit.State);
                if (result && f.FsmName == "Assembly" && DeviceRoot(f.transform) != null && disc != null)
                {
                    CompletePlayerMount(disc);
                    ejecting = null;
                }
                if (result)
                {
                    for (int i = 1; i <= 3; i++)
                        if (DiscAt(i) == disc && disc != null) desiredLocations[i - 1] = f.FsmName == "Removal" ? 0 : DeviceRoot(f.transform) != null ? 2 : 1;
                    if (ejected != null)
                    {
                        ejecting = ejected; ejectingUntil = Time.realtimeSinceStartup + 4;
                        for (int i = 1; i <= 3; i++) if (DiscAt(i) == ejected) desiredLocations[i - 1] = 0;
                    }
                }
                return result;
            }
            finally { if (remoteRadio) Radio.AfterRemote(f.transform, edit.Actor, edit.State); applying = false; Replay.Depth--; }
        }
        static void Apply(int actor, Edit edit)
        {
            if (!clock.Accept(actor, edit.Sequence)) return;
            edit.Actor = actor; lastActor = actor;
            bool applied = Execute(edit);
            Log.Info("MSC CD action #" + actor + " " + edit.Key + " -> " + edit.State + ";disc=" + edit.Disc + ";applied=" + applied);
            if (applied) { Session.Broadcast(Packet(1, edit), true); send = 0; }
        }
        static int Location(GameObject disc)
        { if (disc == null) return -1; if (CaseRoot(disc.transform) != null) return 1; if (DeviceRoot(disc.transform) != null) return 2; return 0; }
        public static bool Mounted(GameObject go)
        { return go != null && go.name.StartsWith("cd(item", StringComparison.Ordinal) && Props.CdKey(go) != null && Location(go) > 0; }
        static void SelectDisc(GameObject disc)
        {
            string identity = Props.CdKey(disc);
            if (player != null && identity != null) player.FsmVariables.GetFsmString("CurrentCD").Value = identity.Substring("w:cd:".Length);
        }
        static void CompletePlayerMount(GameObject disc, bool force = true)
        {
            PlayMakerFSM native = Game.FsmOn(disc, "Player");
            // Native EnableFSM does not restart an already enabled/finished
            // disc. A snapshot repair needs the same initialization as input.
            if (native != null)
            {
                bool initialize = force || !native.enabled || !initializedPlayers.Contains(disc);
                native.enabled = true;
                if (initialize) { Game.SetState(native, "State 1"); initializedPlayers.Add(disc); }
            }
            SelectDisc(disc);
        }
        static Transform FindCase(int index)
        {
            foreach (Node node in nodes.Values) if (node.Key == "case" + index + "/::Open" && node.Fsm != null) return node.Fsm.transform;
            return null;
        }
        static Node DeviceSlot()
        { Node node; return nodes.TryGetValue("player/trigger_disc::Assembly", out node) ? node : null; }
        static void Detach(GameObject disc)
        {
            int location = Location(disc);
            if (location == 0) return;
            if (location == 2)
            {
                CompletePlayerMount(disc, false);
                Node slot = DeviceSlot();
                if (slot != null && slot.Fsm != null)
                {
                    // The snapshot says the host has already ejected it.
                    // Clear the slot without adding the button's one-second
                    // delay again on the receiving peer.
                    FireTools.Resume(slot.Fsm); slot.Fsm.gameObject.SetActive(true); slot.Fsm.enabled = true;
                    Game.SetState(slot.Fsm, "Eject");
                }
            }
            PlayMakerFSM native = Game.FsmOn(disc, location == 1 ? "Removal" : "Player");
            if (native != null) { native.enabled = true; Game.SetState(native, "Remove part"); }
            initializedPlayers.Remove(disc);
        }
        static void ReconcileDiscs()
        {
            applying = true; Replay.Depth++;
            try
            {
                for (int i = 1; i <= 3; i++)
                {
                    GameObject disc = DiscAt(i); int desired = desiredLocations[i - 1];
                    if (disc == null || desired < 0) continue;
                    if (Location(disc) != 2) initializedPlayers.Remove(disc);
                    if (Location(disc) == desired)
                    { if (desired == 2) CompletePlayerMount(disc, false); continue; }
                    if (Time.realtimeSinceStartup < correcting[i - 1]) continue;
                    Props.DropForSimulation(disc);
                    int actual = Location(disc);
                    correcting[i - 1] = Time.realtimeSinceStartup + (actual == 2 ? 1.5f : .35f);
                    if (actual != 0) { Detach(disc); continue; }
                    // Native removal/ejection must finish before a new mount
                    // begins; their delayed SetParent must not undo that mount.
                    Node slot = desired == 2 ? DeviceSlot() : null;
                    Transform box = FindCase(i), trigger = box == null ? null : box.Find("trigger_cd");
                    PlayMakerFSM mount = desired == 1 && trigger != null ? Game.FsmOn(trigger.gameObject, "Assembly") : slot == null ? null : slot.Fsm;
                    if (mount != null)
                    {
                        FireTools.Resume(mount); mount.gameObject.SetActive(true); mount.enabled = true;
                        mount.FsmVariables.GetFsmGameObject("Part").Value = disc; Game.SetState(mount, "Assemble");
                        if (desired == 2) CompletePlayerMount(disc);
                    }
                }
            }
            finally { Replay.Depth--; applying = false; }
        }
        static void Publish()
        {
            if (Session.RemoteCount == 0) return;
            for (int i = 1; i <= 3; i++) if (Location(DiscAt(i)) == 2) SelectDisc(DiscAt(i));
            var w = new NetWriter(Msg.Cd).U8(2).I32(++revision).U8(lastActor);
            for (int i = 1; i <= 3; i++)
            {
                Transform box = FindCase(i); PlayMakerFSM open = box == null ? null : Game.FsmOn(box.gameObject, "Open");
                int location = Location(DiscAt(i));
                // A later snapshot must preserve the accepted ejection while
                // its native delayed event/animation is still running.
                if (DiscAt(i) == ejecting && Time.realtimeSinceStartup < ejectingUntil) location = 0;
                w.Bool(open != null && open.FsmVariables.GetFsmBool("Open").Value).U8(location + 1);
            }
            Node mode, station, trackButton;
            w.Bool(nodes.TryGetValue("player/RadioCDSwitch::Use", out mode) && mode.Fsm != null && mode.Fsm.FsmVariables.GetFsmBool("RadioOn").Value)
                .Bool(nodes.TryGetValue("player/TrackChannelSwitch::ChangeChannel", out station) && station.Fsm != null && station.Fsm.FsmVariables.GetFsmBool("Channel").Value);
            w.Bool(mode != null && mode.Fsm != null && mode.Fsm.enabled)
                .Bool(station != null && station.Fsm != null && station.Fsm.enabled)
                .Bool(nodes.TryGetValue("player/TrackChannelSwitch::ChangeTrack", out trackButton) && trackButton.Fsm != null && trackButton.Fsm.enabled);
            string key = audio == null || audio.clip == null ? "" : MscMusic.ClipKey(audio.clip);
            w.Str(player == null ? "CD1" : player.FsmVariables.GetFsmString("CurrentCD").Value)
                // Index is reset to -1 while the final song is still playing;
                // CurrentTrack is the native displayed, one-based track.
                .I32(player == null ? -1 : player.FsmVariables.GetFsmInt("CurrentTrack").Value - 1)
                .Str(key).F32(MscAudioStream.Time(audio)).Bool(audio != null && audio.isPlaying).F32(audio == null ? 0 : audio.volume);
            Session.Broadcast(w, false);
        }
        public static void OnMessage(Peer from, NetReader r)
        {
            int kind = r.U8();
            if (kind == 0 || kind == 1)
            {
                int canonical = kind == 1 ? r.I32() : 0;
                int actor = kind == 1 ? r.U8() : 0;
                var e = new Edit { Actor = actor, Sequence = r.I32(), Key = r.Str(), State = r.Str(), Disc = r.Str() };
                if (r.More || e.Key.Length > 160 || e.State.Length > 80 || e.Disc.Length > 12) throw new System.IO.IOException("Invalid CD action");
                if (Session.IsHost && kind == 0 && from != null) Apply(from.Id, e);
                else if (!Session.IsHost && kind == 1 && from != null && from.Id == 0 && canonical > received) { received = canonical; Execute(e); }
                return;
            }
            if (kind != 2 || Session.IsHost || from == null || from.Id != 0 || !PlayerSync.InGame) return;
            int rev = r.I32(); if (rev <= received) return; received = rev;
            int snapshotActor = r.U8();
            if (player == null || Time.realtimeSinceStartup >= scan) Scan();
            applying = true; Replay.Depth++;
            try
            {
                for (int i = 1; i <= 3; i++)
                {
                    bool isOpen = r.Bool(); int location = r.U8() - 1;
                    Transform box = FindCase(i); GameObject disc = DiscAt(i);
                    PlayMakerFSM open = box == null ? null : Game.FsmOn(box.gameObject, "Open");
                    if (open != null && open.FsmVariables.GetFsmBool("Open").Value != isOpen)
                    { open.FsmVariables.GetFsmBool("Open").Value = isOpen; Game.SetState(open, isOpen ? "Open" : "Close"); }
                    if (location < -1 || location > 2) throw new System.IO.IOException("Invalid CD location");
                    desiredLocations[i - 1] = location;
                }
                bool radioMode = r.Bool(), channel = r.Bool();
                bool modeEnabled = r.Bool(), stationEnabled = r.Bool(), trackEnabled = r.Bool(); Node mode, station, trackButton;
                nodes.TryGetValue("player/RadioCDSwitch::Use", out mode);
                nodes.TryGetValue("player/TrackChannelSwitch::ChangeChannel", out station);
                bool radioChanged = mode != null && mode.Fsm != null && mode.Fsm.FsmVariables.GetFsmBool("RadioOn").Value != radioMode;
                bool channelChanged = station != null && station.Fsm != null && station.Fsm.FsmVariables.GetFsmBool("Channel").Value != channel;
                bool remoteRadio = snapshotActor != Session.LocalId && (radioChanged || channelChanged);
                if (remoteRadio) Radio.BeforeRemote();
                if (nodes.TryGetValue("player/RadioCDSwitch::Use", out mode) && mode.Fsm != null)
                {
                    if (mode.Fsm.FsmVariables.GetFsmBool("RadioOn").Value != radioMode)
                        Game.SetState(mode.Fsm, radioMode ? "On" : "Off");
                    mode.Fsm.enabled = modeEnabled;
                }
                if (nodes.TryGetValue("player/TrackChannelSwitch::ChangeChannel", out station) && station.Fsm != null)
                {
                    bool changed = station.Fsm.FsmVariables.GetFsmBool("Channel").Value != channel;
                    station.Fsm.FsmVariables.GetFsmBool("Channel").Value = channel;
                    if (changed && radioMode && stationEnabled) Game.SetState(station.Fsm, "State 1");
                    station.Fsm.enabled = stationEnabled;
                }
                if (nodes.TryGetValue("player/TrackChannelSwitch::ChangeTrack", out trackButton) && trackButton.Fsm != null) trackButton.Fsm.enabled = trackEnabled;
                if (remoteRadio && mode != null && mode.Fsm != null) Radio.AfterRemote(mode.Fsm.transform, snapshotActor, radioMode ? "On" : "CD");
                string cd = r.Str(); int track = r.I32(); string key = r.Str(); float time = r.F32(); bool play = r.Bool(); float volume = r.F32();
                // Native Init/STOP CD uses Index=-1 before selecting track 1.
                if (track < -1 || track > 201 || float.IsNaN(time) || float.IsInfinity(time) || time < 0 || float.IsNaN(volume) || float.IsInfinity(volume) || r.More) throw new System.IO.IOException("Invalid CD playback: track=" + track + ";time=" + time + ";volume=" + volume + ";trailing=" + r.More);
                if (player != null)
                {
                    player.enabled = false; player.FsmVariables.GetFsmString("CurrentCD").Value = cd; player.FsmVariables.GetFsmInt("Index").Value = track;
                    player.FsmVariables.GetFsmInt("CurrentTrack").Value = track + 1;
                    FsmString text = player.FsmVariables.FindFsmString("Text"); if (text != null) text.Value = play ? "track " + (track + 1) : "no cd";
                    foreach (FsmStateAction a in FireTools.Actions(player, player.Fsm.GetState("Play track"))) if (a is SetProperty || a is SetFsmString) a.OnEnter();
                }
                desiredTime = time + (play ? Mathf.Clamp(Session.HostPing * .0005f, 0, .25f) : 0); desiredPlaying = play; desiredVolume = Mathf.Clamp01(volume); snapshotAt = Time.realtimeSinceStartup;
                if (desiredKey != key)
                {
                    desiredKey = key; if (stream != null) stream.Dispose(); stream = null;
                    if (audio != null) audio.Stop();
                    string file = MscMusic.FileFor(key); if (file != null) stream = new MscAudioStream(file, key);
                }
            }
            finally { applying = false; Replay.Depth--; }
        }
        static void FollowAudio()
        {
            if (audio == null) return;
            audio.volume = desiredVolume;
            float target = desiredTime + (desiredPlaying ? Time.realtimeSinceStartup - snapshotAt : 0);
            if (stream != null) { stream.Apply(audio, desiredPlaying, target); return; }
            AudioClip clip = MscMusic.Clip(desiredKey ?? "");
            if (clip == null) { if (audio.isPlaying) audio.Stop(); return; }
            if (audio.clip != clip) audio.clip = clip;
            bool start = desiredPlaying && !audio.isPlaying && audio.gameObject.activeInHierarchy;
            if (start) audio.Play(); else if (!desiredPlaying && audio.isPlaying) audio.Stop();
            if (start || Mathf.Abs(MscAudioStream.Time(audio) - target) > .25f) MscAudioStream.Seek(audio, target);
        }
    }
}
