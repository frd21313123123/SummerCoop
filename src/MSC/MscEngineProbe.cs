using System;
using System.IO;
using System.Text;
using System.Reflection;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Explicit isolated QA; the runner copies the seed save before launching.
    public static class MscEngineProbe
    {
        static float began = -1;
        static long clock;
        static int step, turns;
        static float next;
        static PlayMakerFSM filter, screw, removal, assembly;
        static string channel;
        static bool controlsChecked, reloadOnly;
        static readonly HashSet<int> driveSteps = new HashSet<int>();
        static PlayMakerFSM ignition, steeringHorn, starter;
        static GameObject hornSound;
        static string Role { get { return Session.IsHost ? "host" : "guest"; } }
        static void Record(string name, bool pass, string detail)
        { MscProbeFiles.WriteAllText(Path.Combine(channel, Role + "-" + name + ".txt"), "Pass=" + pass + ";" + detail); }
        public static void Update()
        {
            channel = MscRuntime.Argument("--summercoop-test-engine-channel");
            if (channel.Length == 0 || Time.timeSinceLevelLoad < 25) return;
            if (began < 0)
            {
                GameObject initial = Game.FindAny("CARPARTS/PartsMotor/oilfilter0");
                filter = initial == null ? null : Game.FsmOn(initial, "Use");
                if (filter == null) foreach (UnityEngine.Object item in Game.AllFsms())
                {
                    var f = item as PlayMakerFSM;
                    if (f != null && f.FsmName == "Use" && f.FsmVariables.FindFsmString("ID") != null && f.FsmVariables.FindFsmString("ID").Value == "oilfilter0") { filter = f; break; }
                }
                if (filter == null) throw new InvalidOperationException("Starter oil filter missing");
                screw = Game.FsmOn(filter.gameObject, "Screw"); removal = Game.FsmOn(filter.gameObject, "Removal");
                foreach (UnityEngine.Object item in Game.AllFsms())
                {
                    var f = item as PlayMakerFSM;
                    if (f != null && f.FsmName == "Assembly" && f.gameObject.name == "trigger_oil filter") { assembly = f; break; }
                }
                if (assembly == null) throw new InvalidOperationException("Oil filter assembly missing");
                for (Transform p = assembly.transform; p != null; p = p.parent) p.gameObject.SetActive(true);
                filter.gameObject.SetActive(true);
                began = Time.realtimeSinceStartup;
                reloadOnly = filter.FsmVariables.FindFsmBool("Installed").Value;
                if (reloadOnly) Record("reloaded", filter.FsmVariables.FindFsmFloat("Tightness").Value == 8 && filter.GetComponent<Rigidbody>() == null,
                    "Installed=True;Tightness=" + filter.FsmVariables.FindFsmFloat("Tightness").Value + ";Parent=" + filter.transform.parent);
                var dump = new StringBuilder();
                foreach (UnityEngine.Object item in Game.AllFsms())
                {
                    var f = item as PlayMakerFSM;
                    if (f == null || !f.Fsm.Initialized) continue;
                    if (f.FsmName == "Horn" || f.gameObject.name == "IgnitionSatsuma" || f.gameObject == filter.gameObject || f == assembly)
                    { dump.AppendLine("===== " + Recon.Path(f.transform) + "::" + f.FsmName + ";owner=" + Replay.Owner(f)); Recon.AppendFsm(dump, "", f, true); }
                }
                MscProbeFiles.WriteAllText(Path.Combine(channel, Role + "-native.txt"), dump.ToString());
            }
            string start = Path.Combine(channel, "start.txt");
            if (clock == 0)
            {
                MscProbeFiles.WriteAllText(Path.Combine(channel, Role + "-ready.txt"), "ready");
                if (Session.IsHost && File.Exists(Path.Combine(channel, "guest-ready.txt")) && !File.Exists(start)) MscProbeFiles.WriteAllText(start, DateTime.UtcNow.AddSeconds(4).Ticks.ToString());
                if (!File.Exists(start) || !long.TryParse(MscProbeFiles.ReadAllText(start), out clock)) return;
            }
            float elapsed = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - clock).TotalSeconds;
            if (elapsed >= 1 && !controlsChecked) { controlsChecked = true; CheckControls(); }
            CheckDriving(elapsed);
            if (reloadOnly) return;
            if (elapsed >= 5 && step == 0)
            {
                step++;
                Record("identity", MscParts.IdOf(filter.gameObject) == "mscitem:oilfilter0", "ID=" + MscParts.IdOf(filter.gameObject) + ";Bolt=" + MscParts.Key(screw));
                if (!Session.IsHost)
                {
                    MscParts.IdOf(filter.gameObject);
                    filter.gameObject.name = "oil filter(Clone)";
                    assembly.FsmVariables.FindFsmGameObject("Part").Value = filter.gameObject;
                    Game.SetState(assembly, "Assemble");
                }
            }
            if (elapsed >= 10 && elapsed < 20 && !Session.IsHost && Time.realtimeSinceStartup > next && turns < 8)
            {
                if (screw.ActiveStateName != "Get scroll" && screw.ActiveStateName != "Mouse off 2") return;
                next = Time.realtimeSinceStartup + .4f; turns++; Game.SetState(screw, "Screw");
            }
            if (elapsed >= 23 && step == 1)
            {
                step++;
                bool installed = filter.FsmVariables.FindFsmBool("Installed").Value;
                float tight = filter.FsmVariables.FindFsmFloat("Tightness").Value;
                Record("mounted", installed && tight == 8 && filter.GetComponent<Rigidbody>() == null,
                    "Installed=" + installed + ";Tightness=" + tight + ";Stage=" + screw.FsmVariables.FindFsmInt("Stage").Value + ";Parent=" + filter.transform.parent);
                if (Session.IsHost)
                {
                    MscNativeSave.Capture();
                    string file = Path.Combine(MscProfile.SaveDirectory, "items.txt");
                    Record("saved", ES2.Load<bool>(file + "?tag=oilfilter0Installed") && ES2.Load<float>(file + "?tag=oilfilter0Tightness") == 8,
                        "Installed=" + ES2.Load<bool>(file + "?tag=oilfilter0Installed") + ";Tightness=" + ES2.Load<float>(file + "?tag=oilfilter0Tightness"));
                }
            }
        }
        static void CheckControls()
        {
            PlayMakerFSM key = null, horn = null;
            foreach (UnityEngine.Object item in Game.AllFsms())
            {
                var f = item as PlayMakerFSM;
                if (MscVehicleControls.IsIgnition(f)) key = f;
                if (MscVehicleControls.IsHorn(f) && f.gameObject.name.StartsWith("stock steering wheel")) horn = f;
            }
            if (key == null || horn == null || Camera.main == null) throw new InvalidOperationException("Satsuma controls missing");
            ignition = key; steeringHorn = horn;
            var starterRef = key.FsmVariables.FindFsmGameObject("Starter");
            starter = starterRef == null || starterRef.Value == null ? null : Game.FsmOn(starterRef.Value, "Starter");
            foreach (FsmStateAction action in horn.Fsm.GetState("State 1").Actions)
            {
                var active = action as ActivateGameObject;
                if (active != null) hornSound = active.gameObject.GameObject.Value;
            }
            MscVehicleControls.Prepare(key); MscVehicleControls.Prepare(horn);
            bool guarded = false, raw = false;
            foreach (FsmStateAction a in horn.Fsm.GetState("Mouse over 2").Actions)
            {
                if (a is GetMouseButtonDown && a.Enabled) raw = true;
                var pick = a as MousePickEvent;
                if (pick != null && pick.GetType() != typeof(MousePickEvent) && pick.mouseDown != null && pick.mouseDown.Name == "FINISHED") guarded = true;
            }
            Record("input-routing", guarded && !raw && Jobs.Tracks(key), "Guarded=" + guarded + ";RawClick=" + raw + ";IgnitionSynced=" + Jobs.Tracks(key));
            // Reproduce the native layer overlap with real physics queries:
            // a layer-13 key and a layer-19 wheel on the same cursor ray.
            bool keyEnabled = key.enabled; key.enabled = true;
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            var keyCollider = key.gameObject.AddComponent<BoxCollider>();
            var wheelCollider = horn.gameObject.AddComponent<BoxCollider>();
            try
            {
                keyCollider.size = Vector3.one * .03f; keyCollider.center = key.transform.InverseTransformPoint(ray.GetPoint(.2f)); keyCollider.isTrigger = true;
                wheelCollider.size = Vector3.one * .03f; wheelCollider.center = horn.transform.InverseTransformPoint(ray.GetPoint(.3f)); wheelCollider.isTrigger = true;
                typeof(ActionHelpers).GetField("mousePickRaycastTime", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).SetValue(null, -1f);
                bool keyHit = ActionHelpers.IsMouseOver(key.gameObject, 1, 1 << 13);
                bool wheelHit = ActionHelpers.IsMouseOver(horn.gameObject, 1, 1 << 19);
                bool blocked = MscVehicleControls.IgnitionTargeted();
                keyCollider.enabled = false;
                typeof(ActionHelpers).GetField("mousePickRaycastTime", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).SetValue(null, -1f);
                bool unblocked = !MscVehicleControls.IgnitionTargeted();
                Record("overlap", keyHit && wheelHit && blocked && unblocked,
                    "KeyHit=" + keyHit + ";WheelHit=" + wheelHit + ";Blocked=" + blocked + ";WheelOnlyAllowed=" + unblocked);
            }
            finally { key.enabled = keyEnabled; UnityEngine.Object.Destroy(keyCollider); UnityEngine.Object.Destroy(wheelCollider); }
        }
        static bool At(int step, float elapsed, float due) { return elapsed >= due && driveSteps.Add(step); }
        static void CheckDriving(float elapsed)
        {
            if (!controlsChecked || ignition == null || starter == null || hornSound == null) return;
            for (int phase = 0; phase < 2; phase++)
            {
                float t = elapsed - phase * 30;
                bool driver = phase == 0 ? !Session.IsHost : Session.IsHost;
                int n = phase * 10;
                if (At(n, t, 5) && driver) VehicleSync.TestEnter("SATSUMA", false);
                if (t >= 7 && t < 14 && driver && VehicleSync.LocalDrivingRoot == null && Time.frameCount % 15 == 0)
                { VehicleSync.TestEnter("SATSUMA", false); VehicleSync.TestEnter("SATSUMA", true); }
                if (At(n + 2, t, 15) && driver)
                {
                    // Keep the real ACC transition while the test cursor is
                    // unlocked and deliberately not aimed at the key.
                    var picks = new List<FsmStateAction>();
                    foreach (FsmStateAction action in ignition.Fsm.GetState("Wait button").Actions)
                        if (action is MousePickEvent && action.Enabled) { action.Enabled = false; picks.Add(action); }
                    try { Game.SetState(ignition, "Wait button"); ignition.SendEvent("ACC"); }
                    finally { foreach (FsmStateAction action in picks) action.Enabled = true; }
                    Game.SetState(ignition, "ACC on"); ignition.SendEvent("START");
                }
                // The seed car may have no effective battery charge. Its
                // starter can reject Starting while the key is correctly held.
                // This checks the replicated control, not engine combustion.
                if (At(n + 3, t, 17)) Record("start-" + phase, ignition.ActiveStateName == "Motor starting" && !hornSound.activeSelf
                    && (!driver || VehicleSync.LocalDrivingRoot != null),
                    "Driver=" + driver + ";Seated=" + (VehicleSync.LocalDrivingRoot != null) + ";Key=" + ignition.ActiveStateName + ";Starting=" + starter.FsmVariables.FindFsmBool("Starting").Value + ";Horn=" + hornSound.activeSelf);
                if (At(n + 4, t, 18) && driver) ignition.SendEvent("FINISHED");
                if (At(n + 5, t, 19) && driver)
                {
                    Game.SetState(steeringHorn, "State 1");
                    Record("horn-positive-" + phase, hornSound.activeSelf, "Horn=" + hornSound.activeSelf);
                    Game.SetState(steeringHorn, "Mouse off");
                }
                if (At(n + 6, t, 20) && driver) VehicleSync.ExitLocal();
            }
        }
    }
}
