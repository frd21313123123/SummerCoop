using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Explicit isolated-game QA: wrong references, displaced meshes and hidden suspension.
    public static class MscBoltPositionProbe
    {
        public static readonly List<string> Results = new List<string>();
        static bool done;
        static float captureAt;
        static Transform buttonVisual;
        static Vector3 buttonExpected;
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        public static void Update()
        {
            if (captureAt > 0 && Time.realtimeSinceStartup >= captureAt)
            {
                captureAt = 0;
                bool correct = buttonVisual != null && Vector3.Distance(buttonVisual.localPosition, buttonExpected) < .00001f;
                Results.Add("BoltPositionsButton=" + (correct ? "PASS" : "FAIL"));
                Application.CaptureScreenshot(MscRuntime.Argument("--summercoop-test-report").Replace("-report.txt", "-bolt-positions.png"));
            }
            if (done || MscRuntime.Argument("--summercoop-test-bolt-positions") != "yes"
                || Time.timeSinceLevelLoad < 35 || MscParts.DiscoveryPending != 0) return;
            done = true;
            GameObject other = new GameObject("bolt1");
            other.transform.position = new Vector3(7, 8, 9);
            int tested = 0, hidden = 0, suspension = 0, snapshots = 0;
            try
            {
                foreach (UnityEngine.Object item in Game.AllFsms())
                {
                    PlayMakerFSM f = item as PlayMakerFSM;
                    if (f == null || !f.Fsm.Initialized || !MscParts.IsBolt(f) || !f.transform.root.name.StartsWith("SATSUMA", StringComparison.Ordinal)) continue;
                    GameObject visual = MscBoltPositions.Visual(f);
                    if (visual == null) continue;
                    if (buttonVisual == null) buttonVisual = visual.transform;
                    string path = Recon.Path(f.transform);
                    bool chassis = path.Contains("/FR/") || path.Contains("/FL/") || path.Contains("/bolts_");
                    // Keep inactive models as well as ordinary engine/body fasteners.
                    if (tested >= 16 && (hidden >= 8 || f.gameObject.activeInHierarchy) && (suspension >= 8 || !chassis)) continue;
                    FsmInt stage = f.FsmVariables.FindFsmInt("Stage");
                    FsmGameObject reference = f.FsmVariables.FindFsmGameObject("ThisBolt");
                    int originalStage = stage.Value;
                    string state = f.ActiveStateName;
                    bool active = f.gameObject.activeSelf, enabled = f.enabled;
                    Vector3 anchor = f.transform.localPosition;
                    FsmString boltName = f.FsmVariables.FindFsmString("Bolt");
                    FsmInt index = f.FsmVariables.FindFsmInt("Index");
                    FsmGameObject assembled = f.FsmVariables.FindFsmGameObject("PartAssembled");
                    PlayMakerFSM check = assembled != null && assembled.Value != null ? Game.FsmOn(assembled.Value, "BoltCheck") : null;
                    FsmFloat tightness = check == null ? null : check.FsmVariables.FindFsmFloat("Tightness");
                    float total = tightness == null ? 0 : tightness.Value;
                    stage.Value = 0; MscBoltPositions.Restore(f);
                    Quaternion neutral = f.transform.localRotation;
                    Vector3 neutralMesh = visual.transform.localPosition;
                    for (int value = 0; value <= 8; value++)
                    {
                        stage.Value = value;
                        MscBoltPositions.Prepare(f);
                        // Each absolute pose starts at the same mount. Sequential
                        // Euler readback is ambiguous on pivots at +/-90 degrees.
                        f.transform.localRotation = neutral;
                        visual.transform.localPosition = neutralMesh;
                        // Native actions are the independent oracle for the current stage.
                        reference.Value = visual;
                        FsmState pose = f.Fsm.GetState(value + " 2") ?? f.Fsm.GetState(value.ToString());
                        if (pose == null) throw new Exception("Missing pose " + path);
                        foreach (FsmStateAction action in pose.Actions) if (action is SetPosition || action is SetRotation) action.OnEnter();
                        Vector3 expected = visual.transform.localPosition;
                        Quaternion rotation = f.transform.localRotation;
                        visual.transform.localPosition = new Vector3(1, -2, 3);
                        f.transform.localRotation = Quaternion.Euler(17, 28, 39);
                        reference.Value = other;
                        foreach (FsmState native in f.FsmStates) foreach (FsmStateAction action in native.Actions)
                        {
                            SetPosition position = action as SetPosition;
                            if (position != null && position.gameObject.OwnerOption == OwnerDefaultOption.SpecifyGameObject
                                && position.gameObject.GameObject.Value == visual) position.gameObject.GameObject.Value = other;
                        }
                        if (boltName != null) boltName.Value = "";
                        if (index != null) index.Value = -1;
                        if (!Session.IsHost)
                        {
                            byte[] packet = new NetWriter(Msg.Bolt).U8(0).Str(MscParts.Key(f)).I32(value)
                                .I32(100000 + tested * 10 + value).I32(1).Bool(false).ToArray();
                            MscParts.OnBolt(null, new NetReader(packet, 1, packet.Length - 1));
                            snapshots++;
                        }
                        else Check(MscBoltPositions.Restore(f), "Repair skipped " + path);
                        Check(Vector3.Distance(visual.transform.localPosition, expected) < .00001f, "Mesh pose " + path + " stage=" + value
                            + ";expected=" + expected.ToString("F6") + ";actual=" + visual.transform.localPosition.ToString("F6")
                            + ";visual=" + visual.name);
                        // Quaternion.Angle(q, q) can report ~0.04 degrees for
                        // native single-precision rotations. Compare the dot directly.
                        Check(Mathf.Abs(Quaternion.Dot(f.transform.localRotation, rotation)) > .999999f, "Pivot pose " + path + ";stage=" + value
                            + ";expected=" + rotation.eulerAngles + ";actual=" + f.transform.localEulerAngles);
                        Check(reference.Value == visual, "Wrong native mesh reference " + path);
                        Check(boltName == null || boltName.Value == visual.name, "Wrong saved bolt name " + path);
                        Check(index == null || index.Value == int.Parse(visual.name.Substring(visual.name.Length - 1)), "Wrong saved array index " + path);
                        Check(stage.Value == value && f.ActiveStateName == state, "Repair entered native input or changed stage " + path);
                        Check(tightness == null || tightness.Value == total, "Repair changed total tightness " + path);
                        FsmInt saved = check == null ? null : check.FsmVariables.FindFsmInt(visual.name);
                        if (!Session.IsHost && saved != null) Check(saved.Value == value, "Wrong saved bolt slot " + path);
                        Check(f.gameObject.activeSelf == active && f.enabled == enabled, "Repair activated hidden bolt " + path);
                        Check(Vector3.Distance(f.transform.localPosition, anchor) < .00001f, "Mount anchor changed " + path);
                        Check(Vector3.Distance(other.transform.position, new Vector3(7, 8, 9)) < .00001f, "Another bolt moved " + path);
                        Check(MscBoltPositions.Restore(f), "Repeated repair failed " + path);
                        Check(Vector3.Distance(visual.transform.localPosition, expected) < .00001f, "Repair is not idempotent " + path);
                    }
                    stage.Value = originalStage; MscBoltPositions.Restore(f);
                    tested++; if (!f.gameObject.activeInHierarchy) hidden++; if (chassis) suspension++;
                }
                Check(tested >= 16 && hidden >= 1 && suspension >= 1, "Insufficient bolt coverage");
                Results.Add("BoltPositions=PASS;Bolts=" + tested + ";Hidden=" + hidden + ";Suspension=" + suspension + ";Stages=" + tested * 9 + ";Snapshots=" + snapshots);
                string result = MscBoltPositions.RestoreAll();
                Check(!result.Contains(Lang.R("; failed: ")), "Bulk repair failed: " + result);
                Results.Add("BoltPositionsBulk=PASS;" + result);
                buttonExpected = buttonVisual.localPosition;
                buttonVisual.localPosition = new Vector3(2, -3, 4);
                Menu.TestBoltPositionsButton(); captureAt = Time.realtimeSinceStartup + 1;
            }
            catch (Exception e) { Results.Add("BoltPositions=FAIL;" + e); Log.Error("MSC bolt position QA: " + e); }
            finally { UnityEngine.Object.Destroy(other); }
        }
    }
}
