using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;
using MWCoop.Net;

namespace MWCoop
{
    public static class MscEngineLifecycleProbe
    {
        static bool described;
        static long clock;
        static readonly HashSet<int> actions = new HashSet<int>();
        static string channel;
        static PlayMakerFSM keyFsm, starter, jank, shutOff, sound, simulation, gear;
        static GameObject starterSound;
        static FsmFloat charge;
        static void Record(string key, bool pass, string detail)
        { MscProbeFiles.WriteAllText(Path.Combine(channel, key + "-" + Session.LocalId + ".txt"), "Pass=" + pass + ";" + detail); }
        static bool At(int id, float t, float due) { return t >= due && actions.Add(id); }
        public static void Update()
        {
            channel = MscRuntime.Argument("--summercoop-test-engine-lifecycle");
            if (channel.Length == 0 || Time.timeSinceLevelLoad < 25 || VehicleSync.Count == 0) return;
            if (!described) Describe();
            string start = Path.Combine(channel, "start.txt");
            if (clock == 0)
            {
                MscProbeFiles.WriteAllText(Path.Combine(channel, "ready-" + Session.LocalId + ".txt"), "ready");
                if (Session.IsHost && File.Exists(Path.Combine(channel, "ready-1.txt")) && File.Exists(Path.Combine(channel, "ready-2.txt")) && !File.Exists(start))
                    MscProbeFiles.WriteAllText(start, DateTime.UtcNow.AddSeconds(5).Ticks.ToString());
                if (!File.Exists(start) || !long.TryParse(MscProbeFiles.ReadAllText(start), out clock)) return;
            }
            float elapsed = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - clock).TotalSeconds;
            if (elapsed < 0) return;
            int stage = (int)(elapsed / 32f); if (stage >= 4) return;
            float t = elapsed - stage * 32f; int actor = stage % 2 == 0 ? 1 : 0, n = stage * 20;
            Rigidbody car = VehicleSync.Body(stage < 2 ? "SATSUMA" : "BOAT");
            if (car == null) throw new InvalidOperationException("Lifecycle fixture missing vehicle");
            if (At(n, t, 1) && Session.LocalId == actor)
            {
                Transform player = Game.PlayerT;
                var cc = player.GetComponent<CharacterController>(); if (cc != null) cc.enabled = false;
                player.position = car.transform.position + car.transform.right * 2f + Vector3.up;
                if (cc != null) cc.enabled = true;
                if (stage < 2 && charge != null) charge.Value = 150;
            }
            if (stage < 2)
            {
                if (At(n + 1, t, 3) && Session.LocalId == actor)
                {
                    Game.SetState(keyFsm, "ACC on"); keyFsm.SendEvent("START");
                    // The default isolated save has an unassembled Satsuma.
                    // Enter its real crank state with a charged battery; fuel
                    // remains unavailable, so combustion is not faked.
                    starter.FsmVariables.FindFsmBool("ShutOff").Value = false;
                    starter.FsmVariables.FindFsmBool("Starting").Value = true;
                    Game.SetState(starter, "Turn key");
                }
                if (At(n + 2, t, 8)) Record("car-crank-" + stage,
                    VehicleSync.Authority(car) == actor && starterSound.activeSelf && VehicleSync.LocalDriving < 0
                        && starter.FsmVariables.FindFsmBool("Starting").Value && (Session.LocalId == actor ? starter.enabled : !starter.enabled),
                    "Owner=" + VehicleSync.Authority(car) + ";Sound=" + starterSound.activeSelf + ";State=" + starter.ActiveStateName
                        + ";Applied=" + MscEngineSync.AppliedState(car.transform) + ";Enabled=" + starter.enabled + ";Charge=" + (charge != null ? charge.Value.ToString("F2") : "?"));
                if (At(n + 3, t, 11) && Session.LocalId == actor) keyFsm.SendEvent("FINISHED");
                if (At(n + 4, t, 15)) Record("car-release-" + stage, !starterSound.activeSelf && !starter.FsmVariables.FindFsmBool("Starting").Value,
                    "Sound=" + starterSound.activeSelf + ";State=" + starter.ActiveStateName + ";Applied=" + MscEngineSync.AppliedState(car.transform));
                if (At(n + 5, t, 18) && Session.LocalId == actor) Game.SetState(keyFsm, "Motor OFF");
            }
            else
            {
                if (At(n + 1, t, 3) && Session.LocalId == actor)
                {
                    jank.enabled = true; jank.FsmVariables.FindFsmFloat("Distance").Value = .8f;
                    Game.SetState(jank, "Wait player");
                }
                if (At(n + 2, t, 8)) Record("boat-start-" + stage,
                    VehicleSync.Authority(car) == actor && sound.FsmVariables.FindFsmBool("Start").Value
                        && MscEngineSync.PullCount(car.transform) == (stage == 2 ? 1 : 3) && car.transform.Find("GFX/Audio/Engine/1").gameObject.activeInHierarchy,
                    "Owner=" + VehicleSync.Authority(car) + ";Running=" + sound.FsmVariables.FindFsmBool("Start").Value + ";State=" + sound.ActiveStateName
                        + ";Pulls=" + MscEngineSync.PullCount(car.transform) + ";RPM=" + sound.FsmVariables.FindFsmFloat("RPM").Value);
                if (At(n + 8, t, 9) && Session.LocalId == actor) Game.SetState(shutOff, "Shut Off");
                if (At(n + 9, t, 11)) Record("boat-outside-stop-" + stage, !sound.FsmVariables.FindFsmBool("Start").Value,
                    "Running=" + sound.FsmVariables.FindFsmBool("Start").Value + ";Owner=" + VehicleSync.Authority(car));
                if (At(n + 10, t, 12) && Session.LocalId == actor)
                { jank.enabled = true; jank.FsmVariables.FindFsmFloat("Distance").Value = .8f; Game.SetState(jank, "Wait player"); }
                if (At(n + 3, t, 15) && Session.LocalId == actor) VehicleSync.TestEnter("BOAT", false);
                if (t >= 16 && t < 20 && Session.LocalId == actor && VehicleSync.LocalDrivingRoot != car.transform && Time.frameCount % 15 == 0)
                { VehicleSync.TestEnter("BOAT", false); VehicleSync.TestEnter("BOAT", true); }
                if (At(n + 11, t, 19) && Session.LocalId == 2) Game.SetState(gear, "First");
                if (At(n + 4, t, 21)) Record("boat-drive-" + stage,
                    VehicleSync.Authority(car) == actor && sound.FsmVariables.FindFsmBool("Start").Value
                        && (Session.LocalId == actor ? VehicleSync.LocalDrivingRoot == car.transform && simulation.enabled : !simulation.enabled && car.isKinematic),
                    "Owner=" + VehicleSync.Authority(car) + ";Running=" + sound.FsmVariables.FindFsmBool("Start").Value
                        + ";Simulation=" + simulation.enabled + ";Driver=" + (VehicleSync.LocalDrivingRoot == car.transform));
                if (At(n + 12, t, 21)) Record("boat-gear-" + stage, simulation.FsmVariables.FindFsmFloat("Gear").Value == -10
                    && gear.FsmVariables.FindFsmInt("SelectedGear").Value == 1,
                    "Ratio=" + simulation.FsmVariables.FindFsmFloat("Gear").Value + ";Selected=" + gear.FsmVariables.FindFsmInt("SelectedGear").Value);
                if (At(n + 13, t, 22) && Session.LocalId == 2) Game.SetState(gear, "Neutral");
                if (At(n + 5, t, 23) && Session.LocalId == actor) Game.SetState(shutOff, "Shut Off");
                if (At(n + 6, t, 27)) Record("boat-stop-" + stage, !sound.FsmVariables.FindFsmBool("Start").Value
                    && !car.transform.Find("GFX/Audio/Engine/1").gameObject.activeInHierarchy,
                    "Running=" + sound.FsmVariables.FindFsmBool("Start").Value + ";State=" + sound.ActiveStateName);
                if (At(n + 7, t, 29) && Session.LocalId == actor)
                { car.velocity = car.angularVelocity = Vector3.zero; VehicleSync.ExitLocal(); }
            }
        }
        static void Describe()
        {
            described = true;
            var text = new StringBuilder();
            foreach (string key in new[] { "SATSUMA", "HAYOSIKO", "KEKMET", "BOAT" })
            {
                Rigidbody body = VehicleSync.Body(key); if (body == null) continue;
                foreach (PlayMakerFSM f in body.GetComponentsInChildren<PlayMakerFSM>(true))
                    if (key == "BOAT" || f.FsmName == "Starter" || MscVehicleControls.IsIgnition(f))
                    {
                        text.AppendLine("==== " + Recon.Path(f.transform) + "::" + f.FsmName + ";Enabled=" + f.enabled + ";Active=" + f.gameObject.activeInHierarchy);
                        Recon.AppendFsm(text, "", f, true);
                    }
                foreach (AudioSource a in body.GetComponentsInChildren<AudioSource>(true))
                    text.AppendLine("AUDIO " + Recon.Path(a.transform) + ";Active=" + a.gameObject.activeSelf + ";Loop=" + a.loop + ";Playing=" + a.isPlaying + ";Pitch=" + a.pitch + ";Volume=" + a.volume);
            }
            MscProbeFiles.WriteAllText(Path.Combine(channel, "native-" + Session.LocalId + ".txt"), text.ToString());
            Rigidbody satsuma = VehicleSync.Body("SATSUMA"), boat = VehicleSync.Body("BOAT");
            foreach (PlayMakerFSM f in satsuma.GetComponentsInChildren<PlayMakerFSM>(true))
            { if (MscVehicleControls.IsIgnition(f)) keyFsm = f; if (f.FsmName == "Starter") starter = f; }
            starterSound = satsuma.transform.Find("Sounds/StarterSound").gameObject;
            FsmGameObject battery = starter.FsmVariables.FindFsmGameObject("db_Battery");
            charge = Game.FsmOn(battery.Value, "Data").FsmVariables.FindFsmFloat("Charge");
            foreach (PlayMakerFSM f in boat.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (f.FsmName == "Jank") jank = f;
                if (f.FsmName == "Use" && f.name == "ShutOff") shutOff = f;
                if (f.FsmName == "Sound Engine") sound = f;
                if (f.FsmName == "Simulation" && f.name == "Engine") simulation = f;
                if (f.FsmName == "Use" && f.name == "Gear") gear = f;
            }
        }
    }
}
