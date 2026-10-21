using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;
using MWCoop.Net;

namespace MWCoop
{
    // Component QA with native controls enabled. Only the absent electrical
    // installation is supplied by this disposable fixture.
    public static class MscStereoProbe
    {
        static readonly HashSet<int> steps = new HashSet<int>();
        static PlayMakerFSM volume, mode, tracks, playback, status, slot, opener;
        static float openerSpeed;
        static bool At(int id, float t, float when) { return t >= when && steps.Add(id); }
        static PlayMakerFSM Find(string name, string kind, string path)
        {
            foreach (UnityEngine.Object obj in Game.AllFsms())
            {
                PlayMakerFSM f = obj as PlayMakerFSM;
                if (f != null && f.hideFlags == HideFlags.None && f.name == name && f.FsmName == kind && Recon.Path(f.transform).Contains(path)) return f;
            }
            throw new Exception("Missing stereo fixture: " + path + "/" + name + "::" + kind);
        }
        public static void Tick(float t, Action<string, bool, string> record)
        {
            if (t < 195) return;
            if (At(0, t, 195))
            {
                volume = Find("RadioVolume", "Knob", "cd player(Clone)");
                mode = Find("RadioCDSwitch", "Use", "cd player(Clone)");
                tracks = Find("TrackChannelSwitch", "ChangeTrack", "cd player(Clone)");
                slot = Find("trigger_disc", "Assembly", "cd player(Clone)");
                playback = Find("CDPlayer", "Update", "SATSUMA");
                status = Game.FsmOn(Game.FindAny("Database/PartsStatus/Radio"), "Status");
                opener = Find("opener", "Knob", "HAYOSIKO");
                openerSpeed = opener.FsmVariables.GetFsmFloat("Speed").Value;
                status.enabled = false; status.FsmVariables.GetFsmBool("CDPlayerOK").Value = true;
                status.FsmVariables.GetFsmBool("RadioOK").Value = true;
                Game.FsmOn(Game.FindAny("Database/DatabaseWiring/WiringSpeakers"), "Data").FsmVariables.GetFsmBool("Installed").Value = true;
                PlayMakerFSM fetch = Game.FsmOn(playback.gameObject, "Fetch"); fetch.enabled = true; Game.SetState(fetch, "Fetch");
                volume.enabled = true; volume.FsmVariables.GetFsmFloat("Volume").Value = 0;
                Game.SetState(volume, "Mouse off 2");
            }
            status.FsmVariables.GetFsmBool("CDPlayerOK").Value = true;
            status.FsmVariables.GetFsmBool("RadioOK").Value = true;
            for (int i = 0; i < 6; i++)
                if (At(10 + i, t, 196 + i * .3f) && Session.IsHost) Game.SetState(volume, "On");
            if (At(20, t, 200)) record("stereo-cd-power-and-volume", Near(.6f) && mode.enabled, Detail());
            if (At(21, t, 201) && !Session.IsHost) Game.SetState(volume, "Decrease");
            if (At(22, t, 203))
            {
                record("stereo-guest-volume-down-stays-on", Near(.5f) && mode.enabled, Detail());
                record("stereo-volume-reaches-speakers", Mathf.Abs(playback.GetComponent<AudioSource>().volume - .5f) < .001f, Detail());
            }
            if (At(23, t, 204) && Session.IsHost)
            {
                if (mode.FsmVariables.GetFsmBool("RadioOn").Value) Game.SetState(mode, "Switch");
                slot.FsmVariables.GetFsmGameObject("Part").Value = MscCd.DiscAt(2);
                Game.SetState(slot, "Assemble");
            }
            if (At(24, t, 209)) record("stereo-native-cd-start", playback.GetComponent<AudioSource>().isPlaying && tracks.enabled, Detail());
            if (At(25, t, 210) && !Session.IsHost) Game.SetState(tracks, "State 1");
            if (At(26, t, 213)) record("stereo-native-next-track", playback.FsmVariables.GetFsmInt("CurrentTrack").Value == 2 && playback.GetComponent<AudioSource>().isPlaying, Detail());
            // Same-direction simultaneous wheel steps must both survive host
            // arbitration, including the return packet to their authors.
            if (At(27, t, 214)) Game.SetState(volume, "On");
            if (At(28, t, 217)) record("stereo-concurrent-turns", Near(.7f) && mode.enabled, Detail());
            if (At(29, t, 218) && !Session.IsHost)
            {
                volume.FsmVariables.GetFsmFloat("Volume").Value = .2f;
                opener.FsmVariables.GetFsmFloat("Speed").Value = 1234;
                Menu.TestResyncButton();
            }
            if (At(31, t, 219) && !Session.IsHost)
            {
                record("stereo-sync-button", MscResync.Requests > 0 && Menu.Open, "Requests=" + MscResync.Requests);
                Application.CaptureScreenshot(MscRuntime.Argument("--summercoop-test-report").Replace("-report.txt", "-sync-menu.png"));
            }
            if (At(30, t, 223))
            {
                float restored = opener.FsmVariables.GetFsmFloat("Speed").Value;
                record("stereo-full-sync-recovery", Near(.7f) && MscResync.Requests > 0 && mode.enabled && Mathf.Abs(restored - openerSpeed) < .001f,
                    Detail() + ";FsmSpeed=" + restored);
                Menu.Open = false;
            }
            for (int i = 0; i < 7; i++)
                if (At(40 + i, t, 224 + i * .3f) && !Session.IsHost) Game.SetState(volume, "Decrease");
            if (At(48, t, 229))
                record("stereo-power-off-buttons", Near(0) && !playback.GetComponent<AudioSource>().isPlaying && !mode.enabled && !tracks.enabled
                    && !Find("TrackChannelSwitch", "ChangeChannel", "cd player(Clone)").enabled, Detail());
            string[] cars = { "HAYOSIKO", "GIFU", "FERNDALE", "KEKMET", "SATSUMA" };
            for (int i = 0; i < cars.Length; i++)
            {
                float at = 232 + i * 8; int id = 100 + i * 10;
                if (t < at || t >= at + 8) continue;
                PlayMakerFSM knob = Find("RadioVolume", "Knob", cars[i]);
                PlayMakerFSM tuner = Find("RadioTuner", "Knob", cars[i]);
                if (At(id, t, at))
                {
                    knob.enabled = true;
                    if (Session.IsHost) { knob.FsmVariables.GetFsmFloat("Volume").Value = 0; tuner.FsmVariables.GetFsmFloat("Tune").Value = .091f; }
                    Game.SetState(knob, "Mouse off 2");
                }
                if (At(id + 1, t, at + 2) && Session.IsHost) Game.SetState(knob, "Increase");
                if (At(id + 2, t, at + 3) && !Session.IsHost) Game.SetState(knob, "Increase");
                if (At(id + 3, t, at + 4) && !Session.IsHost) Game.SetState(knob, "Decrease");
                if (At(id + 4, t, at + 5) && !Session.IsHost) Game.SetState(tuner, "Increase");
                if (At(id + 5, t, at + 6) && Session.IsHost) Game.SetState(tuner, "Decrease");
                if (At(id + 6, t, at + 7))
                {
                    float v = knob.FsmVariables.GetFsmFloat("Volume").Value, tune = tuner.FsmVariables.GetFsmFloat("Tune").Value;
                    record("stereo-radio-" + cars[i], Mathf.Abs(v - .1f) < .001f && Mathf.Abs(tune - .091f) < .0001f && Radio.PlayingIn(VehicleSync.CarRoot(knob.transform)),
                        "Volume=" + v + ";Tune=" + tune + ";Playing=" + Radio.PlayingIn(VehicleSync.CarRoot(knob.transform)) + ";State=" + knob.ActiveStateName);
                }
            }
        }
        static bool Near(float value) { return Mathf.Abs(volume.FsmVariables.GetFsmFloat("Volume").Value - value) < .001f; }
        static string Detail()
        {
            return "Volume=" + volume.FsmVariables.GetFsmFloat("Volume").Value + ";Power=" + mode.enabled + ";Tracks=" + tracks.enabled
                + ";AudioVolume=" + playback.GetComponent<AudioSource>().volume + ";Playing=" + playback.GetComponent<AudioSource>().isPlaying + ";Track=" + playback.FsmVariables.GetFsmInt("CurrentTrack").Value
                + ";State=" + volume.ActiveStateName;
        }
    }
}
