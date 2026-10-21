using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HutongGames.PlayMaker;
using MWCoop.Net;
using SummerCoop.Game;
using UnityEngine;

namespace MWCoop
{
    // Explicit isolated QA. A known horizontal motion separates replication
    // errors from road/suspension movement. Swap driver/passenger roles, with
    // an optional third peer observing both; never touch the user's save.
    public static class MscCabinProbe
    {
        static long clock;
        static int stage = -1, samples, valid;
        static bool approached, sat, reported, prepared;
        static float next, minY, maxY;
        static Vector3 cabinPosition;
        static Rigidbody body;
        static bool gravity, collisions;
        static RigidbodyConstraints constraints;
        static Vector3 motionOrigin;
        static bool road;
        static MscRoadCarController roadController;
        static AxisCarController nativeController;
        static bool nativeControllerWas;
        static PlayMakerFSM ignition;
        static float maxRpm, roadDistance;
        sealed class FixtureBody
        {
            public Rigidbody Body; public bool Gravity, Collisions;
            public RigidbodyConstraints Constraints; public Vector3 Position; public Quaternion Rotation;
        }
        static readonly List<FixtureBody> fixtureBodies = new List<FixtureBody>();
        static readonly List<KeyValuePair<PlayMakerFSM, bool>> fixtureHeadForces = new List<KeyValuePair<PlayMakerFSM, bool>>();
        static void RestoreBodies()
        {
            foreach (FixtureBody fixture in fixtureBodies) if (fixture.Body != null)
            { fixture.Body.useGravity = fixture.Gravity; fixture.Body.detectCollisions = fixture.Collisions; fixture.Body.constraints = fixture.Constraints; }
            foreach (var force in fixtureHeadForces) if (force.Key != null) force.Key.enabled = force.Value;
        }
        static void PlaceBodies(Vector3 velocity)
        {
            foreach (FixtureBody fixture in fixtureBodies)
            {
                if (fixture.Body == null || fixture.Body == body) continue;
                fixture.Body.position = body.transform.TransformPoint(fixture.Position);
                fixture.Body.rotation = body.rotation * fixture.Rotation;
                if (!fixture.Body.isKinematic) { fixture.Body.velocity = velocity; fixture.Body.angularVelocity = Vector3.zero; }
            }
        }
        static string entry;
        static string car = "HAYOSIKO";
        static float previousX, largestStep, lastFrame;
        static int reversals;
        static float fixedStageStart, phaseAtStageStart;
        static readonly HashSet<int> actions = new HashSet<int>();
        public static void Update()
        {
            string channel = MscRuntime.Argument("--summercoop-test-cabin-channel");
            if (channel.Length == 0 || Time.timeSinceLevelLoad < 25 || VehicleSync.Count == 0) return;
            if (body == null) body = VehicleSync.Body(car);
            if (body == null) throw new InvalidOperationException("Cabin QA van missing");
            if (!prepared)
            {
                string selected = MscRuntime.Argument("--summercoop-test-cabin-vehicle");
                if (selected.Length > 0 && selected != "HAYOSIKO" && selected != "SATSUMA") throw new InvalidOperationException("Unknown cabin QA vehicle");
                if (selected.Length > 0 && selected != car) { car = selected; body = VehicleSync.Body(car); if (body == null) return; }
                road = car == "SATSUMA" && MscRuntime.Argument("--summercoop-test-cabin-road") == "yes";
                prepared = true; gravity = body.useGravity; collisions = body.detectCollisions; constraints = body.constraints;
                if (!road) { body.useGravity = false; body.detectCollisions = false; }
                if (car == "SATSUMA" && !road) body.constraints = RigidbodyConstraints.FreezeRotation | RigidbodyConstraints.FreezePositionX | RigidbodyConstraints.FreezePositionY;
                if (car == "SATSUMA" && !road)
                    foreach (Rigidbody part in body.GetComponentsInChildren<Rigidbody>(true))
                    {
                        if (part == body || MscVehicleControls.PlayerObject(part.transform)) continue;
                        fixtureBodies.Add(new FixtureBody { Body = part, Gravity = part.useGravity, Collisions = part.detectCollisions,
                            Constraints = part.constraints, Position = body.transform.InverseTransformPoint(part.position),
                            Rotation = Quaternion.Inverse(body.rotation) * part.rotation });
                        part.useGravity = false; part.detectCollisions = false;
                        part.constraints = RigidbodyConstraints.FreezeRotation | RigidbodyConstraints.FreezePositionX | RigidbodyConstraints.FreezePositionY;
                    }
                if (car == "SATSUMA" && !road)
                    foreach (PlayMakerFSM force in body.GetComponentsInChildren<PlayMakerFSM>(true))
                        if (force.FsmName == "HeadForce") fixtureHeadForces.Add(new KeyValuePair<PlayMakerFSM, bool>(force, force.enabled));
                if (road)
                {
                    nativeController = body.GetComponent<AxisCarController>(); nativeControllerWas = nativeController.enabled;
                    foreach (PlayMakerFSM f in body.GetComponentsInChildren<PlayMakerFSM>(true))
                        if (MscVehicleControls.IsIgnition(f)) ignition = f;
                    MscProbeFiles.WriteAllText(Path.Combine(channel, "assembly-" + Session.LocalId + ".txt"), string.Join("\n", MscParts.IntegrityLines()));
                }
                Transform radioPivot = body.transform.Find("RadioPivot"), speaker = body.transform.Find("RadioPivot/Speaker");
                if (radioPivot != null) radioPivot.gameObject.SetActive(true);
                if (speaker != null) speaker.gameObject.SetActive(true);
                var lines = new List<string>();
                foreach (Rigidbody part in body.GetComponentsInChildren<Rigidbody>(true))
                    if (!MscVehicleControls.PlayerObject(part.transform)) lines.Add("BODY " + Recon.Path(part.transform)
                        + ";Installed=" + NativeAssembly.Installed(part.gameObject) + ";Kinematic=" + part.isKinematic
                        + ";Hinge=" + (part.GetComponent<HingeJoint>() != null));
                foreach (PlayMakerFSM f in body.GetComponentsInChildren<PlayMakerFSM>(true))
                    if (Recon.Path(f.transform).ToLowerInvariant().Contains("radio"))
                    {
                        var states = new List<string>(); foreach (FsmState s in f.FsmStates) states.Add(s.Name);
                        lines.Add(Recon.Path(f.transform) + "::" + f.FsmName + "=" + string.Join("|", states.ToArray()));
                        foreach (FsmState s in f.FsmStates) lines.Add(Jobs.DumpActions(f, s.Name));
                    }
                MscProbeFiles.WriteAllText(Path.Combine(channel, "radio-" + Session.LocalId + ".txt"), string.Join("\n", lines.ToArray()));
            }
            string start = Path.Combine(channel, "start.txt");
            if (clock == 0)
            {
                if (Session.IsHost && !File.Exists(start))
                {
                    if (!road) { body.position += Vector3.up * 2f; if (car == "SATSUMA") PlaceBodies(Vector3.zero); }
                    MscProbeFiles.WriteAllText(start, DateTime.UtcNow.AddSeconds(8).Ticks.ToString(CultureInfo.InvariantCulture));
                }
                if (!File.Exists(start) || !long.TryParse(MscProbeFiles.ReadAllText(start), out clock)) return;
            }
            float elapsed = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - clock).TotalSeconds;
            if (elapsed < 0) return;
            int current = (int)(elapsed / 40f);
            if (current >= 2)
            {
                body.useGravity = gravity; body.detectCollisions = collisions; body.constraints = constraints;
                RestoreBodies();
                if (roadController != null) roadController.enabled = false;
                if (road && nativeController != null) nativeController.enabled = nativeControllerWas;
                return;
            }
            float t = elapsed - current * 40f;
            int driver = current == 0 ? 1 : 0, passenger = current == 0 ? 0 : 1;
            if (current != stage)
            {
                stage = current; approached = sat = reported = false; samples = valid = 0;
                minY = float.MaxValue; maxY = float.MinValue; entry = "";
                previousX = body.position.z; largestStep = lastFrame = 0; reversals = 0;
                motionOrigin = body.position;
                fixedStageStart = Time.fixedTime; phaseAtStageStart = t;
                maxRpm = roadDistance = 0;
            }
            if (Session.LocalId == driver && !approached)
            { approached = true; entry = VehicleSync.LocalDrivingRoot == body.transform ? "native driver already seated" : VehicleSync.TestEnter(car, false); }
            if (Session.LocalId == driver && t >= 1 && t < 10 && VehicleSync.LocalDrivingRoot != body.transform && Time.frameCount % 15 == 0)
            {
                // The vanilla LOD may still be asleep at the test teleport.
                foreach (PlayMakerFSM f in body.GetComponentsInChildren<PlayMakerFSM>(true))
                    if (f.FsmName == "PlayerTrigger" && f.name.StartsWith("DriveTrigger"))
                    {
                        for (Transform p = f.transform; p != null && p != body.transform.parent; p = p.parent) p.gameObject.SetActive(true);
                        f.enabled = true;
                    }
                VehicleSync.TestEnter(car, false); entry = VehicleSync.TestEnter(car, true);
            }
            if (Session.LocalId == passenger && t >= 11 && !sat)
            { sat = true; entry = Seats.TestSit(car, 0); }
            // Artificial constraints/placement are not crash-physics evidence.
            // Keep the native head-impact detector out of this component test.
            if (!road) foreach (var force in fixtureHeadForces) if (force.Key != null) force.Key.enabled = false;
            if (car == "SATSUMA" && !road && Session.LocalId == driver && VehicleSync.LocalDrivingRoot == body.transform)
                body.isKinematic = false; // The component source has prescribed motion even if native LOD left it parked.
            if (road)
            {
                bool driveHere = Session.LocalId == driver && VehicleSync.LocalDrivingRoot == body.transform;
                if (driveHere)
                {
                    if (roadController == null) roadController = body.gameObject.AddComponent<MscRoadCarController>();
                    nativeController.enabled = false; roadController.enabled = true;
                    roadController.Driving = t >= 19 && t < 25;
                    if (t >= 5 && actions.Add(current * 10 + 4) && ignition != null)
                    {
                        foreach (PlayMakerFSM starter in body.GetComponentsInChildren<PlayMakerFSM>(true))
                        {
                            if (starter.FsmName != "Starter") continue;
                            FsmGameObject battery = starter.FsmVariables.FindFsmGameObject("db_Battery");
                            PlayMakerFSM data = battery == null || battery.Value == null ? null : Game.FsmOn(battery.Value, "Data");
                            FsmFloat charge = data == null ? null : data.FsmVariables.FindFsmFloat("Charge");
                            if (charge != null && charge.Value < 120) charge.Value = 120;
                        }
                        Game.SetState(ignition, "ACC on"); ignition.SendEvent("START");
                    }
                    if (t >= 10 && actions.Add(current * 10 + 5) && ignition != null) ignition.SendEvent("FINISHED");
                    if (t >= 31 && actions.Add(current * 10 + 6) && ignition != null) Game.SetState(ignition, "Motor OFF");
                }
                else if (roadController != null) roadController.enabled = false;
                maxRpm = Mathf.Max(maxRpm, VehicleSync.EngineRpm(body.transform));
            }
            if (!road && car != "SATSUMA" && Session.LocalId == driver && VehicleSync.LocalDrivingRoot == body.transform && !body.isKinematic)
            {
                body.velocity = t >= 19 && t < 29 ? Vector3.forward * 3f : Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            if (car == "HAYOSIKO" && t >= 14 && actions.Add(current * 10) && Session.LocalId == passenger)
            {
                string tunerPath = Recon.Path(body.transform) + "/RadioPivot/Radio/TunerPivot/RadioTuner";
                string volumePath = Recon.Path(body.transform) + "/RadioPivot/Radio/ButtonsRadio/RadioVolume";
                PlayMakerFSM tuner = Game.FindFsm(tunerPath, "Knob"), volume = Game.FindFsm(volumePath, "Knob");
                if (tuner == null || volume == null) throw new InvalidOperationException("Native radio controls missing");
                MscStereo.TestSet(tuner, .091f);
                string result = MscStereo.TestSet(volume, .6f);
                MscProbeFiles.WriteAllText(Path.Combine(channel, "radio-input-" + current + ".txt"), result
                    + ";Native=" + volume.ActiveStateName + ";Enabled=" + volume.enabled + ";Active=" + volume.gameObject.activeInHierarchy);
            }
            if (car == "HAYOSIKO" && t >= 19 && actions.Add(current * 10 + 1))
                MscProbeFiles.WriteAllText(Path.Combine(channel, "radio-on-" + current + "-" + Session.LocalId + ".txt"), "Pass=" + Radio.PlayingIn(body.transform) + ";" + Radio.GhostState());
            if (car == "HAYOSIKO" && t >= 22 && actions.Add(current * 10 + 2) && Session.LocalId == passenger) Radio.TestCut();
            if (car == "HAYOSIKO" && t >= 28 && actions.Add(current * 10 + 3))
                MscProbeFiles.WriteAllText(Path.Combine(channel, "radio-off-" + current + "-" + Session.LocalId + ".txt"), "Pass=" + !Radio.PlayingIn(body.transform) + ";" + Radio.GhostState());
            // Rendered transform, not Rigidbody.position: these must agree for
            // the passenger and the outside observer on a buffered Satsuma.
            if (!road && t >= 20 && t < 28)
            {
                // Update ordering and time spent in other modules vary between
                // peers. Measure the pose with the clock that applied it.
                float now = car == "SATSUMA" && Session.LocalId != driver ? VehicleSync.PresentationTime(body) : Time.unscaledTime;
                if (lastFrame > 0 && now > lastFrame)
                {
                    float step = body.transform.position.z - previousX;
                    if (step < -.001f) reversals++;
                    largestStep = Mathf.Max(largestStep, step - 3f * (now - lastFrame));
                }
                if (now > lastFrame) { previousX = body.transform.position.z; lastFrame = now; }
            }
            if (t >= 14 && t < 29 && Time.realtimeSinceStartup >= next)
            {
                next = Time.realtimeSinceStartup + .05f;
                bool owner = VehicleSync.Authority(body) == driver;
                bool role = Session.LocalId == driver ? VehicleSync.LocalDrivingRoot == body.transform && !body.isKinematic
                    : VehicleSync.LocalDriving < 0 && body.isKinematic;
                if (Session.LocalId == passenger) role &= Seats.SeatedCar == body.transform;
                if (samples == 0 && Session.LocalId == passenger) cabinPosition = body.transform.InverseTransformPoint(Game.PlayerT.position);
                if (road && samples == 0) motionOrigin = body.position;
                roadDistance = Mathf.Max(roadDistance, Vector3.Distance(body.transform.position, motionOrigin));
                if (Session.LocalId == passenger) role &= (body.transform.InverseTransformPoint(Game.PlayerT.position) - cabinPosition).magnitude < .1f;
                samples++; if (owner && role) valid++;
                minY = Mathf.Min(minY, body.transform.position.y); maxY = Mathf.Max(maxY, body.transform.position.y);
            }
            if (t >= 30 && !reported)
            {
                reported = true;
                bool pass = samples >= 30 && valid == samples && (road ? maxRpm > 500 && roadDistance > 2 : maxY - minY < .25f
                    && (car != "SATSUMA" || reversals == 0 && (Session.LocalId == driver || largestStep < .05f)));
                string result = "Pass=" + pass + ";Driver=" + driver + ";Local=" + Session.LocalId + ";Samples=" + valid + "/" + samples
                    + ";VerticalRange=" + (maxY - minY).ToString("F4", CultureInfo.InvariantCulture) + ";Vehicle=" + car
                    + ";Reversals=" + reversals + ";ExcessStep=" + largestStep.ToString("F4", CultureInfo.InvariantCulture)
                    + ";Rpm=" + maxRpm.ToString("F1", CultureInfo.InvariantCulture) + ";Distance=" + roadDistance.ToString("F3", CultureInfo.InvariantCulture)
                    + ";Fixture=" + (road ? "native engine and road physics" : "airborne controlled trajectory") + ";Entry=" + entry;
                MscProbeFiles.WriteAllText(Path.Combine(channel, "motion-" + current + "-" + Session.LocalId + ".txt"), result);
                if (Session.LocalId == passenger) Seats.TestLeave();
            }
            if (t >= 32 && Session.LocalId == driver && VehicleSync.LocalDriving >= 0) VehicleSync.ExitLocal();
        }

        // The assembled car's moving joints can push even an airborne chassis
        // while an Update-only velocity fixture is running. A constrained,
        // analytic source at physics time isolates replication from that motion.
        // This is component QA, never evidence of a normal engine-powered drive.
        internal static void FixedUpdate()
        {
            if (!prepared || road || car != "SATSUMA" || clock == 0 || stage < 0 || stage >= 2 || body == null
                || body.isKinematic || VehicleSync.LocalDrivingRoot != body.transform) return;
            float elapsed = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - clock).TotalSeconds;
            if ((int)(elapsed / 40f) != stage) return;
            // Pose and outgoing timestamp must describe the same physics step.
            // UTC advances during slow frames/physics catch-up; using it here
            // creates artificial jumps in the source even at constant speed.
            float t = phaseAtStageStart + Time.fixedTime - fixedStageStart;
            body.position = motionOrigin + Vector3.forward * (Mathf.Clamp(t - 19, 0, 10) * 3f);
            body.velocity = t >= 19 && t < 29 ? Vector3.forward * 3f : Vector3.zero;
            body.angularVelocity = Vector3.zero;
            PlaceBodies(body.velocity);
        }
    }

    // Explicit road fixture: supplies pedals/gear to the game's controller.
    // Engine combustion, wheels, collisions and the rigidbodies stay native.
    public sealed class MscRoadCarController : AxisCarController
    {
        public bool Driving;
        protected override void GetInput(out float throttle, out float brake, out float steer, out float handbrake,
            out float clutch, out bool startEngine, out int targetGear)
        {
            throttle = Driving ? .4f : 0; brake = Driving ? 0 : .5f; steer = handbrake = clutch = 0; startEngine = false;
            Drivetrain engine = GetComponent<Drivetrain>(); targetGear = Driving ? engine.first : engine.neutral;
        }
    }
}
