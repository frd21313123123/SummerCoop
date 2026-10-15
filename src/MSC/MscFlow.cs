using System.IO;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    public static class MscFlow
    {
        public static bool Requested;
        static bool loading;
        static float since;
        public static void Update()
        {
            if (Application.loadedLevelName == "GAME") { loading = false; since = 0; return; }
            if (Application.loadedLevelName != "MainMenu" || loading || !Session.Active) return;
            if (since == 0) since = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - since < 1.5f) return;
            if (Session.IsHost)
            {
                if (!Requested && !MscRuntime.AutoStart) return;
            }
            else if (Session.Host == null || Session.Host.Level != 1 || !SaveTransfer.Done || SaveTransfer.HostHasSave && !SaveTransfer.Received) return;
            MscRuntime.ConfigureGame(); loading = true; Requested = false;
            if (Session.IsHost && !File.Exists(Path.Combine(MscProfile.SaveDirectory, "defaultES2File.txt"))) SaveTransfer.MscNewWorld();
            Log.Info("MSC: load isolated world " + MscProfile.SaveDirectory);
            Application.LoadLevel("GAME");
        }
    }
}
