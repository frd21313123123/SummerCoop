using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SummerCoop.Setup;

internal static class InstallerTests
{
    private static int passed;
    private sealed class Services : InstallerServices, IDisposable
    {
        internal bool Running, RefuseMigration, BlockCore;
        internal int Migrations;
        private FileStream block;
        internal override bool IsGameRunning(string game) { return Running; }
        internal override void Migrate(string game, string profile, Payload payload, Action<string> log)
        {
            Migrations++;
            File.WriteAllText(Path.Combine(profile, "world.scw"), "new checkpoint");
            File.WriteAllText(Path.Combine(profile, "world.scj"), "new journal");
            File.WriteAllText(Path.Combine(profile, "created-during-update"), "new");
            if (RefuseMigration) throw new IOException("Incompatible checkpoint");
            if (BlockCore) block = new FileStream(Path.Combine(payload.DirectoryPath, Payload.Files[1]), FileMode.Open, FileAccess.Read, FileShare.None);
        }
        public void Dispose() { if (block != null) block.Dispose(); }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root, Game, Profile;
        internal readonly Payload Payload = new Payload();
        internal readonly Services Services = new Services();
        internal Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "SummerCoop-InstallerTests-" + Guid.NewGuid().ToString("N"));
            Game = Path.Combine(Root, "Игра с пробелами"); Profile = Path.Combine(Root, "Мир хоста");
            Write(Game, "mysummercar.exe", "fixture game, not executable");
            Write(Game, @"mysummercar_Data\Managed\MSCLoader.dll", "loader");
            Write(Game, "doorstop_config.ini", "[General]\r\nenabled = true\r\n[MSCLoader]\r\nmods = GF\r\n");
            Directory.CreateDirectory(Profile);
            Payload.DirectoryPath = Path.Combine(Root, "payload");
            foreach (string file in Payload.Files) { Write(Payload.DirectoryPath, file, "new " + file); Payload.Hashes.Add(file, SafePath.Hash(Path.Combine(Payload.DirectoryPath, file))); }
        }
        internal void OldWorld()
        {
            Write(Game, @"Mods\SummerCoop.dll", "old mod"); Write(Game, @"Mods\References\SummerCoop.Core.dll", "old core");
            Write(Profile, "world.scw", "old checkpoint"); Write(Profile, "world.scw.bak", "old backup");
            Write(Profile, "world.scj", "old journal"); Write(Profile, "defaultES2File.txt", "native car assembly");
            Write(Profile, "trophies.txt", "native trophies");
        }
        internal InstallResult Install(bool disable) { return new Installer(Services).Install(Game, Profile, Payload, disable, delegate(string message) { }); }
        public void Dispose()
        {
            Services.Dispose();
            SafePath.NoLinks(Root);
            if (!Path.GetFullPath(Root).StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\SummerCoop-InstallerTests-", StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe fixture cleanup");
            Directory.Delete(Root, true);
        }
    }
    private static void Write(string root, string file, string value) { string path = SafePath.Under(root, file); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, value, Encoding.UTF8); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Refuses(Action action)
    {
        bool refused = false;
        try { action(); } catch (IOException) { refused = true; }
        Check(refused, "Operation should have been refused");
    }
    private static void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    private static void OldWorldUnchanged(Fixture f)
    {
        Check(File.ReadAllText(Path.Combine(f.Profile, "world.scw")) == "old checkpoint", "checkpoint changed");
        Check(File.ReadAllText(Path.Combine(f.Profile, "world.scw.bak")) == "old backup", "checkpoint backup changed");
        Check(File.ReadAllText(Path.Combine(f.Profile, "world.scj")) == "old journal", "journal changed");
        Check(File.ReadAllText(Path.Combine(f.Profile, "defaultES2File.txt")) == "native car assembly", "native save changed");
        Check(!File.Exists(Path.Combine(f.Profile, "created-during-update")), "new profile file was not rolled back");
    }
    private static int Main()
    {
        try
        {
            Test("Steam modern/legacy libraries, escaped Unicode paths and installed manifest", delegate
            {
                using (Fixture f = new Fixture())
                {
                    string steam = Path.Combine(f.Root, "Steam"), library = Path.Combine(f.Root, "Библиотека Steam"), legacy = Path.Combine(f.Root, "Old Steam");
                    string found = Path.Combine(library, @"steamapps\common\Custom Game Name");
                    Write(found, "mysummercar.exe", "game"); Directory.CreateDirectory(Path.Combine(found, "mysummercar_Data"));
                    Write(library, @"steamapps\appmanifest_516750.acf", "\"AppState\" { \"appid\" \"516750\" \"installdir\" \"Custom Game Name\" }");
                    string second = Path.Combine(legacy, @"steamapps\common\My Summer Car");
                    Write(second, "mysummercar.exe", "game"); Directory.CreateDirectory(Path.Combine(second, "mysummercar_Data"));
                    Write(steam, @"steamapps\libraryfolders.vdf", "// steam file\n\"libraryfolders\" { \"0\" { \"path\" \"" + library.Replace("\\", "\\\\") + "\" \"apps\" { \"516750\" \"1\" } } \"1\" \"" + legacy.Replace("\\", "\\\\") + "\" }");
                    List<string> games = SteamDiscovery.Discover(new string[] { steam, steam });
                    Check(games.Count == 2 && games.Contains(found) && games.Contains(second), "Steam discovery returned wrong directories");
                    Write(library, @"steamapps\appmanifest_516750.acf", "\"AppState\" { \"installdir\" \"..\\..\\escape\" }");
                    Check(SteamDiscovery.Discover(new string[] { steam }).Count == 1, "Manifest escaped common directory");
                }
            });
            Test("Fresh install, backups, unrelated assets and original saves", delegate
            {
                using (Fixture f = new Fixture())
                {
                    Write(f.Game, @"Mods\Other.dll", "other mod"); Write(f.Game, @"Mods\References\Dependency.dll", "other reference");
                    Write(f.Game, @"Mods\MSCLoader_Console.dll", "loader console"); Write(f.Game, @"Mods\MSCLoader_Settings.dll", "loader settings");
                    Write(f.Game, @"Mods\Assets\Other\asset.bin", "asset"); Write(f.Profile, "defaultES2File.txt", "native save");
                    InstallResult result = f.Install(true);
                    Check(result.DisabledMods == 2, "Wrong disabled DLL count");
                    Check(!File.Exists(Path.Combine(f.Game, @"Mods\Other.dll")), "Other mod remains active");
                    Check(File.ReadAllText(Path.Combine(result.BackupPath, @"disabled\References\Dependency.dll")) == "other reference", "Reference not backed up");
                    Check(File.Exists(Path.Combine(f.Game, @"Mods\MSCLoader_Console.dll")) && File.Exists(Path.Combine(f.Game, @"Mods\MSCLoader_Settings.dll")), "Loader service mods removed");
                    Check(File.ReadAllText(Path.Combine(f.Game, @"Mods\Assets\Other\asset.bin")) == "asset", "Asset changed");
                    Check(File.ReadAllText(Path.Combine(f.Profile, "defaultES2File.txt")) == "native save", "Native save changed");
                    for (int i = 0; i < 2; i++) Check(SafePath.Hash(Path.Combine(f.Game, "Mods", Payload.Files[i])) == f.Payload.Hashes[Payload.Files[i]], "Installed checksum differs");
                    Check(!File.Exists(Path.Combine(f.Game, "Mods", Payload.Files[2])), "Migration helper was installed as a mod");
                }
            });
            Test("Other mods can be retained explicitly", delegate
            { using (Fixture f = new Fixture()) { Write(f.Game, @"Mods\Other.dll", "other"); Check(f.Install(false).DisabledMods == 0 && File.Exists(Path.Combine(f.Game, @"Mods\Other.dll")), "Retention option ignored"); } });
            Test("Repeat install keeps an already matching world byte for byte", delegate
            {
                using (Fixture f = new Fixture())
                { f.Install(true); Write(f.Profile, "world.scw", "matching world"); Write(f.Profile, "world.scj", "matching journal"); f.Install(true); Check(f.Services.Migrations == 0 && File.ReadAllText(Path.Combine(f.Profile, "world.scw")) == "matching world" && File.ReadAllText(Path.Combine(f.Profile, "world.scj")) == "matching journal", "Repeat install migrated a matching world"); }
            });
            Test("Compatible world update backs up old pair and native profile", delegate
            {
                using (Fixture f = new Fixture())
                {
                    f.OldWorld(); InstallResult result = f.Install(true);
                    Check(f.Services.Migrations == 1, "Migration was not invoked");
                    Check(File.ReadAllText(Path.Combine(result.BackupPath, @"previous\SummerCoop.dll")) == "old mod", "Old mod missing");
                    Check(File.ReadAllText(Path.Combine(result.BackupPath, @"profile\world.scw")) == "old checkpoint", "Old checkpoint missing");
                    Check(File.ReadAllText(Path.Combine(result.BackupPath, @"profile\defaultES2File.txt")) == "native car assembly", "Native profile not backed up");
                }
            });
            Test("Refused migration restores profile and preserves other mods", delegate
            {
                using (Fixture f = new Fixture())
                {
                    f.OldWorld(); f.Services.RefuseMigration = true; Write(f.Game, @"Mods\Other.dll", "other"); Refuses(delegate { f.Install(true); }); OldWorldUnchanged(f);
                    Check(File.ReadAllText(Path.Combine(f.Game, @"Mods\SummerCoop.dll")) == "old mod" && File.ReadAllText(Path.Combine(f.Game, @"Mods\Other.dll")) == "other", "DLLs changed on migration failure");
                }
            });
            Test("Failure after first DLL restores old pair, profile and disabled mods", delegate
            {
                using (Fixture f = new Fixture())
                {
                    f.OldWorld(); f.Services.BlockCore = true; Write(f.Game, @"Mods\Other.dll", "other"); Refuses(delegate { f.Install(true); }); OldWorldUnchanged(f);
                    Check(File.ReadAllText(Path.Combine(f.Game, @"Mods\SummerCoop.dll")) == "old mod", "First DLL not restored");
                    Check(File.ReadAllText(Path.Combine(f.Game, @"Mods\References\SummerCoop.Core.dll")) == "old core", "Second DLL changed");
                    Check(File.ReadAllText(Path.Combine(f.Game, @"Mods\Other.dll")) == "other", "Disabled mod not restored");
                }
            });
            Test("World without previous DLLs is refused", delegate
            { using (Fixture f = new Fixture()) { Write(f.Profile, "world.scw", "existing"); Refuses(delegate { f.Install(true); }); Check(!Directory.Exists(Path.Combine(f.Game, "Mods")), "Files installed despite missing previous pair"); } });
            Test("Damaged payload is refused before touching installed DLLs", delegate
            { using (Fixture f = new Fixture()) { f.OldWorld(); Write(f.Payload.DirectoryPath, Payload.Files[0], "corrupt"); Refuses(delegate { f.Install(true); }); OldWorldUnchanged(f); Check(File.ReadAllText(Path.Combine(f.Game, @"Mods\SummerCoop.dll")) == "old mod", "Old mod changed"); } });
            Test("Missing loader, wrong mod folder and disabled loader are refused", delegate
            {
                using (Fixture f = new Fixture())
                {
                    File.Delete(Path.Combine(f.Game, @"mysummercar_Data\Managed\MSCLoader.dll")); Refuses(delegate { f.Install(true); });
                    Write(f.Game, @"mysummercar_Data\Managed\MSCLoader.dll", "loader"); Write(f.Game, "doorstop_config.ini", "[MSCLoader]\nmods = AD"); Refuses(delegate { f.Install(true); });
                    Write(f.Game, "doorstop_config.ini", "[General]\nenabled = false\n[MSCLoader]\nmods = GF"); Refuses(delegate { f.Install(true); });
                    Check(!Directory.Exists(Path.Combine(f.Game, "Mods")), "Dependency failure installed DLLs");
                }
            });
            Test("Running game and concurrent installation are refused", delegate
            {
                using (Fixture f = new Fixture())
                {
                    f.Services.Running = true; Refuses(delegate { f.Install(true); }); f.Services.Running = false;
                    string locks = Path.Combine(f.Game, "SummerCoop-disabled-mods"); Directory.CreateDirectory(locks);
                    using (FileStream locked = new FileStream(Path.Combine(locks, "setup.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None)) Refuses(delegate { f.Install(true); });
                    Check(!Directory.Exists(Path.Combine(f.Game, "Mods")), "Locked install modified DLLs");
                }
            });
            Test("Invalid game and escaping paths are refused", delegate
            { using (Fixture f = new Fixture()) { File.Delete(Path.Combine(f.Game, "mysummercar.exe")); Refuses(delegate { f.Install(true); }); Refuses(delegate { SafePath.Under(f.Root, @"..\outside"); }); Refuses(delegate { SafePath.Under(f.Root, "E:\\outside"); }); } });
            Console.WriteLine("ALL " + passed + " INSTALLER TESTS PASSED"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
