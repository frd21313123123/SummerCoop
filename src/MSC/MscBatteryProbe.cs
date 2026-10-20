using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;
using Session = MWCoop.Net.Session;

namespace MWCoop
{
    // Explicit, isolated two-player regression using a copied native save.
    public static class MscBatteryProbe
    {
        public static readonly List<string> Results = new List<string>();
        static float readyAt = -1, elapsed;
        static long start;
        static GameObject battery;
        static PlayMakerFSM mount;
        static readonly List<PlayMakerFSM> screws = new List<PlayMakerFSM>();
        static readonly HashSet<int> steps = new HashSet<int>();
        static string Report { get { return MscRuntime.Argument("--summercoop-test-report"); } }
        static string Channel { get { return Report.Replace("-host-report.txt", "-battery").Replace("-client-report.txt", "-battery"); } }
        static string Role { get { return Session.IsHost ? "host" : "guest"; } }
        static bool At(int step, float when) { return elapsed >= when && steps.Add(step); }
        static PlayMakerFSM Data(string name) { return Game.FindFsm("Database/DatabaseWiring/" + name, "Data"); }
        static void Record(string name, bool pass, string detail)
        {
            string line = "Battery=" + name + ";Pass=" + pass + ";" + detail;
            Results.Add(line); Log.Info(line);
        }
        static string Terminal(PlayMakerFSM f)
        {
            GameObject part = f.FsmVariables.FindFsmGameObject("PartAssembled").Value;
            PlayMakerFSM check = Game.FsmOn(part, "BoltCheck");
            PlayMakerFSM db = Game.FsmOn(check.FsmVariables.FindFsmGameObject("db_ThisPart").Value, "Data");
            GameObject visual = f.FsmVariables.FindFsmGameObject("ThisBolt").Value;
            int colliders = 0;
            foreach (Collider collider in f.GetComponentsInChildren<Collider>(true)) if (collider.enabled) colliders++;
            return "name=" + part.name + ";installed=" + db.FsmVariables.FindFsmBool("Installed").Value
                + ";bolted=" + db.FsmVariables.FindFsmBool("Bolted").Value + ";stage=" + f.FsmVariables.FindFsmInt("Stage").Value
                + ";total=" + check.FsmVariables.FindFsmFloat("Tightness").Value + ";active=" + f.gameObject.activeInHierarchy
                + ";enabled=" + f.enabled + ";colliders=" + colliders + ";visual=" + (visual == null ? "none" : Recon.Path(visual.transform))
                + ";pose=" + f.transform.localPosition.ToString("F5");
        }
        static void Dump(string phase)
        {
            var text = new StringBuilder();
            foreach (UnityEngine.Object value in Game.AllFsms())
            {
                PlayMakerFSM f = value as PlayMakerFSM;
                if (f == null || f.hideFlags != HideFlags.None || Recon.Path(f.transform).IndexOf("battery", StringComparison.OrdinalIgnoreCase) < 0) continue;
                text.AppendLine("===== " + Recon.Path(f.transform) + "::" + f.FsmName);
                Recon.AppendFsm(text, "", f, true);
            }
            foreach (PlayMakerFSM f in screws) text.AppendLine(Terminal(f));
            MscProbeFiles.WriteAllText(Report.Replace("-report.txt", "-battery-" + phase + ".txt"), text.ToString());
        }
        static void Check(string phase, int stage, bool attached)
        {
            bool mounted = phase != "removed" && phase != "joint-loss";
            bool batteryInstalled = battery != null && Game.FsmOn(battery, "Use").FsmVariables.FindFsmBool("Installed").Value;
            int joints = battery == null ? -1 : battery.GetComponents<FixedJoint>().Length;
            foreach (PlayMakerFSM f in screws)
            {
                bool pass = screws.Count == 2 && batteryInstalled == mounted && joints == (mounted ? 1 : 0);
                PlayMakerFSM db = Data(f.transform.parent.parent.name.Contains("minus") ? "WiringBatteryMinus" : "WiringBatteryPlus");
                GameObject visual = f.FsmVariables.FindFsmGameObject("ThisBolt").Value;
                bool visible = f.gameObject.activeInHierarchy;
                PlayMakerFSM check = Game.FsmOn(f.transform.parent.parent.gameObject, "BoltCheck");
                int meshes = 0;
                foreach (Renderer renderer in check.GetComponentsInChildren<Renderer>(true))
                    if (!renderer.transform.IsChildOf(f.transform) && renderer.enabled && renderer.gameObject.activeInHierarchy) meshes++;
                pass &= f.FsmVariables.FindFsmInt("Stage").Value == stage && visible == attached
                    && db.FsmVariables.FindFsmBool("Installed").Value == attached
                    && db.FsmVariables.FindFsmBool("Bolted").Value == (stage >= 2)
                    && Mathf.Abs(check.FsmVariables.FindFsmFloat("Tightness").Value - stage) < .001f
                    && (meshes > 0) == attached
                    && visual != null && visual.transform.parent == f.transform;
                Record(phase + "-" + (db.name.Contains("Minus") ? "minus" : "plus"), pass,
                    "batteryInstalled=" + batteryInstalled + ";joints=" + joints + ";clampMeshes=" + meshes + ";" + Terminal(f));
            }
        }
        static void CheckInput()
        {
            foreach (PlayMakerFSM f in screws)
            {
                Collider collider = f.GetComponent<Collider>();
                bool before = collider != null && collider.enabled;
                string state = f.ActiveStateName;
                // Use the native tool-mode states, including their real saved
                // Collider reference, rather than enabling a test collider.
                Game.SetState(f, "State 1");
                bool enabled = collider != null && collider.enabled && f.gameObject.activeInHierarchy;
                Game.SetState(f, "State 2");
                bool disabled = collider != null && !collider.enabled;
                if (before) Game.SetState(f, "State 1");
                Game.SetState(f, state);
                Record("tool-input-" + (f.transform.parent.parent.name.Contains("minus") ? "minus" : "plus"), enabled && disabled,
                    "native tool mode enables/disables the terminal's own collider");
            }
        }
        static void CheckSave()
        {
            MscNativeSave.Capture();
            string file = Path.Combine(MscProfile.SaveDirectory, "defaultES2File.txt") + "?tag=";
            foreach (string side in new[] { "plus", "minus" })
            {
                string tag = "battery_terminal_" + side + "(xxxxx)";
                string key = "WiringBattery" + (side == "plus" ? "Plus" : "Minus");
                var bolts = ES2.LoadList<string>(file + key + "Bolts");
                bool pass = ES2.Load<bool>(file + tag + "Installed") && ES2.Load<bool>(file + tag + "Bolted")
                    && ES2.Load<float>(file + key + "Tightness") == 8 && bolts.Count == 1 && bolts[0] == "int(8)";
                Record("saved-" + side, pass, "installed/bolted/total/array read back from native ES2 save");
            }
        }
        static void Wire(string path)
        {
            PlayMakerFSM first = Game.FindFsm("SATSUMA(557kg, 248)/Wiring/Triggers/" + path, "Assemble");
            PlayMakerFSM second = Game.FsmOn(first.FsmVariables.FindFsmGameObject("_OtherEnd").Value, "Assemble");
            foreach (PlayMakerFSM f in new[] { first, second })
            {
                for (Transform t = f.transform; t != null; t = t.parent) t.gameObject.SetActive(true);
                f.enabled = true;
            }
            Game.SetState(first, "Sound"); Game.SetState(second, "Sound");
        }
        static void Install()
        {
            mount.gameObject.SetActive(true); mount.enabled = true;
            mount.FsmVariables.FindFsmGameObject("Part").Value = battery;
            Game.SetState(mount, "Assemble");
        }
        public static void Update()
        {
            string mode = MscRuntime.Argument("--summercoop-test-battery");
            if (mode.Length == 0) return;
            if (readyAt < 0) readyAt = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - readyAt < 15 || MscParts.DiscoveryPending != 0 && battery == null) return;
            Directory.CreateDirectory(Channel);
            if (battery == null)
            {
                mount = Game.FindFsm("SATSUMA(557kg, 248)/MiscParts/trigger_battery", "Assembly");
                battery = mount.FsmVariables.FindFsmGameObject("Part").Value;
                if (battery == null) throw new Exception("Copied save must contain an installed battery");
                Rigidbody car = VehicleSync.Body("SATSUMA"); car.constraints = RigidbodyConstraints.FreezeAll;
                foreach (Rigidbody rb in car.GetComponentsInChildren<Rigidbody>(true)) { rb.useGravity = false; rb.detectCollisions = false; }
                foreach (string name in new[] { "WiringBatteryPlus", "WiringBatteryMinus" })
                {
                    GameObject terminal = Data(name).FsmVariables.FindFsmGameObject("ActivateThis").Value;
                    foreach (PlayMakerFSM f in terminal.GetComponentsInChildren<PlayMakerFSM>(true)) if (MscParts.IsBolt(f)) screws.Add(f);
                }
                Check("save-recovered", mode == "reload" ? 8 : 0, mode != "unwired"); Dump("initial");
                if (mode != "unwired") CheckInput();
                foreach (PlayMakerFSM f in screws) Log.Info("BatteryInitial=" + Terminal(f));
                MscProbeFiles.WriteAllText(Path.Combine(Channel, Role + "-ready.txt"), "ready");
            }
            if (mode == "reload" || mode == "unwired") return;
            string clock = Path.Combine(Channel, "start.txt");
            if (start == 0)
            {
                if (Session.IsHost && File.Exists(Path.Combine(Channel, "guest-ready.txt")) && !File.Exists(clock))
                    MscProbeFiles.WriteAllText(clock, DateTime.UtcNow.AddSeconds(3).Ticks.ToString());
                if (!File.Exists(clock) || !long.TryParse(MscProbeFiles.ReadAllText(clock), out start)) return;
            }
            elapsed = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - start).TotalSeconds;
            if (At(0, 0) && Session.IsHost) { Wire("BatteryHarness/BatteryPlusHarness"); Wire("GroundBattery/BatteryMinusStarter"); }
            if (At(1, 3)) { Check("connected", 0, true); Dump("connected"); }
            if (elapsed >= 4 && elapsed < 8 && Session.IsHost && At(100 + (int)(elapsed * 3), 4))
                foreach (PlayMakerFSM f in screws) if (f.FsmVariables.FindFsmInt("Stage").Value < 8) Game.SetState(f, "Screw 2");
            if (At(2, 10)) { Check("host-tightened", 8, true); Dump("host-tightened"); }
            if (elapsed >= 11 && elapsed < 15 && !Session.IsHost && At(200 + (int)(elapsed * 3), 11))
                foreach (PlayMakerFSM f in screws) if (f.FsmVariables.FindFsmInt("Stage").Value > 0) Game.SetState(f, "Unscrew 2");
            if (At(3, 17)) Check("guest-loosened", 0, true);
            if (At(4, 18) && !Session.IsHost) Game.SetState(Game.FsmOn(battery, "Removal"), "Remove part");
            if (At(5, 21)) { Check("removed", 0, true); Dump("removed"); }
            if (At(6, 22) && !Session.IsHost) Install();
            if (At(7, 26)) { Check("reinstalled", 0, true); Dump("reinstalled"); }
            if (elapsed >= 27 && elapsed < 31 && !Session.IsHost && At(300 + (int)(elapsed * 3), 27))
                foreach (PlayMakerFSM f in screws) if (f.FsmVariables.FindFsmInt("Stage").Value < 8) Game.SetState(f, "Screw 2");
            if (At(8, 33)) Check("guest-retightened", 8, true);
            if (At(9, 34) && Session.IsHost) UnityEngine.Object.Destroy(battery.GetComponent<FixedJoint>());
            if (At(10, 38)) { Check("joint-loss", 0, true); Dump("joint-loss"); }
            if (At(11, 39) && Session.IsHost) Install();
            if (At(12, 43)) Check("after-joint-loss", 0, true);
            if (elapsed >= 44 && elapsed < 48 && Session.IsHost && At(400 + (int)(elapsed * 3), 44))
                foreach (PlayMakerFSM f in screws) if (f.FsmVariables.FindFsmInt("Stage").Value < 8) Game.SetState(f, "Screw 2");
            if (At(13, 50)) Check("host-retightened", 8, true);
            if (At(14, 52) && Session.IsHost) CheckSave();
        }
    }
}
