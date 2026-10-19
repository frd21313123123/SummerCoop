using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Explicit QA mode only: inspect native MSC fasteners and adjustment FSMs.
    public static class MscWorkshopProbe
    {
        public static readonly List<string> Results = new List<string>();
        static bool inspected;
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static readonly List<PlayMakerFSM> tuners = new List<PlayMakerFSM>();
        static readonly List<PlayMakerFSM> bolts = new List<PlayMakerFSM>();
        static readonly string[] fixtureParts = { "starter(Clone)", "carburator(Clone)", "distributor(Clone)", "alternator(Clone)", "rocker shaft(Clone)", "seat driver(Clone)", "wheel_regula" };
        static float preparedAt = -1, nextStep;
        static long clockTicks;
        static int phaseIndex = -1, step;
        static int busyBoltStates, boltInputs;
        static readonly Dictionary<PlayMakerFSM, int> boltAttempts = new Dictionary<PlayMakerFSM, int>();
        static readonly HashSet<PlayMakerFSM> raced = new HashSet<PlayMakerFSM>();
        static int repairRaces, lostTurns;
        static bool initialized, recorded, physicsPaused;
        static string Role { get { return Session.IsHost ? "host" : "guest"; } }
        public static void Update()
        {
            string channel = MscRuntime.Argument("--summercoop-test-workshop-channel");
            if (channel.Length == 0 || MscParts.Count == 0 || VehicleSync.Count == 0 || Time.timeSinceLevelLoad < 22) return;
            // Loosening suspension changes the wheel simulation between the
            // actor's and observer's inspection. This fixture checks native
            // writes and replication; road loads belong to a separate test.
            if (!physicsPaused && Time.timeSinceLevelLoad >= 30) { Time.timeScale = 0; physicsPaused = true; }
            if (preparedAt < 0)
            {
                preparedAt = Time.realtimeSinceStartup;
                if (Session.IsHost) foreach (string part in fixtureParts) Results.Add("WorkshopFixture=" + MscParts.TestInstall(part));
                foreach (UnityEngine.Object item in Game.AllFsms())
                {
                    PlayMakerFSM f = item as PlayMakerFSM;
                    if (f == null || f.hideFlags != HideFlags.None || Recon.Path(f.transform).StartsWith("STORE/", StringComparison.Ordinal)) continue;
                    if (!MscTuning.Owns(f) && !(MscParts.IsBolt(f) && FixtureBolt(f))) continue;
                    for (Transform p = f.transform; p != null; p = p.parent) p.gameObject.SetActive(true);
                    f.enabled = true;
                }
                return;
            }
            if (Time.realtimeSinceStartup - preparedAt < 5) return;
            if (!initialized)
            {
                initialized = true;
                foreach (UnityEngine.Object item in Game.AllFsms())
                {
                    PlayMakerFSM f = item as PlayMakerFSM;
                    if (f == null || !f.Fsm.Initialized || f.hideFlags != HideFlags.None || Recon.Path(f.transform).StartsWith("STORE/", StringComparison.Ordinal)) continue;
                    if (MscTuning.Owns(f))
                    {
                        tuners.Add(f);
                        if (f.FsmName == "Screw") Game.SetState(f, f.Fsm.GetState("Idle") != null ? "Idle" : "State 3");
                    }
                    else if (MscParts.IsBolt(f) && FixtureBolt(f)) bolts.Add(f);
                }
                tuners.Sort((a, b) => string.CompareOrdinal(MscParts.Key(a), MscParts.Key(b)));
                bolts.Sort((a, b) => string.CompareOrdinal(MscParts.Key(a), MscParts.Key(b)));
                CheckSpecialParts();
                Inspect(channel);
                MscProbeFiles.WriteAllText(Path.Combine(channel, Role + "-ready.txt"), "tuners=" + tuners.Count + ";bolts=" + bolts.Count);
            }
            string clock = Path.Combine(channel, "start.txt");
            if (clockTicks == 0)
            {
                if (Session.IsHost && File.Exists(Path.Combine(channel, "guest-ready.txt")) && !File.Exists(clock)) MscProbeFiles.WriteAllText(clock, DateTime.UtcNow.AddSeconds(4).Ticks.ToString(C));
                if (!File.Exists(clock) || !long.TryParse(MscProbeFiles.ReadAllText(clock), out clockTicks)) return;
            }
            float elapsed = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - clockTicks).TotalSeconds;
            if (elapsed >= 110) { Time.timeScale = 1; return; }
            if (elapsed < 0) return;
            int current = (int)(elapsed / 22);
            float phase = elapsed - current * 22;
            bool actor = (current == 0 || current == 3 || current == 4) != Session.IsHost;
            if (phaseIndex != current) { phaseIndex = current; step = busyBoltStates = boltInputs = 0; boltAttempts.Clear(); if (current == 4) raced.Clear(); nextStep = 0; recorded = false; }
            if (actor && phase >= 2 && phase < 12 && Time.realtimeSinceStartup >= nextStep)
            {
                nextStep = Time.realtimeSinceStartup + .3f;
                bool tighten = current == 0 || current == 2 || current == 4;
                foreach (PlayMakerFSM f in bolts)
                {
                    for (Transform p = f.transform; p != null; p = p.parent) p.gameObject.SetActive(true);
                    int stage = f.FsmVariables.FindFsmInt("Stage").Value;
                    if (stage == (tighten ? 8 : 0)) continue;
                    int attempts; boltAttempts.TryGetValue(f, out attempts);
                    if (attempts >= 8) continue;
                    // Native scroll input is read from Idle. Re-entering an
                    // unfinished pose/storage chain drops native continuations
                    // on slow frames. Keep the same eight-input limit per bolt.
                    if (f.ActiveStateName != "Idle") { busyBoltStates++; continue; }
                    string state = tighten ? "Screw 2" : "Unscrew 2";
                    if (f.Fsm.GetState(state) == null) state = tighten ? "Screw" : "Unscrew";
                    byte[] stale = !Session.IsHost && raced.Add(f) ? MscParts.CaptureBoltRepair(f) : null;
                    if (!Game.SetState(f, state)) throw new InvalidOperationException("Native bolt input state missing: " + MscParts.Key(f));
                    if (stale != null)
                    {
                        int turned = f.FsmVariables.FindFsmInt("Stage").Value;
                        MscParts.OnBolt(null, new NetReader(stale, 1, stale.Length - 1));
                        repairRaces++;
                        if (f.FsmVariables.FindFsmInt("Stage").Value != turned) lostTurns++;
                    }
                    boltAttempts[f] = attempts + 1; boltInputs++;
                }
                for (int i = 0; i < tuners.Count; i++)
                {
                    if (step >= 8 || step >= 1 + i % 3) continue;
                    PlayMakerFSM f = tuners[i];
                    string state = MscTuning.IsTiming(f) ? "Adjust" : f.FsmName == "HandRotate" ? tighten ? "Clockwise" : "Counterwise" : tighten ? "Unscrew 2" : "Screw 2";
                    if (f.Fsm.GetState(state) == null) state = tighten ? "Unscrew" : "Screw";
                    Game.SetState(f, state);
                }
                step++;
            }
            string expected = Path.Combine(channel, "state-" + current + ".txt");
            if (!recorded && phase >= (actor ? 15 : 19) && (actor || File.Exists(expected)))
            {
                recorded = true;
                SortedDictionary<string, float> snapshot = Snapshot();
                List<string> rows = new List<string>();
                foreach (KeyValuePair<string, float> pair in snapshot) rows.Add(pair.Key + "|" + pair.Value.ToString("R", C));
                MscProbeFiles.WriteAllLines(Path.Combine(channel, Role + "-state-" + current + ".txt"), rows.ToArray());
                int mismatches = 0;
                if (actor) MscProbeFiles.WriteAllLines(expected, rows.ToArray());
                else foreach (string row in MscProbeFiles.ReadAllLines(expected))
                {
                    int at = row.LastIndexOf('|'); string key = row.Substring(0, at); float value = float.Parse(row.Substring(at + 1), C), actual;
                    float error = snapshot.TryGetValue(key, out actual) ? key.Contains("angle.") ? Mathf.Abs(Mathf.DeltaAngle(value, actual)) : Mathf.Abs(value - actual) : float.MaxValue;
                    if (error > .002f) { mismatches++; Results.Add("WorkshopMismatch=" + current + ";" + key + ";expected=" + value.ToString("R", C) + ";actual=" + actual.ToString("R", C)); }
                }
                int wrongStages = 0, wrongBolted = 0;
                int displacedClamps = 0;
                foreach (PlayMakerFSM f in bolts) if (f.FsmVariables.FindFsmInt("Stage").Value != (current == 0 || current == 2 || current == 4 ? 8 : 0)) wrongStages++;
                foreach (PlayMakerFSM f in bolts) if (MscBoltPositions.IsHoseClamp(f))
                {
                    GameObject visual = MscBoltPositions.Visual(f);
                    if (visual == null || Mathf.Abs(visual.transform.localPosition.z) > .00001f) displacedClamps++;
                }
                if (displacedClamps > 0) throw new Exception("Hose clamp screw translated: " + displacedClamps);
                foreach (var value in snapshot) if (value.Key.EndsWith("/bolted", StringComparison.Ordinal) && value.Value != (current == 0 || current == 2 || current == 4 ? 1 : 0)) wrongBolted++;
                string result = "Workshop=" + current + ";Role=" + (actor ? "actor" : "observer") + ";Tuners=" + tuners.Count + ";Bolts=" + bolts.Count + ";Fields=" + rows.Count + ";Mismatches=" + mismatches + ";WrongStages=" + wrongStages + ";WrongBolted=" + wrongBolted;
                Results.Add(result); Log.Info(result);
                string pacing = "WorkshopPacing=" + current + ";Inputs=" + boltInputs + ";BusyStates=" + busyBoltStates;
                Results.Add(pacing); Log.Info(pacing);
                string race = "BoltRepairRace=" + current + ";Attempts=" + repairRaces + ";LostTurns=" + lostTurns;
                Results.Add(race); Log.Info(race);
            }
        }
        static bool FixtureBolt(PlayMakerFSM f)
        {
            if (MscBoltPositions.IsHoseClamp(f) || Recon.Path(f.transform).Contains("/battery_terminal_")) return true;
            FsmGameObject part = f.FsmVariables.FindFsmGameObject("PartAssembled");
            string id = part == null ? "" : MscParts.IdOf(part.Value);
            if (id == "mscdb:Database/DatabaseBody/Subframe" || id.Contains("/Wishbone_") || id.Contains("/SteeringRod_") || id.Contains("/Shock_")) return true;
            for (Transform p = f.transform; p != null; p = p.parent)
                foreach (string name in fixtureParts) if (p.name == name) return true;
            return false;
        }
        static void CheckSpecialParts()
        {
            int terminals = 0, clamps = 0;
            foreach (PlayMakerFSM f in bolts)
            {
                if (MscBoltPositions.IsHoseClamp(f)) clamps++;
                if (!Recon.Path(f.transform).Contains("/battery_terminal_")) continue;
                terminals++;
                PlayMakerFSM check = Game.FsmOn(f.transform.parent.parent.gameObject, "BoltCheck");
                if (check == null || !MscParts.Owns(check) || Replay.Owner(check) != null || WorldFsms.Tracks(check))
                    throw new Exception("Battery terminal total also owned by generic replay: " + Recon.Path(f.transform));
            }
            if (terminals != 2 || clamps < 4) throw new Exception("Insufficient terminal/clamp coverage: " + terminals + "/" + clamps);
            Results.Add("SpecialFasteners=PASS;Terminals=" + terminals + ";Clamps=" + clamps);
            int dashboards = 0, physical = 0;
            foreach (UnityEngine.Object item in Game.AllFsms())
            {
                PlayMakerFSM removal = item as PlayMakerFSM;
                if (removal == null || removal.FsmName != "Removal" || !removal.name.StartsWith("dashboard", StringComparison.OrdinalIgnoreCase)) continue;
                // Installed panels have no body. Temporarily supply the loose
                // body's ownership while physics is paused, without replaying
                // removal, moving the part or changing its saved installation.
                Rigidbody body = removal.GetComponent<Rigidbody>();
                bool temporary = body == null;
                if (temporary) { body = removal.gameObject.AddComponent<Rigidbody>(); body.isKinematic = true; }
                var originals = new Dictionary<Collider, bool>();
                try
                {
                    foreach (Collider collider in body.GetComponentsInChildren<Collider>(true))
                        if (collider.attachedRigidbody == body) { originals[collider] = collider.isTrigger; if (!collider.isTrigger) physical++; }
                    if (originals.Count == 0) continue;
                    MscHeldDashboard.Set(body.gameObject, true);
                    foreach (Collider collider in originals.Keys) if (!collider.isTrigger) throw new Exception("Native dashboard still collides: " + collider.name);
                    MscHeldDashboard.Set(body.gameObject, false);
                    foreach (var original in originals) if (original.Key.isTrigger != original.Value) throw new Exception("Native dashboard collision not restored");
                    dashboards++;
                }
                finally { MscHeldDashboard.Set(body.gameObject, false); if (temporary) UnityEngine.Object.Destroy(body); }
            }
            if (dashboards == 0 || physical == 0) throw new Exception("Native dashboard colliders not tested: parts=" + dashboards + ";physical=" + physical);
            Results.Add("DashboardNative=PASS;Parts=" + dashboards + ";PhysicalColliders=" + physical);
            GameObject dash = new GameObject("dashboard(test)"), child = new GameObject("collider"), other = new GameObject("starter(test)");
            try
            {
                dash.AddComponent<Rigidbody>().isKinematic = true;
                Collider body = dash.AddComponent<BoxCollider>();
                child.transform.parent = dash.transform;
                Collider trigger = child.AddComponent<BoxCollider>(); trigger.isTrigger = true;
                Collider ordinary = other.AddComponent<BoxCollider>();
                MscHeldDashboard.Local(dash);
                if (!body.isTrigger || !trigger.isTrigger || !body.enabled) throw new Exception("Held dashboard colliders");
                MscHeldDashboard.Local(other);
                if (body.isTrigger || !trigger.isTrigger || ordinary.isTrigger) throw new Exception("Dashboard drop/ordinary part collisions");
                MscHeldDashboard.Set(dash, true); MscHeldDashboard.Set(dash, true); MscHeldDashboard.Set(dash, false);
                if (body.isTrigger || !trigger.isTrigger) throw new Exception("Remote dashboard collision restoration");
                MscHeldDashboard.Local(dash); MscHeldDashboard.Reset();
                if (body.isTrigger || !trigger.isTrigger) throw new Exception("Dashboard scene reset collisions");
                MscHeldDashboard.Set(dash, true);
                UnityEngine.Object.DestroyImmediate(dash.GetComponent<Rigidbody>());
                MscHeldDashboard.Sweep();
                if (body.isTrigger || !trigger.isTrigger) throw new Exception("Dashboard native mount removed body without restoring collisions");
                Results.Add("DashboardCollisions=PASS;LocalDrop=PASS;RemoteDrop=PASS;Mount=PASS;Reset=PASS;Triggers=PASS");
            }
            finally { MscHeldDashboard.Local(null); UnityEngine.Object.Destroy(dash); UnityEngine.Object.Destroy(child); UnityEngine.Object.Destroy(other); }
        }
        static SortedDictionary<string, float> Snapshot()
        {
            SortedDictionary<string, float> values = new SortedDictionary<string, float>(StringComparer.Ordinal);
            foreach (PlayMakerFSM f in tuners)
            {
                string key = MscParts.Key(f);
                values[key + "/value"] = f.FsmVariables.FindFsmFloat(MscTuning.ValueName(f)).Value;
                foreach (string name in new[] { "Setup", "Wait1", "Wait", "Idle", "Clockwise", "Adjust", "Set angle" })
                {
                    FsmState state = f.Fsm.GetState(name); if (state == null) continue;
                    foreach (FsmStateAction action in state.Actions)
                    {
                        SetFsmFloat set = action as SetFsmFloat;
                        if (set != null)
                        {
                            GameObject go = set.gameObject.OwnerOption == OwnerDefaultOption.UseOwner ? f.gameObject : set.gameObject.GameObject.Value;
                            PlayMakerFSM data = go != null ? Game.FsmOn(go, set.fsmName.Value) : null;
                            FsmFloat value = data != null ? data.FsmVariables.FindFsmFloat(set.variableName.Value) : null;
                            if (value != null) values[key + "/data/" + set.variableName.Value] = value.Value;
                        }
                        SetRotation rotate = action as SetRotation;
                        if (rotate != null)
                        {
                            GameObject go = rotate.gameObject.OwnerOption == OwnerDefaultOption.UseOwner ? f.gameObject : rotate.gameObject.GameObject.Value;
                            if (go != null) Pose(values, key + "/" + name, go.transform);
                        }
                    }
                }
            }
            foreach (PlayMakerFSM f in bolts)
            {
                string key = MscParts.Key(f);
                values[key + "/stage"] = f.FsmVariables.FindFsmInt("Stage").Value;
                FsmGameObject bolt = f.FsmVariables.FindFsmGameObject("ThisBolt");
                if (bolt != null && bolt.Value != null) Pose(values, key, bolt.Value.transform);
                for (Transform p = f.transform; p != null; p = p.parent)
                {
                    PlayMakerFSM check = Game.FsmOn(p.gameObject, "BoltCheck");
                    FsmFloat tightness = check != null ? check.FsmVariables.FindFsmFloat("Tightness") : null;
                    if (tightness != null) { values[key + "/tightness"] = tightness.Value; break; }
                }
                // Suspension bolts can be siblings of their mounted model.
                // Follow the native action instead of guessing from ancestry.
                foreach (FsmState state in f.FsmStates) foreach (FsmStateAction action in state.Actions)
                {
                    AddFsmFloat add = action as AddFsmFloat;
                    if (add == null || add.variableName.Value != "Tightness") continue;
                    GameObject go = add.gameObject.OwnerOption == OwnerDefaultOption.UseOwner ? f.gameObject : add.gameObject.GameObject.Value;
                    PlayMakerFSM check = go == null ? null : Game.FsmOn(go, add.fsmName.Value);
                    if (check == null) continue;
                    values[key + "/total"] = check.FsmVariables.FindFsmFloat("Tightness").Value;
                    FsmGameObject db = check.FsmVariables.FindFsmGameObject("db_ThisPart");
                    PlayMakerFSM data = db == null || db.Value == null ? null : Game.FsmOn(db.Value, "Data");
                    if (data == null) continue;
                    FsmBool bolted = data.FsmVariables.FindFsmBool("Bolted"), installed = data.FsmVariables.FindFsmBool("Installed");
                    if (bolted != null) values[key + "/bolted"] = bolted.Value ? 1 : 0;
                    if (installed != null) values[key + "/installed"] = installed.Value ? 1 : 0;
                }
            }
            return values;
        }
        static void Pose(IDictionary<string, float> values, string key, Transform t)
        {
            values[key + "/angle.x"] = t.localEulerAngles.x; values[key + "/angle.y"] = t.localEulerAngles.y; values[key + "/angle.z"] = t.localEulerAngles.z;
        }
        static void Inspect(string channel)
        {
            if (inspected) return;
            inspected = true;
            Directory.CreateDirectory(channel);
            StringBuilder text = new StringBuilder();
            int screws = 0, adjustments = 0;
            foreach (UnityEngine.Object item in Game.AllFsms())
            {
                PlayMakerFSM f = item as PlayMakerFSM;
                if (f == null || f.hideFlags != HideFlags.None || Game.UnderPlayer(f.transform)) continue;
                string path = Recon.Path(f.transform), lower = path.ToLowerInvariant();
                bool screw = f.FsmName == "Screw";
                bool tune = screw && f.FsmVariables.FindFsmFloat("Alignment") != null || f.FsmName == "HandRotate" && f.FsmVariables.FindFsmFloat("Rotation") != null;
                if (!screw && !lower.Contains("wiring") && !lower.Contains("engine/fuel") && !lower.Contains("carb") && !lower.Contains("rocker") && !lower.Contains("distributor")
                    && !lower.Contains("alternator") && !lower.Contains("steering") && !lower.Contains("absorber") && !lower.Contains("strut")) continue;
                if (screw) screws++; else adjustments++;
                text.Append("\nFSM ").Append(path).Append(" :: ").Append(f.FsmName).Append(" initialized=").Append(f.Fsm.Initialized)
                    .Append(" active=").Append(f.gameObject.activeInHierarchy).Append(" owner=").Append(Replay.Owner(f)).Append(" state=").Append(f.ActiveStateName).Append('\n');
                foreach (FsmFloat v in f.FsmVariables.FloatVariables) text.Append("FLOAT ").Append(v.Name).Append('=').Append(v.Value.ToString("R", C)).Append('\n');
                foreach (FsmInt v in f.FsmVariables.IntVariables) text.Append("INT ").Append(v.Name).Append('=').Append(v.Value).Append('\n');
                foreach (FsmBool v in f.FsmVariables.BoolVariables) text.Append("BOOL ").Append(v.Name).Append('=').Append(v.Value).Append('\n');
                foreach (FsmGameObject v in f.FsmVariables.GameObjectVariables) text.Append("OBJECT ").Append(v.Name).Append('=').Append(v.Value != null ? Recon.Path(v.Value.transform) : "").Append('\n');
                if (!f.Fsm.Initialized && tune && !lower.StartsWith("store/")) f.Fsm.InitData();
                if (!tune && !f.Fsm.Initialized) continue;
                foreach (FsmState state in f.FsmStates)
                {
                    text.Append("STATE ").Append(state.Name).Append(':');
                    foreach (FsmTransition t in state.Transitions) text.Append(' ').Append(t.EventName).Append('>').Append(t.ToState);
                    text.Append('\n');
                    try { if (state.IsInitialized) text.Append(Jobs.DumpActions(f, state.Name)).Append('\n'); }
                    catch (Exception e) { text.Append("UNINITIALIZED ").Append(e.Message).Append('\n'); }
                }
            }
            MscProbeFiles.WriteAllText(Path.Combine(channel, Session.IsHost ? "host-inventory.txt" : "guest-inventory.txt"), text.ToString());
            Results.Add("WorkshopInventory=Screws=" + screws + ";Other=" + adjustments);
        }
    }
}
