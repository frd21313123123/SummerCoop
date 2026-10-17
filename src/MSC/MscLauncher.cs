using System;
using System.Collections.Generic;
using System.IO;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Launcher settings and preview assets belong to the original game directory.
    // World saves, logs and network commands remain in the isolated MSC profile.
    public static class MscLauncher
    {
        public static readonly string[] CustomizationKeys = {
            "Apparence", "Pantalon", "Visage", "Corps", "Chapeau", "Lunettes", "Cheveux", "CouleurVoiture"
        };
        static float nextRead;
        static DateTime lastWrite;
        static long lastLength = -1;
        static volatile bool reading;
        sealed class Customization { public string Path; public Dictionary<string, string> Values; }
        static Customization pending;
        static readonly object pendingGate = new object();

        public static void Initialize(string gameDir, string profileData, string launchFile)
        {
            string shared = MscRuntime.Argument("--summercoop-launcher-dir");
            if (shared.Length == 0 && File.Exists(launchFile))
            {
                // A launch copy can start without arguments (Steam). The fresh
                // launch request carries the original directory in UTF-8.
                var request = ReadSection(launchFile, "Lancement");
                string stamp, directory;
                long seconds;
                long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                if (request.TryGetValue("Horodatage", out stamp) && long.TryParse(stamp, out seconds)
                    && now - seconds < 180 && seconds - now < 60 && request.TryGetValue("LauncherDir", out directory)) shared = directory;
            }
            if (shared.Length == 0) shared = Path.Combine(gameDir, "MWCoop");
            shared = Path.GetFullPath(shared);
            Directory.CreateDirectory(shared);
            string config = Path.Combine(shared, "mwcoop.ini");
            string previous = Path.Combine(profileData, "mwcoop.ini");
            if (!File.Exists(config) && File.Exists(previous)) File.Copy(previous, config);
            Environment.SetEnvironmentVariable("MWCOOP_CONFIG", config);
            Environment.SetEnvironmentVariable("MWCOOP_LAUNCHER_DIR", shared);
            nextRead = 0; lastWrite = DateTime.MinValue; lastLength = -1;
        }

        static Dictionary<string, string> ReadSection(string path, string wanted)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string section = "";
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[') { section = line.Trim('[', ']'); continue; }
                int eq = line.IndexOf('=');
                if (eq > 0 && string.Equals(section, wanted, StringComparison.OrdinalIgnoreCase))
                    result[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return result;
        }

        // Only appearance is reloaded during play. Connection and world settings
        // must retain the values selected for the current session.
        public static bool ReloadCustomization(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            Dictionary<string, string> values = ReadSection(path, "Coop");
            return ApplyCustomization(values);
        }
        static bool ApplyCustomization(Dictionary<string, string> values)
        {
            bool changed = false;
            foreach (string key in CustomizationKeys)
            {
                string value;
                if (!values.TryGetValue(key, out value)) continue;
                if (Config.Get("Coop", key, "") == value) continue;
                Config.Override("Coop", key, value);
                changed = true;
            }
            return changed;
        }

        public static void Update()
        {
            Customization update;
            lock (pendingGate) { update = pending; pending = null; }
            if (update != null && update.Path == Config.IniPath && ApplyCustomization(update.Values) && Session.Me != null)
            {
                CustomFace.Forget();
                string look = Looks.FromConfig();
                if (Session.Me.Skin != look)
                {
                    Session.Me.Skin = look; Session.SendProfile();
                    Log.Info("MSC launcher: appearance " + look);
                }
            }
            if (Time.realtimeSinceStartup < nextRead) return;
            nextRead = Time.realtimeSinceStartup + 1f;
            if (string.IsNullOrEmpty(Config.IniPath) || reading) return;
            string path = Config.IniPath;
            reading = true;
            if (!MscIo.TryQueue(delegate {
                try
                {
                    var file = new FileInfo(path);
                    if (!file.Exists || (file.LastWriteTimeUtc == lastWrite && file.Length == lastLength)) return;
                    DateTime written = file.LastWriteTimeUtc; long length = file.Length;
                    var values = ReadSection(path, "Coop");
                    lock (pendingGate) pending = new Customization { Path = path, Values = values };
                    lastWrite = written; lastLength = length;
                }
                finally { reading = false; }
            })) reading = false;
        }
    }
}
