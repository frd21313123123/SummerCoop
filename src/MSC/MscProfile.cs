using System;
using System.IO;
using System.Reflection;
using SummerCoop.Core;
using UnityEngine;

namespace MWCoop
{
    public static class MscProfile
    {
        public static string SaveDirectory { get; private set; }
        public static string OriginalDirectory { get; private set; }
        public static bool Isolated { get; private set; }
        public static void Activate(string root, bool guest, bool import)
        {
            string profile = Config.Get("Coop", "Profil", "");
            string shared = Environment.GetEnvironmentVariable("MWCOOP_LAUNCHER_DIR");
            if (string.IsNullOrEmpty(shared)) shared = Path.Combine(Path.GetDirectoryName(Application.dataPath), "MWCoop");
            string profilesRoot = Config.Get("Coop", "ProfilesRoot", Path.Combine(shared, "profils"));
            bool newGame = !guest && string.Equals(Config.Get("Coop", "Partie", ""), "nouvelle", StringComparison.OrdinalIgnoreCase);
            if (OriginalDirectory == null) OriginalDirectory = Application.persistentDataPath;
            string path = MscSaveProfiles.Resolve(root, profilesRoot, profile, guest);
            if (Isolated && path == SaveDirectory) return;
            if (Isolated && Application.loadedLevelName == "GAME") throw new InvalidOperationException("Cannot change profile while the world is loaded");
            if (string.Equals(path.TrimEnd('\\', '/'), Path.GetFullPath(OriginalDirectory).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) throw new IOException("MSC profile overlaps single player save");
            Type type = typeof(ES2).Assembly.GetType("ES2FilenameData", true);
            FieldInfo field = type.GetField("persistentDataPath", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null || field.FieldType != typeof(string)) throw new InvalidOperationException("MSC save isolation is unavailable");
            Directory.CreateDirectory(path);
            if (!guest)
            {
                string legacy = Path.Combine(root, "world");
                string source = File.Exists(Path.Combine(legacy, "defaultES2File.txt")) ? legacy : OriginalDirectory;
                MscSaveProfiles.Prepare(path, source, import, newGame, string.IsNullOrEmpty(profile));
            }
            string options = Path.Combine(OriginalDirectory, "options.txt");
            if (!File.Exists(Path.Combine(path, "options.txt")) && File.Exists(options)) File.Copy(options, Path.Combine(path, "options.txt"));
            // Same hook used by the existing SummerCoop implementation. Keep it
            // active through all native quit callbacks, including ES2Auto saves.
            field.SetValue(null, path + Path.DirectorySeparatorChar);
            SaveDirectory = path; Isolated = true;
        }
    }
}
