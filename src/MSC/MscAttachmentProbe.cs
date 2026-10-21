using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HutongGames.PlayMaker;
using SummerCoop.Game;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Runs only in explicitly requested, isolated game instances.
    public static class MscAttachmentProbe
    {
        public static readonly List<string> Results = new List<string>();
        static bool dumped;
        static long start;
        static string batteryId, wheelId;
        static PlayMakerFSM batteryMount, wheelMount, doorMount;
        static Rigidbody car;
        static float elapsed, maxBoltError, maxBatteryError;
        static int samples, movingSamples;
        static float readyAt = -1;
        static float nextDoorTurn;
        static readonly HashSet<int> steps = new HashSet<int>();
        sealed class Screw { public Transform Pivot, Mesh; public Vector3 Position, MeshPosition; }
        static readonly List<Screw> screws = new List<Screw>();
        static string Report { get { return MscRuntime.Argument("--summercoop-test-report"); } }
        static string Channel { get { return Report.Replace("-host-report.txt", "-attachments").Replace("-client-report.txt", "-attachments"); } }
        static string Role { get { return Session.IsHost ? "host" : "guest"; } }
        static void Record(string name, bool pass, string detail)
        {
            string line = "Attachments=" + name + ";Pass=" + pass + ";" + detail;
            Results.Add(line); Log.Info(line);
        }
        static bool At(int step, float when) { return elapsed >= (when >= 20 ? when + 10 : when) && steps.Add(step); }
        static PlayMakerFSM Fsm(string path, string name) { return Game.FsmOn(Game.FindAny(path), name); }
        static bool Installed(string id) { return NativeAssembly.Installed(MscParts.Find(id)); }
        static void Remove(string id)
        {
            var part = MscParts.Find(id);
            var f = Game.FsmOn(part, "Removal");
            if (f == null) throw new Exception("No Removal: " + id);
            f.enabled = true;
            Game.SetState(f, f.Fsm.GetState("Reset angle") != null ? "Reset angle" : "Remove part");
        }
        static void Install(string id, PlayMakerFSM mount)
        {
            var part = MscParts.Find(id);
            if (part == null) throw new Exception("Missing part: " + id);
            mount.gameObject.SetActive(true); mount.enabled = true; part.SetActive(true);
            mount.FsmVariables.FindFsmGameObject("Part").Value = part;
            string state = mount.Fsm.GetState("Freeze car") != null ? "Freeze car" : "Assemble";
            if (!Game.SetState(mount, state)) throw new Exception("No assembly entry: " + Recon.Path(mount.transform));
            if (id.EndsWith("/Door_Left", StringComparison.Ordinal)) TraceDoor("install-entry");
        }
        static void CheckBattery(string phase, bool installed)
        {
            GameObject part = MscParts.Find(batteryId);
            if (part == null) { Record(phase, false, "missing=" + batteryId); return; }
            bool correct = part != null && Installed(batteryId) == installed;
            int joints = part == null ? -1 : part.GetComponents<FixedJoint>().Length;
            PlayMakerFSM status = Fsm("Database/PartsStatus/Battery", "Data");
            correct &= status.FsmVariables.FindFsmBool("Installed").Value == installed;
            if (installed) correct &= part.transform.parent != null && part.transform.parent.name == "pivot_battery" && joints == 1 && part.transform.localPosition.magnitude < .025f;
            // Props legitimately makes the guest's loose copy kinematic while
            // it follows the host. The physical owner must regain dynamics.
            else correct &= VehicleSync.CarRoot(part.transform) == null && joints == 0
                && (!Session.IsHost || part.GetComponent<Rigidbody>() == null || !part.GetComponent<Rigidbody>().isKinematic);
            foreach (string name in new[] { "WiringBatteryPlus", "WiringBatteryMinus" })
            {
                var data = Fsm("Database/DatabaseWiring/" + name, "Data");
                if (!installed) correct &= !data.FsmVariables.FindFsmBool("Bolted").Value;
            }
            Record(phase, correct, "id=" + batteryId + ";installed=" + Installed(batteryId) + ";joints=" + joints + ";parent=" + (part == null || part.transform.parent == null ? "none" : part.transform.parent.name));
        }
        static void CheckWheel(string phase, bool installed)
        {
            var part = MscParts.Find(wheelId);
            Record(phase, part != null && Installed(wheelId) == installed && (!installed || part.activeInHierarchy && part.GetComponent<Rigidbody>() == null),
                "id=" + wheelId + ";installed=" + Installed(wheelId));
        }
        static void Exercise()
        {
            Directory.CreateDirectory(Channel);
            if (car == null)
            {
                car = VehicleSync.Body("SATSUMA");
                batteryMount = Fsm("SATSUMA(557kg, 248)/MiscParts/trigger_battery", "Assembly");
                wheelMount = Fsm("SATSUMA(557kg, 248)/FR/AckerFR/wheelFR/TriggerWheelFR_New", "Assembly");
                doorMount = Fsm("SATSUMA(557kg, 248)/Body/trigger_door_left", "Assembly");
                batteryId = MscParts.IdOf(batteryMount.FsmVariables.FindFsmGameObject("Part").Value);
                wheelId = MscParts.IdOf(wheelMount.FsmVariables.FindFsmGameObject("Part").Value);
                if (batteryId.Length == 0 || wheelId.Length == 0) throw new Exception("Missing installed fixture identities");
                foreach (Rigidbody rb in car.GetComponentsInChildren<Rigidbody>(true)) { rb.useGravity = false; rb.detectCollisions = false; }
                car.constraints = RigidbodyConstraints.FreezeAll;
                Time.timeScale = 0;
                // Establish the same intact fixture on both peers. Native load
                // settling is outside this controlled driving/assembly test.
                if (!Installed(batteryId)) Install(batteryId, batteryMount);
                if (!Installed(wheelId)) Install(wheelId, wheelMount);
                GameObject door = MscParts.Find("mscdb:Database/DatabaseBody/Door_Left");
                if (!NativeAssembly.Installed(door) || door.GetComponent<HingeJoint>() == null)
                { Remove("mscdb:Database/DatabaseBody/Door_Left"); Install("mscdb:Database/DatabaseBody/Door_Left", doorMount); }
                MscProbeFiles.WriteAllText(Path.Combine(Channel, Role + "-ready.txt"), "ready");
            }
            string clock = Path.Combine(Channel, "start.txt");
            if (start == 0)
            {
                Time.timeScale = 0;
                if (Session.IsHost && File.Exists(Path.Combine(Channel, "guest-ready.txt")) && !File.Exists(clock)) MscProbeFiles.WriteAllText(clock, DateTime.UtcNow.AddSeconds(4).Ticks.ToString());
                if (!File.Exists(clock) || !long.TryParse(MscProbeFiles.ReadAllText(clock), out start)) return;
            }
            elapsed = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - start).TotalSeconds;
            if (elapsed < 0) return;
            if (MscRuntime.Argument("--summercoop-test-attachments") == "doors")
            {
                if (At(200, 0)) { Time.timeScale = 1; car.constraints = RigidbodyConstraints.FreezeAll; MscParts.Find("mscdb:Database/DatabaseBody/Door_Left").AddComponent<MscDoorJointTrace>(); TraceDoor("initial"); }
                if (At(201, 2) && Session.IsHost) Remove("mscdb:Database/DatabaseBody/Door_Left");
                if (At(202, 4) && Session.IsHost) Install("mscdb:Database/DatabaseBody/Door_Left", doorMount);
                if (At(203, 4.2f)) TraceDoor("4.2");
                if (At(204, 4.8f)) TraceDoor("4.8");
                if (At(205, 5.5f)) TraceDoor("5.5");
                if (At(206, 7)) TraceDoor("7");
                if (elapsed >= 4.5f && elapsed < 7.8f && Session.IsHost) TightenDoor();
                if (At(207, 8) && !Session.IsHost) NativeDoor(true);
                if (At(208, 10) && !Session.IsHost) CarDoors.TestState("SATSUMA", "Mouse off", "door left");
                if (At(209, 12)) CheckDoor("focused-door-open", true);
                if (At(210, 14) && !Session.IsHost) NativeDoor(false);
                if (At(211, 18)) CheckDoor("focused-door-close", false);
                return;
            }
            if (At(0, 0)) { Time.timeScale = 1; car.constraints = RigidbodyConstraints.FreezeRotation; if (Session.IsHost) VehicleSync.TestEnter("SATSUMA", false); }
            if (At(1, 1) && Session.IsHost) VehicleSync.TestEnter("SATSUMA", true);
            if (elapsed >= 3 && elapsed < 22 && Session.IsHost && !car.isKinematic)
            {
                float speed = elapsed < 9 ? Mathf.Min(35, (elapsed - 3) * 6) : elapsed < 15 ? 35 : Mathf.Max(0, (21 - elapsed) * 6);
                car.velocity = car.transform.forward * speed;
            }
            if (At(100, 5))
            {
                foreach (PlayMakerFSM f in car.GetComponentsInChildren<PlayMakerFSM>())
                {
                    GameObject mesh = MscParts.IsBolt(f) ? MscBoltPositions.Visual(f) : null;
                    if (mesh == null || f.GetComponent<Rigidbody>() == null) continue;
                    MscBoltPositions.Restore(f);
                    screws.Add(new Screw { Pivot = f.transform, Mesh = mesh.transform, Position = f.transform.localPosition, MeshPosition = mesh.transform.localPosition });
                }
            }
            if (At(2, 16))
            {
                Record("moving-fasteners", samples > 30 && movingSamples > 20 && maxBoltError < .002f && maxBatteryError < .025f,
                    "samples=" + samples + ";moving=" + movingSamples + ";bolts=" + screws.Count + ";boltError=" + maxBoltError + ";batteryError=" + maxBatteryError);
            }
            if (At(101, 22) && Session.IsHost) VehicleSync.ExitLocal();
            if (At(3, 20) && Session.IsHost) Remove(batteryId);
            if (At(4, 24)) CheckBattery("host-battery-remove", false);
            if (At(5, 26) && Session.IsHost) Install(batteryId, batteryMount);
            if (At(6, 32)) CheckBattery("host-battery-install", true);
            if (At(7, 36) && !Session.IsHost) Remove(batteryId);
            if (At(8, 40)) CheckBattery("guest-battery-remove", false);
            if (At(9, 42) && !Session.IsHost) Install(batteryId, batteryMount);
            if (At(10, 48)) CheckBattery("guest-battery-install", true);
            if (At(11, 52) && Session.IsHost) Remove(wheelId);
            if (At(12, 55)) CheckWheel("host-wheel-remove", false);
            if (At(13, 57) && Session.IsHost) Install(wheelId, wheelMount);
            if (At(14, 63)) CheckWheel("host-wheel-install", true);
            if (At(15, 66) && !Session.IsHost) Remove(wheelId);
            if (At(16, 69)) CheckWheel("guest-wheel-remove", false);
            if (At(17, 71) && !Session.IsHost) Install(wheelId, wheelMount);
            if (At(18, 77)) CheckWheel("guest-wheel-install", true);
            if (At(19, 80) && Session.IsHost) CarDoors.TestOpen("SATSUMA", true, "door left");
            if (At(20, 82) && Session.IsHost) CarDoors.TestState("SATSUMA", "Mouse off", "door left");
            if (At(21, 84)) CheckDoor("host-door-open", true);
            if (At(22, 86) && Session.IsHost) CarDoors.TestOpen("SATSUMA", false, "door left");
            if (At(23, 91)) CheckDoor("host-door-close", false);
            if (At(24, 94) && Session.IsHost) Remove("mscdb:Database/DatabaseBody/Door_Left");
            if (At(25, 98) && Session.IsHost) Install("mscdb:Database/DatabaseBody/Door_Left", doorMount);
            if (elapsed >= 109 && elapsed < 112.5f && Session.IsHost) TightenDoor();
            if (At(26, 103) && !Session.IsHost) CarDoors.TestOpen("SATSUMA", true, "door left");
            if (At(27, 105) && !Session.IsHost) CarDoors.TestState("SATSUMA", "Mouse off", "door left");
            if (At(28, 108)) CheckDoor("remounted-guest-door-open", true);
            if (At(29, 110) && !Session.IsHost) CarDoors.TestOpen("SATSUMA", false, "door left");
            if (At(30, 115)) CheckDoor("remounted-guest-door-close", false);
            if (At(31, 118) && Session.IsHost) Remove(batteryId);
            if (At(32, 122))
            {
                GameObject starter = MscParts.Find("mscitem:battery0");
                Record("starter-battery-identity", starter != null && MscParts.IdOf(starter) == "mscitem:battery0", "found=" + (starter != null));
                batteryId = "mscitem:battery0";
                if (Session.IsHost && starter != null) Install(batteryId, batteryMount);
            }
            if (At(33, 130)) CheckBattery("starter-battery-install", true);
        }
        static void CheckDoor(string phase, bool open)
        {
            var part = MscParts.Find("mscdb:Database/DatabaseBody/Door_Left");
            var use = Game.FsmOn(part, "Use");
            HingeJoint hinge = part == null ? null : part.GetComponent<HingeJoint>();
            float angle = part == null ? 0 : Quaternion.Angle(part.transform.localRotation, Quaternion.identity);
            Record(phase, use != null && use.FsmVariables.FindFsmBool("Open").Value == open && hinge != null && hinge.connectedBody == car
                && (open ? angle > 10 : angle < 2.5f) && part.GetComponents<FixedJoint>().Length <= 1,
                CarDoors.StateOf("SATSUMA", "door left"));
        }
        static void NativeDoor(bool open)
        {
            PlayMakerFSM use = Game.FsmOn(MscParts.Find("mscdb:Database/DatabaseBody/Door_Left"), "Use");
            Game.SetState(use, open ? "Open door" : "Sound");
        }
        static void TightenDoor()
        {
            if (Time.realtimeSinceStartup < nextDoorTurn) return;
            nextDoorTurn = Time.realtimeSinceStartup + .15f;
            GameObject part = MscParts.Find("mscdb:Database/DatabaseBody/Door_Left");
            foreach (PlayMakerFSM f in part.GetComponentsInChildren<PlayMakerFSM>())
                if (MscParts.IsBolt(f) && f.FsmVariables.FindFsmInt("Stage").Value < 8 && f.ActiveStateName == "Idle") Game.SetState(f, "Screw 2");
        }
        static void TraceDoor(string phase)
        {
            var part = MscParts.Find("mscdb:Database/DatabaseBody/Door_Left");
            var sb = new StringBuilder("DoorTrace=" + phase + ";role=" + Role + ";installed=" + NativeAssembly.Installed(part));
            if (part != null)
            {
                sb.Append(";parent=").Append(part.transform.parent == null ? "none" : Recon.Path(part.transform.parent)).Append(";local=").Append(part.transform.localPosition.ToString("F5"));
                var body = part.GetComponent<Rigidbody>();
                sb.Append(";kin=").Append(body != null && body.isKinematic).Append(";carKin=").Append(car.isKinematic).Append(";mass=").Append(body.mass).Append(";inertia=").Append(body.inertiaTensor).Append(";scale=").Append(part.transform.lossyScale);
                foreach (Joint joint in part.GetComponents<Joint>()) sb.Append(";joint=").Append(joint.GetType().Name).Append("->").Append(joint.connectedBody == null ? "WORLD" : joint.connectedBody.name).Append(";force=").Append(joint.breakForce).Append(";torque=").Append(joint.breakTorque).Append(";axis=").Append(joint.axis).Append(";anchor=").Append(joint.anchor).Append(";connected=").Append(joint.connectedAnchor);
                foreach (PlayMakerFSM f in part.GetComponents<PlayMakerFSM>()) sb.Append(";fsm=").Append(f.FsmName).Append(':').Append(f.ActiveStateName).Append('/').Append(f.enabled);
            }
            Log.Info(sb.ToString());
            var text = new StringBuilder(); Recon.AppendFsm(text, "", doorMount, true);
            MscProbeFiles.WriteAllText(Report.Replace("-report.txt", "-door-" + phase + ".txt"), sb + "\n" + text);
        }
        public static void LateUpdate()
        {
            if (start == 0 || elapsed < 6 || elapsed > 14) return;
            samples++;
            if (VehicleSync.CarVelocity(VehicleSync.CarIndex(car)).magnitude > 20) movingSamples++;
            foreach (Screw screw in screws)
            {
                maxBoltError = Mathf.Max(maxBoltError, Vector3.Distance(screw.Pivot.localPosition, screw.Position));
                maxBoltError = Mathf.Max(maxBoltError, Vector3.Distance(screw.Mesh.localPosition, screw.MeshPosition));
            }
            GameObject battery = MscParts.Find(batteryId);
            if (battery == null || !Installed(batteryId)) maxBatteryError = 100;
            else maxBatteryError = Mathf.Max(maxBatteryError, battery.transform.localPosition.magnitude);
        }
        public static void Update()
        {
            if (MscRuntime.Argument("--summercoop-test-attachments").Length == 0) return;
            if (readyAt < 0) readyAt = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - readyAt < 15) return;
            if (dumped) { Exercise(); return; }
            dumped = true;
            var text = new StringBuilder();
            int screws = 0;
            foreach (UnityEngine.Object obj in Game.AllFsms())
            {
                var f = obj as PlayMakerFSM;
                if (f == null || f.hideFlags != HideFlags.None) continue;
                string path = Recon.Path(f.transform);
                bool screw = MscParts.IsBolt(f) && screws < 2;
                if (!(path.IndexOf("battery", StringComparison.OrdinalIgnoreCase) >= 0 || path.Contains("door left") || path.Contains("TriggerWheel") || screw)) continue;
                if (screw) screws++;
                text.AppendLine("===== " + path + "::" + f.FsmName + ";id=" + MscParts.IdOf(f.gameObject));
                try { Recon.AppendFsm(text, "", f, true); }
                catch (Exception e) { text.AppendLine(e.ToString()); }
                for (Transform t = f.transform; t != null; t = t.parent)
                {
                    var rb = t.GetComponent<Rigidbody>();
                    text.AppendLine("PARENT " + Recon.Path(t) + ";local=" + t.localPosition.ToString("F6") + ";rotation=" + t.localEulerAngles + ";body=" + (rb == null ? "none" : "kin=" + rb.isKinematic));
                }
            }
            MscProbeFiles.WriteAllText(MscRuntime.Argument("--summercoop-test-report").Replace("-report.txt", "-attachments.txt"), text.ToString());
        }
    }
    public sealed class MscDoorJointTrace : MonoBehaviour
    {
        void OnJointBreak(float force) { Log.Info("DoorJointBreak=" + force + ";position=" + transform.position + ";local=" + transform.localPosition); }
    }
}
