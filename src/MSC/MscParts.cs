using System;
using System.Collections.Generic;
using System.Reflection;
using Harmony;
using HutongGames.PlayMaker;
using MWCoop.Net;
using SummerCoop.Core;
using SummerCoop.Game;
using UnityEngine;
using Object = UnityEngine.Object;
using Session = MWCoop.Net.Session;

namespace MWCoop
{
    // MSC uses db_ThisPart/ThisPart and Assembly/Removal/Stage rather than
    // MWC's VIN, Install 2 and BoltTightness. The network/replay engine is MWCoop.
    public static class MscParts
    {
        sealed class Binding { public string Key, MountState; public PlayMakerFSM Fsm; public BoltTotal Total; public bool BoltChanged; public readonly MscEditClock Clock = new MscEditClock(); }
        sealed class BoltTotal { public PlayMakerFSM Fsm; public FsmFloat Tightness; public FsmBool Bolted; }
        sealed class Pending { public Binding Binding; public string State; public GameObject Part; public int Frame; public float ReadyAt; }
        sealed class PendingBolt { public int Who, Target, Revision; public float Until, Tightness; public bool HasTotal, HasBolted, Bolted; }
        sealed class Scope { public float Until; }
        static readonly Dictionary<string, GameObject> parts = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        static readonly Dictionary<GameObject, string> ids = new Dictionary<GameObject, string>();
        static readonly Dictionary<string, Binding> bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
        static readonly Dictionary<PlayMakerFSM, Binding> byFsm = new Dictionary<PlayMakerFSM, Binding>();
        static readonly HashSet<PlayMakerFSM> hooked = new HashSet<PlayMakerFSM>();
        static readonly List<Pending> pending = new List<Pending>();
        static readonly Dictionary<string, PendingBolt> pendingBolts = new Dictionary<string, PendingBolt>(StringComparer.Ordinal);
        static readonly List<string> completedBolts = new List<string>();
        static readonly Dictionary<PlayMakerFSM, MscEditClock> totalClocks = new Dictionary<PlayMakerFSM, MscEditClock>();
        static readonly Dictionary<Binding, float> repairs = new Dictionary<Binding, float>();
        static readonly List<Binding> repaired = new List<Binding>();
        static readonly Dictionary<Fsm, Scope> remote = new Dictionary<Fsm, Scope>();
        static readonly HashSet<MethodBase> patched = new HashSet<MethodBase>();
        static readonly List<Fsm> finished = new List<Fsm>();
        static HarmonyInstance harmony;
        static float nextScan, nextBoltSnapshot;
        static int localBoltRevision, hostBoltRevision;
        static Binding exercisedBolt;
        public static int Count { get { return bindings.Count; } }
        public static void Reset() { parts.Clear(); ids.Clear(); bindings.Clear(); byFsm.Clear(); hooked.Clear(); pending.Clear(); pendingBolts.Clear(); completedBolts.Clear(); totalClocks.Clear(); repairs.Clear(); remote.Clear(); nextScan = nextBoltSnapshot = 0; localBoltRevision = hostBoltRevision = 0; }
        public static bool Owns(PlayMakerFSM f)
        {
            return f != null && !MscBrewing.Owns(f) && (f.FsmName == "Assembly" || f.FsmName == "Removal" || f.FsmName == "Assemble" && Recon.Path(f.transform).IndexOf("Wiring", StringComparison.OrdinalIgnoreCase) >= 0 || IsBolt(f));
        }
        public static bool IsBolt(PlayMakerFSM f) { return f != null && f.FsmName == "Screw" && f.FsmVariables.FindFsmInt("Stage") != null; }
        public static string IdOf(GameObject go)
        {
            if (go == null) return "";
            string id; if (ids.TryGetValue(go, out id)) return id;
            foreach (PlayMakerFSM f in go.GetComponents<PlayMakerFSM>())
            {
                if (f.FsmName != "Removal") continue;
                FsmGameObject database = f.FsmVariables.FindFsmGameObject("db_ThisPart");
                if (database == null || database.Value == null) continue;
                id = "mscdb:" + Parts.RankPath(database.Value.transform);
                ids[go] = id; parts[id] = go; return id;
            }
            id = MscBelt.Identity(go);
            if (id.Length > 0) { ids[go] = id; parts[id] = go; return id; }
            return "";
        }
        public static GameObject Find(string id) { GameObject go; return parts.TryGetValue(id, out go) && go != null ? go : null; }
        public static void Index()
        {
            foreach (Object value in Game.AllFsms())
            {
                PlayMakerFSM f = value as PlayMakerFSM;
                if (f != null && f.FsmName == "Use" && f.hideFlags == HideFlags.None) IdOf(f.gameObject);
                if (f == null || f.FsmName != "Data" || f.hideFlags != HideFlags.None) continue;
                FsmGameObject part = f.FsmVariables.FindFsmGameObject("ThisPart");
                if (part == null || part.Value == null || f.FsmVariables.FindFsmBool("Installed") == null) continue;
                string id = "mscdb:" + Parts.RankPath(f.transform);
                GameObject previous;
                if (parts.TryGetValue(id, out previous) && previous != null && previous != part.Value) throw new InvalidOperationException("Ambiguous MSC database part: " + id);
                parts[id] = part.Value; ids[part.Value] = id;
            }
        }
        internal static string Key(PlayMakerFSM f)
        {
            Transform t = f.transform;
            string relative = "";
            for (Transform p = t; p != null; p = p.parent)
            {
                string id = IdOf(p.gameObject);
                if (id.Length > 0) return id + "/" + relative + "::" + f.FsmName;
                relative = p.name + "[" + SameNameIndex(p) + "]/" + relative;
            }
            return relative + "::" + f.FsmName;
        }
        static int SameNameIndex(Transform t)
        {
            if (t.parent == null) return 0;
            int rank = 0;
            for (int i = 0; i < t.parent.childCount; i++) { Transform other = t.parent.GetChild(i); if (other == t) break; if (other.name == t.name) rank++; }
            return rank;
        }
        sealed class Hook : ModHook
        {
            public Binding Binding; public string StateName;
            public override string Module { get { return "msc-parts"; } }
            public override void OnEnter()
            {
                try
                {
                    if (Session.Active && PlayerSync.InGame && Time.timeSinceLevelLoad > 3 && Replay.Depth == 0 && !remote.ContainsKey(Fsm))
                    {
                        RefreshKey(Binding);
                        FsmGameObject variable = Binding.Fsm.FsmVariables.FindFsmGameObject("Part") ?? Binding.Fsm.FsmVariables.FindFsmGameObject("ThisPart");
                        GameObject part = variable != null ? variable.Value : null;
                        if (part == null && Binding.Fsm.FsmName == "Removal") part = Binding.Fsm.gameObject;
                        if (Binding.Fsm.FsmName == "Assembly") Binding.MountState = StateName;
                        bool bolt = IsBolt(Binding.Fsm);
                        pending.Add(new Pending { Binding = Binding, State = StateName, Part = part, Frame = Time.frameCount + (bolt ? 2 : 1), ReadyAt = bolt ? Time.realtimeSinceStartup + .1f : 0 });
                    }
                }
                catch (Exception e) { Replay.HookError(e); }
                Finish();
            }
        }
        static bool ActionState(PlayMakerFSM f, FsmState state)
        {
            string name = state.Name;
            if (IsBolt(f)) return name == "Screw 2" || name == "Unscrew 2" || name == "Screw" || name == "Unscrew";
            if (f.FsmName == "Assembly") return name == "Assemble" || name.StartsWith("Assemble ", StringComparison.Ordinal);
            if (f.FsmName == "Removal") return name.StartsWith("Remove", StringComparison.Ordinal) || name.StartsWith("Disassemble", StringComparison.Ordinal);
            if (f.FsmName == "Assemble" && Recon.Path(f.transform).IndexOf("Wiring", StringComparison.OrdinalIgnoreCase) >= 0) return name == "Finish assembly";
            return name == "Assemble" || name == "Connect";
        }
        static void Scan()
        {
            Index();
            foreach (Object value in Game.AllFsms())
            {
                PlayMakerFSM f = value as PlayMakerFSM;
                if (f == null || !Owns(f) || Game.UnderPlayer(f.transform) || f.hideFlags != HideFlags.None || !f.Fsm.Initialized) continue;
                Binding known;
                if (byFsm.TryGetValue(f, out known)) { RefreshKey(known); continue; }
                // Use.State 1 assigns a consumable's ID after Instantiate.
                // Never freeze its temporary scene/hand path into the binding.
                if (f.FsmName == "Removal" && MscBelt.IsBelt(f.gameObject) && IdOf(f.gameObject).Length == 0) continue;
                if (!Replay.Claim(f, "msc-parts")) continue;
                Binding binding = new Binding { Key = Key(f), Fsm = f };
                Binding previous;
                if (bindings.TryGetValue(binding.Key, out previous) && previous.Fsm != null && previous.Fsm != f) { Log.Warn("MSC ambiguous assembly: " + binding.Key); continue; }
                bool installed = false;
                foreach (FsmState state in f.Fsm.States)
                {
                    if (!ActionState(f, state)) continue;
                    List<FsmStateAction> actions = new List<FsmStateAction>(state.Actions); actions.Insert(0, new Hook { Binding = binding, StateName = state.Name }); state.Actions = actions.ToArray(); installed = true;
                }
                if (installed) { bindings[binding.Key] = binding; byFsm[f] = binding; hooked.Add(f); PatchActions(f); if (IsBolt(f)) binding.Total = TotalOf(f); RepairMount(binding); }
            }
        }
        static void RefreshKey(Binding binding)
        {
            string current = Key(binding.Fsm);
            if (current == binding.Key || !(current.StartsWith("mscdb:", StringComparison.Ordinal) || current.StartsWith("mscitem:", StringComparison.Ordinal))) return;
            Binding existing;
            if (bindings.TryGetValue(current, out existing) && existing != binding) throw new InvalidOperationException("Duplicate stable MSC binding: " + current);
            bindings.Remove(binding.Key); binding.Key = current; bindings[current] = binding;
        }
        static void RepairMount(Binding binding)
        {
            if (binding.Fsm.FsmName != "Assembly") return;
            FsmGameObject held = binding.Fsm.FsmVariables.FindFsmGameObject("Part");
            if (held != null && MscBelt.IsBelt(held.Value) && binding.MountState != null)
            {
                NativeAssembly.Complete(new FsmBinding { Component = binding.Fsm }, binding.MountState, held.Value);
                if (!MscBelt.Installed(held.Value))
                {
                    FsmState end = binding.Fsm.Fsm.GetState("End 2");
                    if (end != null)
                    {
                        Replay.Depth++;
                        try { foreach (FsmStateAction action in end.Actions) if (action != null && !(action is ModHook)) action.OnEnter(); }
                        finally { Replay.Depth--; }
                    }
                }
                return;
            }
            foreach (FsmGameObject db in binding.Fsm.FsmVariables.GameObjectVariables)
            {
                if (!db.Name.StartsWith("db_ThisPart", StringComparison.Ordinal) || db.Value == null) continue;
                PlayMakerFSM data = Game.FsmOn(db.Value, "Data");
                FsmGameObject part = data == null ? null : data.FsmVariables.FindFsmGameObject("ThisPart");
                if (part == null || part.Value == null || part.Value.GetComponent<Rigidbody>() != null || !NativeAssembly.Installed(part.Value)) continue;
                FsmState[] states = binding.Fsm.Fsm.States;
                FsmState selected = binding.MountState == null ? null : binding.Fsm.Fsm.GetState(binding.MountState);
                if (selected != null) states = new[] { selected };
                foreach (FsmState state in states)
                    if (NativeAssembly.MountAction(state.Actions) != null && NativeAssembly.BelongsToSlot(new FsmBinding { Component = binding.Fsm }, part.Value))
                    { NativeAssembly.Complete(new FsmBinding { Component = binding.Fsm }, state.Name, part.Value); break; }
            }
        }
        public static void Update()
        {
            if (!PlayerSync.InGame) return;
            if (Time.realtimeSinceStartup >= nextScan) { nextScan = Time.realtimeSinceStartup + 3f; Scan(); }
            for (int i = 0; i < pending.Count; i++)
            {
                Pending item = pending[i]; if (Time.frameCount < item.Frame || Time.realtimeSinceStartup < item.ReadyAt) continue; pending.RemoveAt(i--);
                if (item.Binding.Fsm == null) continue;
                if (IsBolt(item.Binding.Fsm))
                {
                    item.Binding.BoltChanged = true;
                    int revision = Session.IsHost ? ++hostBoltRevision : ++localBoltRevision;
                    if (Session.IsHost) item.Binding.Clock.AcceptSnapshot(revision);
                    Session.SendAll(BoltPacket(item.Binding, Session.LocalId, revision), true);
                }
                else
                {
                    if (item.Part != null && NativeAssembly.Installed(item.Part)) RepairMount(item.Binding);
                    Session.SendAll(new NetWriter(Msg.Part).U8(Session.LocalId).Str(item.Binding.Key).Str(item.State).Str(IdOf(item.Part)), true);
                }
            }
            finished.Clear();
            foreach (KeyValuePair<Fsm, Scope> item in remote)
            {
                string state = item.Key.ActiveStateName;
                if (Time.realtimeSinceStartup > item.Value.Until || state == "Idle" || state == "Check for Part collision" || state == "Wait for cap") finished.Add(item.Key);
            }
            foreach (Fsm f in finished) remote.Remove(f);
            repaired.Clear();
            foreach (KeyValuePair<Binding, float> repair in repairs)
            {
                if (repair.Key.Fsm == null || Time.realtimeSinceStartup > repair.Value) { repaired.Add(repair.Key); continue; }
                RepairMount(repair.Key);
            }
            foreach (Binding binding in repaired) repairs.Remove(binding);
            completedBolts.Clear();
            foreach (KeyValuePair<string, PendingBolt> item in pendingBolts)
            {
                Binding b;
                if (bindings.TryGetValue(item.Key, out b) && b.Fsm != null && IsBolt(b.Fsm))
                { ReceiveBolt(b, item.Value); completedBolts.Add(item.Key); }
                else if (Time.realtimeSinceStartup > item.Value.Until) completedBolts.Add(item.Key);
            }
            foreach (string key in completedBolts) pendingBolts.Remove(key);
            if (Session.IsHost && Session.Active && Time.realtimeSinceStartup >= nextBoltSnapshot)
            {
                nextBoltSnapshot = Time.realtimeSinceStartup + 2;
                foreach (Binding b in bindings.Values) if (b.Fsm != null && b.BoltChanged) Session.Broadcast(BoltPacket(b, Session.LocalId, b.Clock.HostRevision), true);
            }
        }
        public static void OnPart(Peer from, NetReader reader)
        {
            int who = reader.U8(); string key = reader.Str(), state = reader.Str(), id = reader.Str();
            if (Session.IsHost) who = from.Id;
            Binding binding;
            if (!bindings.TryGetValue(key, out binding) || binding.Fsm == null) { Scan(); bindings.TryGetValue(key, out binding); }
            GameObject part = Find(id);
            if ((binding == null || binding.Fsm == null) && MscBelt.IsBelt(part))
            {
                // Removal is addressed by the numbered item as well as the FSM
                // key, including an item that was initialized after the scan.
                PlayMakerFSM removal = Game.FsmOn(part, "Removal");
                if (removal != null) byFsm.TryGetValue(removal, out binding);
            }
            if (binding == null || binding.Fsm == null) throw new ProtocolException("Unknown MSC assembly point: " + key + " (part " + id + ")");
            FsmState native = binding.Fsm.Fsm.GetState(state);
            if (native == null || !ActionState(binding.Fsm, native) || IsBolt(binding.Fsm)) throw new ProtocolException("Invalid MSC assembly state");
            if (binding.Fsm.FsmName == "Assembly" && (part == null || !NativeAssembly.Accepts(binding.Fsm.FsmVariables.StringVariables, MscBelt.IsBelt(part) ? "alternator belt(Clone)" : part.name))) throw new ProtocolException("Incorrect MSC part");
            if (binding.Fsm.FsmName == "Assembly" && (NativeAssembly.Installed(part) || MscBelt.Installed(part))) { RepairMount(binding); return; }
            if (binding.Fsm.FsmName == "Assembly") binding.MountState = state;
            FsmGameObject variable = binding.Fsm.FsmVariables.FindFsmGameObject("Part"); if (variable != null && part != null) variable.Value = part;
            Execute(binding.Fsm, state);
            if (binding.Fsm.FsmName == "Assembly") repairs[binding] = Time.realtimeSinceStartup + 3;
            RepairMount(binding);
            if (Session.IsHost) Session.Broadcast(new NetWriter(Msg.Part).U8(who).Str(key).Str(state).Str(id), true, who);
        }
        public static void OnBolt(Peer from, NetReader reader)
        {
            int who = reader.U8(); string key = reader.Str(); int target = reader.I32(), revision = reader.I32(); if (Session.IsHost) who = from.Id;
            PendingBolt edit = new PendingBolt { Who = who, Target = target, Revision = revision, Until = Time.realtimeSinceStartup + 60, HasTotal = reader.Bool() };
            if (edit.HasTotal) { edit.Tightness = reader.F32(); edit.HasBolted = reader.Bool(); edit.Bolted = reader.Bool(); }
            Binding binding;
            if (target < 0 || target > 8 || revision <= 0 || reader.More) throw new ProtocolException("Invalid MSC bolt stage/revision");
            if (!bindings.TryGetValue(key, out binding) || binding.Fsm == null || !IsBolt(binding.Fsm))
            {
                PendingBolt previous;
                if (!pendingBolts.TryGetValue(key, out previous) || Session.IsHost && previous.Who != who || revision > previous.Revision)
                    pendingBolts[key] = edit;
                return;
            }
            ReceiveBolt(binding, edit);
        }
        static NetWriter BoltPacket(Binding binding, int who, int revision)
        {
            NetWriter writer = new NetWriter(Msg.Bolt).U8(who).Str(binding.Key).I32(binding.Fsm.FsmVariables.FindFsmInt("Stage").Value).I32(revision).Bool(binding.Total != null);
            if (binding.Total != null) writer.F32(binding.Total.Tightness.Value).Bool(binding.Total.Bolted != null).Bool(binding.Total.Bolted != null && binding.Total.Bolted.Value);
            return writer;
        }
        static BoltTotal TotalOf(PlayMakerFSM f)
        {
            FsmState wait = f.Fsm.GetState("Wait 3"); if (wait == null) return null;
            foreach (FsmStateAction action in wait.Actions)
            {
                var add = action as HutongGames.PlayMaker.Actions.AddFsmFloat;
                if (add == null || add.variableName.Value != "Tightness") continue;
                GameObject go = add.gameObject.OwnerOption == OwnerDefaultOption.UseOwner ? f.gameObject : add.gameObject.GameObject.Value;
                PlayMakerFSM check = go != null ? Game.FsmOn(go, add.fsmName.Value) : null;
                FsmFloat tightness = check != null ? check.FsmVariables.FindFsmFloat("Tightness") : null;
                if (tightness == null) return null;
                FsmGameObject db = check.FsmVariables.FindFsmGameObject("db_ThisPart");
                PlayMakerFSM data = db != null && db.Value != null ? Game.FsmOn(db.Value, "Data") : null;
                return new BoltTotal { Fsm = check, Tightness = tightness, Bolted = data != null ? data.FsmVariables.FindFsmBool("Bolted") : null };
            }
            return null;
        }
        static void ReceiveBolt(Binding binding, PendingBolt edit)
        {
            if (Session.IsHost ? !binding.Clock.AcceptRequest(edit.Who, edit.Revision) : !binding.Clock.AcceptSnapshot(edit.Revision)) return;
            FsmInt stage = binding.Fsm.FsmVariables.FindFsmInt("Stage");
            int target = edit.Target;
            FsmState pose = binding.Fsm.Fsm.GetState(target + " 2") ?? binding.Fsm.Fsm.GetState(target.ToString());
            if (pose == null) throw new ProtocolException("Unknown MSC bolt pose");
            PatchActions(binding.Fsm); remote[binding.Fsm.Fsm] = new Scope { Until = Time.realtimeSinceStartup + 2 };
            Replay.Depth++;
            try
            {
                // A disabled or distant part does not run PlayMaker transitions.
                // Commit the game's absolute pose and storage actions directly.
                // Tightness has native clamps and exceptions (alternator pivot),
                // so it follows the actor's actual total, not a sum of deltas.
                stage.Value = target;
                HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
                for (int hop = 0; pose != null && hop < 6 && visited.Add(pose.Name); hop++)
                {
                    foreach (FsmStateAction action in pose.Actions)
                        if (action != null && !(action is ModHook) && !action.GetType().Name.StartsWith("GetMouse", StringComparison.Ordinal)
                            && action.GetType().Name != "GetAxis" && action.GetType().Name != "MousePickEvent") action.OnEnter();
                    string next = null;
                    foreach (FsmTransition t in pose.Transitions) if (t.EventName == "FINISHED") { next = t.ToState; break; }
                    if (next == "Idle" || next == null) break;
                    pose = binding.Fsm.Fsm.GetState(next);
                }
                if (binding.Total != null && edit.HasTotal)
                {
                    MscEditClock clock;
                    if (!totalClocks.TryGetValue(binding.Total.Fsm, out clock)) totalClocks[binding.Total.Fsm] = clock = new MscEditClock();
                    if (Session.IsHost ? clock.AcceptRequest(edit.Who, edit.Revision) : clock.AcceptSnapshot(edit.Revision))
                    {
                        binding.Total.Tightness.Value = edit.Tightness;
                        if (binding.Total.Bolted != null && edit.HasBolted)
                        {
                            binding.Total.Bolted.Value = edit.Bolted;
                            FsmState check = binding.Total.Fsm.Fsm.GetState(edit.Bolted ? "Bolts ON" : "Bolts OFF");
                            if (check != null) foreach (FsmStateAction action in check.Actions)
                            {
                                string name = action.GetType().Name;
                                if (name == "SetProperty" || name == "ActivateGameObject" || name == "SetFsmBool") action.OnEnter();
                            }
                        }
                    }
                }
            }
            finally { Replay.Depth--; }
            binding.BoltChanged = true;
            Log.Info("MSC bolt applied " + binding.Key + " = " + stage.Value);
            if (Session.IsHost) { int revision = ++hostBoltRevision; binding.Clock.AcceptSnapshot(revision); Session.Broadcast(BoltPacket(binding, edit.Who, revision), true); }
        }
        static void Execute(PlayMakerFSM f, string state)
        {
            PatchActions(f); remote[f.Fsm] = new Scope { Until = Time.realtimeSinceStartup + 2 };
            Replay.Depth++;
            try { if (!Game.SetState(f, state)) throw new InvalidOperationException("MSC native state missing: " + state); }
            finally { Replay.Depth--; }
        }
        static void PatchActions(PlayMakerFSM f)
        {
            if (harmony == null) harmony = HarmonyInstance.Create("SummerCoop.MSC.NativeParts");
            foreach (FsmState state in f.Fsm.States) foreach (FsmStateAction action in state.Actions)
            {
                if (action == null || action is ModHook) continue;
                foreach (string name in new[] { "OnEnter", "OnUpdate" })
                {
                    MethodInfo method = action.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public);
                    if (method == null || method.DeclaringType == typeof(FsmStateAction) || !patched.Add(method)) continue;
                    harmony.Patch(method, new HarmonyMethod(typeof(MscParts).GetMethod("BeforeAction", BindingFlags.Static | BindingFlags.NonPublic)));
                }
            }
        }
        static bool Personal(object value)
        {
            FsmOwnerDefault owner = value as FsmOwnerDefault; if (owner != null) return owner.OwnerOption == OwnerDefaultOption.SpecifyGameObject && Personal(owner.GameObject);
            FsmGameObject variable = value as FsmGameObject; if (variable != null) return Personal(variable.Value) || variable.Name == "Hand" || variable.Name == "Player" || variable.Name == "FPSCamera";
            GameObject go = value as GameObject; if (go != null) { for (Transform t = go.transform; t != null; t = t.parent) if (IdOf(t.gameObject).Length > 0) return false; return WorldRegistry.PersonalObject(go); }
            return false;
        }
        static bool BeforeAction(FsmStateAction __instance)
        {
            if (__instance == null || !remote.ContainsKey(__instance.Fsm)) return true;
            NativeActionInfo info = NativeActionReader.Describe(__instance, Personal);
            if (NativeActionPolicy.Allowed(info, NativeActionScope.SharedActor)) return true;
            __instance.Finish(); return false;
        }

        public static string StarterState()
        {
            foreach (GameObject part in parts.Values) if (part != null && part.name == "starter(Clone)")
            {
                int stage = -1;
                foreach (PlayMakerFSM f in part.GetComponentsInChildren<PlayMakerFSM>(true)) if (IsBolt(f)) { stage = f.FsmVariables.FindFsmInt("Stage").Value; break; }
                return "Installed=" + NativeAssembly.Installed(part) + ";Parent=" + (part.transform.parent != null ? part.transform.parent.name : "") + ";Bolt=" + stage;
            }
            return "Missing";
        }
        public static void ExerciseStarter(bool bolt)
        {
            if (!Session.IsHost || MscRuntime.Argument("--summercoop-test-report").Length == 0) throw new InvalidOperationException("Native exercise is test-only");
            GameObject part = null;
            foreach (GameObject candidate in parts.Values) if (candidate != null && candidate.name == "starter(Clone)") { part = candidate; break; }
            if (part == null) throw new InvalidOperationException("MSC starter not found");
            foreach (Binding binding in bindings.Values)
            {
                if (binding.Fsm == null) continue;
                if (bolt)
                {
                    if (!IsBolt(binding.Fsm) || !binding.Fsm.transform.IsChildOf(part.transform)) continue;
                    exercisedBolt = binding; Execute(binding.Fsm, "Screw 2"); return;
                }
                if (binding.Fsm.FsmName != "Assembly" || !NativeAssembly.Accepts(binding.Fsm.FsmVariables.StringVariables, part.name)) continue;
                FsmGameObject variable = binding.Fsm.FsmVariables.FindFsmGameObject("Part"); variable.Value = part;
                part.transform.position = binding.Fsm.transform.position;
                binding.MountState = "Assemble"; Execute(binding.Fsm, "Assemble");
                repairs[binding] = Time.realtimeSinceStartup + 3;
                Session.Broadcast(new NetWriter(Msg.Part).U8(Session.LocalId).Str(binding.Key).Str("Assemble").Str(IdOf(part)), true); return;
            }
            throw new InvalidOperationException("MSC starter native binding not found");
        }
        public static string ExerciseDriverSeat()
        {
            if (!Session.IsHost || MscRuntime.Argument("--summercoop-test-vehicle-channel").Length == 0) throw new InvalidOperationException("Native seat exercise is test-only");
            GameObject part = null;
            foreach (GameObject candidate in parts.Values)
                if (candidate != null && candidate.name == "seat driver(Clone)") { part = candidate; break; }
            if (part == null) return "driver seat missing";
            if (NativeAssembly.Installed(part)) return "driver seat already mounted";
            foreach (Binding binding in bindings.Values)
            {
                if (binding.Fsm == null || binding.Fsm.FsmName != "Assembly" || !NativeAssembly.Accepts(binding.Fsm.FsmVariables.StringVariables, part.name)) continue;
                foreach (FsmState native in binding.Fsm.FsmStates)
                {
                    if (!ActionState(binding.Fsm, native) || NativeAssembly.MountAction(native.Actions) == null) continue;
                    binding.Fsm.FsmVariables.FindFsmGameObject("Part").Value = part;
                    part.transform.position = binding.Fsm.transform.position;
                    binding.MountState = native.Name; Execute(binding.Fsm, native.Name);
                    if (!NativeAssembly.Installed(part)) continue;
                    repairs[binding] = Time.realtimeSinceStartup + 3;
                    Session.Broadcast(new NetWriter(Msg.Part).U8(Session.LocalId).Str(binding.Key).Str(native.Name).Str(IdOf(part)), true);
                    return "driver seat via native " + native.Name + ", installed=True";
                }
            }
            return "driver seat assembly not found";
        }
        internal static string TestInstall(string partName)
        {
            if (!Session.IsHost || MscRuntime.Argument("--summercoop-test-workshop-channel").Length == 0) throw new InvalidOperationException("Workshop fixture is test-only");
            GameObject part = null;
            foreach (GameObject candidate in parts.Values) if (candidate != null && candidate.name == partName) { part = candidate; break; }
            if (part == null) return partName + ": missing";
            if (NativeAssembly.Installed(part)) return partName + ": already installed";
            foreach (Binding binding in bindings.Values)
            {
                if (binding.Fsm == null || binding.Fsm.FsmName != "Assembly" || !NativeAssembly.Accepts(binding.Fsm.FsmVariables.StringVariables, part.name)) continue;
                List<FsmState> candidates = new List<FsmState>(binding.Fsm.FsmStates);
                candidates.Sort((a, b) => a.Name == "Assemble" ? -1 : b.Name == "Assemble" ? 1 : string.CompareOrdinal(a.Name, b.Name));
                foreach (FsmState state in candidates)
                {
                    if (!state.IsInitialized || !ActionState(binding.Fsm, state) || NativeAssembly.MountAction(state.Actions) == null) continue;
                    binding.Fsm.FsmVariables.FindFsmGameObject("Part").Value = part;
                    part.transform.position = binding.Fsm.transform.position;
                    binding.MountState = state.Name; Execute(binding.Fsm, state.Name);
                    if (!NativeAssembly.Installed(part)) continue;
                    repairs[binding] = Time.realtimeSinceStartup + 3;
                    Session.Broadcast(new NetWriter(Msg.Part).U8(Session.LocalId).Str(binding.Key).Str(state.Name).Str(IdOf(part)), true);
                    return partName + ": installed through " + state.Name;
                }
            }
            return partName + ": no native mount";
        }
        public static void PublishStarterBolt()
        {
            Binding binding = exercisedBolt;
            if (binding == null || binding.Fsm == null) throw new InvalidOperationException("Exercised MSC bolt was not retained");
            int stage = binding.Fsm.FsmVariables.FindFsmInt("Stage").Value;
            binding.BoltChanged = true;
            int revision = ++hostBoltRevision; binding.Clock.AcceptSnapshot(revision);
            Session.Broadcast(BoltPacket(binding, Session.LocalId, revision), true);
            Log.Info("MSC bolt published " + binding.Key + " = " + stage);
        }
    }
}
