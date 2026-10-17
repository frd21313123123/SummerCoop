using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HutongGames.PlayMaker;
using MWCoop.Net;
using SummerCoop.Game;
using UnityEngine;

namespace MWCoop
{
    // Only enabled by the isolated two-process native regression runner.
    public static class MscAssemblyProbe
    {
        public static readonly List<string> Results = new List<string>();
        sealed class Mount { public PlayMakerFSM Fsm, Data; public string State, Id; public int Depth; }
        static readonly List<Mount> mounts = new List<Mount>();
        static bool inspected;
        static long start;
        static int phaseIndex = -1, actionIndex, rotations, tilted;
        static bool recorded;
        static string Role { get { return Session.IsHost ? "host" : "guest"; } }
        static bool Installed(Mount mount) { return mount.Data != null ? mount.Data.FsmVariables.FindFsmBool("Installed").Value : NativeAssembly.Installed(MscParts.Find(mount.Id)); }
        static bool RotationRegression(Mount mount)
        {
            return mount.Id == "mscdb:Database/DatabaseMotor/Oilpan"
                || mount.Id == "mscdb:Database/DatabaseMotor/Piston1" || mount.Id == "mscdb:Database/DatabaseMotor/Piston2"
                || mount.Id == "mscdb:Database/DatabaseMotor/Piston3" || mount.Id == "mscdb:Database/DatabaseMotor/Piston4";
        }
        public static void Update()
        {
            string channel = MscRuntime.Argument("--summercoop-test-assembly-channel");
            if (channel.Length == 0 || Time.timeSinceLevelLoad < 30) return;
            if (!inspected) Inspect(channel);
            // Keep the fixture stationary while otherwise incompatible variants
            // are exercised. Crash physics and road behaviour are separate tests.
            foreach (Mount mount in mounts)
            {
                GameObject fixturePart = MscParts.Find(mount.Id);
                Rigidbody body = fixturePart == null ? null : fixturePart.GetComponent<Rigidbody>();
                if (body != null) { body.constraints = RigidbodyConstraints.FreezeAll; body.useGravity = false; body.detectCollisions = false; }
                if (fixturePart != null) foreach (Joint joint in fixturePart.GetComponents<Joint>()) { joint.breakForce = float.PositiveInfinity; joint.breakTorque = float.PositiveInfinity; }
                body = mount.Fsm.transform.root.GetComponent<Rigidbody>();
                if (body != null) { body.constraints = RigidbodyConstraints.FreezeAll; body.useGravity = false; body.detectCollisions = false; }
            }
            string clock = Path.Combine(channel, "start.txt");
            if (start == 0)
            {
                if (Session.IsHost && File.Exists(Path.Combine(channel, "guest-ready.txt")) && !File.Exists(clock)) MscProbeFiles.WriteAllText(clock, DateTime.UtcNow.AddSeconds(10).Ticks.ToString());
                if (!File.Exists(clock) || !long.TryParse(MscProbeFiles.ReadAllText(clock), out start)) return;
            }
            float elapsed = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - start).TotalSeconds;
            if (elapsed < 0 || elapsed >= 112) return;
            int phase = (int)(elapsed / 28); float at = elapsed - phase * 28;
            bool actor = (phase < 2) != Session.IsHost, install = phase % 2 == 1;
            if (phaseIndex != phase)
            {
                phaseIndex = phase; actionIndex = rotations = tilted = 0; recorded = false;
                mounts.Sort((a, b) => a.Depth == b.Depth ? string.CompareOrdinal(a.Id, b.Id) : install ? a.Depth.CompareTo(b.Depth) : b.Depth.CompareTo(a.Depth));
            }
            if (actor && at >= 2 && actionIndex < mounts.Count)
            {
                Mount mount = mounts[actionIndex++];
                GameObject part = MscParts.Find(mount.Id);
                if (part == null) throw new InvalidOperationException("Assembly fixture part missing: " + mount.Id);
                if (install)
                {
                    for (Transform t = mount.Fsm.transform; t != null; t = t.parent) t.gameObject.SetActive(true);
                    mount.Fsm.enabled = true;
                    part.SetActive(true);
                    mount.Fsm.FsmVariables.FindFsmGameObject("Part").Value = part;
                    NativeAssembly.PrepareJoint(mount.Fsm, mount.State);
                    if (!Game.SetState(mount.Fsm, mount.State)) throw new InvalidOperationException("Assembly fixture state missing");
                    if (RotationRegression(mount))
                    {
                        // Reproduce a loose/held pose arriving after native SetParent.
                        // Parenting is already correct: repair must fix the rotation too.
                        GameObject parent = NativeAssembly.MountParent(new FsmBinding { Component = mount.Fsm }, mount.State);
                        if (parent == null || part.transform.parent != parent.transform) throw new InvalidOperationException("Rotation fixture is not mounted");
                        part.transform.localRotation = Quaternion.Euler(17, -23, 41); tilted++;
                    }
                    foreach (Joint joint in part.GetComponents<Joint>()) { joint.breakForce = float.PositiveInfinity; joint.breakTorque = float.PositiveInfinity; }
                    Log.Info("Assembly fixture mount " + mount.Id + ";installed=" + Installed(mount) + ";joints=" + part.GetComponents<Joint>().Length);
                }
                else
                {
                    PlayMakerFSM removal = Game.FsmOn(part, "Removal");
                    if (removal == null) throw new InvalidOperationException("Removal fixture missing: " + mount.Id);
                    for (Transform t = removal.transform; t != null; t = t.parent) t.gameObject.SetActive(true);
                    removal.enabled = true;
                    string entry = removal.Fsm.GetState("Reset angle") != null ? "Reset angle" : "Remove part";
                    if (!Game.SetState(removal, entry)) throw new InvalidOperationException("Removal fixture state missing: " + mount.Id);
                    FsmGameObject loose = mount.Data == null ? null : mount.Data.FsmVariables.FindFsmGameObject("SpawnThis");
                    if (loose != null && loose.Value != null && mount.Data.FsmVariables.FindFsmGameObject("ActivateThis") != null && MscParts.IdOf(loose.Value) != mount.Id)
                        throw new InvalidOperationException("Freshly removed chassis clone lost its identity: " + mount.Id);
                }
            }
            if (actor && install && at >= 12 && rotations < 7)
            {
                rotations++;
                foreach (UnityEngine.Object item in Game.AllFsms())
                {
                    PlayMakerFSM f = item as PlayMakerFSM;
                    if (f != null && f.hideFlags == HideFlags.None && MscTuning.IsTiming(f)) Game.SetState(f, "Adjust");
                }
            }
            string expected = Path.Combine(channel, "expected-" + phase + ".txt");
            if (!recorded && at >= (actor ? 21 : 25) && (actor || File.Exists(expected)))
            {
                recorded = true;
                SortedDictionary<string, string> snapshot = Snapshot();
                List<string> rows = new List<string>();
                foreach (KeyValuePair<string, string> value in snapshot) rows.Add(value.Key + "|" + value.Value);
                MscProbeFiles.WriteAllLines(Path.Combine(channel, Role + "-state-" + phase + ".txt"), rows.ToArray());
                int mismatches = 0, wrong = 0, wrongJoints = 0, wrongRotation = 0;
                if (actor) MscProbeFiles.WriteAllLines(expected, rows.ToArray());
                else foreach (string row in MscProbeFiles.ReadAllLines(expected))
                {
                    int split = row.LastIndexOf('|'); string actual;
                    if (!snapshot.TryGetValue(row.Substring(0, split), out actual) || !Equivalent(actual, row.Substring(split + 1), row.Substring(0, split).EndsWith("/rotation", StringComparison.Ordinal)))
                    { mismatches++; Results.Add("AssemblyMismatch=" + phase + ";" + row + ";actual=" + actual); }
                }
                foreach (Mount mount in mounts) if (Installed(mount) != install)
                {
                    wrong++;
                    GameObject part = MscParts.Find(mount.Id);
                    PlayMakerFSM removal = part == null ? null : Game.FsmOn(part, "Removal"), bolts = part == null ? null : Game.FsmOn(part, "BoltCheck");
                    FsmBool detach = removal == null ? null : removal.FsmVariables.FindFsmBool("Detach");
                    string detail = "AssemblyWrong=" + phase + ";" + mount.Id + ";detach=" + (detach == null ? "none" : detach.Value.ToString()) + ";bolts=" + (bolts == null ? "none" : bolts.ActiveStateName) + ";part=" + (part == null ? "none" : Recon.Path(part.transform)) + ";data=" + (mount.Data == null ? "none" : mount.Data.ActiveStateName);
                    Results.Add(detail); Log.Info(detail);
                }
                if (install) foreach (Mount mount in mounts)
                {
                    // The game's SetParent for these five parts resets local rotation.
                    // Compare against that native contract, not just the other peer.
                    GameObject mounted = MscParts.Find(mount.Id);
                    if (RotationRegression(mount) && (mounted == null || Quaternion.Angle(mounted.transform.localRotation, Quaternion.identity) > .1f))
                    { wrongRotation++; Results.Add("AssemblyWrongRotation=" + phase + ";" + mount.Id); }
                    bool required = false;
                    foreach (FsmStateAction action in mount.Fsm.Fsm.GetState(mount.State).Actions) if (action.GetType().Name == "SetJointConnectedBody") required = true;
                    if (!required) continue;
                    GameObject part = MscParts.Find(mount.Id); bool connected = false;
                    if (part != null) foreach (Joint joint in part.GetComponents<Joint>()) if (joint.connectedBody != null) connected = true;
                    if (!connected) wrongJoints++;
                }
                string result = "Assembly=" + phase + ";Role=" + (actor ? "actor" : "observer") + ";Parts=" + mounts.Count + ";Fields=" + snapshot.Count + ";Mismatches=" + mismatches + ";WrongInstalled=" + wrong + ";WrongJoints=" + wrongJoints + ";WrongRotation=" + wrongRotation + ";Tilted=" + tilted;
                Results.Add(result); Log.Info(result);
            }
        }
        static SortedDictionary<string, string> Snapshot()
        {
            SortedDictionary<string, string> values = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (Mount mount in mounts)
            {
                bool installed = Installed(mount);
                values[mount.Id + "/installed"] = installed.ToString();
                GameObject part = MscParts.Find(mount.Id);
                values[mount.Id + "/present"] = (part != null).ToString();
                if (!installed || part == null) continue;
                values[mount.Id + "/visible"] = part.activeSelf.ToString();
                values[mount.Id + "/body"] = (part.GetComponent<Rigidbody>() != null).ToString();
                int joints = 0; foreach (Joint joint in part.GetComponents<Joint>()) if (joint.connectedBody != null) joints++;
                values[mount.Id + "/joints"] = joints.ToString();
                SetPose(values, mount.Id, part.transform);
            }
            foreach (UnityEngine.Object item in Game.AllFsms())
            {
                PlayMakerFSM f = item as PlayMakerFSM;
                if (f == null || f.hideFlags != HideFlags.None || !MscTuning.IsTiming(f)) continue;
                string key = MscParts.Key(f);
                values[key + "/timing"] = f.FsmVariables.FindFsmFloat("Angle").Value.ToString("R", CultureInfo.InvariantCulture);
                FsmGameObject db = f.FsmVariables.FindFsmGameObject("db_ThisPart");
                values[key + "/saved-timing"] = Game.FsmOn(db.Value, "Data").FsmVariables.FindFsmFloat("Angle").Value.ToString("R", CultureInfo.InvariantCulture);
                if (Game.FsmOn(db.Value, "Data").FsmVariables.FindFsmBool("Installed").Value) SetPose(values, key, f.transform);
            }
            return values;
        }
        static void SetPose(IDictionary<string, string> values, string key, Transform transform)
        {
            values[key + "/parent"] = transform.parent == null ? "" : Recon.Path(transform.parent);
            values[key + "/position"] = transform.localPosition.ToString("F3");
            values[key + "/rotation"] = transform.localEulerAngles.ToString("F2");
        }
        static bool Equivalent(string actual, string expected, bool angle)
        {
            if (actual == expected) return true;
            if (!actual.StartsWith("(", StringComparison.Ordinal) || !expected.StartsWith("(", StringComparison.Ordinal)) return false;
            string[] a = actual.Trim('(', ')').Split(','), b = expected.Trim('(', ')').Split(',');
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                float av = float.Parse(a[i], CultureInfo.InvariantCulture), bv = float.Parse(b[i], CultureInfo.InvariantCulture);
                if (angle ? Mathf.Abs(Mathf.DeltaAngle(av, bv)) > .1f : Math.Abs(av - bv) > .02f) return false;
            }
            return true;
        }
        static void Inspect(string channel)
        {
            inspected = true;
            // This fixture tests replication, not unbolted crash forces. Native
            // mount continuations unfreeze the car after our Update; suppress
            // physics steps so those continuations cannot tear fixture joints.
            Time.timeScale = 0;
            StringBuilder text = new StringBuilder();
            foreach (UnityEngine.Object item in Game.AllFsms())
            {
                PlayMakerFSM f = item as PlayMakerFSM;
                if (f == null || f.hideFlags != UnityEngine.HideFlags.None) continue;
                string path = Recon.Path(f.transform);
                if (!(path.StartsWith("SATSUMA", StringComparison.Ordinal) || path.StartsWith("CARPARTS/", StringComparison.Ordinal) || path.StartsWith("Database/", StringComparison.Ordinal))) continue;
                if (!(MscParts.Owns(f) || MscTuning.Owns(f) || f.FsmName == "BoltCheck" || f.FsmName == "Data")) continue;
                if (!f.Fsm.Initialized) f.Fsm.InitData();
                text.Append("\nFSM ").Append(path).Append(" :: ").Append(f.FsmName).Append(" owner=").Append(Replay.Owner(f)).Append('\n');
                foreach (NamedVariable v in f.FsmVariables.GetAllNamedVariables()) text.Append(v.GetType().Name).Append(' ').Append(v.Name).Append('=').Append(v.ToString()).Append('\n');
                foreach (FsmState state in f.FsmStates)
                {
                    text.Append("STATE ").Append(state.Name).Append('\n');
                    text.Append(Jobs.DumpActions(f, state.Name)).Append('\n');
                    if (f.FsmName != "Assembly" || !(state.Name.StartsWith("Assemble", StringComparison.Ordinal) || NativeAssembly.MountAction(state.Actions) != null)) continue;
                    PlayMakerFSM db = NativeAssembly.Database(f, state);
                    FsmGameObject nativePart = f.FsmVariables.FindFsmGameObject("Part");
                    GameObject wheel = db == null && nativePart != null ? nativePart.Value : null;
                    PlayMakerFSM use = wheel == null ? null : Game.FsmOn(wheel, "Use");
                    if (db == null && (use == null || use.FsmVariables.FindFsmString("Corner") == null || state.Name != "Assemble")) continue;
                    string id = db == null ? MscParts.IdOf(wheel) : "mscdb:" + Parts.RankPath(db.transform);
                    GameObject part = MscParts.Find(id);
                    if (part == null || Game.FsmOn(part, "Removal") == null) continue;
                    Mount previous = mounts.Find(m => m.Fsm == f || m.Id == id);
                    if (previous != null)
                    {
                        if (Installed(previous) || db == null || !db.FsmVariables.FindFsmBool("Installed").Value) continue;
                        mounts.Remove(previous);
                    }
                    int depth = 0; for (Transform t = f.transform; t != null; t = t.parent) depth++;
                    mounts.Add(new Mount { Fsm = f, Data = db, State = state.Name, Id = id, Depth = depth });
                }
            }
            MscProbeFiles.WriteAllText(Path.Combine(channel, Session.IsHost ? "host-inventory.txt" : "guest-inventory.txt"), text.ToString());
            MscProbeFiles.WriteAllText(Path.Combine(channel, Role + "-ready.txt"), "parts=" + mounts.Count);
            List<string> manifest = new List<string>();
            foreach (Mount mount in mounts) manifest.Add(mount.Id + "|" + MscParts.Key(mount.Fsm) + "|" + mount.State);
            manifest.Sort(StringComparer.Ordinal);
            MscProbeFiles.WriteAllLines(Path.Combine(channel, Role + "-manifest.txt"), manifest.ToArray());
            foreach (Mount mount in mounts)
            {
                FsmBool purchased = mount.Data == null ? null : mount.Data.FsmVariables.FindFsmBool("Purchased");
                if (purchased != null) purchased.Value = true;
                GameObject part = MscParts.Find(mount.Id);
                if (part != null) for (Transform t = part.transform; t != null; t = t.parent) t.gameObject.SetActive(true);
            }
        }
    }
}
