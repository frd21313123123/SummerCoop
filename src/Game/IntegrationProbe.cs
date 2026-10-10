using System;
using System.IO;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using SummerCoop.Core;
using UnityEngine;

namespace SummerCoop.Game
{
    // Opt-in diagnostics in an isolated test copy, never enabled in a normal session.
    public sealed class IntegrationProbe : MonoBehaviour
    {
        private float began;
        private float joined;
        private int step;
        private float sentAt;
        private string partId, assemblyId, boltId;
        private long sequence;
        private CommandResult result;
        private int boltBefore;
        private string outcome = "NOT_REQUESTED";
        private Action<CommandResult> previous;
        private readonly List<double> updateTimes = new List<double>();
        private readonly List<double> frameTimes = new List<double>();
        private readonly List<double>[] stages = { new List<double>(), new List<double>(), new List<double>(), new List<double>(), new List<double>(), new List<double>(), new List<double>(), new List<double>(), new List<double>(), new List<double>() };
        private double frameSeconds;
        private int frames;
        private string meshOutcome = "NOT_REQUESTED", meshId, previousMeshHash;
        private Vector3 meshExpected;
        private float meshRequested;
        private readonly List<string> outliers = new List<string>();
        private int previousCollections, measuredCollections, collectionFrames;
        private long previousHeap;
        private int objectVisits, fsmVisits;
        private void Start() { began = Time.realtimeSinceStartup; int limit; if (int.TryParse(CoopRuntime.Argument("--summercoop-test-fps-limit"), out limit) && limit > 0) Application.targetFrameRate = limit; }
        private void Update()
        {
            CoopRuntime r = CoopRuntime.Instance;
            if (r == null || !r.Active) return;
            bool pair = CoopRuntime.Argument("--summercoop-test-pair") == "yes";
            if (pair && (r.Session.World.Players.Count < 2 || r.Joining)) { joined = 0; frames = 0; frameSeconds = 0; updateTimes.Clear(); frameTimes.Clear(); foreach (List<double> values in stages) values.Clear(); return; }
            if (joined == 0) joined = Time.realtimeSinceStartup;
            if (CoopRuntime.Argument("--summercoop-test-mesh-proof") != "" && Time.realtimeSinceStartup - joined > 12) CheckMesh(r);
            int collections = GC.CollectionCount(0); long heap = GC.GetTotalMemory(false);
            if (Time.realtimeSinceStartup - joined > 5)
            {
                updateTimes.Add(r.LastUpdateMilliseconds); frameTimes.Add(Time.unscaledDeltaTime * 1000.0); for (int i = 0; i < stages.Length; i++) stages[i].Add(r.LastStages[i]); frameSeconds += Time.unscaledDeltaTime; frames++;
                if (collections > previousCollections) { measuredCollections += collections - previousCollections; collectionFrames++; }
                objectVisits += r.Registry.LastObjectVisits; fsmVisits += r.Registry.LastFsmVisits;
                if ((Time.unscaledDeltaTime > 0.05f || r.LastUpdateMilliseconds > 50 || collections > previousCollections) && outliers.Count < 256)
                {
                    string line = "t=" + (Time.realtimeSinceStartup - joined).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " frame=" + (Time.unscaledDeltaTime * 1000).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " update=" + r.LastUpdateMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " gc=" + (collections - previousCollections) + " heapDelta=" + ((long)heap - previousHeap) + " stages=";
                    foreach (double ms in r.LastStages) line += ms.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ",";
                    outliers.Add(line);
                }
            }
            previousCollections = collections; previousHeap = heap;
            if (CoopRuntime.Argument("--summercoop-test-actions") == "yes" && !r.Session.IsHost)
            {
                if (Time.realtimeSinceStartup - joined < 3) return;
                try { if (!Exercise(r)) return; } catch (Exception e) { outcome = "FAIL: " + e.Message; step = 99; }
            }
            float duration; if (!float.TryParse(CoopRuntime.Argument("--summercoop-test-duration"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out duration)) duration = 20;
            if (Time.realtimeSinceStartup - (pair ? joined : began) < duration) return;
            string path = CoopRuntime.Argument("--summercoop-test-report");
            using (StreamWriter w = new StreamWriter(path))
            {
                w.WriteLine("SCENE=" + Application.loadedLevelName); w.WriteLine("ROLE=" + (r.Session.IsHost ? "host" : "client"));
                w.WriteLine("PHASE=" + r.Session.Phase); w.WriteLine("OBJECTS=" + r.Registry.Objects.Count); w.WriteLine("FSMS=" + r.Registry.Fsms.Count);
                w.WriteLine("PLAYERS=" + r.Session.World.Players.Count); w.WriteLine("REVISION=" + r.Session.World.Revision);
                w.WriteLine("RESOLUTION=" + Screen.width + "x" + Screen.height); w.WriteLine("TARGET_FRAME_RATE=" + Application.targetFrameRate); w.WriteLine("VSYNC=" + QualitySettings.vSyncCount);
                w.WriteLine("SNAPSHOT_BYTES=" + r.Session.LastSnapshotBytes); w.WriteLine("SCHEMA=" + r.Registry.SchemaHash);
                w.WriteLine("SAVE_PATH=" + r.SaveRoot);
                w.WriteLine("NATIVE_ACTIONS=" + outcome);
                w.WriteLine("NATIVE_MESH=" + meshOutcome);
                updateTimes.Sort();
                w.WriteLine("FPS_MEAN=" + (frameSeconds > 0 ? frames / frameSeconds : 0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
                if (updateTimes.Count > 0) { w.WriteLine("MOD_UPDATE_P95_MS=" + updateTimes[(updateTimes.Count - 1) * 95 / 100].ToString("F2", System.Globalization.CultureInfo.InvariantCulture)); w.WriteLine("MOD_UPDATE_MAX_MS=" + updateTimes[updateTimes.Count - 1].ToString("F2", System.Globalization.CultureInfo.InvariantCulture)); }
                frameTimes.Sort(); if (frameTimes.Count > 0) { w.WriteLine("FRAME_P95_MS=" + frameTimes[(frameTimes.Count - 1) * 95 / 100].ToString("F2", System.Globalization.CultureInfo.InvariantCulture)); w.WriteLine("FRAME_MAX_MS=" + frameTimes[frameTimes.Count - 1].ToString("F2", System.Globalization.CultureInfo.InvariantCulture)); }
                int over50 = 0, over100 = 0; foreach (double ms in frameTimes) { if (ms > 50) over50++; if (ms > 100) over100++; } w.WriteLine("FRAMES_OVER_50_MS=" + over50); w.WriteLine("FRAMES_OVER_100_MS=" + over100); w.WriteLine("MEASURED_FRAMES=" + frames);
                w.WriteLine("GC_COLLECTIONS=" + measuredCollections); w.WriteLine("GC_FRAMES=" + collectionFrames); w.WriteLine("MONO_USED_BYTES=" + heap); w.WriteLine("OBJECT_VISITS=" + objectVisits); w.WriteLine("FSM_VISITS=" + fsmVisits);
                foreach (string line in outliers) w.WriteLine("OUTLIER=" + line);
                string[] names = { "NETWORK", "ADAPTER", "DISCOVER", "CAPTURE_ENFORCE", "JOURNAL", "PUBLISH", "MOTION", "SAVE", "MOTION_CAPTURE", "MOTION_SEND" };
                for (int i = 0; i < stages.Length; i++) { List<double> values = stages[i]; values.Sort(); if (values.Count > 0) w.WriteLine(names[i] + "_P95_MAX_MS=" + values[(values.Count - 1) * 95 / 100].ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "," + values[values.Count - 1].ToString("F2", System.Globalization.CultureInfo.InvariantCulture)); }
                int missing = 0, missingActive = 0, mismatched = 0, dynamic = 0;
                foreach (Entity e in r.Session.World.Entities.Values)
                {
                    if (e.Kind == EntityKind.Fsm || e.Kind == EntityKind.Globals) continue;
                    GameObject go = r.Registry.ObjectOf(e.Id);
                    if (go == null) { missing++; if (e.Active) missingActive++; if (missing <= 12) w.WriteLine("MISSING=" + e.Key + " active=" + e.Active); continue; }
                    if (go.activeSelf != e.Active) mismatched++;
                    Rigidbody body = go.GetComponent<Rigidbody>(); if (body != null && !body.isKinematic) dynamic++;
                }
                w.WriteLine("MISSING_OBJECTS=" + missing); w.WriteLine("MISSING_ACTIVE_OBJECTS=" + missingActive); w.WriteLine("ACTIVE_MISMATCHES=" + mismatched); w.WriteLine("DYNAMIC_BODIES=" + dynamic);
            }
            enabled = false;
            if (CoopRuntime.Argument("--summercoop-test-exit") == "yes") Application.Quit();
        }
        private void CheckMesh(CoopRuntime runtime)
        {
            if (meshOutcome.StartsWith("PASS", StringComparison.Ordinal) || meshOutcome.StartsWith("FAIL", StringComparison.Ordinal)) return;
            try
            {
                string path = CoopRuntime.Argument("--summercoop-test-mesh-proof");
                if (runtime.Session.IsHost && meshId == null)
                {
                    foreach (ObjectBinding binding in runtime.Registry.Objects.Values)
                    {
                        Entity entity; Atom hash;
                        if (binding.Mesh == null || binding.Mesh.sharedMesh == null || !binding.Mesh.sharedMesh.isReadable || binding.Mesh.sharedMesh.vertexCount == 0 || !runtime.Session.World.Entities.TryGetValue(binding.Id, out entity) || !entity.Properties.TryGetValue("@mesh/hash", out hash)) continue;
                        meshId = binding.Id; previousMeshHash = hash.Text;
                        Mesh mesh = binding.Mesh.mesh; Vector3[] vertices = mesh.vertices; vertices[0].x += 0.0001f; meshExpected = vertices[0]; mesh.vertices = vertices; mesh.RecalculateBounds(); binding.NextMeshCapture = 0;
                        AtomicFile.Write(path, Codec.Make(delegate(BinaryWriter writer) { Codec.Text(writer, meshId); Codec.Text(writer, previousMeshHash); Codec.Vector(writer, WorldRegistry.From(meshExpected)); }));
                        meshRequested = Time.realtimeSinceStartup; meshOutcome = "WAITING"; break;
                    }
                    if (meshId == null) meshOutcome = "FAIL: readable replicated mesh not found";
                }
                else if (!runtime.Session.IsHost && meshId == null && File.Exists(path))
                {
                    Codec.Read(File.ReadAllBytes(path), delegate(BinaryReader reader) { meshId = Codec.Text(reader); previousMeshHash = Codec.Text(reader); meshExpected = WorldRegistry.To(Codec.Vector(reader)); }); meshRequested = Time.realtimeSinceStartup; meshOutcome = "WAITING";
                }
                if (meshId == null) return;
                Entity current; Atom currentHash;
                if (runtime.Session.World.Entities.TryGetValue(meshId, out current) && current.Properties.TryGetValue("@mesh/hash", out currentHash) && currentHash.Text != previousMeshHash)
                {
                    ObjectBinding binding = runtime.Registry.Objects[meshId]; Vector3 vertex = binding.Mesh.sharedMesh.vertices[0];
                    if (vertex.x == meshExpected.x && vertex.y == meshExpected.y && vertex.z == meshExpected.z) { meshOutcome = "PASS: exact changed vertices replicated"; return; }
                }
                if (Time.realtimeSinceStartup - meshRequested > 20) meshOutcome = "FAIL: changed mesh did not converge";
            }
            catch (Exception e) { meshOutcome = "FAIL: " + e.Message; }
        }
        private bool Exercise(CoopRuntime r)
        {
            if (step == 99) return true;
            if (step == 0)
            {
                previous = r.Session.CommandCompleted;
                r.Session.CommandCompleted = delegate(CommandResult c) { if (previous != null) previous(c); if (c.Sequence == sequence) result = c; };
                foreach (ObjectBinding b in r.Registry.Objects.Values) if (b.Object != null && b.Object.name == "starter(Clone)") { partId = b.Id; break; }
                foreach (FsmBinding b in r.Registry.Fsms.Values)
                {
                    FsmString name = b.Component.FsmVariables.FindFsmString("HandChild");
                    if (b.Component.FsmName == "Assembly" && name != null && name.Value == "starter(Clone)") { assemblyId = b.Id; break; }
                }
                if (partId == null || assemblyId == null) throw new Exception("Starter/attachment point not found");
                Position(r, r.Registry.ObjectOf(partId).transform.position); sentAt = Time.realtimeSinceStartup; step = 1; return false;
            }
            if (step == 1)
            {
                Position(r, r.Registry.ObjectOf(partId).transform.position);
                if (Time.realtimeSinceStartup - sentAt < 1) return false;
                Send(r, new Command { Kind = CommandKind.Take, Target = partId }); step = 2; return false;
            }
            if (step == 2)
            {
                if (!Complete()) return false;
                if (r.Session.World.Players[r.Session.LocalId].Held != partId) return false;
                Debug.Log("SummerCoop native test take confirmed");
                Position(r, r.Registry.FsmOf(assemblyId).Component.transform.position - Vector3.up * 1.35f - Vector3.forward * 1.2f);
                sentAt = Time.realtimeSinceStartup; step = 3; return false;
            }
            if (step == 3)
            {
                Position(r, r.Registry.FsmOf(assemblyId).Component.transform.position - Vector3.up * 1.35f - Vector3.forward * 1.2f);
                if (Time.realtimeSinceStartup - sentAt < 2) return false;
                Send(r, new Command { Kind = CommandKind.Attach, Target = assemblyId, Argument = "Assemble" }); step = 4; return false;
            }
            if (step == 4)
            {
                if (!Complete()) return false;
                if (r.Session.World.Players[r.Session.LocalId].Held != "") return false;
                Debug.Log("SummerCoop native test attachment confirmed");
                GameObject part = r.Registry.ObjectOf(partId);
                foreach (FsmBinding b in r.Registry.Fsms.Values) if (b.Component.transform.IsChildOf(part.transform) && b.Component.FsmName == "Screw" && b.Component.FsmVariables.FindFsmInt("Stage") != null) { boltId = b.Id; break; }
                if (boltId == null) throw new Exception("Native starter bolt not found");
                FsmBinding bolt = r.Registry.FsmOf(boltId); boltBefore = bolt.Component.FsmVariables.FindFsmInt("Stage").Value;
                Position(r, bolt.Component.transform.position); sentAt = Time.realtimeSinceStartup; step = 5; return false;
            }
            if (step == 5)
            {
                FsmBinding bolt = r.Registry.FsmOf(boltId); Position(r, bolt.Component.transform.position);
                if (Time.realtimeSinceStartup - sentAt < 1) return false;
                FsmFloat size = bolt.Component.FsmVariables.FindFsmFloat("BoltSize");
                Send(r, new Command { Kind = CommandKind.Bolt, Target = boltId, Argument = "Screw 2", Number = size == null ? 7 : size.Value }); step = 6; return false;
            }
            if (step == 6)
            {
                if (!Complete()) return false;
                int stage = r.Registry.FsmOf(boltId).Component.FsmVariables.FindFsmInt("Stage").Value;
                if (stage <= boltBefore && Time.realtimeSinceStartup - sentAt < 15) return false;
                GameObject part = r.Registry.ObjectOf(partId);
                if (stage <= boltBefore || part.transform.parent == null || part.tag == "PART") throw new Exception("Native convergence: stage=" + boltBefore + " -> " + stage + ", parent=" + (part.transform.parent == null ? "NULL" : part.transform.parent.name) + ", tag=" + part.tag);
                outcome = "PASS: remote take, native starter attachment, native bolt Stage " + boltBefore + " -> " + stage;
                Debug.Log("SummerCoop native test " + outcome); step = 99; return true;
            }
            return false;
        }
        private static void Position(CoopRuntime r, Vector3 point)
        {
            GameObject p = GameObject.Find("PLAYER"); if (p != null) { p.transform.position = point; p.transform.rotation = Quaternion.identity; }
            if (Camera.main != null) Camera.main.transform.rotation = Quaternion.identity;
            r.Session.SendPose(new Pose { Position = WorldRegistry.From(point), Rotation = WorldRegistry.From(Quaternion.identity), Pitch = 0 });
        }
        private void Send(CoopRuntime r, Command command)
        {
            result = null; r.Session.Submit(command); sequence = command.Sequence; sentAt = Time.realtimeSinceStartup;
        }
        private bool Complete()
        {
            if (result == null) { if (Time.realtimeSinceStartup - sentAt > 20) throw new Exception("Native command timeout"); return false; }
            if (!result.Accepted) throw new Exception(result.Reason); return true;
        }
    }
}
