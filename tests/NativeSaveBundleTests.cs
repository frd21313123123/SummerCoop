using System;
using System.IO;
using SummerCoop.Core;

internal static class NativeSaveBundleTests
{
    static void Check(bool value) { if (!value) throw new Exception("MSC save bundle assertion failed"); }
    static void Reject(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Invalid MSC save accepted"); }
    static NativeSaveBundle Bundle(byte[] data) { return new NativeSaveBundle(new[] { "defaultES2File.txt" }, new[] { data.Length }, new[] { Codec.Hash(data) }); }
    public static void Run(Action<string, Action> test)
    {
        test("MSC manifest rejects arbitrary filenames, duplicate files and options before allocating", delegate {
            string hash = Codec.Hash(new byte[0]);
            foreach (string name in new[] { "../defaultES2File.txt", "options.txt", "C:\\x.txt", "Mods.txt", "savefile.txt" }) Reject(delegate { new NativeSaveBundle(new[] { name }, new[] { 0 }, new[] { hash }); });
            Reject(delegate { new NativeSaveBundle(new[] { "items.txt", "items.txt" }, new[] { 0, 0 }, new[] { hash, hash }); });
        });
        test("MSC manifest bounds sizes, total bytes and checksums before allocation", delegate {
            string hash = Codec.Hash(new byte[0]);
            Reject(delegate { new NativeSaveBundle(new[] { "items.txt" }, new[] { -1 }, new[] { hash }); });
            Reject(delegate { new NativeSaveBundle(new[] { "items.txt" }, new[] { NativeSaveBundle.MaxFileBytes + 1 }, new[] { hash }); });
            Reject(delegate { new NativeSaveBundle(new[] { "items.txt" }, new[] { 1 }, new[] { new string('z', 64) }); });
            string[] names = { "items.txt", "meshsave.txt", "carparts.txt", "trophies.txt", "graveyard.txt" }; int[] sizes = { 16777216, 16777216, 16777216, 16777216, 1 };
            Reject(delegate { new NativeSaveBundle(names, sizes, new[] { hash, hash, hash, hash, hash }); });
        });
        test("MSC save rejects duplicate, reordered, empty and oversized chunks", delegate {
            byte[] bytes = { 1, 2, 3 }; NativeSaveBundle bundle = Bundle(bytes);
            Reject(delegate { bundle.Append(1, 0, bytes); }); Reject(delegate { bundle.Append(0, 1, bytes); }); Reject(delegate { bundle.Append(0, 0, new byte[0]); });
            bundle.Append(0, 0, new byte[] { 1 }); Reject(delegate { bundle.Append(0, 0, bytes); }); Reject(delegate { bundle.Append(0, 1, bytes); });
            bundle.Append(0, 1, new byte[] { 2, 3 }); Check(bundle.Received == 3 && bundle.Expected == 3);
        });
        test("MSC save commits exact files, removes stale native data and preserves local options", delegate {
            string dir = Path.Combine(Path.GetTempPath(), "msc-bundle-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            try {
                File.WriteAllText(Path.Combine(dir, "options.txt"), "my controls"); File.WriteAllText(Path.Combine(dir, "items.txt"), "stale");
                byte[] bytes = { 5, 4, 3, 2, 1 }; NativeSaveBundle bundle = Bundle(bytes); bundle.Append(0, 0, bytes); bundle.Commit(dir);
                Check(Codec.Hash(File.ReadAllBytes(Path.Combine(dir, "defaultES2File.txt"))) == Codec.Hash(bytes));
                Check(!File.Exists(Path.Combine(dir, "items.txt")) && File.ReadAllText(Path.Combine(dir, "options.txt")) == "my controls");
            } finally { Directory.Delete(dir, true); }
        });
        test("incomplete or corrupt MSC saves never alter the previous profile", delegate {
            string dir = Path.Combine(Path.GetTempPath(), "msc-corrupt-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            try {
                string path = Path.Combine(dir, "defaultES2File.txt"); File.WriteAllText(path, "old");
                NativeSaveBundle incomplete = Bundle(new byte[] { 1, 2 }); incomplete.Append(0, 0, new byte[] { 1 }); Reject(delegate { incomplete.Commit(dir); });
                NativeSaveBundle corrupt = Bundle(new byte[] { 1, 2 }); corrupt.Append(0, 0, new byte[] { 2, 1 }); Reject(delegate { corrupt.Commit(dir); }); Check(File.ReadAllText(path) == "old");
            } finally { Directory.Delete(dir, true); }
        });
        test("MSC profile preflight failure preserves all old files", delegate {
            string dir = Path.Combine(Path.GetTempPath(), "msc-refused-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            try {
                string path = Path.Combine(dir, "defaultES2File.txt"); File.WriteAllText(path, "old"); Directory.CreateDirectory(Path.Combine(dir, "items.txt"));
                NativeSaveBundle bundle = Bundle(new byte[] { 1 }); bundle.Append(0, 0, new byte[] { 1 }); Reject(delegate { bundle.Commit(dir); }); Check(File.ReadAllText(path) == "old");
            } finally { Directory.Delete(dir, true); }
        });
    }
}
