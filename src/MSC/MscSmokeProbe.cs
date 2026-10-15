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
        int actionStep;
        string actionError = "";
        readonly List<float> frames = new List<float>();
        void Start() { began = Time.realtimeSinceStartup; Application.targetFrameRate = 60; }
        void Update()
        {
            bool ready = PlayerSync.InGame && Session.Players.Count >= 2;
            foreach (PlayerInfo player in Session.Players.Values) if (player.Level != 1) ready = false;
            if (ready && readyAt < 0) readyAt = Time.realtimeSinceStartup;
            if (readyAt >= 0)
            {
                frames.Add(Time.unscaledDeltaTime * 1000);
                if (!chatSent) { chatSent = true; Chat.Send("MSC smoke " + (Session.IsHost ? "host" : "guest")); }
                try { MscWorkshopProbe.Update(); }
                catch (Exception e) { if (actionError.Length == 0) { actionError = e.ToString(); Log.Error("MSC workshop probe: " + e); } }
                try { MscBeltProbe.Update(); }
                catch (Exception e) { if (actionError.Length == 0) { actionError = e.ToString(); Log.Error("MSC belt probe: " + e); } }
                try { MscWiringProbe.Update(); }
                catch (Exception e) { if (actionError.Length == 0) { actionError = e.ToString(); Log.Error("MSC wiring probe: " + e); } }
                try { MscVehicleProbe.Update(); }
                catch (Exception e) { if (actionError.Length == 0) { actionError = e.ToString(); Log.Error("MSC vehicle probe: " + e); } }
                if (MscRuntime.Argument("--summercoop-test-garage-channel").Length > 0)
                {
                    try { MscGarageProbe.Update(); }
                    catch (Exception e) { if (actionError.Length == 0) { actionError = e.ToString(); Log.Error("MSC garage probe: " + e); } }
                }
                if (Session.IsHost && MscRuntime.Argument("--summercoop-test-actions") == "yes" && actionError.Length == 0)
                {
                    try
                    {
                        float elapsed = Time.realtimeSinceStartup - readyAt;
                        if (elapsed > 5 && actionStep == 0) { actionStep = 1; MscParts.ExerciseStarter(false); }
                        if (elapsed > 9 && actionStep == 1) { actionStep = 2; MscParts.ExerciseStarter(true); }
                        if (elapsed > 10 && actionStep == 2) { actionStep = 3; MscParts.PublishStarterBolt(); }
                    }
                    catch (Exception e) { actionError = e.ToString(); Log.Error("MSC smoke native action: " + e); }
                }
            }
            int duration; if (!int.TryParse(MscRuntime.Argument("--summercoop-test-duration"), out duration)) duration = 35;
            float finish = duration + (Session.IsHost ? 8 : 0); // Guest records before the host closes its transport.
            float timeout = MscRuntime.Argument("--summercoop-test-garage-channel").Length > 0 || MscRuntime.Argument("--summercoop-test-workshop-channel").Length > 0 ? 420 : 180;
            if (!(readyAt >= 0 && Time.realtimeSinceStartup - readyAt >= finish) && Time.realtimeSinceStartup - began < timeout) return;
            List<string> report = new List<string>();
            report.Add("Engine=MWCoop 0.95.3 / MSC adapter 1"); report.Add("Level=" + Application.loadedLevelName);
            report.Add("ModVersion=" + Version.Text); report.Add("NetVersion=" + Session.NetVersion);
            report.Add("Ready=" + ready); report.Add("Players=" + Session.Players.Count); report.Add("PartsBindings=" + MscParts.Count);
            report.Add("Vehicles=" + VehicleSync.Count); report.Add("WorldFsms=" + WorldFsms.Count);
            report.Add("SaveReceived=" + SaveTransfer.Received); report.Add("SaveDirectory=" + MscProfile.SaveDirectory);
            report.Add("Chat=" + string.Join(" | ", Chat.History.ToArray()));
            report.Add("Starter=" + MscParts.StarterState()); if (actionError.Length > 0) report.Add("ActionError=" + actionError);
            report.AddRange(MscVehicleProbe.Results);
            report.AddRange(MscGarageProbe.Results);
            report.AddRange(MscWorkshopProbe.Results);
            report.AddRange(MscWiringProbe.Results);
            report.AddRange(MscBeltProbe.Results);
            foreach (string failure in MscRuntime.Failures) report.Add("ModuleError=" + failure);
            frames.Sort();
            if (frames.Count > 0) { report.Add("FrameP95ms=" + frames[(int)((frames.Count - 1) * .95)].ToString("F2", CultureInfo.InvariantCulture)); report.Add("FrameMaxms=" + frames[frames.Count - 1].ToString("F2", CultureInfo.InvariantCulture)); }
            File.WriteAllLines(MscRuntime.Argument("--summercoop-test-report"), report.ToArray());
            Application.Quit(); enabled = false;
        }
    }
}
