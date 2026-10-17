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
        internal static bool MayStartOffline(string mode, bool automatic, bool requested) { return mode == "solo" && (automatic || requested); }
        public static void Update()
        {
            if (Application.loadedLevelName == "GAME") { loading = false; since = 0; return; }
            if (Application.loadedLevelName != "MainMenu" || loading) return;
            if (since == 0) since = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - since < 1.0f) return;
            if (Contenu.MenuBusy) return;

            bool canStart = false;
            if (!Session.Active)
            {
                // A failed guest handshake must remain at the connection/menu
                // screen, rather than auto-loading an empty offline profile.
                canStart = MayStartOffline(Config.Get("Coop", "Mode", ""), MscRuntime.AutoStart, Requested);
            }
            else if (Session.IsHost)
            {
                canStart = MscRuntime.AutoStart || Requested;
            }
            else
            {
                canStart = Session.Host != null && Session.Host.Level == 1 && SaveTransfer.Done && (!SaveTransfer.HostHasSave || SaveTransfer.Received);
            }

            if (!canStart) return;

            MscRuntime.ConfigureGame();
            loading = true;
            Requested = false;
            if ((!Session.Active || Session.IsHost) && !File.Exists(Path.Combine(MscProfile.SaveDirectory, "defaultES2File.txt")))
                SaveTransfer.MscNewWorld();
            Log.Info("MSC: load world " + MscProfile.SaveDirectory);
            MscRuntime.TouchLoaderTrace();
            Application.LoadLevel("GAME");
        }
    }
}
