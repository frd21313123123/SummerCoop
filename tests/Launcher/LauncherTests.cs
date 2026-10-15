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
            Test("Steam hosting and joining always route through the installed Steam client", delegate
            {
                string fixture = System.IO.Path.GetTempFileName();
                try
                {
                    foreach (ConnectionMode mode in new[] { ConnectionMode.HostSteam, ConnectionMode.JoinSteam })
                    foreach (LaunchMethod method in new[] { LaunchMethod.Auto, LaunchMethod.Steam })
                    {
                        LauncherSettings s = Base(); s.Mode = mode; s.LaunchMethod = method;
                        var launch = GameArgs.Launch(s, fixture);
                        Check(launch.FileName == fixture && launch.Arguments.StartsWith("-applaunch 516750 "), "Steam mode launched the game EXE directly");
                        Check(launch.Arguments.EndsWith(GameArgs.ToCommandLine(GameArgs.Build(s))), "Steam lost game connection arguments");
                        Check(launch.WorkingDirectory == s.GamePath && !launch.UseShellExecute, "launch context changed");
                    }
                }
                finally { System.IO.File.Delete(fixture); }
            });
            Test("a missing Steam client cannot silently fall back to direct EXE", delegate
            {
                foreach (ConnectionMode mode in new[] { ConnectionMode.HostSteam, ConnectionMode.JoinSteam })
                foreach (string missing in new[] { null, "", System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N")) })
                {
                    LauncherSettings s = Base(); s.Mode = mode;
                    bool refused = false;
                    try { GameArgs.Launch(s, missing); } catch (System.IO.FileNotFoundException) { refused = true; }
                    Check(refused, "Steam launch fell back to direct EXE");
                }
            });
            Test("direct EXE is refused for Steam multiplayer and retained for offline LAN", delegate
            {
                LauncherSettings s = Base(); s.LaunchMethod = LaunchMethod.DirectExe;
                foreach (ConnectionMode mode in new[] { ConnectionMode.HostSteam, ConnectionMode.JoinSteam })
                {
                    s.Mode = mode; bool refused = false;
                    try { GameArgs.Launch(s, null); } catch (ArgumentException) { refused = true; }
                    Check(refused, "Steam multiplayer accepted direct EXE");
                }
                foreach (ConnectionMode mode in new[] { ConnectionMode.HostLan, ConnectionMode.JoinLan, ConnectionMode.Solo })
                {
                    s.Mode = mode;
                    var launch = GameArgs.Launch(s, null);
                    Check(launch.FileName == System.IO.Path.Combine(s.GamePath, "mysummercar.exe") && !launch.Arguments.StartsWith("-applaunch"), "offline launch requires Steam");
                }
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
            Test("Steam invitation accepts the full unsigned lobby id and rejects malformed commands", delegate
            {
                SteamLaunchRequest request;
                Check(SteamLaunchRequest.TryParse(new[] { "+connect_lobby", "18446744073709551615" }, out request) && request.LobbyId == ulong.MaxValue, "64-bit lobby truncated");
                Check(SteamLaunchRequest.TryParse(new[] { "+connect_lobby=123456" }, out request) && request.LobbyId == 123456, "equals form lost");
                foreach (string[] args in new[] { new[] { "+connect_lobby" }, new[] { "+connect_lobby", "0" }, new[] { "+connect_lobby", "-1" }, new[] { "+connect_lobby", "+123" }, new[] { "+connect_lobby", " 123" }, new[] { "+connect_lobby", "18446744073709551616" }, new[] { "+connect_lobby", "123", "+connect_lobby", "456" } })
                {
                    bool refused = false;
                    try { SteamLaunchRequest.TryParse(args, out request); } catch (ArgumentException) { refused = true; }
                    Check(refused, "malformed Steam command accepted: " + string.Join(" ", args));
                }
                Check(!SteamLaunchRequest.TryParse(new[] { "--print-args", "--mode=host-lan" }, out request), "CLI was intercepted");
                Check(!SteamLaunchRequest.TryParse(new[] { "other.exe", "+connect_lobby", "123" }, out request), "arbitrary executable was accepted");
            });
            Test("Steam launch option extracts the game path and lobby without executing supplied commands", delegate
            {
                SteamLaunchRequest request;
                Check(SteamLaunchRequest.TryParse(new[] { @"E:\Steam Library\My Summer Car\mysummercar.exe", "-force-d3d9", "+connect_lobby", "109775241234567890" }, out request), "Steam %command% not recognized");
                Check(request.GamePath == @"E:\Steam Library\My Summer Car" && request.LobbyId == 109775241234567890UL, "game path or lobby lost");
                SteamLaunchRequest copy = SteamLaunchRequest.Decode(request.Encode());
                Check(copy != null && copy.LobbyId == request.LobbyId && copy.GamePath == request.GamePath, "forwarding changed invite");
                Check(SteamLaunchRequest.Decode("SummerCoop.Steam.1\n1\nD:\\Game\nextra") == null, "extra IPC fields accepted");
                Check(SteamLaunchRequest.Decode("SummerCoop.Steam.1\n-1\n") == null, "negative IPC lobby accepted");
                Check(SteamLaunchRequest.Decode(new string('x', 4097)) == null, "oversized IPC accepted");
            });
            Test("second launcher forwards multiple invites atomically and discards stale or invalid input", delegate
            {
                string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SummerCoop-Invites-" + Guid.NewGuid().ToString("N"));
                try
                {
                    SteamLaunchInbox sender = new SteamLaunchInbox(System.IO.Path.Combine(root, "test.ini"));
                    SteamLaunchInbox receiver = new SteamLaunchInbox(System.IO.Path.Combine(root, "test.ini"));
                    sender.Send(new SteamLaunchRequest { LobbyId = 123 });
                    sender.Send(new SteamLaunchRequest { LobbyId = 456 });
                    string queue = System.IO.Path.Combine(root, "steam-inbox");
                    System.IO.File.WriteAllText(System.IO.Path.Combine(queue, "incomplete.tmp"), "not complete");
                    System.IO.File.WriteAllText(System.IO.Path.Combine(queue, "invalid.invite"), "invalid");
                    string old = System.IO.Path.Combine(queue, "expired.invite");
                    System.IO.File.WriteAllText(old, new SteamLaunchRequest { LobbyId = 789 }.Encode());
                    System.IO.File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-11));
                    List<SteamLaunchRequest> requests = receiver.Drain(DateTime.UtcNow);
                    Check(requests.Count == 2 && requests[0].LobbyId + requests[1].LobbyId == 579, "lost, duplicated or stale invite");
                    Check(receiver.Drain(DateTime.UtcNow).Count == 0, "invite replayed");
                    Check(System.IO.File.Exists(System.IO.Path.Combine(queue, "incomplete.tmp")), "partial write consumed");
                }
                finally { if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true); }
            });
            Test("initialized Steam launcher starts the game child without reopening its own Steam launch option", delegate
            {
                LauncherSettings s = Base(); s.Mode = ConnectionMode.JoinSteam; s.LaunchMethod = LaunchMethod.Steam;
                var launch = GameArgs.Launch(s, null, true);
                Check(launch.FileName == System.IO.Path.Combine(s.GamePath, "mysummercar.exe"), "launcher recursion remains");
                Check(launch.Arguments == GameArgs.ToCommandLine(GameArgs.Build(s)) && Has(GameArgs.Build(s), "--summercoop-join-steam", s.LobbyId), "wrong lobby on child");
                Check(launch.EnvironmentVariables["SteamAppId"] == "516750" && launch.EnvironmentVariables["SteamGameId"] == "516750", "Steam context missing");
                s.LaunchMethod = LaunchMethod.DirectExe;
                bool refused = false;
                try { GameArgs.Launch(s, null, true); } catch (ArgumentException) { refused = true; }
                Check(refused, "Steam direct launch setting bypassed");
            });
            Test("invitation names are bounded and safe to display", delegate
            {
                SteamInvitation invite = new SteamInvitation(123, 456, "Player\r\n\t" + new string('a', 200));
                Check(invite.SenderName.Length == 80 && invite.SenderName.IndexOf('\n') < 0, "invalid display name");
                Check(new SteamInvitation(123, 0, null).SenderName.Length != 0, "empty inviter");
            });
            Console.WriteLine("ALL " + passed + " LAUNCHER TESTS PASSED");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
