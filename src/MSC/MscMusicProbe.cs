using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HutongGames.PlayMaker;
using UnityEngine;
using MWCoop.Net;

namespace MWCoop
{
    // Invoked only by --summercoop-test-summerfixes, in disposable profiles.
    public static class MscMusicProbe
    {
        static readonly HashSet<int> steps = new HashSet<int>();
        static PlayMakerFSM device, playback;
        static AudioSource cdAudio, radioAudio;
        static MscAudioStream copyA, copyB;
        static AudioSource audioA, audioB;
        static float sampleAt, maxRadioError;
        static int radioSamples;
        static string cdTrace;
        static bool sampleMismatch;
        static readonly List<PlayMakerFSM> powerKnobs = new List<PlayMakerFSM>();
        static bool At(int id, float t, float when) { return t >= when && steps.Add(id); }
        static PlayMakerFSM Find(string obj, string fsm)
        {
            foreach (UnityEngine.Object value in Game.AllFsms())
            { PlayMakerFSM f = value as PlayMakerFSM; if (f != null && f.name == obj && f.FsmName == fsm && f.hideFlags == HideFlags.None) return f; }
            throw new Exception("Missing native music control: " + obj + "::" + fsm);
        }
        static Transform Case(int index) { return Game.FindAny("ITEMS/CDs/cd case(item" + index + ")").transform; }
        static void Awake(Transform t) { for (; t != null; t = t.parent) t.gameObject.SetActive(true); }
        static int Location(GameObject disc)
        {
            for (Transform t = disc.transform.parent; t != null; t = t.parent)
            { if (t.name.StartsWith("cd case(item", StringComparison.Ordinal)) return 1; if (t.name == "cd player(Clone)") return 2; }
            return 0;
        }
        static void Sample(string path, AudioSource audio)
        { MscProbeFiles.WriteAllText(path, DateTime.UtcNow.Ticks + "|" + MscAudioStream.Time(audio).ToString("R", CultureInfo.InvariantCulture) + "|" + audio.isPlaying + "|" + MscMusic.ClipKey(audio.clip)); }
        static float Error(string path, AudioSource audio)
        {
            if (audio == null || !audio.isPlaying)
            { if (!sampleMismatch) { sampleMismatch = true; Log.Warn("Music QA sample unavailable: playing=" + (audio != null && audio.isPlaying)); } return 999; }
            // OpenSnapshot retries Windows' transient rename boundary; an
            // Exists check can observe that gap before the retrying reader.
            string[] fields = MscProbeFiles.ReadAllText(path).Split('|'); long ticks; float time;
            if (fields.Length != 4 || !long.TryParse(fields[0], out ticks) || !float.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out time) || fields[2] != "True" || fields[3] != MscMusic.ClipKey(audio.clip))
            { if (!sampleMismatch) { sampleMismatch = true; Log.Warn("Music QA sample differs: host=" + string.Join("|", fields) + ";local=" + MscMusic.ClipKey(audio.clip) + ";time=" + MscAudioStream.Time(audio)); } return 999; }
            float elapsed = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - ticks).TotalSeconds;
            if (elapsed > 1) { if (!sampleMismatch) { sampleMismatch = true; Log.Warn("Music QA stale sample: " + elapsed); } return 999; }
            return Mathf.Abs(MscAudioStream.Time(audio) - time - elapsed);
        }
        public static void Tick(float t, string channel, Action<string, bool, string> record)
        {
            if (At(0, t, 114))
            {
                device = Find("trigger_disc", "Assembly"); Awake(device.transform);
                playback = Find("CDPlayer", "Update"); Awake(playback.transform);
                cdAudio = playback.GetComponent<AudioSource>();
                // Supply real wired/powered conditions for native controls.
                PlayMakerFSM fetch = Game.FsmOn(playback.gameObject, "Fetch"); if (fetch != null) fetch.enabled = false;
                // These isolated fixtures have no wired/powered receiver. Its
                // stock knobs otherwise send STOP CD every two seconds.
                foreach (UnityEngine.Object value in Game.AllFsms())
                {
                    PlayMakerFSM f = value as PlayMakerFSM; if (f == null || f.FsmName != "Knob") continue;
                    FsmState off = f.Fsm.GetState("Off 2"); if (off == null) continue;
                    bool stop = false;
                    foreach (FsmStateAction action in FireTools.Actions(f, off) ?? new FsmStateAction[0])
                        if (action is HutongGames.PlayMaker.Actions.SendEventByName)
                            foreach (var field in action.GetType().GetFields())
                            { FsmString text = field.GetValue(action) as FsmString; if (text != null && text.Value == "STOP CD") stop = true; }
                    if (stop) { powerKnobs.Add(f); f.enabled = false; }
                }
                PlayMakerFSM mode = Find("RadioCDSwitch", "Use");
                if (Session.IsHost && mode.FsmVariables.GetFsmBool("RadioOn").Value) Game.SetState(mode, "Switch");
                if (Session.IsHost) { playback.enabled = true; Game.SetState(playback, "State 1"); }
                for (int i = 1; i <= 3; i++) Awake(Case(i));
            }
            if (t >= 114 && t < 166)
            {
                GameObject status = Game.FindAny("Database/PartsStatus/Radio");
                PlayMakerFSM checks = status == null ? null : Game.FsmOn(status, "Status");
                FsmBool power = checks == null ? null : checks.FsmVariables.FindFsmBool("CDPlayerOK"); if (power != null) power.Value = true;
                if (t >= 153.5f && t < 159)
                {
                    GameObject disc = MscCd.DiscAt(3); PlayMakerFSM native = Game.FsmOn(disc, "Player"), removal = Game.FsmOn(disc, "Removal");
                    string trace = Recon.Path(disc.transform) + ";Player=" + native.ActiveStateName + "/" + native.enabled + ";Removal=" + removal.ActiveStateName + "/" + removal.enabled + ";Slot=" + device.ActiveStateName + ";Part=" + Props.CdKey(device.FsmVariables.GetFsmGameObject("Part").Value);
                    if (trace != cdTrace) { cdTrace = trace; Log.Info("CD3 QA " + trace); }
                }
            }
            for (int i = 1; i <= 3; i++)
            {
                float when = 115 + (i - 1) * 16; bool actor = Session.IsHost == (i != 2); int id = i * 20;
                if (At(id, t, when) && actor) { PlayMakerFSM open = Game.FsmOn(Case(i).gameObject, "Open"); if (!open.FsmVariables.GetFsmBool("Open").Value) Game.SetState(open, "Bool test"); }
                if (At(id + 1, t, when + 1) && actor) Game.SetState(Game.FsmOn(MscCd.DiscAt(i), "Removal"), "Remove part");
                if (At(id + 2, t, when + 3)) record("cd-" + i + "-remove-case", Location(MscCd.DiscAt(i)) == 0, "Location=" + Location(MscCd.DiscAt(i)));
                if (At(id + 3, t, when + 3.2f) && actor)
                { PlayMakerFSM mount = Game.FsmOn(Case(i).Find("trigger_cd").gameObject, "Assembly"); mount.FsmVariables.GetFsmGameObject("Part").Value = MscCd.DiscAt(i); Game.SetState(mount, "Assemble"); }
                if (At(id + 4, t, when + 5)) record("cd-" + i + "-return-case", Location(MscCd.DiscAt(i)) == 1, "Location=" + Location(MscCd.DiscAt(i)));
                if (At(id + 5, t, when + 5.2f) && actor) Game.SetState(Game.FsmOn(MscCd.DiscAt(i), "Removal"), "Remove part");
                if (At(id + 6, t, when + 6.5f) && actor) { device.FsmVariables.GetFsmGameObject("Part").Value = MscCd.DiscAt(i); Game.SetState(device, "Assemble"); }
                if (At(id + 7, t, when + 8.5f) && Session.IsHost)
                {
                    playback.FsmVariables.GetFsmInt("Index").Value = 0;
                    FsmGameObject database = FsmVariables.GlobalVariables.FindFsmGameObject("SongDatabaseCD");
                    Log.Info("CD QA start " + i + ";CurrentCD=" + playback.FsmVariables.GetFsmString("CurrentCD").Value + ";Database=" + (database == null || database.Value == null ? "NULL" : Recon.Path(database.Value.transform)) + ";Pause=" + AudioListener.pause + ";Enabled=" + cdAudio.enabled + ";Active=" + cdAudio.gameObject.activeInHierarchy);
                    playback.SendEvent("PLAY CD");
                }
                if (i == 2 && At(101, t, when + 9.5f) && !Session.IsHost) Game.SetState(Find("TrackChannelSwitch", "ChangeTrack"), "State 1");
                if (t >= when + 9 && t < when + 12 && Session.IsHost) Sample(Path.Combine(channel, "cd-time.txt"), cdAudio);
                if (At(id + 8, t, when + 11))
                {
                    float error = Session.IsHost ? 0 : Error(Path.Combine(channel, "cd-time.txt"), cdAudio);
                    record("cd-" + i + "-playback", Location(MscCd.DiscAt(i)) == 2 && cdAudio.isPlaying && error <= .5f && MscMusic.ClipKey(cdAudio.clip).StartsWith("msc-music:CD" + i + "/"), "Location=" + Location(MscCd.DiscAt(i)) + ";Playing=" + cdAudio.isPlaying + ";Error=" + error + ";Key=" + MscMusic.ClipKey(cdAudio.clip) + ";Parent=" + Recon.Path(MscCd.DiscAt(i).transform) + ";Slot=" + device.ActiveStateName + ";AudioState=" + playback.ActiveStateName + ";Index=" + playback.FsmVariables.GetFsmInt("Index").Value + ";Max=" + playback.FsmVariables.GetFsmInt("MaxTracks").Value + ";CD=" + playback.FsmVariables.GetFsmString("CurrentCD").Value);
                    if (i == 2) record("cd-next-track", playback.FsmVariables.GetFsmInt("CurrentTrack").Value == 2 && MscMusic.ClipKey(cdAudio.clip).StartsWith("msc-music:CD2/track2.ogg:"), "Track=" + playback.FsmVariables.GetFsmInt("CurrentTrack").Value + ";Key=" + MscMusic.ClipKey(cdAudio.clip));
                }
                if (At(id + 9, t, when + 12) && actor) Game.SetState(Find("Eject", "Use"), "Switch");
                if (At(id + 10, t, when + 15)) record("cd-" + i + "-eject", Location(MscCd.DiscAt(i)) == 0 && !cdAudio.isPlaying, "Location=" + Location(MscCd.DiscAt(i)) + ";Playing=" + cdAudio.isPlaying);
            }
            if (At(102, t, 163.2f) && !Session.IsHost) Game.SetState(Find("RadioCDSwitch", "Use"), "Switch");
            if (At(103, t, 164)) record("cd-switch-radio", Find("RadioCDSwitch", "Use").FsmVariables.GetFsmBool("RadioOn").Value && !cdAudio.isPlaying, "RadioOn=" + Find("RadioCDSwitch", "Use").FsmVariables.GetFsmBool("RadioOn").Value);
            if (At(104, t, 164.2f) && Session.IsHost) Game.SetState(Find("RadioCDSwitch", "Use"), "Switch");
            if (At(105, t, 165.2f)) record("cd-switch-back", !Find("RadioCDSwitch", "Use").FsmVariables.GetFsmBool("RadioOn").Value, "RadioOn=" + Find("RadioCDSwitch", "Use").FsmVariables.GetFsmBool("RadioOn").Value);
            if (At(90, t, 166))
            {
                PlayMakerFSM fetch = Find("RadioChannels", "Fetch"); fetch.enabled = false; Awake(fetch.transform);
                radioAudio = fetch.transform.Find("Folk").GetComponent<AudioSource>(); radioAudio.gameObject.SetActive(true);
                PlayMakerFSM update = Game.FsmOn(radioAudio.gameObject, "Update"); update.enabled = false;
                if (Session.IsHost) { radioAudio.clip = (AudioClip)MscMusic.Songs("Radio")[1]; radioAudio.Play(); MscAudioStream.Seek(radioAudio, 7); }
            }
            if (At(91, t, 169))
            {
                string key = MscMusic.ClipKey((AudioClip)MscMusic.Songs("Radio")[1]); string file = MscMusic.FileFor(key);
                copyA = new MscAudioStream(file, key); copyB = new MscAudioStream(file, key);
                audioA = new GameObject("SummerCoop QA receiver A").AddComponent<AudioSource>(); audioB = new GameObject("SummerCoop QA receiver B").AddComponent<AudioSource>();
            }
            if (copyA != null) { copyA.Apply(audioA, radioAudio.isPlaying, MscAudioStream.Time(radioAudio)); copyB.Apply(audioB, radioAudio.isPlaying, MscAudioStream.Time(radioAudio)); }
            if (t >= (Session.IsHost ? 171 : 177) && t < 187 && t >= sampleAt)
            {
                sampleAt = t + .2f;
                if (Session.IsHost) Sample(Path.Combine(channel, "radio-time.txt"), radioAudio);
                else { maxRadioError = Mathf.Max(maxRadioError, Error(Path.Combine(channel, "radio-time.txt"), radioAudio)); radioSamples++; }
            }
            if (At(92, t, 188))
            {
                record("radio-playback-drift", radioAudio.isPlaying && (Session.IsHost || radioSamples > 30 && maxRadioError <= .5f), "Playing=" + radioAudio.isPlaying + ";Samples=" + radioSamples + ";MaxError=" + maxRadioError);
                record("independent-radio-streams", audioA.isPlaying && audioB.isPlaying && radioAudio.isPlaying && audioA.clip != audioB.clip && audioA.clip != radioAudio.clip && Mathf.Abs(MscAudioStream.Time(audioA) - MscAudioStream.Time(radioAudio)) <= .5f && Mathf.Abs(MscAudioStream.Time(audioB) - MscAudioStream.Time(radioAudio)) <= .5f, "A=" + MscAudioStream.Time(audioA) + ";B=" + MscAudioStream.Time(audioB) + ";Main=" + MscAudioStream.Time(radioAudio));
            }
            if (At(93, t, 190) && Session.IsHost)
            { radioAudio.Stop(); if (powerKnobs.Count > 0) Game.SetState(powerKnobs[0], "Off 2"); else playback.SendEvent("STOP CD"); }
            if (At(94, t, 193)) record("music-power-off", !radioAudio.isPlaying && !audioA.isPlaying && !audioB.isPlaying && !cdAudio.isPlaying, "Radio=" + radioAudio.isPlaying + ";CD=" + cdAudio.isPlaying);
        }
    }
}
