using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SummerCoop.Core;

// Explicit same-schema update only; never guesses a game/protocol migration.
internal static class MigrateProfile
{
    private static string GameHash(string game, string mod, string core)
    {
        string data = Path.Combine(game, "mysummercar_Data"), managed = Path.Combine(data, "Managed");
        return Codec.Hash(Codec.Make(delegate(BinaryWriter writer)
        {
            foreach (string file in new string[] { "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll", "PlayMaker.dll", "UnityEngine.dll" }) Codec.Text(writer, Codec.Hash(File.ReadAllBytes(Path.Combine(managed, file))));
            List<string> files = new List<string>(Directory.GetFiles(data, "sharedassets*.assets")); files.Sort(StringComparer.Ordinal); files.Insert(0, Path.Combine(data, "level2"));
            foreach (string file in files)
            {
                if (file.IndexOf("_BACKUP", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                Codec.Text(writer, Path.GetFileName(file));
                using (FileStream input = File.OpenRead(file)) using (SHA256 sha = SHA256.Create()) Codec.Text(writer, BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant());
            }
            Codec.Text(writer, Codec.Hash(File.ReadAllBytes(mod))); Codec.Text(writer, Codec.Hash(File.ReadAllBytes(core))); Codec.Text(writer, "Unity 5.0.0f4 / SummerCoop schema v2");
        }));
    }
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 6 || args[5] != "--same-schema-v2") throw new ArgumentException("Usage: MigrateProfile game profile old-mod old-core new-build-directory --same-schema-v2");
            string game = Path.GetFullPath(args[0]), profile = Path.GetFullPath(args[1]);
            if (!File.Exists(Path.Combine(profile, "world.scw"))) { Console.WriteLine("No existing world checkpoint to migrate."); return 0; }
            string oldHash = GameHash(game, args[2], args[3]);
            string newHash = GameHash(game, Path.Combine(args[4], "SummerCoop.dll"), Path.Combine(args[4], "SummerCoop.Core.dll"));
            World world = new CheckpointStore(profile).Recover();
            if (world == null) throw new IOException("Profile cannot be recovered; it was not modified.");
            string oldSchema = Codec.Hash(Encoding.UTF8.GetBytes(oldHash + "/native-schema-2"));
            if (world.GameHash != oldHash || world.SchemaHash != oldSchema) throw new InvalidOperationException("Checkpoint does not match the installed old DLLs and current game resources; automatic migration refused.");
            string backup = Path.Combine(profile, "before-performance-update-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(backup);
            foreach (string file in Directory.GetFiles(profile)) File.Copy(file, Path.Combine(backup, Path.GetFileName(file)), false);
            world.GameHash = newHash; world.SchemaHash = Codec.Hash(Encoding.UTF8.GetBytes(newHash + "/native-schema-2"));
            new CheckpointStore(profile).Checkpoint(world);
            Console.WriteLine("Compatible profile updated; world ID, entities, money and native save files preserved."); Console.WriteLine("Backup: " + backup);
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }
}
