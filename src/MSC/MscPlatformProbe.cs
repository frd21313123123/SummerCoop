using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Opt-in QA: native vehicle entry, real vehicle colliders and the unmodified
    // CharacterMotor. Only the fixture's speed/pose and input are controlled.
    public static class MscPlatformProbe
    {
        public static readonly List<string> Results = new List<string>();
        static readonly string[] targets = { "BOAT", "HAYOSIKO", "FLATBED" };
        static long ticks;
        static int stage = -1, samples, supported, remoteSamples, remoteAttached, remoteRendered;
        static bool placed, prepared, recorded, jumped, detached;
        static Rigidbody carrier, lead;
        static Behaviour input, motor;
        static object movement;
        static Vector3 rest, start, walkStart, home;
        static float drift, distance, walk, nextSample, renderGap;
        static FieldInfo direction, jump;
        static readonly List<string> diagnostics = new List<string>();
        static float nextDiagnostic;
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static readonly Dictionary<Rigidbody, RigidbodyConstraints> constraints = new Dictionary<Rigidbody, RigidbodyConstraints>();
        static readonly Dictionary<Rigidbody, bool> gravity = new Dictionary<Rigidbody, bool>();
        public static void Update()
        {
            string channel = MscRuntime.Argument("--summercoop-test-platform-channel");
            if (channel.Length == 0 || VehicleSync.Count < 9 || Time.timeSinceLevelLoad < 22 || Game.PlayerT == null) return;
            string clock = Path.Combine(channel, "start.txt");
            if (ticks == 0)
            {
                home = Game.PlayerT.position;
                motor = Game.PlayerT.GetComponent("CharacterMotor") as Behaviour;
                input = Game.PlayerT.GetComponent("FPSInputController") as Behaviour;
                if (motor == null || input == null) throw new InvalidOperationException("Native platform motor/input missing");
                direction = motor.GetType().GetField("inputMoveDirection"); jump = motor.GetType().GetField("inputJump");
                movement = motor.GetType().GetField("movement").GetValue(motor);
                string ready = Path.Combine(channel, Session.IsHost ? "host-ready.txt" : "guest-ready.txt");
                if (!File.Exists(ready)) File.WriteAllText(ready, MscPlatforms.NativeState ?? "waiting");
                if (Session.IsHost && File.Exists(Path.Combine(channel, "guest-ready.txt")) && !File.Exists(clock)) File.WriteAllText(clock, DateTime.UtcNow.AddSeconds(4).Ticks.ToString(C));
                if (!File.Exists(clock) || !long.TryParse(File.ReadAllText(clock), out ticks)) return;
            }
            float elapsed = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - ticks).TotalSeconds;
            if (elapsed < 0) return;
            int current = (int)(elapsed / 26);
            if (current >= targets.Length * 2) { Restore(); return; }
            bool driver = (current % 2 == 0) != Session.IsHost;
            float phase = elapsed - current * 26;
            string name = targets[current / 2], leadName = name;
            if (stage != current)
            {
                Restore(); stage = current;
                samples = supported = remoteSamples = remoteAttached = remoteRendered = 0;
                drift = distance = walk = nextSample = renderGap = 0; placed = prepared = recorded = jumped = detached = false;
                diagnostics.Clear(); nextDiagnostic = 0;
                carrier = VehicleSync.Body(name); lead = VehicleSync.Body(leadName);
                if (carrier == null || lead == null) throw new InvalidOperationException("Missing native carrier " + name);
                if (driver)
                {
                    // Boat buoyancy retains its launch frame after entry. Place
                    // it before entering, as opposed to warping an occupied hull.
                    if (name == "BOAT")
                    {
                        Freeze(lead); MoveBody(lead, new Vector3(current * 35, 35, 0)); prepared = true;
                        File.WriteAllText(Path.Combine(channel, "carrier-" + stage + ".txt"), name);
                    }
                    if (name != "FLATBED") VehicleSync.TestEnter(leadName, false);
                }
                else input.enabled = false;
                Log.Info("MSC platform stage=" + stage + ";carrier=" + name + ";role=" + (driver ? "driver" : "rider") + ";native=" + MscPlatforms.NativeState);
            }
            if (driver && name != "FLATBED" && phase >= 1 && phase < 5 && Time.frameCount % 15 == 0 && VehicleSync.LocalDrivingRoot != lead.transform)
                VehicleSync.TestEnter(leadName, true);
            if (driver && name == "FLATBED" && phase >= 2 && phase < 20) VehicleSync.TestPushBody(name);
            if (driver && !prepared && phase >= 3 && (name == "FLATBED" || VehicleSync.LocalDrivingRoot == lead.transform))
            {
                // The previous driver can still have release packets in flight.
                // Set the fixture pose only after native entry claims authority.
                Freeze(lead); MoveBody(lead, new Vector3(current * 35, 35, 0)); prepared = true;
                File.WriteAllText(Path.Combine(channel, "carrier-" + stage + ".txt"), name);
            }
            if (driver && phase >= 6 && phase < 20 && !lead.isKinematic)
            {
                lead.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
                lead.velocity = lead.transform.forward * 4;
                lead.angularVelocity = phase >= 8 && phase < 12 ? Vector3.up * .16f : Vector3.zero;
            }
            if (!driver)
            {
                if (!placed && phase >= 4 && File.Exists(Path.Combine(channel, "carrier-" + stage + ".txt")))
                {
                    placed = true;
                    Vector3 point;
                    if (!Floor(carrier, out point)) throw new InvalidOperationException("Native vehicle has no walkable collider: " + name);
                    Teleport(point + Vector3.up * .04f);
                    start = carrier.position; rest = carrier.transform.InverseTransformPoint(PlayerSync.BuildLocal().Feet);
                    File.WriteAllText(Path.Combine(channel, "point-" + stage + ".txt"), rest.x.ToString("R", C) + "|" + rest.y.ToString("R", C) + "|" + rest.z.ToString("R", C));
                }
                direction.SetValue(motor, phase >= 12 && phase < 14 ? carrier.transform.forward * .07f : Vector3.zero);
                jump.SetValue(motor, phase >= 15 && phase < 15.8f);
                if (placed && phase >= 5 && phase < 6) { rest = carrier.transform.InverseTransformPoint(PlayerSync.BuildLocal().Feet); start = carrier.position; }
                if (placed && phase >= 6 && phase < 11 && Time.realtimeSinceStartup >= nextSample)
                {
                    nextSample = Time.realtimeSinceStartup + .1f; samples++;
                    if (MscPlatforms.SupportBody == carrier) supported++;
                    Vector3 at = carrier.transform.InverseTransformPoint(PlayerSync.BuildLocal().Feet);
                    Vector3 gap = at - rest; gap.y = 0; drift = Mathf.Max(drift, gap.magnitude);
                    distance = Mathf.Max(distance, (carrier.position - start).magnitude);
                }
                if (phase >= 11 && phase < 12) walkStart = carrier.transform.InverseTransformPoint(PlayerSync.BuildLocal().Feet);
                if (phase >= 14 && phase < 15) walk = Mathf.Max(walk, Mathf.Abs(carrier.transform.InverseTransformPoint(PlayerSync.BuildLocal().Feet).z - walkStart.z));
                if (phase >= 15.01f && phase < 16.3f && !(bool)motor.GetType().GetField("grounded").GetValue(motor)
                    && ((Vector3)movement.GetType().GetField("velocity").GetValue(movement)).y > .05f && MscPlatforms.SupportBody == null) jumped = true;
                if (phase >= 20 && !detached) { detached = true; Teleport(home - Vector3.up * 0.9f); }
                if (phase >= 21 && MscPlatforms.SupportBody != null) detached = false;
                if (placed && phase >= 4 && phase < 18 && Time.realtimeSinceStartup >= nextDiagnostic)
                {
                    nextDiagnostic = Time.realtimeSinceStartup + .5f;
                    object support = motor.GetType().GetField("movingPlatform").GetValue(motor);
                    object jumping = motor.GetType().GetField("jumping").GetValue(motor);
                    Transform active = support.GetType().GetField("activePlatform").GetValue(support) as Transform;
                    diagnostics.Add(phase.ToString("F2", C) + " local=" + carrier.transform.InverseTransformPoint(PlayerSync.BuildLocal().Feet).ToString("F3")
                        + " move=" + movement.GetType().GetField("velocity").GetValue(movement) + " frame=" + movement.GetType().GetField("frameVelocity").GetValue(movement)
                        + " platform=" + support.GetType().GetField("platformVelocity").GetValue(support) + " grounded=" + motor.GetType().GetField("grounded").GetValue(motor)
                        + " jumpEnabled=" + jumping.GetType().GetField("enabled").GetValue(jumping) + " input=" + jump.GetValue(motor) + " motor=" + motor.enabled + ";dead=" + Respawn.Choosing + " active=" + (active != null ? Recon.Path(active) : "none"));
                }
            }
            else if (phase >= 6 && phase < 11 && Time.realtimeSinceStartup >= nextSample)
            {
                nextSample = Time.realtimeSinceStartup + .1f; remoteSamples++;
                int other = Session.IsHost ? 1 : 0; PlayerInfo pi;
                if (Session.Players.TryGetValue(other, out pi) && pi.State.Support.VehicleIndex == VehicleSync.CarIndex(carrier))
                {
                    remoteAttached++;
                    foreach (Avatar avatar in PlayerSync.Avatars)
                    {
                        if (avatar.Player.Id != other || avatar.Root == null) continue;
                        float gap = Vector3.Distance(avatar.Root.transform.position, carrier.transform.TransformPoint(pi.State.Support.Feet));
                        renderGap = Mathf.Max(renderGap, gap);
                        if (!avatar.InVehicle && gap < .35f) remoteRendered++;
                    }
                }
            }
            if (!recorded && phase >= 23)
            {
                recorded = true;
                bool pass = driver ? remoteSamples > 15 && remoteAttached >= remoteSamples * .8f && remoteRendered >= remoteSamples * .8f
                    : samples > 20 && supported >= samples * .8f && distance > 10 && drift < .3f && walk > .1f && jumped && detached;
                string result = "Platform=" + name + ";Driver=" + (current % 2 == 0 ? "guest" : "host") + ";Role=" + (driver ? "driver" : "rider") + ";Pass=" + pass
                    + ";Contact=" + supported + "/" + samples + ";Drift=" + drift.ToString("F3", C) + ";Distance=" + distance.ToString("F2", C)
                    + ";Walk=" + walk.ToString("F3", C) + ";Jump=" + jumped + ";Left=" + detached + ";Remote=" + remoteAttached + "/" + remoteSamples
                    + ";Rendered=" + remoteRendered + "/" + remoteSamples + ";RenderGap=" + renderGap.ToString("F3", C);
                Results.Add(result); Log.Info(result);
                File.WriteAllText(Path.Combine(channel, (Session.IsHost ? "host-" : "guest-") + stage + ".txt"), result);
                if (!driver) File.WriteAllLines(Path.Combine(channel, "diagnostic-" + stage + ".txt"), diagnostics.ToArray());
            }
        }
        static void Freeze(Rigidbody b)
        {
            constraints[b] = b.constraints; gravity[b] = b.useGravity;
            b.constraints = RigidbodyConstraints.FreezePosition | RigidbodyConstraints.FreezeRotation; b.useGravity = false;
        }
        static void MoveBody(Rigidbody b, Vector3 at)
        {
            b.transform.position = at; b.transform.rotation = Quaternion.identity;
            b.velocity = b.angularVelocity = Vector3.zero;
        }
        static void Restore()
        {
            if (VehicleSync.LocalDriving >= 0) VehicleSync.ExitLocal();
            foreach (var pair in constraints) if (pair.Key != null) { pair.Key.constraints = pair.Value; pair.Key.velocity = pair.Key.angularVelocity = Vector3.zero; }
            foreach (var pair in gravity) if (pair.Key != null) pair.Key.useGravity = pair.Value;
            constraints.Clear(); gravity.Clear();
            if (input != null) input.enabled = true;
            if (motor != null && direction != null) { direction.SetValue(motor, Vector3.zero); jump.SetValue(motor, false); }
        }
        static bool Floor(Rigidbody b, out Vector3 point)
        {
            point = Vector3.zero;
            float highest = float.MinValue;
                foreach (RaycastHit hit in Physics.RaycastAll(b.position + Vector3.up * 12, Vector3.down, 20))
                {
                    if (hit.collider.isTrigger || hit.normal.y < .6f || VehicleSync.CarRoot(hit.transform) != b.transform || hit.point.y <= highest) continue;
                    highest = hit.point.y; point = hit.point;
                }
            return highest > float.MinValue;
        }
        static void Teleport(Vector3 feet)
        {
            Transform p = Game.PlayerT; CharacterController cc = p.GetComponent<CharacterController>();
            cc.enabled = false; p.position = feet + Vector3.up * (cc.height * .5f - cc.center.y); cc.enabled = true;
            motor.GetType().GetField("grounded").SetValue(motor, false);
            movement.GetType().GetField("velocity").SetValue(movement, Vector3.zero);
            movement.GetType().GetField("frameVelocity").SetValue(movement, Vector3.zero);
            object support = motor.GetType().GetField("movingPlatform").GetValue(motor);
            support.GetType().GetField("activePlatform").SetValue(support, null);
            support.GetType().GetField("hitPlatform").SetValue(support, null);
        }
    }
}
