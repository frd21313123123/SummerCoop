using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    public sealed class MscSmokeProbe : MonoBehaviour
    {
        float began, readyAt = -1;
        bool chatSent;
        int expected = 2, readyPlayers;
        string participants = "";
        int actionStep;
        string actionError = "";
        string boltBefore = "", boltAfter = "";
        readonly List<float> frames = new List<float>();
        void Start() { began = Time.realtimeSinceStartup; Application.targetFrameRate = 60; int value; if (int.TryParse(MscRuntime.Argument("--summercoop-test-players"), out value)) expected = Math.Max(2, Math.Min(8, value)); }
        void Update()
        {
            bool ready = PlayerSync.InGame && Session.Players.Count >= expected;
            foreach (PlayerInfo player in Session.Players.Values) if (player.Level != 1) ready = false;
            if (ready && readyAt < 0)
            {
                readyAt = Time.realtimeSinceStartup; readyPlayers = Session.Players.Count;
                var names = new List<string>(); foreach (PlayerInfo member in Session.Players.Values) names.Add(member.Name);
                participants = string.Join(",", names.ToArray());
            }
            if (readyAt >= 0)
            {
                frames.Add(Time.unscaledDeltaTime * 1000);
                if (!chatSent) { chatSent = true; Chat.Send("MSC smoke " + (Session.IsHost ? "host" : "guest")); }
                if (Time.realtimeSinceStartup - readyAt > 3)
                    try { MscOptimizationProbe.Update(); }
                    catch (Exception e) { if (actionError.Length == 0) actionError = e.ToString(); }
                if (Session.IsHost || Session.LocalId == 1)
                {
                try { MscWorkshopProbe.Update(); }
                catch (Exception e) { if (actionError.Length == 0) { actionError = e.ToString(); Log.Error("MSC workshop probe: " + e); } }
                try { MscBeltProbe.Update(); }
                catch (Exception e) { if (actionError.Length == 0) { actionError = e.ToString(); Log.Error("MSC belt probe: " + e); } }
                try { MscWiringProbe.Update(); }
                catch (Exception e) { if (actionError.Length == 0) { actionError = e.ToString(); Log.Error("MSC wiring probe: " + e); } }
                try { MscVehicleProbe.Update(); }
                catch (Exception e) { if (actionError.Length == 0) { actionError = e.ToString(); Log.Error("MSC vehicle probe: " + e); } }
                try { MscPlatformProbe.Update(); }
                catch (Exception e) { if (actionError.Length == 0) { actionError = e.ToString(); Log.Error("MSC platform probe: " + e); } }
                try { MscActivitiesProbe.Update(); }
                catch (Exception e) { if (actionError.Length == 0) { actionError = e.ToString(); Log.Error("MSC activities probe: " + e); } }
                try { MscRegressionProbe.Update(); }
                catch (Exception e) { if (actionError.Length == 0) { actionError = e.ToString(); Log.Error("MSC regression probe: " + e); } }
                try { MscAssemblyProbe.Update(); }
                catch (Exception e) { if (actionError.Length == 0) { actionError = e.ToString(); Log.Error("MSC assembly probe: " + e); } }
                try { MscCarrierProbe.Update(); }
                catch (Exception e) { if (actionError.Length == 0) { actionError = e.ToString(); Log.Error("MSC carrier probe: " + e); } }
                if (MscRuntime.Argument("--summercoop-test-garage-channel").Length > 0)
                {
                    try { MscGarageProbe.Update(); }
                    catch (Exception e) { if (actionError.Length == 0) { actionError = e.ToString(); Log.Error("MSC garage probe: " + e); } }
                }
                }
                bool actionActor = MscRuntime.Argument("--summercoop-test-action-actor") == "guest" ? Session.LocalId == 1 : Session.IsHost;
                if (actionActor && MscRuntime.Argument("--summercoop-test-actions") == "yes" && actionError.Length == 0)
                {
                    try
                    {
                        float elapsed = Time.realtimeSinceStartup - readyAt;
                        if (elapsed > 5 && actionStep == 0) { actionStep = 1; MscParts.ExerciseStarter(false); }
                        if (elapsed > 9 && actionStep == 1) { actionStep = 2; boltBefore = MscParts.StarterState(); MscParts.ExerciseStarter(true); }
                        if (elapsed > 10 && actionStep == 2) { actionStep = 3; MscParts.PublishStarterBolt(); boltAfter = MscParts.StarterState(); }
                    }
                    catch (Exception e) { actionError = e.ToString(); Log.Error("MSC smoke native action: " + e); }
                }
            }
            int duration; if (!int.TryParse(MscRuntime.Argument("--summercoop-test-duration"), out duration)) duration = 35;
            float finish = duration + (Session.IsHost ? 8 : 0); // Guest records before the host closes its transport.
            float timeout = MscRuntime.Argument("--summercoop-test-garage-channel").Length > 0 || MscRuntime.Argument("--summercoop-test-workshop-channel").Length > 0 || MscRuntime.Argument("--summercoop-test-platform-channel").Length > 0 || MscRuntime.Argument("--summercoop-test-activities-channel").Length > 0 ? 420 : 180;
            if (MscRuntime.Argument("--summercoop-test-regression-channel").Length > 0) timeout = 600;
            if (MscRuntime.Argument("--summercoop-test-assembly-channel").Length > 0) timeout = 360;
            // Long reconnect runs must be allowed to reach their requested
            // duration after loading, rather than hitting the default 180 s.
            timeout = Mathf.Max(timeout, duration + 180);
            if (!(readyAt >= 0 && Time.realtimeSinceStartup - readyAt >= finish) && Time.realtimeSinceStartup - began < timeout) return;
            List<string> report = new List<string>();
            report.Add("Engine=MWCoop 0.95.3 / MSC adapter 1"); report.Add("Level=" + Application.loadedLevelName);
            report.Add("ModVersion=" + Version.Text); report.Add("NetVersion=" + Session.NetVersion);
            report.Add("ModMvid=" + typeof(MscRuntime).Assembly.ManifestModule.ModuleVersionId);
            report.Add("TestCursorUnlocked=" + MscTestEnvironment.CursorUnlocked);
            report.Add("TestCursorLockMode=" + Cursor.lockState + ";Visible=" + Cursor.visible);
            report.Add("TestAudioMuted=" + MscTestEnvironment.AudioMuted);
            report.Add("TestCursorActionsSuppressed=" + MscTestEnvironment.SuppressedCursorActions);
            report.Add("Ready=" + ready); report.Add("Players=" + Session.Players.Count); report.Add("PartsBindings=" + MscParts.Count);
            report.Add("ReadyPlayers=" + readyPlayers); report.Add("Participants=" + participants);
            report.Add("Vehicles=" + VehicleSync.Count); report.Add("WorldFsms=" + WorldFsms.Count);
            report.Add("SaveReceived=" + SaveTransfer.Received); report.Add("SaveDirectory=" + MscProfile.SaveDirectory);
            report.Add("LauncherConfig=" + Config.IniPath);
            report.Add("LauncherAppearance=" + (Session.Me != null ? Session.Me.Skin : ""));
            report.Add("LauncherColor=" + Config.Get("Coop", "CouleurVoiture", ""));
            report.Add("LauncherPreviews=" + File.Exists(Looks.PreviewPath) + "," + File.Exists(CarColor.MeshPath));
            foreach (PlayerInfo player in Session.Players.Values) report.Add("PlayerAppearance=" + player.Name + ":" + player.Skin);
            report.Add("Chat=" + string.Join(" | ", Chat.History.ToArray()));
            report.Add("Starter=" + MscParts.StarterState()); if (actionError.Length > 0) report.Add("ActionError=" + actionError);
            if (boltBefore.Length > 0) report.Add("StarterAction=Before=" + boltBefore + ";After=" + boltAfter);
            report.AddRange(MscVehicleProbe.Results);
            report.AddRange(MscGarageProbe.Results);
            report.AddRange(MscWorkshopProbe.Results);
            report.AddRange(MscWiringProbe.Results);
            report.AddRange(MscBeltProbe.Results);
            report.AddRange(MscPlatformProbe.Results);
            report.AddRange(MscActivitiesProbe.Results);
            report.AddRange(MscRegressionProbe.Results);
            report.AddRange(MscAssemblyProbe.Results);
            report.AddRange(MscCarrierProbe.Results);
            report.AddRange(MscOptimizationProbe.Results);
            report.Add("PartsDiscoveryPending=" + MscParts.DiscoveryPending + ";Age=" + MscParts.DiscoveryAge.ToString("F2", CultureInfo.InvariantCulture));
            foreach (string failure in MscRuntime.Failures) report.Add("ModuleError=" + failure);
            frames.Sort();
            if (frames.Count > 0) { report.Add("FrameP95ms=" + frames[(int)((frames.Count - 1) * .95)].ToString("F2", CultureInfo.InvariantCulture)); report.Add("FrameMaxms=" + frames[frames.Count - 1].ToString("F2", CultureInfo.InvariantCulture)); }
            MscProbeFiles.WriteAllLines(MscRuntime.Argument("--summercoop-test-report"), report.ToArray());
            Application.Quit(); enabled = false;
        }
    }
}
