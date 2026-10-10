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
        private readonly long[] captureHeapBefore = new long[3], captureSamplesBefore = new long[3];
        private long meshBytesBefore, sharedMapsBefore, propertyEntriesBefore;
        private void Start() { int limit; if (int.TryParse(CoopRuntime.Argument("--summercoop-test-fps-limit"), out limit) && limit > 0) Application.targetFrameRate = limit; }
        private void Update()
        {
            CoopRuntime r = CoopRuntime.Instance;
            if (r == null || (!r.Active && !r.NativeBaselineReady)) return;
            bool baseline = r.NativeBaselineReady;
            bool pair = !baseline && CoopRuntime.Argument("--summercoop-test-pair") == "yes";
            if (pair && (r.Session.World.Players.Count < 2 || r.Joining)) { joined = 0; frames = 0; frameSeconds = 0; updateTimes.Clear(); frameTimes.Clear(); foreach (List<double> values in stages) values.Clear(); return; }
            if (joined == 0) joined = Time.realtimeSinceStartup;
            if (!baseline && CoopRuntime.Argument("--summercoop-test-mesh-proof") != "" && Time.realtimeSinceStartup - joined > 12) CheckMesh(r);
            int collections = GC.CollectionCount(0); long heap = GC.GetTotalMemory(false);
            if (Time.realtimeSinceStartup - joined > 5)
            {
                if (frames == 0 && !baseline)
                {
                    Array.Copy(r.Registry.CaptureHeapIncrease, captureHeapBefore, 3); Array.Copy(r.Registry.CaptureHeapSamples, captureSamplesBefore, 3);
                    meshBytesBefore = r.Registry.MeshBytesRead; sharedMapsBefore = r.Registry.SharedPropertyMaps; propertyEntriesBefore = r.Registry.PropertyMapEntries;
                }
                updateTimes.Add(r.LastUpdateMilliseconds); frameTimes.Add(Time.unscaledDeltaTime * 1000.0); for (int i = 0; i < stages.Length; i++) stages[i].Add(r.LastStages[i]); frameSeconds += Time.unscaledDeltaTime; frames++;
                if (collections > previousCollections) { measuredCollections += collections - previousCollections; collectionFrames++; }
                if (!baseline) { objectVisits += r.Registry.LastObjectVisits; fsmVisits += r.Registry.LastFsmVisits; }
                if ((Time.unscaledDeltaTime > 0.05f || r.LastUpdateMilliseconds > 50 || collections > previousCollections) && outliers.Count < 256)
                {
                    string line = "t=" + (Time.realtimeSinceStartup - joined).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " frame=" + (Time.unscaledDeltaTime * 1000).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " update=" + r.LastUpdateMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " gc=" + (collections - previousCollections) + " heapDelta=" + ((long)heap - previousHeap) + " stages=";
                    foreach (double ms in r.LastStages) line += ms.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ",";
                    outliers.Add(line);
                }
            }
            previousCollections = collections; previousHeap = heap;
            if (!baseline && CoopRuntime.Argument("--summercoop-test-actions") == "yes" && !r.Session.IsHost)
            {
                if (Time.realtimeSinceStartup - joined < 3) return;
                try { if (!Exercise(r)) return; } catch (Exception e) { outcome = "FAIL: " + e.Message; step = 99; }
            }
            float duration; if (!float.TryParse(CoopRuntime.Argument("--summercoop-test-duration"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out duration)) duration = 20;
            if (Time.realtimeSinceStartup - joined < duration) return;
            string path = CoopRuntime.Argument("--summercoop-test-report");
            using (StreamWriter w = new StreamWriter(path))
            {
                w.WriteLine("SCENE=" + Application.loadedLevelName); w.WriteLine("ROLE=" + (baseline ? "native-baseline" : r.Session.IsHost ? "host" : "client"));
                w.WriteLine("RESOLUTION=" + Screen.width + "x" + Screen.height); w.WriteLine("TARGET_FRAME_RATE=" + Application.targetFrameRate); w.WriteLine("VSYNC=" + QualitySettings.vSyncCount);
                System.Reflection.PropertyInfo focus = typeof(Application).GetProperty("isFocused", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                w.WriteLine("FOCUSED=" + (focus == null ? "UNKNOWN" : focus.GetValue(null, null).ToString()));
                w.WriteLine("TIME_SCALE=" + Time.timeScale); w.WriteLine("FIXED_DELTA_SECONDS=" + Time.fixedDeltaTime);
                Camera camera = Camera.main; GameObject playerObject = GameObject.Find("PLAYER");
                w.WriteLine("CAMERA_POSITION=" + (camera == null ? "NONE" : camera.transform.position.ToString("F3")));
                w.WriteLine("PLAYER_POSITION=" + (playerObject == null ? "NONE" : playerObject.transform.position.ToString("F3")));
                if (!baseline)
                {
                w.WriteLine("PHASE=" + r.Session.Phase); w.WriteLine("OBJECTS=" + r.Registry.Objects.Count); w.WriteLine("FSMS=" + r.Registry.Fsms.Count);
                w.WriteLine("INDEX_SAMPLES=" + r.Registry.IndexSamples); w.WriteLine("INDEX_MISMATCHES=" + r.Registry.IndexMismatches);
                w.WriteLine("PLAYERS=" + r.Session.World.Players.Count); w.WriteLine("REVISION=" + r.Session.World.Revision);
                w.WriteLine("SNAPSHOT_BYTES=" + r.Session.LastSnapshotBytes); w.WriteLine("SCHEMA=" + r.Registry.SchemaHash);
                w.WriteLine("DELTA_BYTES_SENT=" + r.Session.DeltaBytesSent);
                w.WriteLine("SAVE_PATH=" + r.SaveRoot);
                w.WriteLine("NATIVE_ACTIONS=" + outcome);
                w.WriteLine("NATIVE_MESH=" + meshOutcome);
                }
                updateTimes.Sort();
                w.WriteLine("FPS_MEAN=" + (frameSeconds > 0 ? frames / frameSeconds : 0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
                if (updateTimes.Count > 0) { w.WriteLine("MOD_UPDATE_P95_MS=" + updateTimes[(updateTimes.Count - 1) * 95 / 100].ToString("F2", System.Globalization.CultureInfo.InvariantCulture)); w.WriteLine("MOD_UPDATE_MAX_MS=" + updateTimes[updateTimes.Count - 1].ToString("F2", System.Globalization.CultureInfo.InvariantCulture)); }
                frameTimes.Sort(); if (frameTimes.Count > 0) { w.WriteLine("FRAME_P95_MS=" + frameTimes[(frameTimes.Count - 1) * 95 / 100].ToString("F2", System.Globalization.CultureInfo.InvariantCulture)); w.WriteLine("FRAME_MAX_MS=" + frameTimes[frameTimes.Count - 1].ToString("F2", System.Globalization.CultureInfo.InvariantCulture)); }
                int over50 = 0, over100 = 0; foreach (double ms in frameTimes) { if (ms > 50) over50++; if (ms > 100) over100++; } w.WriteLine("FRAMES_OVER_50_MS=" + over50); w.WriteLine("FRAMES_OVER_100_MS=" + over100); w.WriteLine("MEASURED_FRAMES=" + frames);
                w.WriteLine("GC_COLLECTIONS=" + measuredCollections); w.WriteLine("GC_FRAMES=" + collectionFrames); w.WriteLine("MONO_USED_BYTES=" + heap); w.WriteLine("OBJECT_VISITS=" + objectVisits); w.WriteLine("FSM_VISITS=" + fsmVisits);
                foreach (string line in outliers) w.WriteLine("OUTLIER=" + line);
                string[] names = { "NETWORK", "ADAPTER", "DISCOVER", "CAPTURE_ENFORCE", "JOURNAL", "PUBLISH", "MOTION", "SAVE", "MOTION_CAPTURE", "MOTION_SEND" };
                for (int i = 0; i < stages.Length; i++) { List<double> values = stages[i]; values.Sort(); if (values.Count > 0) w.WriteLine(names[i] + "_P95_MAX_MS=" + values[(values.Count - 1) * 95 / 100].ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "," + values[values.Count - 1].ToString("F2", System.Globalization.CultureInfo.InvariantCulture)); }
                if (!baseline)
                {
                w.WriteLine("MESH_READ_BYTES=" + (r.Registry.MeshBytesRead - meshBytesBefore));
                w.WriteLine("SHARED_PROPERTY_MAPS=" + (r.Registry.SharedPropertyMaps - sharedMapsBefore));
                w.WriteLine("NEW_PROPERTY_MAP_ENTRIES=" + (r.Registry.PropertyMapEntries - propertyEntriesBefore));
                for (int i = 0; i < 3; i++) w.WriteLine("CAPTURE_HEAP_" + i + "=" + (r.Registry.CaptureHeapIncrease[i] - captureHeapBefore[i]) + ",samples=" + (r.Registry.CaptureHeapSamples[i] - captureSamplesBefore[i]));
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
                    byte[] proof;
                    try
                    {
                        using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            if (file.Length > 1024) throw new ProtocolException("Mesh proof is too large");
                            proof = new byte[(int)file.Length]; int read = 0;
                            while (read < proof.Length) { int n = file.Read(proof, read, proof.Length - read); if (n == 0) throw new EndOfStreamException(); read += n; }
                        }
                    }
                    catch (IOException e)
                    {
                        if (e is ProtocolException) throw;
                        if (meshRequested == 0) meshRequested = Time.realtimeSinceStartup;
                        if (Time.realtimeSinceStartup - meshRequested > 5) throw;
                        return;
                    }
                    Codec.Read(proof, delegate(BinaryReader reader) { meshId = Codec.Text(reader); previousMeshHash = Codec.Text(reader); meshExpected = WorldRegistry.To(Codec.Vector(reader)); }); meshRequested = Time.realtimeSinceStartup; meshOutcome = "WAITING";
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
