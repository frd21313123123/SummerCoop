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
        sealed class Binding { public string Key, MountState; public PlayMakerFSM Fsm; public BoltTotal Total; public bool BoltChanged; public int BoltActor, BoltActorRevision; public readonly MscPendingBoltEdit PendingBolt = new MscPendingBoltEdit(); public Transform KeyParent, KeyRoot; public int KeyGeneration = -1; public FsmBinding Native; public string[] MountNames; public FsmGameObject[] RepairVariables; public FsmGameObject Held; public readonly MscEditClock Clock = new MscEditClock(); }
        sealed class Database { public string Id; public PlayMakerFSM Fsm; public FsmGameObject Part, Loose, Model; public FsmBool Installed; public FsmGameObject[] Models, Variables; public FsmBool[] Bools; public bool MotorState; }
        sealed class Model { public PlayMakerFSM Database, Removal; public FsmGameObject Reference; public FsmGameObject[] Variables; public GameObject Part; }
        sealed class BoltTotal { public PlayMakerFSM Fsm; public FsmFloat Tightness; public FsmBool Bolted; }
        sealed class Pending { public Binding Binding; public string State, Id; public GameObject Part; public int Frame; public float ReadyAt, Until; }
        sealed class PartEdit { public int Who, Revision, ActorRevision; public string Key, State, Id; public float Until, Angle; public bool HasAngle; }
        sealed class PartState { public PartEdit Edit; public readonly MscEditClock Clock = new MscEditClock(); public readonly MscPendingPartEdit Pending = new MscPendingPartEdit(); }
        sealed class PendingBolt { public int Who, Target, Revision, ActorRevision; public float Until, Tightness; public bool HasTotal, HasBolted, Bolted; }
        sealed class Scope { public float Until; }
        static readonly Dictionary<string, GameObject> parts = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        static readonly Dictionary<GameObject, string> ids = new Dictionary<GameObject, string>();
        static readonly Dictionary<GameObject, string> modelKeys = new Dictionary<GameObject, string>();
        static readonly Dictionary<string, PlayMakerFSM> databases = new Dictionary<string, PlayMakerFSM>(StringComparer.Ordinal);
        static readonly Dictionary<PlayMakerFSM, Database> databaseCache = new Dictionary<PlayMakerFSM, Database>();
        static readonly Dictionary<GameObject, Model> modelCache = new Dictionary<GameObject, Model>();
        static readonly Queue<GameObject> changedRoots = new Queue<GameObject>();
        static readonly HashSet<GameObject> changedSet = new HashSet<GameObject>();
        static readonly Stack<Transform> changedStack = new Stack<Transform>();
        static Object[] scanFsms;
        static int scanCursor, scanPhase, keyGeneration;
        static float scanBegan;
        public static int DiscoveryPending { get { return (scanFsms != null ? scanFsms.Length * (2 - scanPhase) - scanCursor : 0) + changedRoots.Count + changedStack.Count; } }
        public static float DiscoveryAge { get { return scanFsms != null ? Time.realtimeSinceStartup - scanBegan : 0; } }
        static readonly Dictionary<string, PartState> partStates = new Dictionary<string, PartState>(StringComparer.Ordinal);
        static readonly Dictionary<string, PartEdit> pendingParts = new Dictionary<string, PartEdit>(StringComparer.Ordinal);
        static readonly List<string> completedParts = new List<string>();
        static readonly Dictionary<string, Binding> bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
        static readonly Dictionary<PlayMakerFSM, Binding> byFsm = new Dictionary<PlayMakerFSM, Binding>();
        static readonly HashSet<PlayMakerFSM> hooked = new HashSet<PlayMakerFSM>();
        static readonly List<Pending> pending = new List<Pending>();
        static readonly Dictionary<string, PendingBolt> pendingBolts = new Dictionary<string, PendingBolt>(StringComparer.Ordinal);
        static readonly List<string> completedBolts = new List<string>();
        static readonly Dictionary<PlayMakerFSM, MscEditClock> totalClocks = new Dictionary<PlayMakerFSM, MscEditClock>();
        static readonly Dictionary<PlayMakerFSM, MscPendingBoltEdit> totalPending = new Dictionary<PlayMakerFSM, MscPendingBoltEdit>();
        static readonly Dictionary<Binding, float> repairs = new Dictionary<Binding, float>();
        static readonly List<Binding> repaired = new List<Binding>();
        static readonly Dictionary<Fsm, Scope> remote = new Dictionary<Fsm, Scope>();
        static readonly HashSet<MethodBase> patched = new HashSet<MethodBase>();
        static readonly List<Fsm> finished = new List<Fsm>();
        static HarmonyInstance harmony;
        static float nextScan, nextBoltSnapshot, nextPartSnapshot;
        static int localBoltRevision, hostBoltRevision;
        static Binding exercisedBolt;
        static bool leftHooked;
        public static int Count { get { return bindings.Count; } }
        public static string Workload { get { return "bindings=" + bindings.Count + ";repairs=" + repairs.Count + ";pendingParts=" + pendingParts.Count + ";pendingBolts=" + pendingBolts.Count + ";actions=" + pending.Count + ";scopes=" + remote.Count + ";partStates=" + partStates.Count; } }
        static void ProfileStep(ref long began, string section)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            Perf.Add(section, (now - began) * 1000.0 / System.Diagnostics.Stopwatch.Frequency); began = now;
        }
        // Explicit end-of-test inspection, outside the measured frame window.
        internal static string[] IntegrityLines()
        {
            var rows = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in databases) if (entry.Value != null)
                rows["part\t" + entry.Key] = Installed(entry.Key) ? "1" : "0";
            foreach (var entry in bindings) if (entry.Value.Fsm != null && IsBolt(entry.Value.Fsm))
                rows["bolt\t" + entry.Key] = entry.Value.Fsm.FsmVariables.FindFsmInt("Stage").Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var lines = new List<string>();
            foreach (var row in rows) lines.Add(row.Key + "\t" + row.Value);
            return lines.ToArray();
        }
        public static void Reset() { MscBoltPositions.Reset(); parts.Clear(); ids.Clear(); modelKeys.Clear(); databases.Clear(); databaseCache.Clear(); modelCache.Clear(); MscRepairCache.Clear(); changedRoots.Clear(); changedSet.Clear(); changedStack.Clear(); scanFsms = null; scanCursor = scanPhase = 0; keyGeneration++; partStates.Clear(); pendingParts.Clear(); completedParts.Clear(); bindings.Clear(); byFsm.Clear(); hooked.Clear(); pending.Clear(); pendingBolts.Clear(); completedBolts.Clear(); totalClocks.Clear(); totalPending.Clear(); repairs.Clear(); remote.Clear(); nextScan = nextBoltSnapshot = nextPartSnapshot = 0; localBoltRevision = hostBoltRevision = 0; }
        public static bool Owns(PlayMakerFSM f)
        {
            return f != null && !MscCd.Owns(f) && !MscBrewing.Owns(f) && (IsBoltCheck(f) || f.FsmName == "Assembly" || f.FsmName == "Removal" || f.FsmName == "Assemble" && Recon.Path(f.transform).IndexOf("Wiring", StringComparison.OrdinalIgnoreCase) >= 0 || IsBolt(f));
        }
        // These FSMs consume the totals sent with Msg.Bolt. Generic job/world
        // replay must not also drive their Bolts ON/OFF transitions: a delayed
        // transition can undo a guest's terminal turn or reinstall its wiring.
        internal static bool IsBoltCheck(PlayMakerFSM f)
        {
            return f != null && f.FsmName == "BoltCheck" && f.FsmVariables.FindFsmFloat("Tightness") != null
                && f.Fsm.GetState("Bolts ON") != null && f.Fsm.GetState("Bolts OFF") != null && !MscTuning.IsTiming(f);
        }
        public static bool IsBolt(PlayMakerFSM f) { return f != null && f.FsmName == "Screw" && f.FsmVariables.FindFsmInt("Stage") != null; }
        public static string IdOf(GameObject go)
        {
            if (go == null) return "";
            string id; if (ids.TryGetValue(go, out id)) return id;
            // Removal can create a new loose chassis clone and the player can
            // reinstall it before the next census. SpawnThis is already updated
            // by the native removal, even when that clone has no Removal FSM.
            foreach (Database entry in databaseCache.Values)
            {
                if (entry.Fsm == null) continue;
                FsmGameObject loose = ReferenceEquals(entry.Variables, entry.Fsm.FsmVariables.GameObjectVariables)
                    ? entry.Loose : entry.Fsm.FsmVariables.FindFsmGameObject("SpawnThis");
                if (loose == null || loose.Value != go) continue;
                ids[go] = entry.Id; parts[entry.Id] = go; return entry.Id;
            }
            foreach (PlayMakerFSM f in go.GetComponents<PlayMakerFSM>())
            {
                if (f.FsmName != "Removal") continue;
                FsmGameObject database = f.FsmVariables.FindFsmGameObject("db_ThisPart");
                if (database == null || database.Value == null) continue;
                PlayMakerFSM data = Game.FsmOn(database.Value, "Data");
                FsmGameObject actual = data == null ? null : data.FsmVariables.FindFsmGameObject("ThisPart") ?? data.FsmVariables.FindFsmGameObject("ActivateThis");
                if (actual != null && actual.Value != go && (actual.Name == "ThisPart" || actual.Value == null || !go.transform.IsChildOf(actual.Value.transform))) continue;
                id = "mscdb:" + Parts.RankPath(database.Value.transform);
                ids[go] = id; parts[id] = go; return id;
            }
            id = MscBelt.Identity(go);
            if (id.Length == 0 && !MscBelt.IsBelt(go))
            {
                PlayMakerFSM use = Game.FsmOn(go, "Use");
                FsmString tag = use == null || use.FsmVariables.FindFsmBool("Installed") == null && use.FsmVariables.FindFsmString("Corner") == null ? null : use.FsmVariables.FindFsmString("UniqueTag") ?? use.FsmVariables.FindFsmString("ID");
                // oilfilter0 is the real starter filter, persisted in items.txt.
                // Prefabs have no assigned ID; excluding zero here discarded the
                // starter filter's assembly and changed its bolt key on mounting.
                if (tag != null && tag.Value.Length > 0 && tag.Value != "sparkplug0" && tag.Value != "bulb0" && tag.Value != "wheel0") id = "mscitem:" + tag.Value;
            }
            if (id.Length > 0) { ids[go] = id; parts[id] = go; return id; }
            return "";
        }
        public static GameObject Find(string id)
        {
            PlayMakerFSM data;
            if (databases.TryGetValue(id, out data) && data != null)
            {
                Database cached;
                bool fresh = databaseCache.TryGetValue(data, out cached) && ReferenceEquals(cached.Variables, data.FsmVariables.GameObjectVariables) && ReferenceEquals(cached.Bools, data.FsmVariables.BoolVariables);
                bool installed = fresh ? cached.Installed.Value : Installed(id);
                FsmGameObject value = fresh ? cached.Part ?? (installed ? cached.Model : cached.Loose)
                    : data.FsmVariables.FindFsmGameObject("ThisPart") ?? data.FsmVariables.FindFsmGameObject(installed ? "ActivateThis" : "SpawnThis");
                if (value == null && installed) foreach (FsmGameObject model in fresh ? cached.Models : data.FsmVariables.GameObjectVariables)
                    if (model.Name.StartsWith("ActivateThis", StringComparison.Ordinal) && model.Value != null && (value == null || model.Value.activeSelf)) value = model;
                if (value != null && value.Value != null)
                {
                    GameObject representation = value.Name.StartsWith("ActivateThis", StringComparison.Ordinal) ? ModelPart(value.Value, data) : value.Value;
                    ids[representation] = id; parts[id] = representation; return representation;
                }
                return null;
            }
            GameObject go; return parts.TryGetValue(id, out go) && go != null ? go : null;
        }
        static bool Installed(string id)
        {
            PlayMakerFSM data;
            if (databases.TryGetValue(id, out data) && data != null)
            {
                Database cached;
                if (databaseCache.TryGetValue(data, out cached) && ReferenceEquals(cached.Bools, data.FsmVariables.BoolVariables)) return cached.Installed.Value;
                return data.FsmVariables.FindFsmBool("Installed").Value;
            }
            GameObject part; if (!parts.TryGetValue(id, out part) || part == null) return false;
            PlayMakerFSM use = Game.FsmOn(part, "Use");
            FsmBool installed = use == null ? null : use.FsmVariables.FindFsmBool("Installed");
            return installed != null ? installed.Value : NativeAssembly.Installed(part);
        }
        static GameObject ModelPart(GameObject root, PlayMakerFSM data)
        {
            Model cached;
            if (modelCache.TryGetValue(root, out cached) && cached.Database == data && cached.Part != null && cached.Removal != null
                && ReferenceEquals(cached.Variables, cached.Removal.FsmVariables.GameObjectVariables)
                && cached.Reference.Value == data.gameObject && cached.Part.transform.IsChildOf(root.transform)) return cached.Part;
            // Wishbone ActivateThis points at an IK pivot; the native Removal
            // FSM and mesh are on its child. Both belong to the same DB part.
            foreach (PlayMakerFSM f in root.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                FsmGameObject db = f.FsmName == "Removal" ? f.FsmVariables.FindFsmGameObject("db_ThisPart") : null;
                if (db != null && db.Value == data.gameObject) { modelCache[root] = new Model { Database = data, Removal = f, Reference = db, Variables = f.FsmVariables.GameObjectVariables, Part = f.gameObject }; return f.gameObject; }
            }
            return root;
        }
        public static void Index()
        {
            foreach (Object value in Game.AllFsms()) IndexFsm(value as PlayMakerFSM);
        }
        static void IndexFsm(PlayMakerFSM f)
        {
            if (f == null || f.hideFlags != HideFlags.None) return;
            if (f.FsmName == "Use") { IdOf(f.gameObject); MscBattery.Register(f); return; }
            if (f.FsmName != "Data") return;
            Database data;
            if (!databaseCache.TryGetValue(f, out data) || !ReferenceEquals(data.Variables, f.FsmVariables.GameObjectVariables) || !ReferenceEquals(data.Bools, f.FsmVariables.BoolVariables))
            {
                FsmGameObject part = f.FsmVariables.FindFsmGameObject("ThisPart"), loose = f.FsmVariables.FindFsmGameObject("SpawnThis");
                FsmBool installed = f.FsmVariables.FindFsmBool("Installed");
                if (installed == null) return;
                var models = new List<FsmGameObject>();
                foreach (FsmGameObject model in f.FsmVariables.GameObjectVariables) if (model.Name.StartsWith("ActivateThis", StringComparison.Ordinal)) models.Add(model);
                if (part == null && (models.Count == 0 || loose == null)) return;
                data = new Database { Id = "mscdb:" + Parts.RankPath(f.transform), Fsm = f, Part = part, Loose = loose, Model = f.FsmVariables.FindFsmGameObject("ActivateThis"), Installed = installed, Models = models.ToArray(), Variables = f.FsmVariables.GameObjectVariables, Bools = f.FsmVariables.BoolVariables };
                data.MotorState = data.Id.StartsWith("mscdb:Database/DatabaseMotor/", StringComparison.Ordinal);
                databaseCache[f] = data; databases[data.Id] = f;
            }
            if (data.Part != null && data.Part.Value != null) ids[data.Part.Value] = data.Id;
            if (data.Loose != null && data.Loose.Value != null) ids[data.Loose.Value] = data.Id;
            foreach (FsmGameObject model in data.Models) if (model.Value != null)
            {
                GameObject representation = ModelPart(model.Value, f);
                ids[model.Value] = ids[representation] = data.Id;
                if (data.Models.Length > 1) modelKeys[model.Value] = modelKeys[representation] = "@" + model.Name + "/";
            }
            Find(data.Id);
        }
        internal static string Key(PlayMakerFSM f)
        {
            Transform t = f.transform;
            string relative = "";
            for (Transform p = t; p != null; p = p.parent)
            {
                string id = IdOf(p.gameObject);
                if (id.Length > 0) { string model; modelKeys.TryGetValue(p.gameObject, out model); return id + "/" + model + relative + "::" + f.FsmName; }
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
                    if (Binding.Fsm.FsmName == "Removal")
                    {
                        VehicleSync.PartRemoving(Binding.Fsm.gameObject);
                        CarDoors.PartRemoving(Binding.Fsm.gameObject);
                    }
                    if (Session.Active && PlayerSync.InGame && Time.timeSinceLevelLoad > 3 && Replay.Depth == 0 && !remote.ContainsKey(Fsm))
                    {
                        RefreshKey(Binding);
                        FsmGameObject variable = Binding.Fsm.FsmVariables.FindFsmGameObject("Part") ?? Binding.Fsm.FsmVariables.FindFsmGameObject("ThisPart");
                        GameObject part = variable != null ? variable.Value : null;
                        if (Binding.Fsm.FsmName == "Removal") part = Binding.Fsm.gameObject;
                        if (Binding.Fsm.FsmName == "Assembly" && (Binding.MountState == null || NativeAssembly.MountAction(State.Actions) != null)) Binding.MountState = StateName;
                        bool bolt = IsBolt(Binding.Fsm);
                        if (bolt) MscBoltPositions.Prepare(Binding.Fsm);
                        if (bolt && !Session.IsHost)
                        {
                            Binding.PendingBolt.Begin();
                            if (Binding.Total == null) Binding.Total = TotalOf(Binding.Fsm);
                            if (Binding.Total != null) PendingTotal(Binding.Total).Begin();
                        }
                        // Chassis assembly destroys the loose object in this state.
                        // Capture its database identity before the native actions run.
                        PlayMakerFSM target = Binding.Fsm.FsmName == "Assembly" ? NativeAssembly.Database(Binding.Fsm, Binding.Fsm.Fsm.GetState(StateName)) : null;
                        string id = target == null ? IdOf(part) : "mscdb:" + Parts.RankPath(target.transform);
                        Pending continuing = bolt ? null : pending.Find(p => p.Binding == Binding && p.Id == id);
                        if (continuing != null)
                        {
                            // One native assembly can cross several Assemble states,
                            // with waits before End writes Installed/Corner. Publish
                            // its completed result once, from its actual mount state.
                            if (NativeAssembly.MountAction(State.Actions) != null) continuing.State = StateName;
                            continuing.Frame = Time.frameCount + 1;
                        }
                        else
                        {
                            if (!Session.IsHost && !bolt && (id.Length > 0 || Binding.Fsm.FsmName == "Assemble")) StateFor(new PartEdit { Key = Binding.Key, Id = id }).Pending.Begin();
                            pending.Add(new Pending { Binding = Binding, State = StateName, Part = part, Id = id, Frame = Time.frameCount + (bolt ? 2 : 1), ReadyAt = bolt ? Time.realtimeSinceStartup + .1f : 0, Until = Time.realtimeSinceStartup + 5 });
                        }
                    }
                }
                catch (Exception e) { Replay.HookError(e); }
                Finish();
            }
        }
        sealed class JointPose : ModHook
        {
            public PlayMakerFSM Assembly; public string Mount;
            public override string Module { get { return "msc-parts"; } }
            public override void OnEnter()
            {
                try { NativeAssembly.PlaceForJoint(Assembly, Mount); }
                catch (Exception e) { Replay.HookError(e); }
                Finish();
            }
        }
        sealed class JointReady : ModHook
        {
            public PlayMakerFSM Assembly; public string Mount;
            public override string Module { get { return "msc-parts"; } }
            public override void OnEnter()
            {
                try { NativeAssembly.ConnectJoint(Assembly, Mount, State); }
                catch (Exception e) { Replay.HookError(e); }
                Finish();
            }
        }
        static bool ActionState(PlayMakerFSM f, FsmState state)
        {
            string name = state.Name;
            if (IsBolt(f)) return name == "Screw 2" || name == "Unscrew 2" || name == "Screw" || name == "Unscrew";
            if (f.FsmName == "Assembly") return name == "Assemble" || name.StartsWith("Assemble ", StringComparison.Ordinal) || NativeAssembly.MountAction(state.Actions) != null;
            if (f.FsmName == "Removal") return name.StartsWith("Remove", StringComparison.Ordinal) || name.StartsWith("Disassemble", StringComparison.Ordinal);
            if (f.FsmName == "Assemble" && Recon.Path(f.transform).IndexOf("Wiring", StringComparison.OrdinalIgnoreCase) >= 0) return name == "Finish assembly";
            return name == "Assemble" || name == "Connect";
        }
        static void Scan()
        {
            if (scanFsms != null) return;
            scanFsms = Game.AllFsms(); scanCursor = scanPhase = 0; scanBegan = Time.realtimeSinceStartup;
            if (scanFsms.Length == 0) { scanFsms = null; nextScan = Time.realtimeSinceStartup + 3; }
        }
        public static void Changed(GameObject go)
        {
            VehicleSync.PartsChanged();
            keyGeneration++; Props.InvalidateHierarchy();
            CarDoors.SoonScan();
            if (go != null && changedSet.Add(go)) changedRoots.Enqueue(go);
        }
        public static void Resynchronize() { nextScan = nextPartSnapshot = nextBoltSnapshot = 0; }
        static void ScanStep()
        {
            while (MscMaintenance.Available)
            {
                long start = MscMaintenance.Begin();
                try
                {
                    if (changedStack.Count > 0)
                    {
                        Transform t = changedStack.Pop();
                        if (t != null)
                        {
                            foreach (PlayMakerFSM f in t.GetComponents<PlayMakerFSM>()) { IndexFsm(f); ScanFsm(f); }
                            for (int c = t.childCount - 1; c >= 0; c--) changedStack.Push(t.GetChild(c));
                        }
                    }
                    else if (changedRoots.Count > 0)
                    {
                        GameObject go = changedRoots.Dequeue(); changedSet.Remove(go);
                        if (go != null) changedStack.Push(go.transform);
                    }
                    else if (scanFsms != null)
                    {
                        PlayMakerFSM f = scanFsms[scanCursor++] as PlayMakerFSM;
                        if (scanPhase == 0) IndexFsm(f); else ScanFsm(f);
                        if (scanCursor == scanFsms.Length)
                        {
                            scanCursor = 0;
                            if (++scanPhase == 2) { scanFsms = null; nextScan = Time.realtimeSinceStartup + 3; }
                        }
                    }
                    else return;
                }
                finally { MscMaintenance.End(start); }
            }
        }
        static void ScanFsm(PlayMakerFSM f)
        {
            if (MscCd.Owns(f)) return;
                if (f == null || f.hideFlags != HideFlags.None) return;
                MscVehicleControls.Prepare(f);
                if (IsBoltCheck(f)) return; // Native observer; only its Screw FSM publishes edits.
                Binding known;
                if (byFsm.TryGetValue(f, out known)) { if (!Game.UnderPlayer(f.transform)) RefreshKey(known); return; }
                if (!Owns(f) || Game.UnderPlayer(f.transform)) return;
                if (!f.Fsm.Initialized)
                {
                    string path = Recon.Path(f.transform);
                    if (!path.StartsWith("SATSUMA", StringComparison.Ordinal) && !path.StartsWith("CARPARTS/", StringComparison.Ordinal)) return;
                    // Load actions without activating a distant/hidden object or
                    // entering its input states. Its first activation keeps hooks.
                    f.Fsm.InitData();
                }
                // Use.State 1 assigns a consumable's ID after Instantiate.
                // Never freeze its temporary scene/hand path into the binding.
                if (f.FsmName == "Removal" && IdOf(f.gameObject).Length == 0) return;
                if (!Replay.Claim(f, "msc-parts")) return;
                Binding binding = new Binding { Key = Key(f), Fsm = f };
                Binding previous;
                if (bindings.TryGetValue(binding.Key, out previous) && previous.Fsm != null && previous.Fsm != f) { Log.Warn("MSC ambiguous assembly: " + binding.Key); return; }
                bool installed = false;
                foreach (FsmState state in f.Fsm.States)
                {
                    string jointMount = f.FsmName == "Assembly" ? NativeAssembly.JointMountState(f, state) : null;
                    if (jointMount != null)
                    {
                        var setup = new List<FsmStateAction>(state.Actions);
                        setup.Insert(0, new JointPose { Assembly = f, Mount = jointMount });
                        setup.Add(new JointReady { Assembly = f, Mount = jointMount }); state.Actions = setup.ToArray();
                    }
                    if (!ActionState(f, state)) continue;
                    List<FsmStateAction> actions = new List<FsmStateAction>(state.Actions); actions.Insert(0, new Hook { Binding = binding, StateName = state.Name }); state.Actions = actions.ToArray(); installed = true;
                }
                if (installed) { bindings[binding.Key] = binding; byFsm[f] = binding; hooked.Add(f); PatchActions(f); MscBattery.Prepare(f); if (IsBolt(f)) { MscBoltPositions.Prepare(f); binding.Total = TotalOf(f); } RepairMount(binding); }
        }
        static void RefreshKey(Binding binding)
        {
            Transform t = binding.Fsm.transform;
            Transform parent = t.parent, root = t.root;
            bool stable = binding.Key.StartsWith("mscdb:", StringComparison.Ordinal) || binding.Key.StartsWith("mscitem:", StringComparison.Ordinal);
            if (stable && binding.KeyGeneration == keyGeneration && binding.KeyParent == parent && binding.KeyRoot == root) return;
            binding.KeyParent = parent; binding.KeyRoot = root; binding.KeyGeneration = keyGeneration;
            string current = Key(binding.Fsm);
            if (current == binding.Key || !(current.StartsWith("mscdb:", StringComparison.Ordinal) || current.StartsWith("mscitem:", StringComparison.Ordinal))) return;
            Binding existing;
            if (bindings.TryGetValue(current, out existing) && existing != binding) throw new InvalidOperationException("Duplicate stable MSC binding: " + current);
            bindings.Remove(binding.Key); binding.Key = current; bindings[current] = binding;
        }
        static void RepairMount(Binding binding)
        {
            if (binding.Fsm.FsmName != "Assembly") return;
            if (binding.Native == null) { binding.Native = new FsmBinding { Component = binding.Fsm }; binding.MountNames = new string[8]; }
            FsmGameObject[] variables = binding.Fsm.FsmVariables.GameObjectVariables;
            if (!ReferenceEquals(binding.RepairVariables, variables))
            { binding.RepairVariables = variables; binding.Held = binding.Fsm.FsmVariables.FindFsmGameObject("Part"); }
            FsmGameObject held = binding.Held;
            if (held != null && MscBelt.IsBelt(held.Value) && binding.MountState != null)
            {
                GameObject parent = NativeAssembly.MountParent(binding.Native, binding.MountState);
                // A previous belt leaves MountState behind. Merely holding a
                // replacement must not bypass the native rotation requirements.
                if (!MscBelt.Installed(held.Value) && (parent == null || held.Value.transform.parent != parent.transform)) return;
                NativeAssembly.Complete(binding.Native, binding.MountState, held.Value, binding.MountNames);
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
            FsmState selected = binding.MountState == null ? null : binding.Fsm.Fsm.GetState(binding.MountState);
            if (selected != null) RepairState(binding, selected, held);
            else foreach (FsmState state in binding.Fsm.Fsm.States) RepairState(binding, state, held);
        }
        static void RepairState(Binding binding, FsmState state, FsmGameObject held)
        {
                PlayMakerFSM data = NativeAssembly.Database(binding.Fsm, state);
                Database cached = null;
                bool fresh = data != null && databaseCache.TryGetValue(data, out cached)
                    && ReferenceEquals(cached.Variables, data.FsmVariables.GameObjectVariables) && ReferenceEquals(cached.Bools, data.FsmVariables.BoolVariables);
                FsmGameObject nativePart = data == null ? null : fresh ? cached.Part : data.FsmVariables.FindFsmGameObject("ThisPart");
                if (data != null && nativePart == null)
                {
                    FsmBool installed = fresh ? cached.Installed : data.FsmVariables.FindFsmBool("Installed");
                    if (installed != null && installed.Value)
                    {
                        // Chassis slots activate a built-in model and discard the
                        // loose clone. Repair only visibility/vehicle behaviour;
                        // replaying DestroyObject with the model would destroy it.
                        Replay.Depth++;
                        try
                        {
                            foreach (FsmStateAction action in state.Actions)
                                if (action is HutongGames.PlayMaker.Actions.ActivateGameObject || action is HutongGames.PlayMaker.Actions.EnableBehaviour) MscRepairCache.Apply(action);
                        }
                        finally { Replay.Depth--; }
                    }
                    return;
                }
                if (NativeAssembly.MountAction(state.Actions) == null) return;
                FsmGameObject part = data == null ? held : nativePart;
                if (part == null || part.Value == null || part.Value.GetComponent<Rigidbody>() != null) return;
                string id;
                // Engine DB parts use their native Installed flag. Wheels and
                // consumables retain the native Corner/representation predicate.
                bool installedPart = fresh && cached.MotorState && cached.Part == part && ids.TryGetValue(part.Value, out id) && id == cached.Id
                    ? cached.Installed.Value : NativeAssembly.Installed(part.Value);
                if (!installedPart) return;
                NativeAssembly.Complete(binding.Native, state.Name, part.Value, binding.MountNames);
        }
        public static void Update()
        {
            if (!PlayerSync.InGame) return;
            bool profile = MscPerformanceProbe.PartsProfiling && MscPerformanceProbe.Recording;
            long profileAt = profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (!leftHooked)
            {
                leftHooked = true;
                Session.PlayerLeft += player => {
                    foreach (PartState state in partStates.Values) state.Clock.ForgetActor(player.Id);
                    foreach (Binding b in bindings.Values) b.Clock.ForgetActor(player.Id);
                    foreach (MscEditClock clock in totalClocks.Values) clock.ForgetActor(player.Id);
                };
            }
            if (scanFsms == null && Time.realtimeSinceStartup >= nextScan) Scan();
            ScanStep();
            if (profile) ProfileStep(ref profileAt, "[MSC parts discovery]");
            for (int i = 0; i < pending.Count; i++)
            {
                Pending item = pending[i]; if (Time.frameCount < item.Frame || Time.realtimeSinceStartup < item.ReadyAt) continue;
                // Publish after the native pose/storage chain, even on a slow
                // frame. Stage changes before AddFsmFloat updates the part total.
                if (item.Binding.Fsm != null && IsBolt(item.Binding.Fsm) && !BoltInputReady(item.Binding.Fsm.ActiveStateName)) continue;
                bool incomplete = item.Binding.Fsm != null && (item.Binding.Fsm.FsmName == "Assembly" && !Installed(item.Id)
                    || item.Binding.Fsm.FsmName == "Removal" && Installed(item.Id));
                if (incomplete && Time.realtimeSinceStartup < item.Until) continue;
                pending.RemoveAt(i--);
                if (item.Binding.Fsm == null) continue;
                if (!IsBolt(item.Binding.Fsm)) { Changed(item.Part); Changed(item.Binding.Fsm.gameObject); }
                if (IsBolt(item.Binding.Fsm))
                {
                    item.Binding.BoltChanged = true;
                    PublishBolt(item.Binding);
                }
                else
                {
                    if (item.Part != null && NativeAssembly.Installed(item.Part)) RepairMount(item.Binding);
                    if (item.Id.Length == 0 && item.Binding.Fsm.FsmName != "Assemble") continue;
                    PartEdit edit = new PartEdit { Who = Session.LocalId, Key = item.Binding.Key, State = item.State, Id = item.Id };
                    if (item.Binding.Fsm.FsmName == "Assembly" && !Installed(item.Id) || item.Binding.Fsm.FsmName == "Removal" && Installed(item.Id)) { StateFor(edit).Pending.Cancel(); continue; }
                    PublishPart(edit);
                }
            }
            completedParts.Clear();
            foreach (KeyValuePair<string, PartEdit> item in pendingParts)
            {
                if (TryPart(item.Value)) completedParts.Add(item.Key);
                else if (Time.realtimeSinceStartup > item.Value.Until)
                { Log.Warn("MSC assembly unavailable after retry: " + item.Value.Key + " / " + item.Value.Id); completedParts.Add(item.Key); }
            }
            foreach (string key in completedParts) pendingParts.Remove(key);
            finished.Clear();
            foreach (KeyValuePair<Fsm, Scope> item in remote)
            {
                string state = item.Key.ActiveStateName;
                if (Time.realtimeSinceStartup > item.Value.Until || state == "Idle" || state == "Check for Part collision" || state == "Wait for cap") finished.Add(item.Key);
            }
            foreach (Fsm f in finished) remote.Remove(f);
            if (profile) ProfileStep(ref profileAt, "[MSC parts pending]");
            repaired.Clear();
            foreach (KeyValuePair<Binding, float> repair in repairs)
            {
                if (repair.Key.Fsm == null || Time.realtimeSinceStartup > repair.Value) { repaired.Add(repair.Key); continue; }
                RepairMount(repair.Key);
            }
            foreach (Binding binding in repaired) repairs.Remove(binding);
            if (profile) ProfileStep(ref profileAt, "[MSC parts repairs]");
            completedBolts.Clear();
            foreach (KeyValuePair<string, PendingBolt> item in pendingBolts)
            {
                Binding b;
                if (bindings.TryGetValue(item.Key, out b) && b.Fsm != null && IsBolt(b.Fsm))
                { ReceiveBolt(b, item.Value); completedBolts.Add(item.Key); }
                else if (Time.realtimeSinceStartup > item.Value.Until) completedBolts.Add(item.Key);
            }
            foreach (string key in completedBolts) pendingBolts.Remove(key);
            if (Session.IsHost && Session.Active && Time.realtimeSinceStartup >= nextPartSnapshot)
            {
                nextPartSnapshot = Time.realtimeSinceStartup + 3;
                foreach (PartState state in partStates.Values)
                {
                    if (state.Edit == null || state.Clock.HostRevision <= 0) continue;
                    ReconcileHost(state);
                    Session.Broadcast(PartPacket(state.Edit, state.Clock.HostRevision), true);
                }
            }
            if (Session.IsHost && Session.Active && Time.realtimeSinceStartup >= nextBoltSnapshot)
            {
                nextBoltSnapshot = Time.realtimeSinceStartup + 2;
                foreach (Binding b in bindings.Values) if (b.Fsm != null && b.BoltChanged) Session.Broadcast(BoltPacket(b, b.BoltActor, b.Clock.HostRevision), true);
            }
            if (profile) ProfileStep(ref profileAt, "[MSC parts snapshots]");
        }
        public static void OnPart(Peer from, NetReader reader)
        {
            PartEdit edit = new PartEdit { Who = reader.U8(), Key = reader.Str(), State = reader.Str(), Id = reader.Str(), Revision = reader.I32(), ActorRevision = reader.I32(), Until = Time.realtimeSinceStartup + 60 };
            edit.HasAngle = reader.Bool(); if (edit.HasAngle) edit.Angle = reader.F32();
            if (edit.Revision <= 0 || edit.ActorRevision <= 0 || Session.IsHost && edit.ActorRevision != edit.Revision || reader.More) throw new ProtocolException("Invalid MSC assembly revision");
            if (Session.IsHost) edit.Who = from.Id;
            if (TryPart(edit)) return;
            string address = Address(edit); PartEdit previous;
            if (!pendingParts.TryGetValue(address, out previous) || Session.IsHost && edit.Who != previous.Who || edit.Revision > previous.Revision) pendingParts[address] = edit;
        }
        static string Address(PartEdit edit) { return edit.Id.Length > 0 ? edit.Id : edit.Key; }
        static PartState StateFor(PartEdit edit)
        {
            string address = Address(edit); PartState state;
            if (!partStates.TryGetValue(address, out state)) partStates[address] = state = new PartState();
            return state;
        }
        static NetWriter PartPacket(PartEdit edit, int revision)
        {
            PlayMakerFSM db; FsmFloat angle = databases.TryGetValue(edit.Id, out db) && db != null ? db.FsmVariables.FindFsmFloat("Angle") : null;
            NetWriter packet = new NetWriter(Msg.Part).U8(edit.Who).Str(edit.Key).Str(edit.State).Str(edit.Id).I32(revision).I32(edit.ActorRevision).Bool(angle != null);
            if (angle != null) packet.F32(angle.Value);
            return packet;
        }
        static void PublishPart(PartEdit edit)
        {
            PartState state = StateFor(edit); edit.Revision = state.Clock.Publish(Session.IsHost); state.Edit = edit;
            edit.ActorRevision = edit.Revision;
            if (!Session.IsHost) state.Pending.Sent(edit.ActorRevision);
            MscTuning.RestoreTiming(Find(edit.Id), false);
            Session.SendAll(PartPacket(edit, edit.Revision), true);
        }
        static void ReconcileHost(PartState state)
        {
            PartEdit edit = state.Edit; Binding source;
            if (!bindings.TryGetValue(edit.Key, out source) || source.Fsm == null) return;
            bool installed = Installed(edit.Id);
            if (installed == (source.Fsm.FsmName == "Assembly")) return;
            Binding actual = null; string nativeState = null;
            foreach (Binding b in bindings.Values)
            {
                if (b.Fsm == null) continue;
                if (!installed && b.Fsm.FsmName == "Removal" && IdOf(b.Fsm.gameObject) == edit.Id)
                { actual = b; nativeState = "Remove part"; break; }
                if (installed && b.Fsm.FsmName == "Assembly") foreach (FsmState s in b.Fsm.FsmStates)
                {
                    if (!ActionState(b.Fsm, s)) continue;
                    PlayMakerFSM db = NativeAssembly.Database(b.Fsm, s);
                    if (db != null && "mscdb:" + Parts.RankPath(db.transform) == edit.Id) { actual = b; nativeState = s.Name; break; }
                }
                if (actual != null) break;
            }
            if (actual != null)
            {
                int revision = state.Clock.Publish(true);
                state.Edit = new PartEdit { Who = Session.LocalId, Key = actual.Key, State = nativeState, Id = edit.Id, Revision = revision, ActorRevision = revision };
            }
        }
        static bool TryPart(PartEdit edit)
        {
            string key = edit.Key, state = edit.State, id = edit.Id;
            Binding binding;
            bindings.TryGetValue(key, out binding);
            GameObject part = Find(id);
            if ((binding == null || binding.Fsm == null) && MscBelt.IsBelt(part))
            {
                // Removal is addressed by the numbered item as well as the FSM
                // key, including an item that was initialized after the scan.
                PlayMakerFSM beltRemoval = Game.FsmOn(part, "Removal");
                if (beltRemoval != null) byFsm.TryGetValue(beltRemoval, out binding);
            }
            if (binding == null || binding.Fsm == null) return false;
            FsmState native = binding.Fsm.Fsm.GetState(state);
            if (native == null || !ActionState(binding.Fsm, native) || IsBolt(binding.Fsm)) throw new ProtocolException("Invalid MSC assembly state");
            bool assembly = binding.Fsm.FsmName == "Assembly", removal = binding.Fsm.FsmName == "Removal";
            PlayMakerFSM db = assembly ? NativeAssembly.Database(binding.Fsm, native) : null;
            if ((assembly || removal) && part == null) return false;
            if (assembly && db != null && id != "mscdb:" + Parts.RankPath(db.transform)) throw new ProtocolException("Incorrect MSC database part for " + key);
            if (assembly && db == null && (part == null || !(NativeAssembly.Accepts(binding.Fsm, part) || MscBelt.IsBelt(part) && NativeAssembly.Accepts(binding.Fsm.FsmVariables.StringVariables, "alternator belt(Clone)")))) throw new ProtocolException("Incorrect MSC part for " + key);
            if (removal && IdOf(binding.Fsm.gameObject) != id) throw new ProtocolException("Incorrect MSC removal part");
            PartState accepted = StateFor(edit);
            if (!Session.IsHost && !accepted.Pending.PermitSnapshot(edit.Who == Session.LocalId, edit.ActorRevision)) return true;
            bool fresh = Session.IsHost ? accepted.Clock.AcceptRequest(edit.Who, edit.Revision) : accepted.Clock.AcceptSnapshot(edit.Revision);
            if (!fresh && (Session.IsHost || edit.Revision != accepted.Clock.HostRevision || !(assembly || removal))) return true;
            accepted.Edit = edit;
            PlayMakerFSM angleDb;
            if (fresh && edit.HasAngle && databases.TryGetValue(id, out angleDb) && angleDb != null)
            {
                FsmFloat angle = angleDb.FsmVariables.FindFsmFloat("Angle");
                if (angle != null) angle.Value = edit.Angle;
            }
            if (assembly) { binding.MountState = state; FsmGameObject held = binding.Fsm.FsmVariables.FindFsmGameObject("Part"); if (held != null) held.Value = part; }
            if (assembly && part != null) part.SetActive(true);
            if (!(assembly && Installed(id) || removal && !Installed(id)))
            {
                if (assembly && !binding.Fsm.gameObject.activeInHierarchy) binding.Fsm.gameObject.SetActive(true);
                Execute(binding.Fsm, state);
                Changed(part); Changed(binding.Fsm.gameObject);
            }
            else if (removal && MscBattery.IsBattery(part)) MscBattery.CompleteRemoval(part, null);
            if (binding.Fsm.FsmName == "Assembly") repairs[binding] = Time.realtimeSinceStartup + 3;
            RepairMount(binding);
            if (fresh && edit.HasAngle) MscTuning.RestoreTiming(Find(id), assembly);
            if (Session.IsHost) Session.Broadcast(PartPacket(edit, accepted.Clock.Publish(true)), true);
            return true;
        }
        public static void OnBolt(Peer from, NetReader reader)
        {
            int who = reader.U8(); string key = reader.Str(); int target = reader.I32(), revision = reader.I32(), actorRevision = reader.I32(); if (Session.IsHost) who = from.Id;
            PendingBolt edit = new PendingBolt { Who = who, Target = target, Revision = revision, ActorRevision = actorRevision, Until = Time.realtimeSinceStartup + 60, HasTotal = reader.Bool() };
            if (edit.HasTotal) { edit.Tightness = reader.F32(); edit.HasBolted = reader.Bool(); edit.Bolted = reader.Bool(); }
            Binding binding;
            if (target < 0 || target > 8 || revision <= 0 || actorRevision < 0 || Session.IsHost && actorRevision != revision || reader.More) throw new ProtocolException("Invalid MSC bolt stage/revision");
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
            NetWriter writer = new NetWriter(Msg.Bolt).U8(who).Str(binding.Key).I32(binding.Fsm.FsmVariables.FindFsmInt("Stage").Value).I32(revision).I32(binding.BoltActorRevision).Bool(binding.Total != null);
            if (binding.Total != null) writer.F32(binding.Total.Tightness.Value).Bool(binding.Total.Bolted != null).Bool(binding.Total.Bolted != null && binding.Total.Bolted.Value);
            return writer;
        }
        static MscPendingBoltEdit PendingTotal(BoltTotal total)
        {
            MscPendingBoltEdit value;
            if (!totalPending.TryGetValue(total.Fsm, out value)) totalPending[total.Fsm] = value = new MscPendingBoltEdit();
            return value;
        }
        static bool BoltInputReady(string state)
        {
            return state == "Idle" || state == "Get scroll" || state == "Get scroll 2" || state == "Mouse off 2";
        }
        static void PublishBolt(Binding binding)
        {
            int revision = Session.IsHost ? ++hostBoltRevision : ++localBoltRevision;
            binding.BoltActor = Session.LocalId; binding.BoltActorRevision = revision;
            if (Session.IsHost) binding.Clock.AcceptSnapshot(revision);
            else
            {
                binding.PendingBolt.Sent(revision);
                if (binding.Total != null) PendingTotal(binding.Total).Sent(revision);
            }
            Session.SendAll(BoltPacket(binding, Session.LocalId, revision), true);
        }
        // QA: reproduce a host repair captured before the guest's next turn.
        internal static byte[] CaptureBoltRepair(PlayMakerFSM f)
        {
            if (Session.IsHost || MscRuntime.Argument("--summercoop-test-workshop-channel").Length == 0) return null;
            Binding b;
            return byFsm.TryGetValue(f, out b) ? BoltPacket(b, 0, b.Clock.HostRevision + 1).ToArray() : null;
        }
        internal static void ReleaseBatteryTerminals(PlayMakerFSM removal)
        {
            bool publish = removal != null && Session.Active && Replay.Depth == 0 && !remote.ContainsKey(removal.Fsm);
            foreach (Binding binding in bindings.Values)
            {
                if (binding.Fsm == null || !IsBolt(binding.Fsm)) continue;
                if (binding.Total == null) binding.Total = TotalOf(binding.Fsm);
                BoltTotal total = binding.Total;
                if (total == null || total.Fsm == null || !total.Fsm.name.StartsWith("battery_terminal_", StringComparison.Ordinal)) continue;
                binding.Fsm.FsmVariables.FindFsmInt("Stage").Value = 0;
                total.Tightness.Value = 0;
                if (total.Bolted != null) total.Bolted.Value = false;
                MscBoltPositions.Restore(binding.Fsm);
                // A loose clamp remains connected to its existing cable. Native
                // battery removal does not destroy the wiring database/model;
                // doing so hides the plus clamp and strands the minus clamp.
                // Keep the saved array consistent without disconnecting either.
                var array = total.Fsm.GetComponent<PlayMakerArrayListProxy>();
                if (array != null && array.arrayList != null) for (int i = 0; i < array.arrayList.Count; i++) array.arrayList[i] = 0;
                FsmInt saved = total.Fsm.FsmVariables.FindFsmInt("bolt0"); if (saved != null) saved.Value = 0;
                if (publish)
                {
                    if (!Session.IsHost) { binding.PendingBolt.Begin(); PendingTotal(total).Begin(); }
                    binding.BoltChanged = true;
                    PublishBolt(binding);
                }
            }
        }
        internal static bool SeedBatterySnapshot(PlayMakerFSM mount, GameObject part)
        {
            Binding binding;
            if (mount == null || !byFsm.TryGetValue(mount, out binding)) return false;
            string id = IdOf(part);
            if (id.Length == 0) return false;
            if (!partStates.ContainsKey(id)) foreach (FsmState state in mount.FsmStates)
            {
                if (NativeAssembly.MountAction(state.Actions) == null) continue;
                PublishPart(new PartEdit { Who = Session.LocalId, Key = binding.Key, State = state.Name, Id = id });
                break;
            }
            return partStates.ContainsKey(id);
        }
        static BoltTotal TotalOf(PlayMakerFSM f)
        {
            // Oil filters use Wait; ordinary fasteners use Wait 3. The native
            // target, rather than the state label, identifies the saved total.
            foreach (FsmState state in f.FsmStates) foreach (FsmStateAction action in state.Actions)
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
            if (!Session.IsHost && (edit.Revision <= binding.Clock.HostRevision || !binding.PendingBolt.PermitSnapshot(edit.Who == Session.LocalId, edit.ActorRevision))) return;
            if (Session.IsHost ? !binding.Clock.AcceptRequest(edit.Who, edit.Revision) : !binding.Clock.AcceptSnapshot(edit.Revision)) return;
            if (binding.Total == null) binding.Total = TotalOf(binding.Fsm);
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
                // InitData does not run Save data 2 on hidden/distant bolts.
                // Resolve the native mesh/name/index before writing its saved slot.
                MscBoltPositions.Restore(binding.Fsm);
                HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
                for (int hop = 0; pose != null && hop < 6 && visited.Add(pose.Name); hop++)
                {
                    foreach (FsmStateAction action in pose.Actions)
                        if (action != null && !(action is ModHook) && !(action is HutongGames.PlayMaker.Actions.SetPosition)
                            && !(action is HutongGames.PlayMaker.Actions.SetRotation) && !action.GetType().Name.StartsWith("GetMouse", StringComparison.Ordinal)
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
                    if (Session.IsHost ? clock.AcceptRequest(edit.Who, edit.Revision) : edit.Revision > clock.HostRevision && PendingTotal(binding.Total).PermitSnapshot(edit.Who == Session.LocalId, edit.ActorRevision) && clock.AcceptSnapshot(edit.Revision))
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
            finally { Replay.Depth--; remote.Remove(binding.Fsm.Fsm); }
            binding.BoltChanged = true;
            binding.BoltActor = edit.Who; binding.BoltActorRevision = edit.ActorRevision;
            Log.Info("MSC bolt applied " + binding.Key + " = " + stage.Value);
            if (Session.IsHost) { int revision = ++hostBoltRevision; binding.Clock.AcceptSnapshot(revision); Session.Broadcast(BoltPacket(binding, edit.Who, revision), true); }
        }
        static void Execute(PlayMakerFSM f, string state)
        {
            PatchActions(f); remote[f.Fsm] = new Scope { Until = Time.realtimeSinceStartup + 2 };
            Replay.Depth++;
            try
            {
                if (f.FsmName == "Assembly") NativeAssembly.PrepareJoint(f, state);
                if (!Game.SetState(f, state)) throw new InvalidOperationException("MSC native state missing: " + state);
            }
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
            if (MscRuntime.Argument("--summercoop-test-report").Length == 0 || !Session.IsHost && !(Session.LocalId == 1 && MscRuntime.Argument("--summercoop-test-action-actor") == "guest")) throw new InvalidOperationException("Native exercise is test-only");
            GameObject part = null;
            foreach (GameObject candidate in parts.Values) if (candidate != null && candidate.name == "starter(Clone)") { part = candidate; break; }
            if (part == null) throw new InvalidOperationException("MSC starter not found");
            foreach (Binding binding in bindings.Values)
            {
                if (binding.Fsm == null) continue;
                if (bolt)
                {
                    if (!IsBolt(binding.Fsm) || !binding.Fsm.transform.IsChildOf(part.transform)) continue;
                    exercisedBolt = binding;
                    Execute(binding.Fsm, binding.Fsm.FsmVariables.FindFsmInt("Stage").Value >= 8 ? "Unscrew 2" : "Screw 2"); return;
                }
                if (binding.Fsm.FsmName != "Assembly" || !NativeAssembly.Accepts(binding.Fsm.FsmVariables.StringVariables, part.name)) continue;
                FsmGameObject variable = binding.Fsm.FsmVariables.FindFsmGameObject("Part"); variable.Value = part;
                part.transform.position = binding.Fsm.transform.position;
                binding.MountState = "Assemble"; Execute(binding.Fsm, "Assemble");
                repairs[binding] = Time.realtimeSinceStartup + 3;
                PublishPart(new PartEdit { Who = Session.LocalId, Key = binding.Key, State = "Assemble", Id = IdOf(part) }); return;
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
                if (binding.Fsm == null || binding.Fsm.FsmName != "Assembly" || !NativeAssembly.Accepts(binding.Fsm, part)) continue;
                foreach (FsmState native in binding.Fsm.FsmStates)
                {
                    if (!ActionState(binding.Fsm, native) || NativeAssembly.MountAction(native.Actions) == null) continue;
                    binding.Fsm.FsmVariables.FindFsmGameObject("Part").Value = part;
                    part.transform.position = binding.Fsm.transform.position;
                    binding.MountState = native.Name; Execute(binding.Fsm, native.Name);
                    if (!NativeAssembly.Installed(part)) continue;
                    repairs[binding] = Time.realtimeSinceStartup + 3;
                    PublishPart(new PartEdit { Who = Session.LocalId, Key = binding.Key, State = native.Name, Id = IdOf(part) });
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
                if (binding.Fsm == null || binding.Fsm.FsmName != "Assembly" || !NativeAssembly.Accepts(binding.Fsm, part)) continue;
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
                    PublishPart(new PartEdit { Who = Session.LocalId, Key = binding.Key, State = state.Name, Id = IdOf(part) });
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
            PublishBolt(binding);
            Log.Info("MSC bolt published " + binding.Key + " = " + stage);
        }
    }
}
