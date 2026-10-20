using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SummerCoop.Core
{
    public sealed class MusicFile
    {
        public string Path, Hash;
        public int Size;
        public string Key { get { return "msc-music:" + Path + ":" + Hash; } }
    }
    public sealed class MusicManifest
    {
        public const int MaxFiles = 800, MaxFileBytes = 128 * 1024 * 1024;
        public static readonly string[] Groups = { "Radio", "CD1", "CD2", "CD3" };
        public readonly MusicFile[] Files;
        public readonly string Id;
        public MusicManifest(MusicFile[] files)
        {
            if (files == null || files.Length > MaxFiles) throw new IOException("Music catalogue is too large");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var text = new StringBuilder(); Files = new MusicFile[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                MusicFile f = files[i];
                if (f == null || !ValidPath(f.Path) || !seen.Add(f.Path) || f.Size <= 0 || f.Size > MaxFileBytes || !ValidHash(f.Hash)) throw new IOException("Invalid music catalogue entry");
                Files[i] = new MusicFile { Path = f.Path, Size = f.Size, Hash = f.Hash.ToLowerInvariant() };
                text.Append(f.Path).Append('|').Append(f.Size).Append('|').Append(Files[i].Hash).Append('\n');
            }
            Id = Codec.Hash(Encoding.UTF8.GetBytes(text.ToString()));
        }
        public static bool ValidHash(string hash)
        {
            if (hash == null || hash.Length != 64) return false;
            foreach (char c in hash) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F')) return false;
            return true;
        }
        public static bool ValidPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            int slash = path.IndexOf('/'); if (slash < 0) return false;
            string group = path.Substring(0, slash), name = path.Substring(slash + 1);
            if (Array.IndexOf(Groups, group) < 0 || !name.StartsWith("track", StringComparison.Ordinal) || !name.EndsWith(".ogg", StringComparison.Ordinal) || name.Length < 10) return false;
            int number; string digits = name.Substring(5, name.Length - 9);
            return int.TryParse(digits, out number) && number >= 1 && number <= 200 && digits == number.ToString();
        }
        public static string HashFile(string path)
        {
            using (var file = File.OpenRead(path)) using (var sha = SHA256.Create())
            { var s = new StringBuilder(); foreach (byte b in sha.ComputeHash(file)) s.Append(b.ToString("x2")); return s.ToString(); }
        }
        public static bool Matches(string path, MusicFile file)
        { return File.Exists(path) && new FileInfo(path).Length == file.Size && HashFile(path) == file.Hash; }
        public static MusicManifest Scan(string directory)
        {
            var files = new List<MusicFile>();
            foreach (string group in Groups) for (int track = 1; track <= 200; track++)
            {
                string relative = group + "/track" + track + ".ogg", path = System.IO.Path.Combine(directory, relative);
                if (!File.Exists(path)) continue;
                long size = new FileInfo(path).Length;
                if (size <= 0 || size > MaxFileBytes) throw new IOException("Invalid music size: " + relative);
                files.Add(new MusicFile { Path = relative, Size = (int)size, Hash = HashFile(path) });
            }
            return new MusicManifest(files.ToArray());
        }
    }
}
