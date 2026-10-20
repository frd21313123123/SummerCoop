using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HutongGames.PlayMaker;
using MWCoop.Net;
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
        static string entry;
        static readonly string car = "HAYOSIKO";
        static readonly HashSet<int> actions = new HashSet<int>();
        public static void Update()
        {
            string channel = MscRuntime.Argument("--summercoop-test-cabin-channel");
            if (channel.Length == 0 || Time.timeSinceLevelLoad < 25 || VehicleSync.Count == 0) return;
            if (body == null) body = VehicleSync.Body(car);
            if (body == null) throw new InvalidOperationException("Cabin QA van missing");
            if (!prepared)
            {
                prepared = true; gravity = body.useGravity; collisions = body.detectCollisions;
                body.useGravity = false; body.detectCollisions = false;
                Transform radioPivot = body.transform.Find("RadioPivot"), speaker = body.transform.Find("RadioPivot/Speaker");
                if (radioPivot != null) radioPivot.gameObject.SetActive(true);
                if (speaker != null) speaker.gameObject.SetActive(true);
                var lines = new List<string>();
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
                    body.position += Vector3.up * 2f;
                    MscProbeFiles.WriteAllText(start, DateTime.UtcNow.AddSeconds(8).Ticks.ToString(CultureInfo.InvariantCulture));
                }
                if (!File.Exists(start) || !long.TryParse(MscProbeFiles.ReadAllText(start), out clock)) return;
            }
            float elapsed = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - clock).TotalSeconds;
            if (elapsed < 0) return;
            int current = (int)(elapsed / 40f);
            if (current >= 2) { body.useGravity = gravity; body.detectCollisions = collisions; return; }
            float t = elapsed - current * 40f;
            int driver = current == 0 ? 1 : 0, passenger = current == 0 ? 0 : 1;
            if (current != stage)
            {
                stage = current; approached = sat = reported = false; samples = valid = 0;
                minY = float.MaxValue; maxY = float.MinValue; entry = "";
            }
            if (Session.LocalId == driver && !approached)
            { approached = true; entry = VehicleSync.TestEnter(car, false); }
            if (Session.LocalId == driver && t >= 1 && t < 10 && VehicleSync.LocalDrivingRoot != body.transform && Time.frameCount % 15 == 0)
            {
                // The vanilla LOD may still be asleep at the test teleport.
                foreach (PlayMakerFSM f in body.GetComponentsInChildren<PlayMakerFSM>(true))
                    if (f.FsmName == "PlayerTrigger" && f.name.StartsWith("DriveTrigger"))
                        for (Transform p = f.transform; p != null && p != body.transform.parent; p = p.parent) p.gameObject.SetActive(true);
                VehicleSync.TestEnter(car, false); entry = VehicleSync.TestEnter(car, true);
            }
            if (Session.LocalId == passenger && t >= 11 && !sat)
            { sat = true; entry = Seats.TestSit(car, 0); }
            if (Session.LocalId == driver && VehicleSync.LocalDrivingRoot == body.transform && !body.isKinematic)
            {
                body.velocity = t >= 19 && t < 29 ? Vector3.forward * 3f : Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            if (t >= 14 && actions.Add(current * 10) && Session.LocalId == passenger)
            {
                string tunerPath = Recon.Path(body.transform) + "/RadioPivot/Radio/TunerPivot/RadioTuner";
                string volumePath = Recon.Path(body.transform) + "/RadioPivot/Radio/ButtonsRadio/RadioVolume";
                PlayMakerFSM tuner = Game.FindFsm(tunerPath, "Knob"), volume = Game.FindFsm(volumePath, "Knob");
                if (tuner == null || volume == null) throw new InvalidOperationException("Native radio controls missing");
                tuner.FsmVariables.FindFsmFloat("Tune").Value = .091f;
                Jobs.TestSend(tunerPath + "::Knob", "Get scroll", "QA", "Wait1");
                volume.FsmVariables.FindFsmFloat("Volume").Value = .6f;
                string result = Jobs.TestSend(volumePath + "::Knob", "Mouse off 2", "ON", "Check tune");
                MscProbeFiles.WriteAllText(Path.Combine(channel, "radio-input-" + current + ".txt"), result
                    + ";Native=" + volume.ActiveStateName + ";Enabled=" + volume.enabled + ";Active=" + volume.gameObject.activeInHierarchy);
            }
            if (t >= 19 && actions.Add(current * 10 + 1))
                MscProbeFiles.WriteAllText(Path.Combine(channel, "radio-on-" + current + "-" + Session.LocalId + ".txt"), "Pass=" + Radio.PlayingIn(body.transform) + ";" + Radio.GhostState());
            if (t >= 22 && actions.Add(current * 10 + 2) && Session.LocalId == passenger) Radio.TestCut();
            if (t >= 28 && actions.Add(current * 10 + 3))
                MscProbeFiles.WriteAllText(Path.Combine(channel, "radio-off-" + current + "-" + Session.LocalId + ".txt"), "Pass=" + !Radio.PlayingIn(body.transform) + ";" + Radio.GhostState());
            if (t >= 14 && t < 29 && Time.realtimeSinceStartup >= next)
            {
                next = Time.realtimeSinceStartup + .05f;
                bool owner = VehicleSync.Authority(body) == driver;
                bool role = Session.LocalId == driver ? VehicleSync.LocalDrivingRoot == body.transform && !body.isKinematic
                    : VehicleSync.LocalDriving < 0 && body.isKinematic;
                if (Session.LocalId == passenger) role &= Seats.SeatedCar == body.transform;
                if (samples == 0 && Session.LocalId == passenger) cabinPosition = body.transform.InverseTransformPoint(Game.PlayerT.position);
                if (Session.LocalId == passenger) role &= (body.transform.InverseTransformPoint(Game.PlayerT.position) - cabinPosition).magnitude < .1f;
                samples++; if (owner && role) valid++;
                minY = Mathf.Min(minY, body.position.y); maxY = Mathf.Max(maxY, body.position.y);
            }
            if (t >= 30 && !reported)
            {
                reported = true;
                bool pass = samples >= 30 && valid == samples && maxY - minY < .25f;
                string result = "Pass=" + pass + ";Driver=" + driver + ";Local=" + Session.LocalId + ";Samples=" + valid + "/" + samples
                    + ";VerticalRange=" + (maxY - minY).ToString("F4", CultureInfo.InvariantCulture) + ";Entry=" + entry;
                MscProbeFiles.WriteAllText(Path.Combine(channel, "motion-" + current + "-" + Session.LocalId + ".txt"), result);
                if (Session.LocalId == passenger) Seats.TestLeave();
            }
            if (t >= 32 && Session.LocalId == driver && VehicleSync.LocalDriving >= 0) VehicleSync.ExitLocal();
        }
    }
}
