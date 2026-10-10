using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HutongGames.PlayMaker;
using SummerCoop.Core;
using UnityEngine;

namespace SummerCoop.Game
{
    public sealed class ObjectBinding
    {
        public string Id, Key, Prefab = "";
        public GameObject Object; public Transform Transform;
        public Rigidbody Body;
        public Renderer[] Renderers;
        public Collider[] Colliders;
        public PlayMakerArrayListProxy[] Arrays;
        public Light[] Lights;
        public AudioSource[] Audio;
        public MeshFilter Mesh;
        public EntityKind Kind;
        public bool Empty, Motion, PhysicsDisabled;
        public Rigidbody DisabledBody;
        public float NextMeshCapture;
        public Mesh CapturedMesh;
        public Vector3[] CapturedVertices;
    }
    public sealed class FsmBinding
    {
        public string Id, Key, ObjectId;
        public PlayMakerFSM Component;
        public VariableView Variables;
        public HashSet<string> Requests = new HashSet<string>(StringComparer.Ordinal);
        public string WaitingState = "";
        public bool Personal;
    }
    public sealed class WorldRegistry
    {
        public readonly Dictionary<string, ObjectBinding> Objects = new Dictionary<string, ObjectBinding>(StringComparer.Ordinal);
        public readonly Dictionary<string, FsmBinding> Fsms = new Dictionary<string, FsmBinding>(StringComparer.Ordinal);
        private readonly Dictionary<GameObject, string> objectIds = new Dictionary<GameObject, string>();
        private readonly Dictionary<Fsm, FsmBinding> fsmIds = new Dictionary<Fsm, FsmBinding>();
        private readonly Dictionary<string, List<FsmBinding>> fsmsByObject = new Dictionary<string, List<FsmBinding>>(StringComparer.Ordinal);
        private readonly Dictionary<GameObject, string> partKeys = new Dictionary<GameObject, string>();
        private readonly Dictionary<GameObject, string> sceneKeys = new Dictionary<GameObject, string>();
        private readonly Dictionary<string, int> vehicleNames = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> itemPrefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, UnityEngine.Object> assets = new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
        private sealed class AssetReference { public string Name, Key; }
        private readonly Dictionary<UnityEngine.Object, AssetReference> assetReferences = new Dictionary<UnityEngine.Object, AssetReference>();
        private readonly Dictionary<Component, string> componentReferences = new Dictionary<Component, string>();
        private readonly Dictionary<string, UnityEngine.Object> resolvedReferences = new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
        private struct PropertyIndex : IEquatable<PropertyIndex>
        {
            public string Prefix, Suffix; public int First, Second;
            public bool Equals(PropertyIndex other) { return Prefix == other.Prefix && Suffix == other.Suffix && First == other.First && Second == other.Second; }
            public override bool Equals(object other) { return other is PropertyIndex && Equals((PropertyIndex)other); }
            public override int GetHashCode() { return ((Prefix.GetHashCode() * 397 ^ First) * 397 ^ Second) * 397 ^ Suffix.GetHashCode(); }
        }
        private readonly Dictionary<PropertyIndex, string> indexedProperties = new Dictionary<PropertyIndex, string>();
        private string Property(string prefix, int first, string suffix) { return Property(prefix, first, -1, suffix); }
        private string Property(string prefix, int first, int second) { return Property(prefix, first, second, ""); }
        private string Property(string prefix, int first, int second, string suffix)
        {
            PropertyIndex index = new PropertyIndex { Prefix = prefix, First = first, Second = second, Suffix = suffix }; string key;
            if (indexedProperties.TryGetValue(index, out key)) return key;
            key = prefix + first.ToString(CultureInfo.InvariantCulture) + (second < 0 ? "" : "/" + second.ToString(CultureInfo.InvariantCulture)) + suffix;
            // Native arrays are bounded to 2048 entries; keep unusual component layouts bounded too.
            if (indexedProperties.Count < 32768) indexedProperties.Add(index, key);
            return key;
        }
        private readonly Dictionary<string, long> motionSequences = new Dictionary<string, long>();
        private readonly Dictionary<string, BodyPose> motionTargets = new Dictionary<string, BodyPose>();
        private readonly Dictionary<string, BodyPose> motionPrevious = new Dictionary<string, BodyPose>();
        private readonly bool host;
        private long motionSequence;
        private const string GlobalId = "globals";
        public bool Applying { get; private set; }
        public string SchemaHash { get; private set; }
        public WorldRegistry(bool host, string gameHash)
        {
            this.host = host;
            // Adapter schema is independent of the saved arrangement of parts and item count.
            SchemaHash = Codec.Hash(System.Text.Encoding.UTF8.GetBytes(gameHash + "/native-schema-2"));
        }
        public static bool PersonalObject(GameObject go)
        {
            string root = go.transform.root.name;
            return root == "PLAYER" || root == "GUI" || root == "HUD" || root.StartsWith("MSCLoader", StringComparison.Ordinal) || root.StartsWith("SummerCoop", StringComparison.Ordinal) || root == "Steam";
        }
        public static bool PersonalVariable(string name)
        {
            if (name.StartsWith("Player", StringComparison.Ordinal))
                return name != "PlayerMoney" && name != "PlayerNetIncome" && name != "PlayerFines" && name != "PlayerFinesDay" && !name.StartsWith("PlayerKey", StringComparison.Ordinal);
            return name.StartsWith("GUI", StringComparison.Ordinal) || name.StartsWith("Options", StringComparison.Ordinal) || name.StartsWith("Settings", StringComparison.Ordinal) || name.StartsWith("WWW", StringComparison.Ordinal)
                || name == "POV" || name == "SavePlayer" || name == "SavePlayerCam" || name == "ItemInHand" || name == "ItemPivot" || name == "PickedPart" || name == "LookAtTarget" || name.StartsWith("Tool", StringComparison.Ordinal)
                || name == "SaveSlot" || name == "SaveCarparts" || name == "GameVolume" || name == "InterfaceMute" || name.StartsWith("SoundAmbience", StringComparison.Ordinal);
        }
        public void IndexScene()
        {
            // MSCLoader PreLoad runs before vehicle Start methods detach wheel transforms.
            HashSet<Transform> roots = new HashSet<Transform>();
            foreach (Transform t in UnityEngine.Object.FindObjectsOfType<Transform>()) roots.Add(t.root);
            foreach (Transform root in roots) if (!PersonalObject(root.gameObject)) Index(root, "scene/" + Segment(root));
            if (CoopRuntime.Argument("--summercoop-test-report") != "")
            {
                int index = 0; foreach (KeyValuePair<GameObject, string> entry in sceneKeys)
                    if (index++ % 251 == 0) { IndexSamples++; if (entry.Value != "scene/" + StablePath(entry.Key.transform)) IndexMismatches++; }
                if (IndexMismatches > 0) throw new InvalidOperationException("Indexed native paths differ from the original identity algorithm");
            }
        }
        public int IndexSamples { get; private set; }
        public int IndexMismatches { get; private set; }
        private void Index(Transform t, string path)
        {
            sceneKeys[t.gameObject] = path;
            int children = t.childCount; if (children == 0) return;
            Dictionary<string, int> siblings = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < children; i++)
            {
                Transform child = t.GetChild(i); string name = child.name; int ordinal; siblings.TryGetValue(name, out ordinal); siblings[name] = ordinal + 1;
                Index(child, path + "/" + name + "[" + ordinal.ToString(CultureInfo.InvariantCulture) + "]");
            }
        }
        public void Discover(bool initial)
        {
            foreach (object ignored in DiscoverSteps(initial)) { }
        }
        private IEnumerator<object> discovery;
        public void DiscoverIncremental(bool begin)
        {
            if (discovery == null && begin) discovery = DiscoverSteps(false).GetEnumerator();
            if (discovery == null) return;
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            do
            {
                if (!discovery.MoveNext()) { discovery.Dispose(); discovery = null; break; }
            }
            while (System.Diagnostics.Stopwatch.GetTimestamp() - began < System.Diagnostics.Stopwatch.Frequency / 1000);
        }
        private IEnumerable<object> DiscoverSteps(bool initial)
        {
            PlayMakerFSM[] all = Resources.FindObjectsOfTypeAll<PlayMakerFSM>();
            yield return null;
            HashSet<Transform> roots = new HashSet<Transform>();
            foreach (PlayMakerFSM f in all)
            {
                yield return null;
                if (f == null) continue;
                if (!initial && objectIds.ContainsKey(f.gameObject)) continue;
                string name = f.transform.root.name;
                if ((initial || !objectIds.ContainsKey(f.gameObject)) && !PersonalObject(f.gameObject) && (f.Fsm.Initialized || name == "Spawner" || name == "Systems" || name == "Database")) roots.Add(f.transform.root);
            }
            Rigidbody[] bodies = UnityEngine.Object.FindObjectsOfType<Rigidbody>(); yield return null;
            foreach (Rigidbody body in bodies) { yield return null; if (body != null && (initial || !objectIds.ContainsKey(body.gameObject)) && !PersonalObject(body.gameObject)) roots.Add(body.transform.root); }
            foreach (PlayMakerFSM f in all) { yield return null; if (f != null && !f.Fsm.Initialized && roots.Contains(f.transform.root) && !PersonalObject(f.gameObject)) f.Fsm.Init(f); }
            foreach (PlayMakerFSM f in all)
            {
                yield return null;
                if (f == null) continue;
                if ((!initial && fsmIds.ContainsKey(f.Fsm)) || !f.Fsm.Initialized || PersonalObject(f.gameObject)) continue;
                FsmGameObject db = f.FsmVariables.FindFsmGameObject("db_ThisPart");
                if (db != null && db.Value != null && f.FsmName == "Removal" && (f.gameObject.activeInHierarchy || f.gameObject.name.EndsWith("(Clone)", StringComparison.Ordinal))) partKeys[f.gameObject] = "part/" + StablePath(db.Value.transform);
                FsmString prefix = f.FsmVariables.FindFsmString("ObjectCreateName"); FsmGameObject item = f.FsmVariables.FindFsmGameObject("CreatePrefab");
                if (prefix != null && !string.IsNullOrEmpty(prefix.Value) && item != null && item.Value != null) { AddPrefab(item.Value); itemPrefixes[prefix.Value] = "prefab/" + item.Value.name; }
                foreach (FsmState s in f.FsmStates) foreach (FsmStateAction a in s.Actions)
                {
                    if (a.GetType().Name != "CreateObject") continue;
                    FieldInfo field = a.GetType().GetField("gameObject"); FsmGameObject value = field == null ? null : field.GetValue(a) as FsmGameObject;
                    if (value != null && value.Value != null) AddPrefab(value.Value);
                }
            }
            AddItemAlias("fuseholder", "fuseholder"); AddItemAlias("oilfilter", "oil filter"); AddItemAlias("pizzax", "pizza"); AddItemAlias("milkx", "milk"); AddItemAlias("macaron boxx", "macaron box");
            if (initial)
            {
                vehicleNames.Clear();
                foreach (Transform root in roots) foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    if (VehicleAnchor(t)) { int count; vehicleNames.TryGetValue(t.name, out count); vehicleNames[t.name] = count + 1; }
            }
            foreach (Transform root in roots) if (root != null) foreach (object ignored in WalkSteps(root, initial, null)) yield return null;
            if (initial)
            {
                foreach (Material a in Resources.FindObjectsOfTypeAll<Material>()) RegisterAsset(a);
                foreach (Texture2D a in Resources.FindObjectsOfTypeAll<Texture2D>()) RegisterAsset(a);
                foreach (AudioClip a in Resources.FindObjectsOfTypeAll<AudioClip>()) RegisterAsset(a);
            }
            foreach (PlayMakerFSM f in all)
            {
                yield return null;
                if (f == null) continue;
                if (fsmIds.ContainsKey(f.Fsm) || PersonalObject(f.gameObject) || !objectIds.ContainsKey(f.gameObject)) continue;
                if (!f.Fsm.Initialized) f.Fsm.Init(f);
                string owner = IdOf(f.gameObject); if (owner == "") continue;
                PlayMakerFSM[] siblings = f.GetComponents<PlayMakerFSM>(); int ordinal = Array.IndexOf(siblings, f);
                string key = Objects[owner].Key + "|fsm/" + f.FsmName + "/" + ordinal.ToString(CultureInfo.InvariantCulture);
                FsmBinding binding = new FsmBinding { Id = Identity.Stable(key), Key = key, ObjectId = owner, Component = f, Variables = new VariableView(f.FsmVariables) };
                binding.Personal = IsPersonalFsm(f);
                DescribeRequests(binding); RegisterFsm(binding);
            }
            if (!host && (initial || roots.Count > 0)) DisableClientPhysics();
        }
        private void AddPrefab(GameObject go)
        {
            string key = "prefab/" + go.name;
            GameObject existing;
            if (!prefabs.TryGetValue(key, out existing)) prefabs.Add(key, go);
            // A collision is deliberately not guessed: unsupported spawns end the session.
            else if (existing != go && existing.transform.root != go.transform.root) prefabs[key] = null;
        }
        private void Walk(Transform t, bool initial, string forced)
        {
            foreach (object ignored in WalkSteps(t, initial, forced)) { }
        }
        private IEnumerable<object> WalkSteps(Transform t, bool initial, string forced)
        {
            yield return null;
            if (t == null || PersonalObject(t.gameObject)) yield break;
            string existing;
            if (!objectIds.TryGetValue(t.gameObject, out existing))
            {
                string key = forced ?? KeyOf(t);
                if (!initial && forced == null)
                {
                    key = "spawn/" + Guid.NewGuid().ToString("N");
                    string baseName = t.name.EndsWith("(Clone)", StringComparison.Ordinal) ? t.name.Substring(0, t.name.Length - 7) : t.name;
                    forced = key; // Descendants inherit this spawn ID rather than independent GUIDs.
                    ObjectBinding created = Add(t.gameObject, key); if (prefabs.ContainsKey("prefab/" + baseName)) created.Prefab = "prefab/" + baseName;
                }
                else
                {
                    ObjectBinding b = Add(t.gameObject, key); string prefab;
                    if (ItemPrefab(t.name, out prefab)) b.Prefab = prefab;
                }
            }
            for (int i = 0; t != null && i < t.childCount; i++) foreach (object ignored in WalkSteps(t.GetChild(i), initial, forced == null ? null : forced + "/" + Segment(t.GetChild(i)))) yield return null;
        }
        private string KeyOf(Transform t)
        {
            Transform cursor = t; string suffix = "";
            while (cursor != null)
            {
                string part;
                if (partKeys.TryGetValue(cursor.gameObject, out part)) return part + suffix;
                if (cursor.parent != null && cursor.parent.name.StartsWith("Fuse", StringComparison.Ordinal) && cursor.parent.name.EndsWith("Pivot", StringComparison.Ordinal) && PrefabName(cursor.name).StartsWith("fuseholder", StringComparison.Ordinal)) return "house/fuse-holder/" + cursor.parent.name + suffix;
                string prefab;
                if (ItemPrefab(cursor.name, out prefab)) return "item/" + cursor.name + suffix;
                int count;
                if (VehicleAnchor(cursor) && vehicleNames.TryGetValue(cursor.name, out count) && count == 1) return "vehicle/" + cursor.name + suffix;
                // Junk cars are moved between random spawn transforms during native loading.
                if (cursor.name.StartsWith("JunkCar", StringComparison.Ordinal) && cursor.name.Length == 8) return "vehicle/" + cursor.name + suffix;
                if (cursor.parent != null) suffix = "/" + Segment(cursor) + suffix;
                cursor = cursor.parent;
            }
            // Native dynamic items expose the persisted ID in their local FSM variables.
            foreach (PlayMakerFSM f in t.root.GetComponents<PlayMakerFSM>())
            {
                FsmString id = f.FsmVariables.FindFsmString("ID");
                if (id != null && !string.IsNullOrEmpty(id.Value)) return "item/" + t.root.name + "/" + id.Value + Relative(t.root, t);
            }
            string frozen; if (sceneKeys.TryGetValue(t.gameObject, out frozen)) return frozen;
            return "scene/" + StablePath(t);
        }
        private static bool VehicleAnchor(Transform t) { return t.GetComponent<CarController>() != null || t.GetComponent<CarDynamics>() != null || t.GetComponent<Drivetrain>() != null || t.name == "BOAT"; }
        private bool ItemPrefab(string name, out string prefab)
        {
            foreach (var prefix in itemPrefixes)
            {
                if (!name.StartsWith(prefix.Key, StringComparison.Ordinal) || name.Length == prefix.Key.Length && name != "pizzax" && name != "milkx" && name != "macaron boxx") continue;
                bool digits = true; for (int i = prefix.Key.Length; i < name.Length; i++) if (name[i] < '0' || name[i] > '9') { digits = false; break; }
                if (digits) { prefab = prefix.Value; return true; }
            }
            prefab = ""; return false;
        }
        private void AddItemAlias(string prefix, string nativeName)
        {
            string normalized = PrefabName(nativeName), match = null;
            foreach (string key in prefabs.Keys)
            {
                if (PrefabName(key.Substring(7)) != normalized) continue;
                if (match != null) return; // Ambiguous templates must be resolved explicitly.
                match = key;
            }
            if (match != null) itemPrefixes[prefix] = match;
        }
        private static string PrefabName(string name) { return name.ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("(itemx)", "").Replace("(clone)", ""); }
        public void DumpPrefabs(string path) { using (StreamWriter w = new StreamWriter(path)) foreach (string key in prefabs.Keys) w.WriteLine(key + (prefabs[key] == null ? " AMBIGUOUS" : "")); }
        private static string Segment(Transform t)
        {
            int ordinal = 0;
            if (t.parent != null) for (int i = 0; i < t.GetSiblingIndex(); i++) if (t.parent.GetChild(i).name == t.name) ordinal++;
            return t.name + "[" + ordinal.ToString(CultureInfo.InvariantCulture) + "]";
        }
        private static string StablePath(Transform t)
        {
            string result = Segment(t); while (t.parent != null) { t = t.parent; result = Segment(t) + "/" + result; } return result;
        }
        private static string Relative(Transform root, Transform t)
        {
            string path = ""; while (t != root) { path = "/" + Segment(t) + path; t = t.parent; } return path;
        }
        private ObjectBinding Add(GameObject go, string key)
        {
            string id = Identity.Stable(key); ObjectBinding collision;
            if (Objects.TryGetValue(id, out collision) && collision.Object != go) throw new InvalidOperationException("Ambiguous native object identity: " + key);
            ObjectBinding b = new ObjectBinding { Id = id, Key = key, Object = go, Transform = go.transform, Body = go.GetComponent<Rigidbody>(), Renderers = go.GetComponents<Renderer>(), Colliders = go.GetComponents<Collider>(), Arrays = go.GetComponents<PlayMakerArrayListProxy>(), Lights = go.GetComponents<Light>(), Audio = go.GetComponents<AudioSource>(), Mesh = go.GetComponent<MeshFilter>() };
            b.Kind = go.GetComponent<CarController>() != null || go.name == "BOAT" ? EntityKind.Vehicle : b.Body != null ? EntityKind.Rigidbody : EntityKind.Object;
            b.Empty = go.GetComponents<Component>().Length == 1;
            b.Motion = b.Body != null || b.Kind == EntityKind.Vehicle || go.GetComponent<PlayMakerFSM>() != null || go.GetComponent<Animation>() != null || go.GetComponent<Animator>() != null;
            Objects[id] = b; objectIds.Add(go, id); captureObjects.Add(b); if (b.Motion) motionObjects.Add(b); return b;
        }
        public string IdOf(GameObject go) { if (go == null) return ""; string id; return objectIds.TryGetValue(go, out id) ? id : ""; }
        public GameObject ObjectOf(string id) { ObjectBinding b; return Objects.TryGetValue(id, out b) ? b.Object : null; }
        public FsmBinding Binding(Fsm fsm) { FsmBinding b; return fsmIds.TryGetValue(fsm, out b) ? b : null; }
        public FsmBinding FsmOf(string id) { FsmBinding b; return Fsms.TryGetValue(id, out b) ? b : null; }
        private void RegisterFsm(FsmBinding binding)
        {
            FsmBinding existing; if (Fsms.TryGetValue(binding.Id, out existing) && existing.Component != null && existing.Component != binding.Component) throw new InvalidOperationException("Ambiguous native FSM identity: " + binding.Key);
            Fsms[binding.Id] = binding; fsmIds[binding.Component.Fsm] = binding; captureFsms.Add(binding);
            List<FsmBinding> owned; if (!fsmsByObject.TryGetValue(binding.ObjectId, out owned)) { owned = new List<FsmBinding>(); fsmsByObject.Add(binding.ObjectId, owned); }
            owned.Add(binding);
        }
        private static bool IsPersonalFsm(PlayMakerFSM f)
        {
            string path = GameProbe.PathOf(f.transform);
            return f.FsmName == "PlayerTrigger" || f.FsmName == "Death" || f.FsmName == "BeeDeath" || f.FsmName == "HeadForce" || path.IndexOf("SleepTrigger", StringComparison.Ordinal) >= 0 || path.IndexOf("PlayerDatabase", StringComparison.Ordinal) >= 0;
        }
        private void RegisterAsset(UnityEngine.Object asset)
        {
            AssetKey(asset);
        }
        private string AssetKey(UnityEngine.Object asset)
        {
            string name = asset.name; AssetReference entry;
            if (assetReferences.TryGetValue(asset, out entry) && entry.Name == name) return entry.Key;
            string key = "asset/" + asset.GetType().FullName + "/" + name;
            if (!assets.ContainsKey(key)) assets.Add(key, asset);
            assetReferences[asset] = new AssetReference { Name = name, Key = key }; return key;
        }
        private Atom Reference(UnityEngine.Object obj)
        {
            if (obj == null) return builder.R("");
            GameObject go = obj as GameObject; Component c = obj as Component;
            if (go != null || c != null)
            {
                string id = IdOf(go ?? c.gameObject);
                if (id == "") return null;
                if (c == null) return builder.R(id);
                string reference; if (!componentReferences.TryGetValue(c, out reference)) { reference = id + "#" + c.GetType().FullName; componentReferences.Add(c, reference); }
                return builder.R(reference);
            }
            return builder.R(AssetKey(obj));
        }
        private UnityEngine.Object Resolve(string reference)
        {
            if (reference == "") return null;
            UnityEngine.Object asset; if (reference.StartsWith("asset/", StringComparison.Ordinal)) return assets.TryGetValue(reference, out asset) ? asset : null;
            UnityEngine.Object cached; if (resolvedReferences.TryGetValue(reference, out cached) && cached != null) return cached;
            int separator = reference.IndexOf('#'); GameObject go = ObjectOf(separator < 0 ? reference : reference.Substring(0, separator));
            UnityEngine.Object value = separator < 0 || go == null ? (UnityEngine.Object)go : go.GetComponent(reference.Substring(separator + 1));
            if (value != null && resolvedReferences.Count < Protocol.MaxEntities * 2) resolvedReferences[reference] = value;
            return value;
        }
        private Atom ReadVariable(NamedVariable v)
        {
            FsmFloat f = v as FsmFloat; if (f != null) return V3.Finite(f.Value) ? builder.N(f.Value) : null;
            FsmInt i = v as FsmInt; if (i != null) return builder.N(i.Value);
            FsmBool b = v as FsmBool; if (b != null) return builder.B(b.Value);
            FsmString s = v as FsmString; if (s != null) return s.Value == null || s.Value.Length <= 4000 ? builder.S(s.Value) : null;
            FsmVector3 vec = v as FsmVector3; if (vec != null) return From(vec.Value).IsFinite ? builder.V(From(vec.Value)) : null;
            FsmQuaternion q = v as FsmQuaternion; if (q != null) return From(q.Value).IsFinite ? builder.Q(From(q.Value)) : null;
            FsmColor color = v as FsmColor; if (color != null) return builder.Q(new Q4(color.Value.r, color.Value.g, color.Value.b, color.Value.a));
            FsmGameObject g = v as FsmGameObject; if (g != null) return Reference(g.Value);
            FsmObject o = v as FsmObject; if (o != null) return Reference(o.Value);
            FsmMaterial m = v as FsmMaterial; if (m != null) return Reference(m.Value);
            return null;
        }
        private void WriteVariable(NamedVariable v, Atom a)
        {
            if (v is FsmFloat && a.Kind == AtomKind.Number) ((FsmFloat)v).Value = (float)a.Number;
            else if (v is FsmInt && a.Kind == AtomKind.Number) ((FsmInt)v).Value = checked((int)a.Number);
            else if (v is FsmBool && a.Kind == AtomKind.Boolean) ((FsmBool)v).Value = a.Number != 0;
            else if (v is FsmString && a.Kind == AtomKind.Text) ((FsmString)v).Value = a.Text;
            else if (v is FsmVector3 && a.Kind == AtomKind.Vector) ((FsmVector3)v).Value = To(a.Vector);
            else if (v is FsmQuaternion && a.Kind == AtomKind.Rotation) ((FsmQuaternion)v).Value = To(a.Rotation);
            else if (v is FsmColor && a.Kind == AtomKind.Rotation) ((FsmColor)v).Value = new Color(a.Rotation.X, a.Rotation.Y, a.Rotation.Z, a.Rotation.W);
            else if (a.Kind == AtomKind.Reference)
            {
                UnityEngine.Object value = Resolve(a.Text);
                if (a.Text != "" && value == null) return; // Preserve local native-only references, never guess.
                if (v is FsmGameObject) ((FsmGameObject)v).Value = value as GameObject;
                else if (v is FsmObject) ((FsmObject)v).Value = value;
                else if (v is FsmMaterial) ((FsmMaterial)v).Value = value as Material;
            }
        }
        private readonly List<ObjectBinding> captureObjects = new List<ObjectBinding>();
        private readonly List<FsmBinding> captureFsms = new List<FsmBinding>();
        private readonly List<ObjectBinding> motionObjects = new List<ObjectBinding>();
        private int objectCursor, fsmCursor, enforceCursor;
        private float globalCaptureTime;
        private bool capturedChanges;
        public int LastObjectVisits { get; private set; }
        public int LastFsmVisits { get; private set; }
        private readonly SnapshotBuilder builder = new SnapshotBuilder();
        private VariableView globals;
        private readonly Dictionary<string, string> variableKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        private string VariableKey(string name) { string key; if (variableKeys.TryGetValue(name, out key)) return key; key = "v/" + name; if (variableKeys.Count < 16384) variableKeys[name] = key; return key; }
        private VariableView GlobalVariables() { if (globals == null) globals = new VariableView(FsmVariables.GlobalVariables); else globals.Refresh(FsmVariables.GlobalVariables); return globals; }
        public bool ConsumeChanges() { bool value = capturedChanges; capturedChanges = false; return value; }
        private void ReplaceCaptured(World world, Entity entity)
        {
            if (builder.Publish(world)) capturedChanges = true;
        }
        private void RemoveCaptured(World world, string id) { if (world.Entities.Remove(id)) { world.EntityChanged(id); capturedChanges = true; } }
        public void Capture(World world, bool transforms)
        {
            foreach (ObjectBinding b in captureObjects) { if (transforms) b.NextMeshCapture = 0; CaptureObject(world, b, transforms); }
            foreach (FsmBinding b in captureFsms) CaptureFsm(world, b, transforms);
            CaptureGlobals(world);
        }
        public void CaptureIncremental(World world)
        {
            LastObjectVisits = LastFsmVisits = 0;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            long objectBudget = System.Diagnostics.Stopwatch.Frequency / 500;
            for (int n = 0; n < 256 && n < captureObjects.Count; n++)
            {
                if (objectCursor >= captureObjects.Count) objectCursor = 0;
                CaptureObject(world, captureObjects[objectCursor++], true);
                LastObjectVisits++;
                if (System.Diagnostics.Stopwatch.GetTimestamp() - start >= objectBudget) break;
            }
            long totalBudget = System.Diagnostics.Stopwatch.Frequency * 3 / 1000;
            for (int n = 0; n < 128 && n < captureFsms.Count; n++)
            {
                if (fsmCursor >= captureFsms.Count) fsmCursor = 0;
                CaptureFsm(world, captureFsms[fsmCursor++], true);
                LastFsmVisits++;
                if (System.Diagnostics.Stopwatch.GetTimestamp() - start >= totalBudget) break;
            }
            if (Time.realtimeSinceStartup - globalCaptureTime >= 0.2f) { CaptureGlobals(world); globalCaptureTime = Time.realtimeSinceStartup; }
        }
        private void CaptureObject(World world, ObjectBinding b, bool transforms)
        {
                if (b.Object == null) { RemoveCaptured(world, b.Id); return; }
                b.Body = b.Object.GetComponent<Rigidbody>();
                if (b.Body != null && !b.Motion) { b.Motion = true; motionObjects.Add(b); }
                Entity previous; world.Entities.TryGetValue(b.Id, out previous);
                Entity e = builder.Begin(b.Id, b.Key, b.Kind, previous == null ? 32 : previous.Properties.Count); e.Prefab = b.Prefab; e.Active = b.Object.activeSelf; e.Parent = IdOf(b.Transform.parent == null ? null : b.Transform.parent.gameObject);
                e.Position = transforms || previous == null ? From(b.Transform.position) : previous.Position;
                e.Rotation = transforms || previous == null ? From(b.Transform.rotation) : previous.Rotation;
                if (b.Body != null) { e.Velocity = transforms || previous == null ? From(b.Body.velocity) : previous.Velocity; e.AngularVelocity = transforms || previous == null ? From(b.Body.angularVelocity) : previous.AngularVelocity; e.Properties["@body"] = builder.B(true); e.Properties["@mass"] = builder.N(b.Body.mass); e.Properties["@kinematic"] = builder.B(b.Body.isKinematic); }
                e.Properties["@scale"] = builder.V(From(b.Transform.localScale));
                e.Properties["@name"] = builder.S(b.Object.name);
                e.Properties["@empty"] = builder.B(b.Empty);
                Atom oldTag; string tag = previous != null && previous.Properties.TryGetValue("@tag", out oldTag) && b.Object.CompareTag(oldTag.Text) ? oldTag.Text : b.Object.tag;
                e.Properties["@tag"] = builder.S(tag);
                e.Properties["@loose"] = builder.B(b.Body != null && (tag == "PART" || tag == "ITEM" || tag == "TOOLS"));
                for (int i = 0; i < b.Renderers.Length; i++) if (b.Renderers[i] != null)
                {
                    e.Properties[Property("@render/", i, "")] = builder.B(b.Renderers[i].enabled);
                    Material[] materials = b.Renderers[i].sharedMaterials;
                    for (int k = 0; k < materials.Length; k++) if (materials[k] != null)
                    {
                        e.Properties[Property("@material/", i, k)] = Reference(materials[k]);
                        if (materials[k].HasProperty("_Color")) { Color color = materials[k].color; e.Properties[Property("@color/", i, k)] = builder.Q(new Q4(color.r, color.g, color.b, color.a)); }
                    }
                }
                for (int i = 0; i < b.Colliders.Length; i++) if (b.Colliders[i] != null) e.Properties[Property("@collider/", i, "")] = builder.B(b.Colliders[i].enabled);
                for (int i = 0; i < b.Arrays.Length; i++)
                {
                    PlayMakerArrayListProxy list = b.Arrays[i]; if (list == null || list.arrayList == null || list.arrayList.Count > 2048) continue;
                    e.Properties[Property("@array/", i, "/count")] = builder.N(list.arrayList.Count);
                    for (int k = 0; k < list.arrayList.Count; k++) { Atom value = ReadArrayValue(list.arrayList[k]); if (value != null) e.Properties[Property("@array/", i, k)] = value; }
                }
                Drivetrain drive = b.Object.GetComponent<Drivetrain>(); if (drive != null) { e.Properties["@rpm"] = builder.N(drive.rpm); e.Properties["@gear"] = builder.N(drive.gear); }
                for (int i = 0; i < b.Lights.Length; i++) if (b.Lights[i] != null)
                {
                    Light light = b.Lights[i]; e.Properties[Property("@light/", i, "/on")] = builder.B(light.enabled); e.Properties[Property("@light/", i, "/intensity")] = builder.N(light.intensity); e.Properties[Property("@light/", i, "/color")] = builder.Q(new Q4(light.color.r, light.color.g, light.color.b, light.color.a));
                }
                for (int i = 0; i < b.Audio.Length; i++) if (b.Audio[i] != null)
                {
                    AudioSource audio = b.Audio[i];
                    e.Properties[Property("@audio/", i, "/clip")] = Reference(audio.clip); e.Properties[Property("@audio/", i, "/playing")] = builder.B(audio.isPlaying); e.Properties[Property("@audio/", i, "/loop")] = builder.B(audio.loop); e.Properties[Property("@audio/", i, "/volume")] = builder.N(audio.volume); e.Properties[Property("@audio/", i, "/pitch")] = builder.N(audio.pitch);
                    Atom oldTime; e.Properties[Property("@audio/", i, "/time")] = !transforms && previous != null && previous.Properties.TryGetValue(Property("@audio/", i, "/time"), out oldTime) ? oldTime : builder.N(audio.time);
                }
                bool meshRefresh = transforms && Time.realtimeSinceStartup >= b.NextMeshCapture;
                if (meshRefresh) b.NextMeshCapture = Time.realtimeSinceStartup + 5;
                CaptureMesh(b, e, previous, meshRefresh);
                ReplaceCaptured(world, e);
        }
        private void CaptureFsm(World world, FsmBinding b, bool transforms)
        {
                if (b.Component == null) { RemoveCaptured(world, b.Id); return; }
                b.Variables.Refresh(b.Component.FsmVariables);
                Entity old; world.Entities.TryGetValue(b.Id, out old);
                Entity e = builder.Begin(b.Id, b.Key, EntityKind.Fsm, old == null ? 32 : old.Properties.Count); e.Parent = b.ObjectId; e.Active = b.Component.enabled; e.Position = transforms || old == null ? From(b.Component.transform.position) : old.Position;
                e.Properties["@state"] = builder.S(b.Component.ActiveStateName);
                if (!b.Personal) foreach (NamedVariable v in b.Variables) { if (PersonalVariable(v.Name)) continue; Atom a = ReadVariable(v); if (a != null) e.Properties[VariableKey(v.Name)] = a; }
                ReplaceCaptured(world, e);
        }
        private void CaptureGlobals(World world)
        {
            Entity previous; world.Entities.TryGetValue(GlobalId, out previous);
            Entity global = builder.Begin(GlobalId, "globals", EntityKind.Globals, previous == null ? 256 : previous.Properties.Count);
            foreach (NamedVariable v in GlobalVariables()) if (!PersonalVariable(v.Name)) { Atom a = ReadVariable(v); if (a != null) global.Properties[VariableKey(v.Name)] = a; }
            global.Properties["@ambient"] = builder.Q(new Q4(RenderSettings.ambientLight.r, RenderSettings.ambientLight.g, RenderSettings.ambientLight.b, RenderSettings.ambientLight.a));
            global.Properties["@fog"] = builder.Q(new Q4(RenderSettings.fogColor.r, RenderSettings.fogColor.g, RenderSettings.fogColor.b, RenderSettings.fogColor.a)); global.Properties["@fogDensity"] = builder.N(RenderSettings.fogDensity);
            FsmFloat money = FsmVariables.GlobalVariables.FindFsmFloat("PlayerMoney"); if (money != null) world.Money = money.Value;
            ReplaceCaptured(world, global);
        }
        public void CaptureRelated(World world, string target, string held)
        {
            HashSet<string> relevant = new HashSet<string>(StringComparer.Ordinal);
            FsmBinding binding = FsmOf(target);
            AddRelated(relevant, ObjectOf(binding == null ? target : binding.ObjectId), true);
            AddRelated(relevant, ObjectOf(held), true);
            if (binding != null)
                foreach (NamedVariable variable in new VariableView(binding.Component.FsmVariables))
                {
                    FsmGameObject go = variable as FsmGameObject;
                    if (go != null && go.Value != null) AddRelated(relevant, go.Value, false);
                }
            foreach (string id in relevant) { ObjectBinding b; if (Objects.TryGetValue(id, out b)) CaptureObject(world, b, true); }
            foreach (string id in relevant) { List<FsmBinding> owned; if (fsmsByObject.TryGetValue(id, out owned)) foreach (FsmBinding b in owned) CaptureFsm(world, b, true); }
            CaptureGlobals(world);
        }
        private void AddRelated(HashSet<string> ids, GameObject go, bool descendants)
        {
            if (go == null) return;
            if (descendants)
                foreach (Transform t in go.GetComponentsInChildren<Transform>(true)) { string id = IdOf(t.gameObject); if (id != "") ids.Add(id); }
            else { string id = IdOf(go); if (id != "") ids.Add(id); }
            for (Transform t = go.transform.parent; t != null; t = t.parent) { string id = IdOf(t.gameObject); if (id != "") ids.Add(id); }
        }
        private Atom ReadArrayValue(object value)
        {
            if (value == null) return builder.R("");
            if (value is bool) return builder.B((bool)value);
            if (value is string) return builder.S((string)value);
            if (value is int || value is float || value is double) { double n = Convert.ToDouble(value, CultureInfo.InvariantCulture); return double.IsNaN(n) || double.IsInfinity(n) ? null : builder.N(n); }
            if (value is Vector3) return builder.V(From((Vector3)value));
            if (value is Quaternion) return builder.Q(From((Quaternion)value));
            return value is UnityEngine.Object ? Reference((UnityEngine.Object)value) : null;
        }
        private void CaptureMesh(ObjectBinding binding, Entity entity, Entity previous, bool refresh)
        {
            if (binding.Mesh == null || !(binding.Key.StartsWith("part/", StringComparison.Ordinal) || binding.Key.IndexOf("SATSUMA", StringComparison.Ordinal) >= 0)) return;
            if (!refresh && previous != null) { foreach (KeyValuePair<string, Atom> p in previous.Properties) if (p.Key.StartsWith("@mesh/", StringComparison.Ordinal)) entity.Properties[p.Key] = p.Value; return; }
            Mesh mesh = binding.Mesh.sharedMesh; if (mesh == null || !mesh.isReadable || mesh.vertexCount > 20000) return;
            Vector3[] vertices = mesh.vertices;
            bool same = binding.CapturedMesh == mesh && binding.CapturedVertices != null && binding.CapturedVertices.Length == vertices.Length && previous != null && previous.Properties.ContainsKey("@mesh/hash");
            if (same) for (int i = 0; i < vertices.Length; i++) if (vertices[i].x != binding.CapturedVertices[i].x || vertices[i].y != binding.CapturedVertices[i].y || vertices[i].z != binding.CapturedVertices[i].z) { same = false; break; }
            if (same) { foreach (KeyValuePair<string, Atom> p in previous.Properties) if (p.Key.StartsWith("@mesh/", StringComparison.Ordinal)) entity.Properties[p.Key] = p.Value; return; }
            binding.CapturedMesh = mesh; binding.CapturedVertices = vertices;
            byte[] bytes = Codec.Make(delegate(BinaryWriter w) { w.Write(vertices.Length); foreach (Vector3 vertex in vertices) Codec.Vector(w, From(vertex)); });
            string packed = Convert.ToBase64String(bytes); int chunks = (packed.Length + 11999) / 12000;
            entity.Properties["@mesh/count"] = builder.N(chunks); entity.Properties["@mesh/hash"] = builder.S(Codec.Hash(bytes));
            for (int i = 0; i < chunks; i++) entity.Properties["@mesh/" + i] = builder.S(packed.Substring(i * 12000, Math.Min(12000, packed.Length - i * 12000)));
        }
        private readonly Dictionary<string, string> meshHashes = new Dictionary<string, string>();
        private void ApplyMesh(ObjectBinding binding, Entity entity)
        {
            Atom count, hash; string old;
            if (binding.Mesh == null || !entity.Properties.TryGetValue("@mesh/count", out count) || !entity.Properties.TryGetValue("@mesh/hash", out hash) || meshHashes.TryGetValue(entity.Id, out old) && old == hash.Text) return;
            int n = checked((int)count.Number); if (n < 1 || n > 100) throw new ProtocolException("Invalid mesh chunk count");
            System.Text.StringBuilder packed = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++) { Atom chunk; if (!entity.Properties.TryGetValue("@mesh/" + i, out chunk)) throw new ProtocolException("Missing mesh chunk"); packed.Append(chunk.Text); }
            byte[] bytes = Convert.FromBase64String(packed.ToString()); if (Codec.Hash(bytes) != hash.Text) throw new ProtocolException("Mesh hash mismatch");
            Vector3[] vertices = null;
            Codec.Read(bytes, delegate(BinaryReader r) { int number = Codec.Count(r, 20000); vertices = new Vector3[number]; for (int i = 0; i < number; i++) vertices[i] = To(Codec.Vector(r)); });
            Mesh mesh = binding.Mesh.mesh; if (mesh.vertexCount != vertices.Length) throw new ProtocolException("Native mesh schema mismatch");
            mesh.vertices = vertices; mesh.RecalculateBounds(); mesh.RecalculateNormals(); meshHashes[entity.Id] = hash.Text;
        }
        private object ReadAtom(Atom a, object previous)
        {
            switch (a.Kind)
            {
                case AtomKind.Number: return previous is int ? (object)checked((int)a.Number) : (float)a.Number;
                case AtomKind.Boolean: return a.Number != 0;
                case AtomKind.Text: return a.Text;
                case AtomKind.Reference: return Resolve(a.Text);
                case AtomKind.Vector: return To(a.Vector);
                case AtomKind.Rotation: return To(a.Rotation);
                default: throw new ProtocolException("Unsupported native array value");
            }
        }
        public void Apply(World world, bool full)
        {
            Applying = true;
            try
            {
                foreach (Entity e in world.Entities.Values) if (e.Prefab != "" && !Objects.ContainsKey(e.Id)) Spawn(e);
                // Native loading removes unused random spawn containers. They contain only a Transform.
                foreach (Entity e in world.Entities.Values)
                {
                    Atom empty;
                    if (Objects.ContainsKey(e.Id) || e.Kind == EntityKind.Fsm || e.Kind == EntityKind.Globals || !e.Properties.TryGetValue("@empty", out empty) || empty.Number == 0) continue;
                    string name = e.Key.Substring(e.Key.LastIndexOf('/') + 1); int bracket = name.LastIndexOf('['); if (bracket >= 0) name = name.Substring(0, bracket);
                    Add(new GameObject(name), e.Key);
                }
                List<Entity> ordered = new List<Entity>(world.Entities.Values);
                Dictionary<string, int> depths = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (Entity e in ordered) Depth(e, world, depths, new HashSet<string>(StringComparer.Ordinal));
                ordered.Sort(delegate(Entity a, Entity b) { return depths[a.Id].CompareTo(depths[b.Id]); });
                foreach (Entity e in ordered)
                {
                    ObjectBinding b;
                    if (!Objects.TryGetValue(e.Id, out b) || b.Object == null) continue;
                    if (e.Key == "__deleted") { UnityEngine.Object.Destroy(b.Object); continue; }
                    Transform parent = e.Parent == "" ? null : ObjectOf(e.Parent) == null ? null : ObjectOf(e.Parent).transform;
                    if (e.Parent != "" && parent == null) { Entity missing; throw new ProtocolException("Missing native parent for " + e.Key + ": " + (world.Entities.TryGetValue(e.Parent, out missing) ? missing.Key : e.Parent)); }
                    if (b.Transform.parent != parent) { b.Transform.parent = parent; motionOrderDirty = true; }
                    if (full || !motionTargets.ContainsKey(e.Id)) { b.Transform.position = To(e.Position); b.Transform.rotation = To(e.Rotation); }
                    Atom a; if (e.Properties.TryGetValue("@scale", out a)) b.Transform.localScale = To(a.Vector);
                    if (e.Properties.TryGetValue("@name", out a) && b.Object.name != a.Text) b.Object.name = a.Text;
                    if (e.Properties.TryGetValue("@tag", out a)) b.Object.tag = a.Text;
                    if (b.Object.activeSelf != e.Active) b.Object.SetActive(e.Active);
                    for (int i = 0; i < b.Renderers.Length; i++) if (b.Renderers[i] != null)
                    {
                        if (e.Properties.TryGetValue(Property("@render/", i, ""), out a)) b.Renderers[i].enabled = a.Number != 0;
                        Material[] materials = b.Renderers[i].sharedMaterials; bool changed = false;
                        for (int k = 0; k < materials.Length; k++)
                        {
                            if (e.Properties.TryGetValue(Property("@material/", i, k), out a)) { Material m = Resolve(a.Text) as Material; if (m != null && materials[k] != m) { materials[k] = m; changed = true; } }
                        }
                        if (changed) b.Renderers[i].sharedMaterials = materials;
                        for (int k = 0; k < materials.Length; k++) if (e.Properties.TryGetValue(Property("@color/", i, k), out a) && materials[k] != null && materials[k].HasProperty("_Color"))
                        {
                            Color color = new Color(a.Rotation.X, a.Rotation.Y, a.Rotation.Z, a.Rotation.W);
                            if (materials[k].color != color) b.Renderers[i].materials[k].color = color;
                        }
                    }
                    for (int i = 0; i < b.Colliders.Length; i++) if (b.Colliders[i] != null && e.Properties.TryGetValue(Property("@collider/", i, ""), out a)) b.Colliders[i].enabled = a.Number != 0;
                    for (int i = 0; i < b.Arrays.Length; i++)
                    {
                        if (b.Arrays[i] == null || b.Arrays[i].arrayList == null) continue;
                        if (!e.Properties.TryGetValue(Property("@array/", i, "/count"), out a)) continue;
                        int count = checked((int)a.Number); if (count < 0 || count > 2048) throw new ProtocolException("Array size out of range");
                        ArrayList list = b.Arrays[i].arrayList; while (list.Count > count) list.RemoveAt(list.Count - 1); while (list.Count < count) list.Add(null);
                        for (int k = 0; k < count; k++) if (e.Properties.TryGetValue(Property("@array/", i, k), out a)) list[k] = ReadAtom(a, list[k]);
                    }
                    b.Body = b.Object.GetComponent<Rigidbody>();
                    bool wantsBody = e.Properties.ContainsKey("@body");
                    if (host && wantsBody && b.Body == null) b.Body = b.Object.AddComponent<Rigidbody>();
                    if (!wantsBody && b.Body != null) { if (b.Object.GetComponent<Joint>() == null) { UnityEngine.Object.DestroyImmediate(b.Body); b.Body = null; } else b.Body.isKinematic = true; }
                    if (host && b.Body != null)
                    {
                        if (e.Properties.TryGetValue("@mass", out a)) b.Body.mass = (float)a.Number;
                        if (e.Properties.TryGetValue("@kinematic", out a)) b.Body.isKinematic = a.Number != 0;
                        b.Body.position = To(e.Position); b.Body.rotation = To(e.Rotation);
                        if (!b.Body.isKinematic) { b.Body.velocity = To(e.Velocity); b.Body.angularVelocity = To(e.AngularVelocity); }
                    }
                    Drivetrain drive = b.Object.GetComponent<Drivetrain>(); if (drive != null) { if (e.Properties.TryGetValue("@rpm", out a)) drive.rpm = (float)a.Number; if (e.Properties.TryGetValue("@gear", out a)) drive.gear = checked((int)a.Number); }
                    for (int i = 0; i < b.Lights.Length; i++) if (b.Lights[i] != null)
                    {
                        Light light = b.Lights[i];
                        if (e.Properties.TryGetValue(Property("@light/", i, "/on"), out a)) light.enabled = a.Number != 0;
                        if (e.Properties.TryGetValue(Property("@light/", i, "/intensity"), out a)) light.intensity = (float)a.Number;
                        if (e.Properties.TryGetValue(Property("@light/", i, "/color"), out a)) light.color = new Color(a.Rotation.X, a.Rotation.Y, a.Rotation.Z, a.Rotation.W);
                    }
                    if (!host) for (int i = 0; i < b.Audio.Length; i++) if (b.Audio[i] != null)
                    {
                        AudioSource audio = b.Audio[i];
                        if (e.Properties.TryGetValue(Property("@audio/", i, "/clip"), out a)) audio.clip = Resolve(a.Text) as AudioClip;
                        if (e.Properties.TryGetValue(Property("@audio/", i, "/loop"), out a)) audio.loop = a.Number != 0;
                        if (e.Properties.TryGetValue(Property("@audio/", i, "/volume"), out a)) audio.volume = (float)a.Number;
                        if (e.Properties.TryGetValue(Property("@audio/", i, "/pitch"), out a)) audio.pitch = (float)a.Number;
                        if (e.Properties.TryGetValue(Property("@audio/", i, "/playing"), out a)) { if (a.Number != 0 && !audio.isPlaying && audio.clip != null) audio.Play(); if (a.Number == 0 && audio.isPlaying) audio.Stop(); }
                        if (audio.clip != null && e.Properties.TryGetValue(Property("@audio/", i, "/time"), out a) && Math.Abs(audio.time - a.Number) > 0.5 && a.Number >= 0 && a.Number < audio.clip.length) audio.time = (float)a.Number;
                    }
                    ApplyMesh(b, e);
                    if (!host) DisableClientPhysics(b);
                }
                foreach (Entity e in world.Entities.Values)
                {
                    if (e.Kind == EntityKind.Globals)
                    {
                        foreach (NamedVariable v in GlobalVariables()) { Atom a; if (!PersonalVariable(v.Name) && e.Properties.TryGetValue(VariableKey(v.Name), out a)) WriteVariable(v, a); }
                        Atom setting; if (e.Properties.TryGetValue("@ambient", out setting)) RenderSettings.ambientLight = new Color(setting.Rotation.X, setting.Rotation.Y, setting.Rotation.Z, setting.Rotation.W);
                        if (e.Properties.TryGetValue("@fog", out setting)) RenderSettings.fogColor = new Color(setting.Rotation.X, setting.Rotation.Y, setting.Rotation.Z, setting.Rotation.W);
                        if (e.Properties.TryGetValue("@fogDensity", out setting)) RenderSettings.fogDensity = (float)setting.Number;
                        continue;
                    }
                    FsmBinding b; if (!Fsms.TryGetValue(e.Id, out b) || b.Component == null) continue;
                    if (b.Personal) continue;
                    if (b.Component.enabled != e.Active) b.Component.enabled = e.Active;
                    b.Variables.Refresh(b.Component.FsmVariables);
                    foreach (NamedVariable v in b.Variables) { Atom a; if (!PersonalVariable(v.Name) && e.Properties.TryGetValue(VariableKey(v.Name), out a)) WriteVariable(v, a); }
                    // State assignment does not execute purchases, spawning, or save actions on a client.
                    Atom state; if (!host && b.Requests.Count == 0 && e.Properties.TryGetValue("@state", out state)) { FsmState s = b.Component.Fsm.GetState(state.Text); if (s != null) AssignState(b.Component.Fsm, s); }
                }
                FsmFloat money = FsmVariables.GlobalVariables.FindFsmFloat("PlayerMoney"); if (money != null) money.Value = (float)world.Money;
                if (!host && full) DisableClientPhysics();
            }
            finally { Applying = false; }
        }
        private int Depth(Entity entity, World world, Dictionary<string, int> depths, HashSet<string> visiting)
        {
            int depth;
            if (depths.TryGetValue(entity.Id, out depth)) return depth;
            if (!visiting.Add(entity.Id) || visiting.Count > 256) throw new ProtocolException("Invalid transform hierarchy");
            Entity parent;
            if (entity.Parent == "") depth = 0;
            else if (world.Entities.TryGetValue(entity.Parent, out parent)) depth = Depth(parent, world, depths, visiting) + 1;
            else
            {
                GameObject go = ObjectOf(entity.Parent); depth = 1;
                if (go != null) for (Transform t = go.transform; t.parent != null; t = t.parent) depth++;
            }
            visiting.Remove(entity.Id); depths[entity.Id] = depth; return depth;
        }
        public void EnforceVariables(World world)
        {
            // PlayMaker templates can recreate variables in Start after a snapshot activated bolts.
            Applying = true;
            try
            {
                long began = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int n = 0; n < 64 && n < captureFsms.Count; n++)
                {
                    if (System.Diagnostics.Stopwatch.GetTimestamp() - began >= System.Diagnostics.Stopwatch.Frequency / 1000) break;
                    if (enforceCursor >= captureFsms.Count) enforceCursor = 0;
                    FsmBinding b = captureFsms[enforceCursor++];
                    Entity e;
                    if (b.Personal || b.Component == null || !world.Entities.TryGetValue(b.Id, out e)) continue;
                    b.Variables.Refresh(b.Component.FsmVariables);
                    foreach (NamedVariable v in b.Variables) { Atom a; if (!PersonalVariable(v.Name) && e.Properties.TryGetValue(VariableKey(v.Name), out a)) WriteVariable(v, a); }
                }
            }
            finally { Applying = false; }
        }
        private bool motionOrderDirty;
        private List<string> motionOrder = new List<string>();
        private readonly List<string> completedMotion = new List<string>();
        private void Spawn(Entity e)
        {
            GameObject prefab; if (!prefabs.TryGetValue(e.Prefab, out prefab) || prefab == null) throw new ProtocolException("Unknown or ambiguous native prefab: " + e.Prefab);
            GameObject go = (GameObject)UnityEngine.Object.Instantiate(prefab, To(e.Position), To(e.Rotation));
            Atom name; if (e.Properties.TryGetValue("@name", out name)) go.name = name.Text;
            go.SetActive(false); Walk(go.transform, true, e.Key); Objects[e.Id].Prefab = e.Prefab;
            // Child object IDs are derived from the host-assigned spawn root, including after reparenting.
            foreach (PlayMakerFSM f in go.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (!f.Fsm.Initialized) f.Fsm.Init(f);
                string owner = IdOf(f.gameObject); int ordinal = Array.IndexOf(f.GetComponents<PlayMakerFSM>(), f);
                string key = Objects[owner].Key + "|fsm/" + f.FsmName + "/" + ordinal.ToString(CultureInfo.InvariantCulture);
                FsmBinding b = new FsmBinding { Id = Identity.Stable(key), Key = key, ObjectId = owner, Component = f, Variables = new VariableView(f.FsmVariables) };
                b.Personal = IsPersonalFsm(f);
                DescribeRequests(b); RegisterFsm(b);
            }
        }
        private void DisableClientPhysics()
        {
            foreach (ObjectBinding b in Objects.Values) DisableClientPhysics(b);
        }
        private static void DisableClientPhysics(ObjectBinding b)
        {
            if (b.Object == null) return;
            b.Body = b.Object.GetComponent<Rigidbody>(); if (b.Body != null && !b.Body.isKinematic) b.Body.isKinematic = true;
            if (!b.PhysicsDisabled || b.DisabledBody != b.Body)
            {
                foreach (MonoBehaviour component in b.Object.GetComponents<MonoBehaviour>())
                {
                    string type = component.GetType().Name;
                    if (type == "CarController" || type == "AxisCarController" || type == "Drivetrain" || type == "CarDynamics" || type == "Wheel") component.enabled = false;
                }
                b.PhysicsDisabled = true; b.DisabledBody = b.Body;
            }
        }
        public List<MotionFrame> CaptureMotion()
        {
            List<MotionFrame> frames = new List<MotionFrame>(); MotionFrame frame = null;
            foreach (ObjectBinding b in motionObjects)
            {
                if (b.Object == null || !b.Object.activeInHierarchy) continue;
                BodyPose last; bool seen = motionPrevious.TryGetValue(b.Id, out last);
                V3 position = From(b.Transform.position), velocity = b.Body == null ? new V3() : From(b.Body.velocity);
                Q4 rotation = From(b.Transform.rotation);
                if (seen && position.Distance(last.Position) < 0.001f && Math.Abs(Quaternion.Dot(To(rotation), To(last.Rotation))) > 0.999999f && velocity.Distance(last.Velocity) < 0.01f) continue;
                BodyPose pose = new BodyPose { Id = b.Id, Position = position, Rotation = rotation, Velocity = velocity }; motionPrevious[b.Id] = pose;
                if (!seen && b.Body == null) continue;
                if (frame == null || frame.Bodies.Count == 13) { frame = new MotionFrame { Sequence = ++motionSequence }; frames.Add(frame); }
                frame.Bodies.Add(pose);
            }
            return frames;
        }
        public void ReceiveMotion(MotionFrame frame)
        {
            foreach (BodyPose body in frame.Bodies)
            {
                long last; if (!Objects.ContainsKey(body.Id) || (motionSequences.TryGetValue(body.Id, out last) && frame.Sequence <= last)) continue;
                if (!motionTargets.ContainsKey(body.Id)) motionOrderDirty = true;
                motionSequences[body.Id] = frame.Sequence; motionTargets[body.Id] = body;
            }
        }
        public void Interpolate(float dt)
        {
            float blend = 1 - (float)Math.Exp(-dt * 20);
            completedMotion.Clear();
            if (motionOrderDirty) { motionOrder = new List<string>(motionTargets.Keys); Dictionary<string, int> depths = new Dictionary<string, int>(); foreach (string id in motionOrder) depths[id] = NativeDepth(ObjectOf(id)); motionOrder.Sort(delegate(string a, string b) { return depths[a].CompareTo(depths[b]); }); motionOrderDirty = false; }
            foreach (string id in motionOrder)
            {
                BodyPose pose = motionTargets[id];
                GameObject go = ObjectOf(pose.Id); if (go == null) { completedMotion.Add(id); continue; } Vector3 target = To(pose.Position);
                go.transform.position = Vector3.Distance(go.transform.position, target) > 3 ? target : Vector3.Lerp(go.transform.position, target, blend);
                go.transform.rotation = Quaternion.Slerp(go.transform.rotation, To(pose.Rotation), blend);
                if ((go.transform.position - target).sqrMagnitude < 0.00000001f && Math.Abs(Quaternion.Dot(go.transform.rotation, To(pose.Rotation))) > 0.9999999f) { go.transform.position = target; go.transform.rotation = To(pose.Rotation); completedMotion.Add(id); }
            }
            if (completedMotion.Count > 0) { foreach (string id in completedMotion) motionTargets.Remove(id); motionOrder.RemoveAll(delegate(string id) { return !motionTargets.ContainsKey(id); }); }
        }
        private static int NativeDepth(GameObject go) { int depth = 0; if (go != null) for (Transform t = go.transform; t.parent != null; t = t.parent) depth++; return depth; }
        public static void DescribeRequests(FsmBinding b)
        {
            foreach (FsmState s in b.Component.FsmStates)
            {
                bool input = false;
                foreach (FsmStateAction a in s.Actions)
                {
                    string type = a.GetType().Name;
                    if (type == "GetMouseButtonDown" || type == "GetMouseButtonUp" || type == "GetButtonDown" || type == "GetButtonUp" || type == "GetKeyDown" || type == "GetAxis" || type == "MousePickEvent") input = true;
                }
                if (!input) continue;
                if (b.WaitingState == "") b.WaitingState = s.Name;
                foreach (FsmTransition t in s.Transitions) if (t.EventName != "FINISHED" && t.EventName != "LOOP" && t.EventName != "RESET" && t.EventName != "STOP") b.Requests.Add(t.ToState);
            }
            string name = b.Component.FsmName;
            if (name == "Screw") { if (b.Component.Fsm.GetState("Screw 2") != null) b.Requests.Add("Screw 2"); if (b.Component.Fsm.GetState("Unscrew 2") != null) b.Requests.Add("Unscrew 2"); }
            if (name == "Assembly" && b.Component.Fsm.GetState("Assemble") != null) b.Requests.Add("Assemble");
            if (name == "Assemble" && b.Component.transform.root.name.StartsWith("SATSUMA", StringComparison.Ordinal)) b.Requests.Add("Sound");
        }
        public static V3 From(Vector3 v) { return new V3(v.x, v.y, v.z); }
        private static readonly FieldInfo ActiveStateField = typeof(Fsm).GetField("activeState", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ActiveStateNameField = typeof(Fsm).GetField("activeStateName", BindingFlags.Instance | BindingFlags.NonPublic);
        public static void AssignState(Fsm fsm, FsmState state) { ActiveStateField.SetValue(fsm, state); ActiveStateNameField.SetValue(fsm, state == null ? "" : state.Name); }
        public static Q4 From(Quaternion q) { return new Q4(q.x, q.y, q.z, q.w); }
        public static Vector3 To(V3 v) { return new Vector3(v.X, v.Y, v.Z); }
        public static Quaternion To(Q4 q) { Quaternion r = new Quaternion(q.X, q.Y, q.Z, q.W); return r == new Quaternion(0, 0, 0, 0) ? Quaternion.identity : r; }
    }
}
