using System;
using System.Collections.Generic;
using System.IO;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    internal static class MscSaveQuitProbe
    {
        internal static readonly List<string> Results = new List<string>();
        static float began = -1;
        static DateTime before;
        static bool clicked, checkedResult;
        internal static bool Completed { get { return checkedResult; } }
        internal static void Update(bool ready)
        {
            string actor = MscRuntime.Argument("--summercoop-test-save-quit");
            if (actor.Length == 0) return;
            if (began < 0) { if (!ready) return; began = Time.realtimeSinceStartup; before = DateTime.UtcNow; }
            float elapsed = Time.realtimeSinceStartup - began;
            if (!clicked && elapsed > 12)
            {
                clicked = true;
                if ((actor == "Host") == Session.IsHost)
                {
                    string press = SaveTransfer.TestPress();
                    if (!press.StartsWith("clic sur ", StringComparison.Ordinal)) throw new Exception("Toilet unavailable: " + press);
                    Results.Add("SaveQuitClicks=1;" + press);
                }
            }
            if (checkedResult || elapsed < 35) return;
            bool menu = actor == "Host" || !Session.IsHost;
            if (menu && Application.loadedLevelName != "MainMenu" && elapsed < 110) return;
            checkedResult = true;
            if (Application.loadedLevelName != (menu ? "MainMenu" : "GAME")) throw new Exception("Save/quit level " + Application.loadedLevelName);
            if (!Session.Active || Session.Status.StartsWith("erreur", StringComparison.Ordinal)) throw new Exception("Lobby failed after save/quit: " + Session.Status);
            string save = Path.Combine(MscProfile.SaveDirectory, "defaultES2File.txt");
            if (Session.IsHost && (!File.Exists(save) || new FileInfo(save).Length == 0 || File.GetLastWriteTimeUtc(save) <= before))
                throw new Exception("Authoritative host save was not written");
            if (menu && !MscFlow.SaveQuit) throw new Exception("Automatic reentry was not suppressed");
            Results.Add("SaveQuit=PASS;Actor=" + actor + ";Level=" + Application.loadedLevelName + ";HostSave=" + Session.IsHost);
        }
    }
}
