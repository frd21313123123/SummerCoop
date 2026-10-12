using System;
using System.Collections.Generic;
using SummerCoop.Launcher;

internal static class LauncherTests
{
    private static int passed;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    private static LauncherSettings Base()
    {
        return new LauncherSettings
        {
            GamePath = @"E:\Game",
            ProfilePath = @"D:\Profile",
            PlayerName = "Test",
            Address = "10.0.0.2",
            Port = 27842,
            Password = "secret",
            LobbyId = "123456",
            Mode = ConnectionMode.HostLan
        };
    }
    private static bool Has(List<string> args, string key, string value)
    {
        for (int i = 0; i + 1 < args.Count; i++) if (args[i] == key && args[i + 1] == value) return true;
        return false;
    }
    private static int Main()
    {
        try
        {
            Test("host lan args carry profile, port, name and password", delegate
            {
                LauncherSettings s = Base(); s.Mode = ConnectionMode.HostLan;
                List<string> args = GameArgs.Build(s);
                Check(Has(args, "--summercoop-profile", @"D:\Profile"), "profile missing");
                Check(Has(args, "--summercoop-host-lan", "27842"), "host port missing");
                Check(Has(args, "--summercoop-name", "Test"), "name missing");
                Check(Has(args, "--summercoop-password", "secret"), "password missing");
            });
            Test("join lan args carry address and port", delegate
            {
                LauncherSettings s = Base(); s.Mode = ConnectionMode.JoinLan;
                List<string> args = GameArgs.Build(s);
                Check(Has(args, "--summercoop-join-lan", "10.0.0.2"), "address missing");
                Check(Has(args, "--summercoop-port", "27842"), "port missing");
            });
            Test("steam host and join use new runtime flags", delegate
            {
                LauncherSettings s = Base(); s.Mode = ConnectionMode.HostSteam;
                Check(GameArgs.Build(s).Contains("--summercoop-host-steam"), "host steam flag missing");
                s.Mode = ConnectionMode.JoinSteam;
                Check(Has(GameArgs.Build(s), "--summercoop-join-steam", "123456"), "join steam flag missing");
            });
            Test("solo launches without network flags", delegate
            {
                LauncherSettings s = Base(); s.Mode = ConnectionMode.Solo; s.Password = "";
                List<string> args = GameArgs.Build(s);
                Check(!args.Contains("--summercoop-host-lan") && !args.Contains("--summercoop-join-lan") && !args.Contains("--summercoop-host-steam") && !args.Contains("--summercoop-join-steam"), "solo has network flags");
            });
            Test("invalid connection settings are refused", delegate
            {
                LauncherSettings bad = Base(); bad.Mode = ConnectionMode.JoinLan; bad.Address = "";
                bool refused = false;
                try { GameArgs.Build(bad); } catch (ArgumentException) { refused = true; }
                Check(refused, "empty address accepted");
                bad = Base(); bad.Mode = ConnectionMode.JoinSteam; bad.LobbyId = "abc";
                refused = false;
                try { GameArgs.Build(bad); } catch (ArgumentException) { refused = true; }
                Check(refused, "bad lobby accepted");
                bad = Base(); bad.Port = 80;
                refused = false;
                try { GameArgs.Build(bad); } catch (ArgumentException) { refused = true; }
                Check(refused, "bad port accepted");
            });
            Test("command line quotes spaces like the game process needs", delegate
            {
                LauncherSettings s = Base(); s.ProfilePath = @"D:\My Coop\world"; s.PlayerName = "A B"; s.Mode = ConnectionMode.Solo;
                string line = GameArgs.ToCommandLine(GameArgs.Build(s));
                Check(line.IndexOf("\"D:\\My Coop\\world\"") >= 0 && line.IndexOf("\"A B\"") >= 0, "spaces not quoted: " + line);
            });
            Test("hidden LAN settings do not block Steam or in-game connection", delegate
            {
                LauncherSettings s = Base(); s.Port = 0;
                foreach (ConnectionMode mode in new ConnectionMode[] { ConnectionMode.Solo, ConnectionMode.HostSteam, ConnectionMode.JoinSteam })
                {
                    s.Mode = mode;
                    Check(s.Validate(true).Count == 0, "hidden port blocked " + mode);
                    Check(!GameArgs.Build(s).Contains("--summercoop-port"), "hidden port emitted for " + mode);
                }
                s.Mode = ConnectionMode.JoinLan;
                Check(s.Validate(true).Count != 0, "bad LAN port accepted");
            });
            Test("save import applies only when creating a world", delegate
            {
                LauncherSettings s = Base(); s.ImportSave = true;
                foreach (ConnectionMode mode in new ConnectionMode[] { ConnectionMode.Solo, ConnectionMode.HostLan, ConnectionMode.HostSteam, ConnectionMode.JoinLan, ConnectionMode.JoinSteam })
                {
                    s.Mode = mode;
                    bool host = mode == ConnectionMode.Solo || mode == ConnectionMode.HostLan || mode == ConnectionMode.HostSteam;
                    Check(GameArgs.Build(s).Contains("--summercoop-import-save") == host, "wrong import behavior for " + mode);
                }
            });
            Test("diagnostic command masks the whole password including spaces and quotes", delegate
            {
                LauncherSettings s = Base(); s.Password = "secret with \"quotes\" and \\slashes";
                List<string> args = GameArgs.Build(s);
                string display = GameArgs.ToDisplayCommandLine(args);
                Check(display.Contains("--summercoop-password ***"), "mask missing");
                Check(!display.Contains("secret") && !display.Contains("quotes") && !display.Contains("slashes"), "password leaked: " + display);
                Check(Has(args, "--summercoop-password", s.Password), "actual launch password changed");
            });
            Test("settings survive a save/load roundtrip", delegate
            {
                LauncherSettings s = Base(); s.Mode = ConnectionMode.JoinSteam; s.LaunchMethod = LaunchMethod.Steam; s.CloseOnLaunch = true; s.ImportSave = true;
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SummerCoop-LauncherTests-" + Guid.NewGuid().ToString("N") + ".ini");
                try
                {
                    s.Save(path);
                    LauncherSettings back = LauncherSettings.Load(path);
                    Check(back.Mode == ConnectionMode.JoinSteam && back.LaunchMethod == LaunchMethod.Steam && back.CloseOnLaunch && back.ImportSave && back.Port == 27842 && back.LobbyId == "123456", "roundtrip changed values");
                }
                finally { try { System.IO.File.Delete(path); } catch (Exception) { } }
            });
            Test("launcher version constants match the mod protocol", delegate
            {
                string[] candidates = new string[] { @"src\Core\Model.cs", @"..\..\src\Core\Model.cs", @"..\..\..\src\Core\Model.cs" };
                string model = null;
                foreach (string c in candidates) if (System.IO.File.Exists(c)) { model = System.IO.File.ReadAllText(c); break; }
                Check(model != null, "src/Core/Model.cs not found from test working directory");
                Check(model.IndexOf("ModVersion = \"" + LauncherInfo.ModVersion + "\"") >= 0, "ModVersion drifted from src/Core/Model.cs");
                Check(model.IndexOf("Version = " + LauncherInfo.ProtocolVersion) >= 0, "Protocol version drifted");
                Check(model.IndexOf("DefaultPort = " + LauncherInfo.DefaultPort) >= 0, "DefaultPort drifted");
            });
            Console.WriteLine("ALL " + passed + " LAUNCHER TESTS PASSED");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
