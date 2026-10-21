using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using UnityEngine;
using MWCoop.Net;
using SummerCoop.Game;

namespace MWCoop
{
    // Explicit QA only; normal games do not create fixtures or change state.
    public static class MscSummerFixProbe
    {
        static readonly HashSet<int> steps = new HashSet<int>();
        static readonly string[] panels = { "Door_Left", "Door_Right", "Bootlid", "Hood" };
        static long start;
        static string channel;
        static Transform aim;
        static Rigidbody car;
        static bool failed;
        static float nextBolt;
        static string Role { get { return Session.IsHost ? "host" : "guest"; } }
        static bool At(int id, float t, float at) { return t >= at && steps.Add(id); }
        static PlayMakerFSM Fsm(string path, string name) { GameObject go = Game.FindAny(path); return go == null ? null : Game.FsmOn(go, name); }
        static void Record(string name, bool pass, string detail) { MscRegressionProbe.Record(channel, name, pass, detail); }
        static GameObject Panel(int index) { return MscParts.Find("mscdb:Database/DatabaseBody/" + panels[index]); }
        static PlayMakerFSM Mount(GameObject part)
        {
            if (part == Panel(3)) return Game.FsmOn(car.transform.Find("Body/trigger_hood").gameObject, "Assembly");
            foreach (PlayMakerFSM f in car.GetComponentsInChildren<PlayMakerFSM>(true))
                if (f.FsmName == "Assembly" && NativeAssembly.Accepts(f, part)) return f;
            throw new Exception("No mount for " + part.name);
        }
        static void RemovePanel(int index)
        {
            GameObject part = Panel(index); if (part == null) throw new Exception("Missing " + panels[index]);
            foreach (PlayMakerFSM bolt in part.GetComponentsInChildren<PlayMakerFSM>(true)) if (MscParts.IsBolt(bolt))
            {
                bolt.enabled = true;
                for (int turn = 0; turn < 12 && bolt.FsmVariables.GetFsmInt("Stage").Value > 0; turn++) Game.SetState(bolt, "Unscrew 2");
            }
            PlayMakerFSM f = Game.FsmOn(part, "Removal"); f.enabled = true;
            Game.SetState(f, f.Fsm.GetState("Reset angle") != null ? "Reset angle" : "Remove part");
        }
        static void InstallPanel(int index)
        {
            GameObject part = Panel(index); PlayMakerFSM mount = Mount(part);
            mount.gameObject.SetActive(true); mount.enabled = true; part.SetActive(true);
            mount.FsmVariables.GetFsmGameObject("Part").Value = part;
            Game.SetState(mount, mount.Fsm.GetState("Freeze car") != null ? "Freeze car" : "Assemble");
        }
        static void CheckPanel(int index, string actor)
        {
            GameObject part = Panel(index); Joint[] joints = part.GetComponents<Joint>(); bool connected = false;
            foreach (Joint j in joints) if (j.connectedBody == car) connected = true;
            int stage = 0; foreach (PlayMakerFSM bolt in part.GetComponentsInChildren<PlayMakerFSM>(true)) if (MscParts.IsBolt(bolt)) stage = Mathf.Max(stage, bolt.FsmVariables.GetFsmInt("Stage").Value);
            Record(actor + "-install-" + panels[index], NativeAssembly.Installed(part) && connected && part.transform.parent != null && part.transform.localPosition.magnitude < .05f && stage == 0,
                "Installed=" + NativeAssembly.Installed(part) + ";Joints=" + joints.Length + ";Local=" + part.transform.localPosition + ";Collision=" + part.GetComponent<Rigidbody>().detectCollisions + ";MaxBoltStage=" + stage);
        }
        static void Tighten(int index)
        {
            if (Time.realtimeSinceStartup < nextBolt) return; nextBolt = Time.realtimeSinceStartup + .15f;
            foreach (PlayMakerFSM f in Panel(index).GetComponentsInChildren<PlayMakerFSM>(true))
                if (MscParts.IsBolt(f) && f.FsmVariables.GetFsmInt("Stage").Value < 8 && f.ActiveStateName == "Idle") Game.SetState(f, "Screw 2");
        }
        static string DoorName(int index) { return new[] { "door left", "door right", "bootlid", "hood" }[index]; }
        static PlayMakerFSM Rocket(int index)
        { GameObject root = Props.ObjectOf("msc-rocket-" + index); return root == null ? null : Game.FsmOn(root.transform.Find("Trigger").gameObject, "Use"); }
        static void Hold(int index, float angle)
        {
            PlayMakerFSM hand = Fsm("PLAYER/Pivot/AnimPivot/Camera/FPSCamera/1Hand_Assemble/Hand", "PickUp");
            if (hand.ActiveStateName != "Item picked") Game.SetState(hand, "Item picked");
            aim.rotation = Quaternion.Euler(0, 0, angle); Props.TestHold("msc-rocket-" + index, aim);
        }
        public static void Update()
        {
            channel = MscRuntime.Argument("--summercoop-test-summerfixes");
            if (channel.Length == 0 || failed || !PlayerSync.InGame || Time.timeSinceLevelLoad < 30 || !MscMusic.Ready || Session.Players.Count != 2) return;
            try { Tick(); }
            catch (Exception e) { failed = true; Record("probe-error", false, e.ToString()); }
        }
        static void Tick()
        {
            foreach (PlayerInfo p in Session.Players.Values) if (p.Level != 1) return;
            if (car == null)
            {
                car = VehicleSync.Body("SATSUMA"); if (car == null) return;
                car.constraints = RigidbodyConstraints.FreezeAll;
                aim = new GameObject("SummerCoop QA rocket aim").transform; aim.position = car.position + Vector3.up * 20 + Vector3.right * 20;
                if (Session.IsHost) Cheats.SetAllowed(true);
            }
            car.constraints = RigidbodyConstraints.FreezeAll;
            if (start == 0 && !Cheats.Allowed) return;
            string ready = Path.Combine(channel, Role + "-ready.txt");
            if (!File.Exists(ready)) MscProbeFiles.WriteAllText(ready, "ready");
            string timer = Path.Combine(channel, "start.txt");
            if (start == 0)
            {
                if (Session.IsHost && File.Exists(Path.Combine(channel, "guest-ready.txt")) && !File.Exists(timer)) MscProbeFiles.WriteAllText(timer, DateTime.UtcNow.AddSeconds(3).Ticks.ToString());
                if (!File.Exists(timer) || !long.TryParse(MscProbeFiles.ReadAllText(timer), out start)) return;
            }
            float t = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - start).TotalSeconds; if (t < 0) return;
            if (MscRuntime.HasArgument("--summercoop-test-stereo-passengers")) { MscStereoPassengerProbe.Tick(t, Record); return; }
            if (MscRuntime.HasArgument("--summercoop-test-music-only")) { MscMusicProbe.Tick(t + 114, channel, Record); return; }
            if (At(0, t, 0))
            {
                var songs = MscMusic.Songs("Radio");
                Record("music-import-gaps", songs != null && songs.Count == 2, "Songs=" + (songs == null ? -1 : songs.Count) + ";Catalogue=" + MscMusic.CatalogueId);
                for (int i = 1; i <= 3; i++) Record("cd-identity-" + i, MscCd.DiscAt(i) != null && Props.CdKey(MscCd.DiscAt(i)) == "w:cd:CD" + i, "Found=" + (MscCd.DiscAt(i) != null));
            }
            if (At(1, t, 1)) { Game.SetGlobalFloat("PlayerAllergy", 5); Game.SetState(Fsm("PLAYER/Pivot/AnimPivot/Camera/FPSCamera/FPSCamera", "Blindness"), "Allergy"); }
            if (At(2, t, 1.5f)) { string error = Cheats.Command("/curesting"); Record("cure-sting-command", error == null, "Allergy=" + Game.GlobalFloat("PlayerAllergy") + ";Error=" + error); }
            if (At(3, t, 2)) { var f = Fsm("PLAYER/Pivot/AnimPivot/Camera/FPSCamera/FPSCamera", "Blindness"); Record("cure-sting-effect", Game.GlobalFloat("PlayerAllergy") == 0 && f.enabled && f.ActiveStateName == "State 3", "State=" + f.ActiveStateName); Game.SetState(f, "Blindness"); }
            if (At(4, t, 2.3f)) Record("cure-flash-command", Cheats.Command("/clearblindness") == null, "");
            if (At(5, t, 2.8f)) { var f = Fsm("PLAYER/Pivot/AnimPivot/Camera/FPSCamera/FPSCamera", "Blindness"); Record("cure-flash-effect", f.enabled && f.ActiveStateName == "State 3", "State=" + f.ActiveStateName); Game.SetState(f, "Blindness"); Record("flash-can-recur", f.ActiveStateName == "Blindness", "State=" + f.ActiveStateName); Cheats.Command("/clearblindness"); }
            if (At(6, t, 3) && Session.IsHost) { Cheats.Command("/wasphome on"); Game.SetState(Fsm("MISC/fireworks bag(itemx)", "Open"), "Play anim"); }
            if (At(7, t, 5)) Record("house-wasp-disabled", !Game.FindAny("YARD/WaspHome").activeSelf, "Flag=" + MscComfort.NoHouseWasp);
            if (At(8, t, 6) && !Session.IsHost) Cheats.Command("/wasphome off");
            if (At(9, t, 7)) Record("house-wasp-restored", Game.FindAny("YARD/WaspHome").activeSelf, "Flag=" + MscComfort.NoHouseWasp);
            if (At(10, t, 3.2f)) { Game.SetGlobalFloat("PlayerAllergy", 5); Game.SetState(Fsm("PLAYER/Pivot/AnimPivot/Camera/FPSCamera/FPSCamera", "Blindness"), "Allergy"); Record("sting-can-recur", Game.GlobalFloat("PlayerAllergy") > 0, "Allergy=" + Game.GlobalFloat("PlayerAllergy")); Cheats.Command("/heal"); }
            for (int rocket = 1; rocket <= 2; rocket++)
            {
                int n = rocket; float when = rocket == 1 ? 8 : 29; bool actor = Session.IsHost == (rocket == 1);
                if (rocket == 2 && At(100, t, 24) && actor) Game.SetState(Fsm("MISC/fireworks bag(itemx)", "Open"), "Play anim");
                if (actor && t >= when - .5f && t < when + 2.2f) Hold(n, t < when + .5f ? 0 : 25);
                if (At(110 + n * 10, t, when) && actor) { var f = Rocket(n); if (f == null) throw new Exception("Rocket missing: " + n); Game.SetState(f, "ON 3"); }
                if (At(111 + n * 10, t, when + 1.5f)) { var f = Rocket(n); Record("rocket-" + n + "-fuse", f != null && f.ActiveStateName == "ON 3", "State=" + (f == null ? "missing" : f.ActiveStateName)); }
                if (At(112 + n * 10, t, when + 2)) { var f = Rocket(n); Record("rocket-" + n + "-aim", f != null && Quaternion.Angle(f.transform.parent.rotation, Quaternion.Euler(0, 0, 25)) < 6, "Rotation=" + (f == null ? "missing" : f.transform.parent.rotation.ToString())); }
                if (At(113 + n * 10, t, when + 2.3f) && actor) Props.DropForSimulation(Rocket(n).transform.parent.gameObject);
                if (At(114 + n * 10, t, when + 6.5f)) { var f = Rocket(n); Record("rocket-" + n + "-flight", f != null && MscFireworks.Flying(f.transform.parent.gameObject), "State=" + (f == null ? "missing" : f.ActiveStateName)); }
                if (At(115 + n * 10, t, when + 13)) { var f = Rocket(n); Record("rocket-" + n + "-spent", f != null && !MscFireworks.Flying(f.transform.parent.gameObject), "State=" + (f == null ? "missing" : f.ActiveStateName)); }
            }
            for (int i = 0; i < panels.Length; i++) for (int actor = 0; actor < 2; actor++)
            {
                float when = 45 + i * 16 + actor * 8; bool mine = Session.IsHost == (actor == 0); int id = 300 + i * 30 + actor * 10;
                if (At(id, t, when) && mine) RemovePanel(i);
                if (At(id + 1, t, when + 1.5f) && mine) InstallPanel(i);
                if (At(id + 2, t, when + 4.3f)) CheckPanel(i, actor == 0 ? "host" : "guest");
                if (mine && t >= when + 4.4f && t < when + 7.1f) Tighten(i);
                if (At(id + 3, t, when + 7.1f) && mine) CarDoors.TestOpen("SATSUMA", true, DoorName(i));
                if (At(id + 4, t, when + 7.35f) && mine) CarDoors.TestOpen("SATSUMA", false, DoorName(i));
                if (At(id + 5, t, when + 7.7f))
                {
                    int count = 0, minimum = 8;
                    foreach (PlayMakerFSM f in Panel(i).GetComponentsInChildren<PlayMakerFSM>(true)) if (MscParts.IsBolt(f)) { count++; minimum = Mathf.Min(minimum, f.FsmVariables.GetFsmInt("Stage").Value); }
                    Record((actor == 0 ? "host" : "guest") + "-bolts-open-close-" + panels[i], count > 0 && minimum == 8 && NativeAssembly.Installed(Panel(i)) && Panel(i).GetComponent<HingeJoint>() != null, "Bolts=" + count + ";Minimum=" + minimum);
                }
            }
            if (At(500, t, 110))
            {
                CheatPanel.Open = true; Application.CaptureScreenshot(Path.Combine(channel, Role + "-cheats.png"));
                Record("music-cache-ready", MscMusic.Ready, "Catalogue=" + MscMusic.CatalogueId);
            }
            MscMusicProbe.Tick(t, channel, Record);
            if (At(510, t, 194) && Session.IsHost) { Cheats.Command("/wasphome on"); Cheats.SetAllowed(false); }
            if (At(511, t, 196)) Record("cheats-disabled-restores-wasp", !Cheats.Allowed && Game.FindAny("YARD/WaspHome").activeSelf && Cheats.Command("/clearblindness") != null, "Allowed=" + Cheats.Allowed);
            if (At(512, t, 198) && Session.IsHost) { Cheats.SetAllowed(true); Cheats.Command("/wasphome off"); }
        }
    }
}
