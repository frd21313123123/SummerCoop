using System;
using System.Collections.Generic;
using System.IO;

namespace SummerCoop.Core
{
    public sealed class PersonalKeys
    {
        public static readonly string[] Names = { "PlayerKeyHome", "PlayerKeySatsuma", "PlayerKeyRuscko", "PlayerKeyHayosiko", "PlayerKeyGifu", "PlayerKeyFerndale" };
        private readonly string path;
        private readonly Dictionary<string, int> values = new Dictionary<string, int>(StringComparer.Ordinal);
        private byte[] saved;
        public PersonalKeys(string root, string world, string identity, IDictionary<string, int> initial)
        {
            if (string.IsNullOrEmpty(world) || string.IsNullOrEmpty(identity)) throw new ArgumentException("Key profile identity is required");
            path = Path.Combine(root, Identity.Stable(world + "/" + identity) + ".sck");
            foreach (string name in Names) { int value; Set(name, initial.TryGetValue(name, out value) ? value : 0); }
            if (File.Exists(path))
            {
                byte[] bytes = File.ReadAllBytes(path);
                Codec.Read(bytes, delegate(BinaryReader reader)
                {
                    if (reader.ReadInt32() != 1) throw new ProtocolException("Unsupported personal key profile");
                    foreach (string name in Names) Set(name, reader.ReadInt32());
                });
                saved = bytes;
            }
        }
        public int Get(string name) { return values[name]; }
        public void Set(string name, int value)
        {
            if (Array.IndexOf(Names, name) < 0 || value < 0 || value > 2) throw new ProtocolException("Invalid personal key value");
            values[name] = value;
        }
        public bool Save()
        {
            byte[] bytes = Codec.Make(delegate(BinaryWriter writer) { writer.Write(1); foreach (string name in Names) writer.Write(values[name]); });
            bool equal = saved != null && bytes.Length == saved.Length;
            if (equal) for (int i = 0; i < bytes.Length; i++) if (bytes[i] != saved[i]) { equal = false; break; }
            if (equal) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path)); AtomicFile.Write(path, bytes); saved = bytes; return true;
        }
        public static bool IsVariable(string name) { return name != null && name.StartsWith("PlayerKey", StringComparison.Ordinal); }
        public static bool IsPickup(string name) { return name == "VanKeys" || name == "GifuKeys"; }
    }
}
