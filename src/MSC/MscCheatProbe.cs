using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HutongGames.PlayMaker;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Explicit isolated QA: real money-button click during wallet loading,
    // then host/guest commands and native world state on both peers.
    public static class MscCheatProbe
    {
        static readonly HashSet<int> steps = new HashSet<int>();
        static readonly int[] hours = { 6, 8, 12, 16, 18, 22 };
        static string channel;
        static long clock;
        static bool installed, earlyClicked, soberRequested, soberClicked;
        static float expectedCash;
        static string Role { get { return Session.IsHost ? "host" : "guest"; } }
        static bool At(int step, float t, float at) { return t >= at && steps.Add(step); }
        static void Record(string name, bool pass, string detail) { MscRegressionProbe.Record(channel, "cheat-" + name, pass, detail); }
        static PlayMakerFSM Sun { get { return Game.FindFsm("MAP/SUN/Pivot/SUN", "Color"); } }
        static PlayMakerFSM Weather { get { return Game.FindFsm("MAP/CloudSystem/Clouds", "Weather"); } }
        static PlayMakerFSM Rain { get { GameObject rain = Game.PlayerPart("Rain"); return rain != null ? Game.FsmOn(rain, "Rain") : null; } }
        static void Command(string command) { string error = Cheats.Command(command); Record("command", error == null, command + ";Error=" + error); }

        public static void Tick()
        {
            channel = MscRuntime.Argument("--summercoop-test-cheats-channel");
            if (channel.Length == 0 || !PlayerSync.InGame) return;
            if (!installed)
            {
                installed = true;
                if (Session.IsHost) Cheats.SetAllowed(true);
                new GameObject("SummerCoop cheat QA clicks").AddComponent<ClickDriver>();
            }
            if (!earlyClicked || !Wallet.Ready || !Cheats.Allowed || Session.Players.Count != 2) return;
            foreach (PlayerInfo player in Session.Players.Values) if (player.Level != 1) return;
            string ready = Path.Combine(channel, Role + "-ready.txt");
            if (!File.Exists(ready))
            {
                Record("early-credit-preserved", Mathf.Abs(Wallet.Cash - expectedCash) < .01f, "Expected=" + expectedCash + ";Cash=" + Wallet.Cash);
                MscProbeFiles.WriteAllText(ready, "ready");
            }
            string start = Path.Combine(channel, "start.txt");
            if (Session.IsHost && File.Exists(Path.Combine(channel, "guest-ready.txt")) && !File.Exists(start)) MscProbeFiles.WriteAllText(start, DateTime.UtcNow.AddSeconds(3).Ticks.ToString());
            if (clock == 0 && (!File.Exists(start) || !long.TryParse(MscProbeFiles.ReadAllText(start), out clock))) return;
            float t = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - clock).TotalSeconds;
            if (At(0, t, 0))
            {
                var dump = new StringBuilder();
                foreach (PlayMakerFSM f in Game.PlayerT.GetComponentsInChildren<PlayMakerFSM>(true))
                    if (f.FsmName.IndexOf("drunk", StringComparison.OrdinalIgnoreCase) >= 0 || f.FsmName == "Hangover")
                    { dump.AppendLine(Recon.Path(f.transform)); Recon.AppendFsm(dump, "", f, true); }
                MscProbeFiles.WriteAllText(Path.Combine(channel, Role + "-alcohol-fsms.txt"), dump.ToString());
                Menu.TeleportTo(new Vector3(-20, 2, 0), 180, "cheat QA outdoors");
                float before = Wallet.Cash;
                Command(Session.IsHost ? "/money 10000" : "/money 1000");
                Record("ready-money", Mathf.Abs(Wallet.Cash - before - (Session.IsHost ? 10000 : 1000)) < .01f, "Before=" + before + ";Cash=" + Wallet.Cash);
                expectedCash = Wallet.Cash;
            }
            if (At(1, t, 2)) Record("personal-money", Wallet.Cash == expectedCash, "Cash=" + Wallet.Cash);
            for (int i = 0; i < hours.Length; i++)
            {
                if (At(10 + 2 * i, t, 3 + 4 * i) && Session.IsHost == (i % 2 == 0)) Command("/time " + hours[i]);
                if (At(11 + 2 * i, t, 5 + 4 * i)) Record("time-" + hours[i], Sun != null && Sun.FsmVariables.GetFsmInt("Time").Value == hours[i] && Sun.ActiveStateName != "State 3", "Hour=" + (Sun != null ? Sun.FsmVariables.GetFsmInt("Time").Value : -1) + ";State=" + (Sun != null ? Sun.ActiveStateName : "missing"));
            }
            if (At(30, t, 28) && Session.IsHost) Command("/rain on");
            if (At(31, t, 33)) CheckRain(true, "host-on");
            if (At(32, t, 36) && Session.IsHost) Command("/rain off");
            if (At(33, t, 44)) CheckRain(false, "host-off");
            if (At(34, t, 47) && !Session.IsHost) Command("/rain on");
            if (At(35, t, 52)) CheckRain(true, "guest-on");
            if (At(36, t, 55) && !Session.IsHost) Command("/rain off");
            if (At(37, t, 64)) CheckRain(false, "guest-off");
            if (At(38, t, 67) && Session.IsHost) Command("/time 0");
            if (At(39, t, 70)) Record("midnight", Sun != null && Sun.FsmVariables.GetFsmInt("Time").Value == 24 && Sun.ActiveStateName == "00-02", "State=" + (Sun != null ? Sun.ActiveStateName : "missing"));
            if (At(40, t, 72)) { CheatPanel.Open = true; Application.CaptureScreenshot(Path.Combine(channel, Role + "-cheats.png")); }
            if (At(41, t, 74)) {
                Game.SetGlobalFloat("PlayerDrunk", 2); Game.SetGlobalFloat("PlayerDrunkAdjusted", 2);
                foreach (PlayMakerFSM f in Game.PlayerT.GetComponentsInChildren<PlayMakerFSM>(true)) {
                    if (f.FsmName == "Drunk Mode") Game.SetState(f, "Update drunk");
                    if (f.FsmName != "Hangover") continue;
                    foreach (string name in new[] { "HangoverStrenght", "HangoverStrenghtMinus", "TimeLeft", "X", "Y", "Z" }) {
                        FsmFloat v = f.FsmVariables.FindFsmFloat(name); if (v != null) v.Value = name == "TimeLeft" ? 300 : name == "HangoverStrenghtMinus" ? -.02f : .02f;
                    }
                    Game.SetState(f, "Randomize");
                }
            }
            if (At(42, t, 75) && !Session.IsHost) soberRequested = true;
            if (At(43, t, 77)) {
                bool clean = Game.GlobalFloat("PlayerDrunk") == 0 && Game.GlobalFloat("PlayerDrunkAdjusted") == 0;
                foreach (PlayMakerFSM f in Game.PlayerT.GetComponentsInChildren<PlayMakerFSM>(true)) if (f.FsmName == "Hangover")
                    foreach (string name in new[] { "HangoverStrenght", "TimeLeft", "X", "Y", "Z" }) {
                        FsmFloat v = f.FsmVariables.FindFsmFloat(name); if (v != null && Mathf.Abs(v.Value) > .001f) clean = false;
                    }
                Record("sober-local", Session.IsHost ? Game.GlobalFloat("PlayerDrunk") > 0 : clean && soberClicked,
                    "Drunk=" + Game.GlobalFloat("PlayerDrunk") + ";Adjusted=" + Game.GlobalFloat("PlayerDrunkAdjusted") + ";Clean=" + clean);
            }
            if (At(44, t, 78) && Session.IsHost) Command("/sober");
            if (At(45, t, 79)) Record("sober-command", Game.GlobalFloat("PlayerDrunk") == 0 && Game.GlobalFloat("PlayerDrunkAdjusted") == 0, "Drunk=" + Game.GlobalFloat("PlayerDrunk"));
            if (At(46, t, 80) && !Session.IsHost) {
                Game.SetGlobalFloat("PlayerDrunk", 1); Game.SetGlobalFloat("PlayerDrunkAdjusted", 1);
                foreach (PlayMakerFSM f in Game.PlayerT.GetComponentsInChildren<PlayMakerFSM>(true)) if (f.FsmName == "Drunk Mode") Game.SetState(f, "Update drunk");
            }
            if (At(47, t, 82)) {
                if (!Session.IsHost) {
                    Record("drinking-can-recur", Game.GlobalFloat("PlayerDrunk") > 0, "Drunk=" + Game.GlobalFloat("PlayerDrunk"));
                    Command("/sober");
                } else Record("drinking-can-recur", Game.GlobalFloat("PlayerDrunk") == 0, "Other player's drinking stayed local");
            }
        }

        static void CheckRain(bool on, string label)
        {
            PlayMakerFSM rain = Rain;
            bool rainYes = rain != null && rain.FsmVariables.GetFsmBool("RainYes").Value;
            bool cloudYes = rain != null && rain.FsmVariables.GetFsmBool("CloudYes").Value;
            GameObject particles = rain != null ? rain.FsmVariables.GetFsmGameObject("RainParticle").Value : null;
            GameObject clouds = Game.FindAny("MAP/CloudSystem/Clouds/CloudObjects");
            Record(label, MscWeather.Raining(Weather) == on && rainYes == on && (!on || cloudYes) && clouds != null && clouds.activeInHierarchy == on && particles != null && particles.activeInHierarchy == on,
                "Weather=" + MscWeather.Raining(Weather) + ";Rain=" + rainYes + ";Cloud=" + cloudYes + ";Particles=" + (particles != null && particles.activeInHierarchy) + ";State=" + (rain != null ? rain.ActiveStateName : "missing"));
        }

        public sealed class ClickDriver : MonoBehaviour
        {
            void OnGUI()
            {
                if (soberRequested && !soberClicked && Event.current.type == EventType.Layout) {
                    ClickSober(); return;
                }
                if (earlyClicked || !PlayerSync.InGame || !Cheats.Allowed || Event.current.type != EventType.Layout) return;
                Event real = Event.current;
                try
                {
                    FsmFloat cash = FsmVariables.GlobalVariables.FindFsmFloat("PlayerMoney");
                    float before = cash.Value;
                    bool loading = !Wallet.Ready;
                    expectedCash = before + 1000;
                    if (!Session.IsHost && Config.GetInt("Coop", "ArgentDepart", 1) != 0)
                    {
                        float initial = (float)typeof(Wallet).GetField("startCash", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                        initial = Config.GetFloat("Coop", "ArgentDepartLiquide", initial);
                        if (initial >= 0) expectedCash = initial + 1000;
                    }
                    CheatPanel.Open = true;
                    CheatPanel.Draw();
                    for (int turn = 0; turn < 8 && !CheatPanel.ContentViewport.Contains(CheatPanel.MoneyButtonRect.center); turn++)
                    {
                        Event.current = new Event { type = EventType.ScrollWheel, delta = new Vector2(0, 3), mousePosition = CheatPanel.ContentViewport.center };
                        CheatPanel.Draw();
                    }
                    Event.current = new Event { type = EventType.MouseDown, button = 0, mousePosition = CheatPanel.MoneyButtonRect.center };
                    CheatPanel.Draw();
                    Record("loading-money-click", loading && Mathf.Abs(cash.Value - before - 1000) < .01f, "Loading=" + loading + ";Before=" + before + ";Cash=" + cash.Value
                        + ";Button=" + CheatPanel.MoneyButtonRect + ";Viewport=" + CheatPanel.ContentViewport);
                    CheatPanel.Open = false;
                    bool active = Session.Active;
                    try
                    {
                        Session.Active = false;
                        before = cash.Value;
                        bool credited = Wallet.AddLocal(1000);
                        Record("solo-money", loading && credited && Mathf.Abs(cash.Value - before - 1000) < .01f, "Cash=" + cash.Value);
                        expectedCash += 1000;
                    }
                    finally { Session.Active = active; }
                    earlyClicked = true;
                }
                finally { Event.current = real; }
            }
            void ClickSober()
            {
                Event real = Event.current;
                try {
                    CheatPanel.Open = true; CheatPanel.Draw();
                    for (int turn = 0; turn < 20 && !CheatPanel.ContentViewport.Contains(CheatPanel.SoberButtonRect.center); turn++) {
                        Event.current = new Event { type = EventType.ScrollWheel, delta = new Vector2(0, CheatPanel.SoberButtonRect.center.y < CheatPanel.ContentViewport.y ? -3 : 3), mousePosition = CheatPanel.ContentViewport.center };
                        CheatPanel.Draw();
                    }
                    Event.current = new Event { type = EventType.MouseDown, button = 0, mousePosition = CheatPanel.SoberButtonRect.center };
                    CheatPanel.Draw();
                    soberClicked = Game.GlobalFloat("PlayerDrunk") == 0;
                    bool motion = false;
                    foreach (PlayMakerFSM f in Game.PlayerT.GetComponentsInChildren<PlayMakerFSM>(true)) {
                        if (f.FsmName == "Hangover") motion |= f.transform.localPosition.sqrMagnitude > .000001f || !f.enabled;
                        if (f.FsmName == "Drunk Mode") {
                            motion |= !f.enabled || f.FsmVariables.GetFsmVector3("V3CURRENT").Value.sqrMagnitude > .000001f
                                || f.FsmVariables.GetFsmFloat("FishX").Value != 0 || Quaternion.Angle(f.transform.localRotation, Quaternion.identity) > .01f;
                        }
                    }
                    Record("sober-button", soberClicked && !motion, "Button=" + CheatPanel.SoberButtonRect + ";Viewport=" + CheatPanel.ContentViewport + ";Motion=" + motion);
                    CheatPanel.Open = false;
                } finally { Event.current = real; }
            }
        }
    }
}
