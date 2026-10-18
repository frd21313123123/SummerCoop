using System;
using System.IO;
using SummerCoop.Core;

internal static class MscSaveProfileTests
{
    static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    static void Fixture(Action<string> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "msc-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { test(root); } finally { Directory.Delete(root, true); }
    }
    static void Write(string dir, string name, string text) { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, name), text); }
    public static void Run(Action<string, Action> test)
    {
        test("launcher saves resolve to separate named worlds and keep the previous host and guest paths", delegate { Fixture(delegate(string root) {
            string profiles = Path.Combine(root, "original game profiles");
            string main = MscSaveProfiles.Resolve(root, profiles, "", false);
            string one = MscSaveProfiles.Resolve(root, profiles, "partie-1", false);
            string two = MscSaveProfiles.Resolve(root, profiles, "partie-2", false);
            Check(main == Path.Combine(root, @"mwcoop-profiles\host"), "Previous host world moved");
            Check(one == Path.Combine(profiles, @"partie-1\LocalLow\Amistech\My Summer Car") && one != two && two != main, "Named saves share a world");
            Check(MscSaveProfiles.Resolve(root, profiles, "partie-1", true) == Path.Combine(root, @"mwcoop-profiles\guest"), "Guest can write the selected host world");
            foreach (string invalid in new[] { "../host", "partie-../host", "partie-0", "partie-1000", "partie-01", "partie-1\\other" }) {
                bool rejected = false; try { MscSaveProfiles.Resolve(root, profiles, invalid, false); } catch (IOException) { rejected = true; }
                Check(rejected, "Invalid profile accepted: " + invalid);
            }
        }); });
        test("new lobby worlds stay empty instead of importing the previous host or single-player save", delegate { Fixture(delegate(string root) {
            string source = Path.Combine(root, "single player"), main = MscSaveProfiles.Resolve(root, root, "", false);
            Write(source, "defaultES2File.txt", "single player"); Write(main, "defaultES2File.txt", "old host");
            foreach (string profile in new[] { "partie-1", "partie-2" }) {
                string world = MscSaveProfiles.Resolve(root, Path.Combine(root, "profiles"), profile, false);
                MscSaveProfiles.Prepare(world, source, false, true, false);
                MscSaveProfiles.Prepare(world, source, false, false, false);
                Check(!File.Exists(Path.Combine(world, "defaultES2File.txt")), "Empty world imported an old save");
            }
            Check(File.ReadAllText(Path.Combine(source, "defaultES2File.txt")) == "single player" && File.ReadAllText(Path.Combine(main, "defaultES2File.txt")) == "old host", "New game replaced an old world");
        }); });
        test("switching saves and restarting after an update preserves each world's exact files", delegate { Fixture(delegate(string root) {
            string one = MscSaveProfiles.Resolve(root, root, "partie-1", false), two = MscSaveProfiles.Resolve(root, root, "partie-2", false);
            Write(one, "defaultES2File.txt", "world one"); Write(two, "defaultES2File.txt", "world two");
            for (int i = 0; i < 3; i++) {
                MscSaveProfiles.Prepare(one, root, false, false, false);
                MscSaveProfiles.Prepare(two, root, false, false, false);
            }
            Check(File.ReadAllText(Path.Combine(one, "defaultES2File.txt")) == "world one" && File.ReadAllText(Path.Combine(two, "defaultES2File.txt")) == "world two", "Continue altered another world");
        }); });
        test("main world imports only once, and an explicit restart keeps a recovery copy and personal options", delegate { Fixture(delegate(string root) {
            string source = Path.Combine(root, "source"), world = Path.Combine(root, "host");
            Write(source, "defaultES2File.txt", "legacy"); Write(source, "items.txt", "legacy items");
            MscSaveProfiles.Prepare(world, source, false, false, true);
            Check(File.ReadAllText(Path.Combine(world, "defaultES2File.txt")) == "legacy", "Initial import lost the legacy world");
            Write(world, "options.txt", "my controls");
            MscSaveProfiles.Prepare(world, source, false, true, true);
            string[] backups = Directory.GetDirectories(world, "backup-*");
            Check(backups.Length == 1 && File.ReadAllText(Path.Combine(backups[0], "defaultES2File.txt")) == "legacy", "Restart has no recovery copy");
            Check(!File.Exists(Path.Combine(world, "items.txt")) && File.ReadAllText(Path.Combine(world, "options.txt")) == "my controls", "Restart mixed old world data or removed options");
            MscSaveProfiles.Prepare(world, source, false, false, true);
            Check(!File.Exists(Path.Combine(world, "defaultES2File.txt")), "Old world returned after restart");
        }); });
        test("failed profile reset leaves the previous world intact", delegate { Fixture(delegate(string root) {
            Write(root, "defaultES2File.txt", "old world"); Directory.CreateDirectory(Path.Combine(root, "items.txt"));
            bool rejected = false; try { MscSaveProfiles.Prepare(root, root, false, true, false); } catch (IOException) { rejected = true; }
            Check(rejected && File.ReadAllText(Path.Combine(root, "defaultES2File.txt")) == "old world", "Failed reset destroyed the old world");
        }); });
    }
}
