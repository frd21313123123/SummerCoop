using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HutongGames.PlayMaker;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Explicit local QA only. Uses native enter/exit FSMs and controlled velocity
    // to exercise the real driver -> host -> observer path while still seated.
    public static class MscVehicleProbe
    {
        static string[] vehicles = { "JONNEZ", "BOAT", "KEKMET", "SATSUMA" };
        public static readonly List<string> Results = new List<string>();
        static bool described, entered, approached, captured;
        static int stage = -1, received, correctAuthority;
        static Vector3 first;
        static float distance, nextMeasure;
        static string entry = "", exit = "";
        static long startTicks;
        static int respawnStage;
        static float respawnAt;
        static Rigidbody controlled;
        static bool collisionBefore, gravityBefore, satsumaLifted;
        static RigidbodyConstraints constraintsBefore;
        static void RestoreFixture()
        {
            if (controlled != null) { controlled.detectCollisions = collisionBefore; controlled.useGravity = gravityBefore; controlled.constraints = constraintsBefore; }
            controlled = null;
        }

        public static void Update()
        {
            string channel = MscRuntime.Argument("--summercoop-test-vehicle-channel");
            if (channel.Length == 0) return;
            if (!described)
            {
                described = true;
                string selection = MscRuntime.Argument("--summercoop-test-vehicle-list");
                if (selection.Length > 0)
                {
                    string[] names = selection.Split(',');
                    foreach (string selectedName in names) if (Array.IndexOf(vehicles, selectedName) < 0) throw new InvalidOperationException("Unknown QA vehicle");
                    vehicles = names;
                }
                Describe();
            }
            string clock = Path.Combine(channel, "start.txt");
            if (startTicks == 0)
            {
                if (Session.IsHost && !File.Exists(clock)) MscProbeFiles.WriteAllText(clock, DateTime.UtcNow.AddSeconds(6).Ticks.ToString(CultureInfo.InvariantCulture));
                if (!File.Exists(clock) || !long.TryParse(MscProbeFiles.ReadAllText(clock), out startTicks)) return;
            }
            float elapsed = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - startTicks).TotalSeconds;
            if (elapsed < 0) return;
            int current = (int)(elapsed / 14f);
            if (current >= vehicles.Length * 2) { RestoreFixture(); CheckRespawns(); return; }
            float phase = elapsed - current * 14f;
            string name = vehicles[current / 2];
            bool driver = (current % 2 == 0) != Session.IsHost;
            Rigidbody body = VehicleSync.Body(name);
            if (current != stage)
            {
                if (name != "SATSUMA") RestoreFixture();
                stage = current; approached = entered = captured = false;
                distance = 0; correctAuthority = received = 0; entry = exit = "";
                first = body != null ? body.position : Vector3.zero;
                Log.Info("MSC vehicle probe stage " + stage + " " + name + (driver ? " DRIVER" : " OBSERVER"));
            }
            if (name == "SATSUMA" && body != null && controlled != body)
            {
                controlled = body; collisionBefore = body.detectCollisions; gravityBefore = body.useGravity; constraintsBefore = body.constraints;
                body.detectCollisions = false; body.useGravity = false; body.constraints |= RigidbodyConstraints.FreezeRotation;
                if (Session.IsHost && !satsumaLifted)
                {
                    satsumaLifted = true; body.position += Vector3.up * 3;
                    body.rotation = Quaternion.Euler(0, body.rotation.eulerAngles.y, 0);
                }
            }
            if (driver && !approached)
            {
                approached = true;
                entry = VehicleSync.TestEnter(name, false) ?? "NO DRIVE TRIGGER";
            }
            // Park the QA vehicle between drivers; native MSC deliberately
            // refuses entry into a tractor still coasting downhill.
            if (body != null && !body.isKinematic && (phase < 2 || phase >= 10))
                { body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
            if (driver && phase >= 1 && phase < 3 && VehicleSync.LocalDrivingRoot != (body != null ? body.transform : null))
            {
                if (!entered)
                {
                    entered = true;
                    entry += " -> " + (VehicleSync.TestEnter(name, true) ?? "NO DRIVE TRIGGER");
                }
                else if (Time.frameCount % 15 == 0)
                {
                    VehicleSync.TestEnter(name, false);
                    entry = VehicleSync.TestEnter(name, true) ?? "NO DRIVE TRIGGER";
                }
            }
            if (phase >= 3 && phase <= 9 && body != null)
            {
                if (driver && VehicleSync.LocalDrivingRoot == body.transform && !body.isKinematic)
                {
                    Vector3 direction = body.transform.forward; direction.y = 0;
                    body.velocity = direction.normalized * 3f + Vector3.up * Mathf.Clamp(body.velocity.y, -1f, 1f);
                }
                if (phase >= 5 && Time.realtimeSinceStartup >= nextMeasure)
                {
                    nextMeasure = Time.realtimeSinceStartup + .1f;
                    int owner = VehicleSync.Authority(body);
                    int expected = driver ? Session.LocalId : (Session.IsHost ? 1 : 0);
                    received++;
                    if (owner == expected && (driver ? VehicleSync.LocalDrivingRoot == body.transform : VehicleSync.RemotelyDriven(body.transform) && body.isKinematic)) correctAuthority++;
                    Vector3 moved = body.position - first; moved.y = 0; distance = Mathf.Max(distance, moved.magnitude);
                }
            }
            if (driver && phase >= 10 && exit.Length == 0)
            {
                exit = VehicleSync.ExitLocal() ? "native exit" : "NO EXIT";
                if (body != null && !body.isKinematic) body.velocity = Vector3.zero;
            }
            if (phase >= 12 && !captured)
            {
                captured = true;
                bool ok = body != null && received >= 5 && correctAuthority >= received * .8f && distance > 4f;
                string result = "VehicleProbe=" + name + ",driver=" + (current % 2 == 0 ? "guest" : "host") + ",role=" + (driver ? "driver" : "observer")
                    + ",pass=" + ok + ",distance=" + distance.ToString("F2", CultureInfo.InvariantCulture) + ",ownerSamples=" + correctAuthority + "/" + received
                    + ",fixture=" + (name == "SATSUMA" ? "airborne controlled velocity" : "ground controlled velocity") + ",entry=" + entry + ",exit=" + exit;
                Results.Add(result); Log.Info(result);
            }
        }

        static void Describe()
        {
            if (Session.IsHost) Log.Info("MSC vehicle probe setup: " + MscParts.ExerciseDriverSeat());
            for (int i = 0; i < VehicleSync.LocalCount; i++)
            {
                Rigidbody body = VehicleSync.LocalBody(i); if (body == null) continue;
                foreach (PlayMakerFSM fsm in body.GetComponentsInChildren<PlayMakerFSM>(true))
                    if (fsm.FsmName == "PlayerTrigger")
                    {
                        var states = new List<string>(); foreach (FsmState state in fsm.FsmStates) states.Add(state.Name);
                        Log.Info("MSC native drive " + VehicleSync.LocalKey(i) + " " + Recon.Path(fsm.transform) + " states=" + string.Join("|", states.ToArray()));
                        if (body.name.StartsWith("SATSUMA") && fsm.name.StartsWith("DriveTrigger"))
                            foreach (string state in new[] { "Wait for player", "Press return", "Check seat", "Regular seat", "In car", "Player in car" })
                                Log.Info("MSC Satsuma native " + Jobs.DumpActions(fsm, state));
                    }
            }
            GameObject death = Game.FindAny("Systems/Death");
            if (death != null)
                foreach (PlayMakerFSM fsm in death.GetComponentsInChildren<PlayMakerFSM>(true))
                    foreach (FsmState state in fsm.FsmStates)
                    {
                        string actions = Jobs.DumpActions(fsm, state.Name);
                        if (actions.Contains("SetPosition") || actions.Contains("SetVector3")) Log.Info("MSC native death " + Recon.Path(fsm.transform) + "::" + fsm.FsmName + " " + actions);
                    }
            GameObject church = Game.FindAny("PERAJARVI/CHURCH");
            if (church != null)
                foreach (Collider collider in church.GetComponentsInChildren<Collider>(true))
                    if (collider.transform.name == "collider") Log.Info("MSC church bounds " + collider.bounds);
        }

        static void CheckRespawns()
        {
            if (respawnStage >= 4) return;
            if (respawnStage % 2 == 0)
            {
                Log.Info("MSC respawn probe " + Respawn.TestDie("Hunger"));
                if (!Respawn.Choosing) { Results.Add("RespawnProbe=not intercepted,pass=False"); respawnStage = 4; return; }
                Respawn.TestChoose(respawnStage / 2);
                respawnAt = Time.realtimeSinceStartup; respawnStage++;
                return;
            }
            if (Time.realtimeSinceStartup - respawnAt < 2) return;
            Transform player = Game.PlayerT;
            Vector3 top = respawnStage == 1 ? MscRespawn.ChurchTop() : new Vector3(-9.8f, 1.2f, 11.7f);
            Vector3 gap = player != null ? player.position - top : new Vector3(999, 999, 999);
            gap.y = 0;
            bool ok = player != null && gap.magnitude < 2 && player.position.y > -5 && !Respawn.Choosing && player.GetComponent<CharacterController>().enabled;
            string result = "RespawnProbe=" + (respawnStage == 1 ? "church" : "parents") + ",pass=" + ok + ",position=" + (player != null ? player.position.ToString("F2") : "MISSING") + ",state=" + Respawn.State();
            Results.Add(result); Log.Info(result); respawnStage++;
        }
    }
}
