using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Disposable wired receiver, native driver/passenger seats and native
    // command states. No fixture runs in a normal multiplayer session.
    public static class MscStereoPassengerProbe
    {
        static readonly HashSet<int> steps = new HashSet<int>();
        static PlayMakerFSM volume, mode, tracks, station, eject, slot, playback, status;
        static Transform knob;
        static Quaternion acceptedRotation;
        static Rigidbody car;
        static bool At(int id, float t, float when) { return t >= when && steps.Add(id); }
        static PlayMakerFSM Find(string name, string kind, string path)
        {
            foreach (UnityEngine.Object obj in Game.AllFsms())
            {
                PlayMakerFSM f = obj as PlayMakerFSM;
                if (f != null && f.hideFlags == HideFlags.None && f.name == name && f.FsmName == kind && Recon.Path(f.transform).Contains(path)) return f;
            }
            throw new Exception("Missing passenger stereo fixture: " + name + "::" + kind);
        }
        static void Awake(Transform t) { for (; t != null; t = t.parent) t.gameObject.SetActive(true); }
        static bool Near(float value) { return Mathf.Abs(volume.FsmVariables.GetFsmFloat("Volume").Value - value) < .001f; }
        static string Detail()
        {
            return "Driver=" + VehicleSync.LocalDrivingKey + ";Passenger=" + Seats.SeatedCarKey + ";Authority=" + VehicleSync.Authority(car)
                + ";Volume=" + volume.FsmVariables.GetFsmFloat("Volume").Value + ";Power=" + mode.enabled
                + ";Playing=" + playback.GetComponent<AudioSource>().isPlaying + ";Track=" + playback.FsmVariables.GetFsmInt("CurrentTrack").Value
                + ";Radio=" + mode.FsmVariables.GetFsmBool("RadioOn").Value + ";Knob=" + knob.localEulerAngles
                + ";Distance=" + Vector3.Distance(PlayerSync.LocalCamera.position, volume.transform.position);
        }
        static float Reach()
        {
            float reach = float.MaxValue;
            foreach (PlayMakerFSM f in new[] { volume, mode, tracks, station, eject, slot })
                foreach (FsmState state in f.FsmStates)
                    foreach (FsmStateAction action in FireTools.Actions(f, state) ?? new FsmStateAction[0])
                    {
                        MousePickEvent pick = action as MousePickEvent;
                        if (pick != null) reach = Mathf.Min(reach, pick.rayDistance.Value);
                    }
            return reach;
        }
        static void StaleVisual(int driver)
        {
            // A delayed dashboard sample from the driver must not undo an
            // accepted passenger turn. Exercise the real receiver/LateUpdate.
            Peer sender = null;
            foreach (Peer peer in Session.T.Peers) if (peer.Id == (Session.IsHost ? driver : 0)) sender = peer;
            if (sender == null) throw new Exception("Missing stereo QA peer");
            byte[] packet = new NetWriter(Msg.CarVisual).U8(driver).Str(VehicleSync.KeyOf(car.transform))
                .Str(VehicleSync.RelPath(car.transform, knob) + "#0").U8(1).Vec(knob.localPosition).Quat(Quaternion.Euler(0, 15, 0)).ToArray();
            CarVisuals.OnMessage(sender, new NetReader(packet, 1, packet.Length - 1));
        }
        public static void Tick(float elapsed, Action<string, bool, string> record)
        {
            if (At(0, elapsed, 0))
            {
                car = VehicleSync.Body("SATSUMA");
                volume = Find("RadioVolume", "Knob", "cd player(Clone)");
                mode = Find("RadioCDSwitch", "Use", "cd player(Clone)");
                tracks = Find("TrackChannelSwitch", "ChangeTrack", "cd player(Clone)");
                station = Find("TrackChannelSwitch", "ChangeChannel", "cd player(Clone)");
                eject = Find("Eject", "Use", "cd player(Clone)");
                slot = Find("trigger_disc", "Assembly", "cd player(Clone)");
                playback = Find("CDPlayer", "Update", "SATSUMA");
                Transform device = volume.transform;
                while (device.name != "cd player(Clone)") device = device.parent;
                // The stock test save has a boxed, uninstalled CD player.
                // Put its native controls in the cabin without modifying saves.
                device.parent = car.transform; device.localPosition = new Vector3(0, .55f, .45f); device.localRotation = Quaternion.identity;
                foreach (Rigidbody body in device.GetComponentsInChildren<Rigidbody>(true)) body.isKinematic = true;
                Awake(device); Awake(playback.transform);
                foreach (FsmStateAction action in FireTools.Actions(volume, volume.Fsm.GetState("Wait1")))
                {
                    SetRotation rotate = action as SetRotation;
                    if (rotate != null && rotate.gameObject.GameObject.Value != null) knob = rotate.gameObject.GameObject.Value.transform;
                }
                if (knob == null) throw new Exception("Native CD volume knob target is missing");
                status = Game.FsmOn(Game.FindAny("Database/PartsStatus/Radio"), "Status"); status.enabled = false;
                Game.FsmOn(Game.FindAny("Database/DatabaseWiring/WiringSpeakers"), "Data").FsmVariables.GetFsmBool("Installed").Value = true;
                PlayMakerFSM fetch = Game.FsmOn(playback.gameObject, "Fetch"); fetch.enabled = true; Game.SetState(fetch, "Fetch");
                volume.enabled = true; volume.FsmVariables.GetFsmFloat("Volume").Value = 0; Game.SetState(volume, "Mouse off 2");
                station.FsmVariables.GetFsmBool("Channel").Value = false;
                if (Session.IsHost && mode.FsmVariables.GetFsmBool("RadioOn").Value) Game.SetState(mode, "Switch");
                typeof(CarVisuals).GetMethod("Scan", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            }
            status.FsmVariables.GetFsmBool("CDPlayerOK").Value = true;
            status.FsmVariables.GetFsmBool("RadioOK").Value = true;
            int stage = (int)(elapsed / 48); float t = elapsed - stage * 48;
            if (stage >= 2)
            {
                if (At(999, elapsed, 98)) record("passenger-stereo-foot-range", Reach() <= 1.01f, "Reach=" + Reach());
                return;
            }
            int driver = stage, passenger = 1 - driver, id = 100 + stage * 100;
            string label = "passenger-stereo-" + stage + "-";
            if (t >= 1 && t < 7 && Session.LocalId == driver && VehicleSync.LocalDrivingRoot != car.transform && Time.frameCount % 15 == 0)
            {
                foreach (PlayMakerFSM f in car.GetComponentsInChildren<PlayMakerFSM>(true))
                    if (f.FsmName == "PlayerTrigger" && f.name.StartsWith("DriveTrigger")) { Awake(f.transform); f.enabled = true; }
                VehicleSync.TestEnter("SATSUMA", false); VehicleSync.TestEnter("SATSUMA", true);
            }
            if (At(id, t, 8) && Session.LocalId == passenger) Seats.TestSit("SATSUMA", 0);
            if (At(id + 1, t, 9)) record(label + "roles", VehicleSync.Authority(car) == driver
                && (Session.LocalId == driver ? VehicleSync.LocalDrivingRoot == car.transform : Seats.SeatedCar == car.transform && VehicleSync.LocalDriving < 0), Detail());
            if (At(id + 2, t, 9.5f)) record(label + "reach", Reach() >= 2.49f, "Reach=" + Reach() + ";" + Detail());
            for (int i = 0; i < 3; i++) if (At(id + 3 + i, t, 10 + i * .4f) && Session.LocalId == passenger) Game.SetState(volume, "On");
            if (At(id + 24, t, 12.5f)) acceptedRotation = knob.localRotation;
            if (t >= 13 && t < 14 && Session.LocalId == passenger) StaleVisual(driver);
            if (At(id + 6, t, 14)) record(label + "volume-and-visual", Near(.3f) && mode.enabled
                && Quaternion.Angle(knob.localRotation, acceptedRotation) < 1, Detail());
            if (At(id + 7, t, 15) && Session.LocalId == passenger)
            { slot.FsmVariables.GetFsmGameObject("Part").Value = MscCd.DiscAt(2); Game.SetState(slot, "Assemble"); }
            if (At(id + 8, t, 18)) record(label + "cd-start", playback.GetComponent<AudioSource>().isPlaying && tracks.enabled, Detail());
            if (At(id + 9, t, 19) && Session.LocalId == passenger) Game.SetState(tracks, "State 1");
            if (At(id + 10, t, 22)) record(label + "next-track", playback.GetComponent<AudioSource>().isPlaying
                && playback.FsmVariables.GetFsmInt("CurrentTrack").Value == 2, Detail());
            if (At(id + 11, t, 23) && Session.LocalId == passenger) Game.SetState(mode, "Switch");
            if (At(id + 12, t, 26)) record(label + "radio-mode", mode.FsmVariables.GetFsmBool("RadioOn").Value && station.enabled && !tracks.enabled
                && Radio.PlayingIn(car.transform), Detail() + ";" + Radio.GhostState());
            if (At(id + 13, t, 27) && Session.LocalId == passenger) Game.SetState(station, "Check status");
            if (At(id + 14, t, 30)) record(label + "station", station.FsmVariables.GetFsmBool("Channel").Value == (stage == 0), Detail());
            if (At(id + 15, t, 31) && Session.LocalId == passenger) Game.SetState(mode, "Switch");
            if (At(id + 16, t, 34)) record(label + "cd-resume", !mode.FsmVariables.GetFsmBool("RadioOn").Value && tracks.enabled && playback.GetComponent<AudioSource>().isPlaying, Detail());
            if (At(id + 17, t, 35) && Session.LocalId == passenger) Game.SetState(eject, "Switch");
            if (At(id + 18, t, 38)) record(label + "eject", !playback.GetComponent<AudioSource>().isPlaying
                && !MscCd.Mounted(MscCd.DiscAt(2)), Detail());
            for (int i = 0; i < 3; i++) if (At(id + 19 + i, t, 39 + i * .4f) && Session.LocalId == passenger) Game.SetState(volume, "Decrease");
            if (At(id + 22, t, 43)) record(label + "power-off", Near(0) && !mode.enabled && !tracks.enabled && !station.enabled && !playback.GetComponent<AudioSource>().isPlaying, Detail());
            if (At(id + 23, t, 44)) { Seats.TestLeave(); if (Session.LocalId == driver) VehicleSync.ExitLocal(); }
        }
    }
}
