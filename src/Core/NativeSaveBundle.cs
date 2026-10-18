using System;
using System.Collections.Generic;
using System.IO;

namespace SummerCoop.Core
{
    // MSC files only. Options, arbitrary mod files and paths never cross the network.
    public sealed class NativeSaveBundle
    {
        public static readonly string[] Names = { "defaultES2File.txt", "items.txt", "meshsave.txt", "graveyard.txt", "trophies.txt", "carparts.txt", "MWCoop-sacs.txt", "MSC-personal-items.txt", "MWCoop-monde.txt" };
        public const int MaxFileBytes = 16 * 1024 * 1024;
        private readonly Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly string[] names, hashes;
        private readonly int[] offsets;
        public int Expected { get; private set; }
        public int Received { get; private set; }
        public int Count { get { return names.Length; } }
        public NativeSaveBundle(string[] names, int[] sizes, string[] hashes)
        {
            if (names == null || sizes == null || hashes == null || names.Length != sizes.Length || sizes.Length != hashes.Length || names.Length > Names.Length) throw new ProtocolException("Invalid MSC save manifest");
            this.names = (string[])names.Clone(); this.hashes = (string[])hashes.Clone(); offsets = new int[names.Length];
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            for (int i = 0; i < names.Length; i++)
            {
                if (Array.IndexOf(Names, names[i]) < 0 || !seen.Add(names[i]) || sizes[i] < 0 || sizes[i] > MaxFileBytes) throw new ProtocolException("Invalid MSC save file");
                if (hashes[i] == null || hashes[i].Length != 64) throw new ProtocolException("Invalid save checksum");
                foreach (char c in hashes[i]) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) throw new ProtocolException("Invalid save checksum");
                total += sizes[i]; if (total > Protocol.MaxSnapshot) throw new ProtocolException("MSC save exceeds transfer budget");
            }
            Expected = (int)total;
            for (int i = 0; i < names.Length; i++) files.Add(names[i], new byte[sizes[i]]);
        }
        public void Append(int index, int offset, byte[] bytes)
        {
            if (index < 0 || index >= names.Length || bytes == null || bytes.Length == 0 || offset != offsets[index]) throw new ProtocolException("Out of order MSC save chunk");
            byte[] file = files[names[index]];
            if (offset < 0 || bytes.Length > file.Length - offset) throw new ProtocolException("MSC save chunk exceeds file");
            Buffer.BlockCopy(bytes, 0, file, offset, bytes.Length); offsets[index] += bytes.Length; Received += bytes.Length;
        }
        public void Commit(string directory)
        {
            for (int i = 0; i < names.Length; i++) if (offsets[i] != files[names[i]].Length || Codec.Hash(files[names[i]]) != hashes[i]) throw new ProtocolException("Incomplete or corrupt MSC save");
            directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
            string backup = Path.Combine(directory, ".incoming-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(backup);
            bool cleanup = false;
            List<string> changed = new List<string>();
            try
            {
                // Back up every affected file before changing any existing data.
                foreach (string name in Names)
                {
                    string path = Path.Combine(directory, name);
                    if (Directory.Exists(path)) throw new IOException("Save filename is a directory: " + name);
                    if (File.Exists(path)) File.Copy(path, Path.Combine(backup, name));
                }
                try
                {
                    foreach (string name in Names)
                    {
                        changed.Add(name); byte[] data; string path = Path.Combine(directory, name);
                        if (files.TryGetValue(name, out data)) AtomicFile.Write(path, data);
                        else if (File.Exists(path)) File.Delete(path);
                    }
                }
                catch
                {
                    foreach (string name in changed)
                    {
                        string path = Path.Combine(directory, name), old = Path.Combine(backup, name);
                        if (File.Exists(old)) File.Copy(old, path, true); else if (File.Exists(path)) File.Delete(path);
                    }
                    cleanup = true; throw;
                }
                cleanup = true;
            }
            finally { if (cleanup) { try { Directory.Delete(backup, true); } catch (IOException) { } } }
        }
    }
}
