using System;
using System.Collections.Generic;
using System.IO;

namespace SummerCoop.Core
{
    // Shared by profile activation and game-free regression tests. Named saves
    // retain the launcher's existing layout; the old host world stays in place.
    public static class MscSaveProfiles
    {
        public static string Resolve(string root, string profilesRoot, string profile, bool guest)
        {
            if (guest || string.IsNullOrEmpty(profile))
                return Path.GetFullPath(Path.Combine(Path.Combine(root, "mwcoop-profiles"), guest ? "guest" : "host"));
            int number;
            if (!profile.StartsWith("partie-", StringComparison.Ordinal) || !int.TryParse(profile.Substring(7), out number)
                || number < 1 || number > 999 || profile != "partie-" + number)
                throw new IOException("Invalid MSC save profile");
            return Path.GetFullPath(Path.Combine(Path.Combine(profilesRoot, profile), @"LocalLow\Amistech\My Summer Car"));
        }

        public static void Prepare(string directory, string source, bool import, bool newGame, bool main)
        {
            Directory.CreateDirectory(directory);
            string marker = Path.Combine(directory, ".summercoop-initialized");
            bool copy = !newGame && (import || main && !File.Exists(marker) && !File.Exists(Path.Combine(directory, "defaultES2File.txt")));
            if (copy || newGame)
            {
                var names = new List<string>(); var sizes = new List<int>(); var hashes = new List<string>(); var data = new List<byte[]>();
                if (copy && !string.Equals(Path.GetFullPath(source).TrimEnd('\\', '/'), Path.GetFullPath(directory).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    foreach (string name in NativeSaveBundle.Names)
                    {
                        string path = Path.Combine(source, name);
                        if (!File.Exists(path)) continue;
                        byte[] bytes = File.ReadAllBytes(path);
                        names.Add(name); sizes.Add(bytes.Length); hashes.Add(Codec.Hash(bytes)); data.Add(bytes);
                    }
                else if (copy) throw new IOException("MSC import source overlaps destination");
                var bundle = new NativeSaveBundle(names.ToArray(), sizes.ToArray(), hashes.ToArray());
                for (int i = 0; i < data.Count; i++) if (data[i].Length > 0) bundle.Append(i, 0, data[i]);
                // Explicit restart/import retains a recovery copy before any
                // world files change. Commit also rolls back failed writes.
                string backup = null;
                foreach (string name in NativeSaveBundle.Names)
                {
                    string path = Path.Combine(directory, name);
                    if (!File.Exists(path)) continue;
                    if (backup == null) { backup = Path.Combine(directory, "backup-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(backup); }
                    File.Copy(path, Path.Combine(backup, name));
                }
                bundle.Commit(directory);
            }
            AtomicFile.Write(marker, new byte[] { 1 });
        }
    }
}
