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
        static bool initialized, recorded;
        static string Role { get { return Session.IsHost ? "host" : "guest"; } }
        public static void Update()
        {
            string channel = MscRuntime.Argument("--summercoop-test-workshop-channel");
            if (channel.Length == 0 || MscParts.Count == 0 || VehicleSync.Count == 0 || Time.timeSinceLevelLoad < 22) return;
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
                Inspect(channel);
                File.WriteAllText(Path.Combine(channel, Role + "-ready.txt"), "tuners=" + tuners.Count + ";bolts=" + bolts.Count);
            }
            string clock = Path.Combine(channel, "start.txt");
            if (clockTicks == 0)
            {
                if (Session.IsHost && File.Exists(Path.Combine(channel, "guest-ready.txt")) && !File.Exists(clock)) File.WriteAllText(clock, DateTime.UtcNow.AddSeconds(4).Ticks.ToString(C));
                if (!File.Exists(clock) || !long.TryParse(File.ReadAllText(clock), out clockTicks)) return;
            }
            float elapsed = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - clockTicks).TotalSeconds;
            if (elapsed < 0 || elapsed >= 88) return;
            int current = (int)(elapsed / 22);
            float phase = elapsed - current * 22;
            bool actor = (current == 0 || current == 3) != Session.IsHost;
            if (phaseIndex != current) { phaseIndex = current; step = 0; nextStep = 0; recorded = false; }
            if (actor && phase >= 2 && step < 8 && Time.realtimeSinceStartup >= nextStep)
            {
                nextStep = Time.realtimeSinceStartup + .3f;
                bool tighten = current == 0 || current == 2;
                foreach (PlayMakerFSM f in bolts)
                {
                    for (Transform p = f.transform; p != null; p = p.parent) p.gameObject.SetActive(true);
                    int stage = f.FsmVariables.FindFsmInt("Stage").Value;
                    if (stage == (tighten ? 8 : 0)) continue;
                    string state = tighten ? "Screw 2" : "Unscrew 2";
                    if (f.Fsm.GetState(state) == null) state = tighten ? "Screw" : "Unscrew";
                    Game.SetState(f, state);
                }
                for (int i = 0; i < tuners.Count; i++)
                {
                    if (step >= 1 + i % 3) continue;
                    PlayMakerFSM f = tuners[i];
                    string state = f.FsmName == "HandRotate" ? tighten ? "Clockwise" : "Counterwise" : tighten ? "Unscrew 2" : "Screw 2";
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
                File.WriteAllLines(Path.Combine(channel, Role + "-state-" + current + ".txt"), rows.ToArray());
                int mismatches = 0;
                if (actor) File.WriteAllLines(expected, rows.ToArray());
                else foreach (string row in File.ReadAllLines(expected))
                {
                    int at = row.LastIndexOf('|'); string key = row.Substring(0, at); float value = float.Parse(row.Substring(at + 1), C), actual;
                    float error = snapshot.TryGetValue(key, out actual) ? key.Contains("angle.") ? Mathf.Abs(Mathf.DeltaAngle(value, actual)) : Mathf.Abs(value - actual) : float.MaxValue;
                    if (error > .002f) { mismatches++; Results.Add("WorkshopMismatch=" + current + ";" + key + ";expected=" + value.ToString("R", C) + ";actual=" + actual.ToString("R", C)); }
                }
                int wrongStages = 0;
                foreach (PlayMakerFSM f in bolts) if (f.FsmVariables.FindFsmInt("Stage").Value != (current == 0 || current == 2 ? 8 : 0)) wrongStages++;
                string result = "Workshop=" + current + ";Role=" + (actor ? "actor" : "observer") + ";Tuners=" + tuners.Count + ";Bolts=" + bolts.Count + ";Fields=" + rows.Count + ";Mismatches=" + mismatches + ";WrongStages=" + wrongStages;
                Results.Add(result); Log.Info(result);
            }
        }
        static bool FixtureBolt(PlayMakerFSM f)
        {
            for (Transform p = f.transform; p != null; p = p.parent)
                foreach (string name in fixtureParts) if (p.name == name) return true;
            return false;
        }
        static SortedDictionary<string, float> Snapshot()
        {
            SortedDictionary<string, float> values = new SortedDictionary<string, float>(StringComparer.Ordinal);
            foreach (PlayMakerFSM f in tuners)
            {
                string key = MscParts.Key(f);
                values[key + "/value"] = f.FsmVariables.FindFsmFloat(f.FsmName == "HandRotate" ? "Rotation" : "Alignment").Value;
                foreach (string name in new[] { "Setup", "Wait1", "Wait", "Idle", "Clockwise" })
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
            File.WriteAllText(Path.Combine(channel, Session.IsHost ? "host-inventory.txt" : "guest-inventory.txt"), text.ToString());
            Results.Add("WorkshopInventory=Screws=" + screws + ";Other=" + adjustments);
        }
    }
}
