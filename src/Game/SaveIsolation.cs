using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using SummerCoop.Core;
using UnityEngine;

namespace SummerCoop.Game
{
    public sealed class SaveIsolation
    {
        public static readonly string[] NativeFiles = { "defaultES2File.txt", "items.txt", "meshsave.txt", "graveyard.txt", "trophies.txt", "carparts.txt" };
        private readonly FieldInfo pathField;
        private readonly string original;
        private static string originalPath;
        public string Root { get; private set; }
        public SaveIsolation(string root)
        {
            Root = Path.GetFullPath(root); Directory.CreateDirectory(Root);
            Type type = typeof(ES2).Assembly.GetType("ES2FilenameData", true);
            pathField = type.GetField("persistentDataPath", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (pathField == null || pathField.FieldType != typeof(string)) throw new InvalidOperationException("Save isolation unavailable for this game build");
            if (originalPath == null) originalPath = (string)pathField.GetValue(null);
            original = originalPath;
        }
        public void Activate(string profile, bool import)
        {
            if (profile != "world" && profile != "client") throw new ArgumentException("Invalid save profile");
            string directory = Path.Combine(Root, profile); Directory.CreateDirectory(directory);
            if (import && !File.Exists(Path.Combine(directory, "defaultES2File.txt")))
            {
                foreach (string name in NativeFiles)
                {
                    string source = Path.Combine(Application.persistentDataPath, name);
                    if (File.Exists(source)) CopySafe(source, Path.Combine(directory, name));
                }
            }
            foreach (string name in new string[] { "options.txt" })
            {
                string source = Path.Combine(Application.persistentDataPath, name), destination = Path.Combine(directory, name);
                if (!File.Exists(destination) && File.Exists(source)) CopySafe(source, destination);
            }
            pathField.SetValue(null, directory + Path.DirectorySeparatorChar);
        }
        public string CurrentDirectory { get { return ((string)pathField.GetValue(null)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); } }
        public void Restore() { pathField.SetValue(null, original); }
        private static void CopySafe(string source, string destination) { AtomicFile.Write(destination, File.ReadAllBytes(source)); }
        public static byte[] Pack(string directory)
        {
            return Codec.Make(delegate(BinaryWriter w)
            {
                List<string> present = new List<string>(); foreach (string name in NativeFiles) if (File.Exists(Path.Combine(directory, name))) present.Add(name);
                w.Write(present.Count);
                foreach (string name in present) { byte[] data = File.ReadAllBytes(Path.Combine(directory, name)); if (data.Length > 16777216) throw new ProtocolException("Native save too large"); Codec.Text(w, name); w.Write(data.Length); w.Write(data); }
            });
        }
        public static void Unpack(string directory, byte[] bytes)
        {
            if (bytes.Length > Protocol.MaxSnapshot) throw new ProtocolException("Save bundle too large");
            Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            Codec.Read(bytes, delegate(BinaryReader r)
            {
                int n = Codec.Count(r, NativeFiles.Length);
                for (int i = 0; i < n; i++)
                {
                    string name = Codec.Text(r); if (Array.IndexOf(NativeFiles, name) < 0) throw new ProtocolException("Unexpected save filename");
                    int size = Codec.Count(r, 16777216); byte[] data = r.ReadBytes(size); if (data.Length != size) throw new EndOfStreamException(); files.Add(name, data);
                }
            });
            // Validate the complete bundle before touching the previous client profile.
            Directory.CreateDirectory(directory);
            foreach (string name in NativeFiles)
            {
                byte[] data; string destination = Path.Combine(directory, name);
                if (files.TryGetValue(name, out data)) AtomicFile.Write(destination, data);
                else if (File.Exists(destination)) File.Move(destination, destination + ".old-" + Guid.NewGuid().ToString("N"));
            }
        }
        public static string GameHash()
        {
            string managed = Path.Combine(Application.dataPath, "Managed");
            return Codec.Hash(Codec.Make(delegate(BinaryWriter w)
            {
                foreach (string file in new string[] { "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll", "PlayMaker.dll", "UnityEngine.dll" }) Codec.Text(w, Codec.Hash(File.ReadAllBytes(Path.Combine(managed, file))));
                List<string> files = new List<string>(Directory.GetFiles(Application.dataPath, "sharedassets*.assets")); files.Sort(StringComparer.Ordinal);
                files.Insert(0, Path.Combine(Application.dataPath, "level2"));
                foreach (string file in files)
                {
                    if (file.IndexOf("_BACKUP", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    Codec.Text(w, Path.GetFileName(file));
                    using (FileStream input = File.OpenRead(file)) using (SHA256 hash = SHA256.Create()) Codec.Text(w, BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant());
                }
                Codec.Text(w, Codec.Hash(File.ReadAllBytes(typeof(CoopRuntime).Assembly.Location)));
                Codec.Text(w, Codec.Hash(File.ReadAllBytes(typeof(Protocol).Assembly.Location)));
                Codec.Text(w, "Unity 5.0.0f4 / SummerCoop schema v2");
            }));
        }
    }
}
