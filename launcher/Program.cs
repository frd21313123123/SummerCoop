using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using SummerCoop.Setup;

namespace SummerCoop.Launcher
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            SteamLaunchRequest request;
            try { if (!SteamLaunchRequest.TryParse(args, out request)) return CommandLine(args); }
            catch (Exception error) { MessageBox.Show(error.Message, "Приглашение SummerCoop", MessageBoxButtons.OK, MessageBoxIcon.Warning); return 1; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Steam can start another copy when an invite is accepted. Keep one
            // window per Windows user/session and forward the lobby atomically.
            string identity = WindowsIdentity.GetCurrent().User.Value;
            using (Mutex instance = new Mutex(false, @"Local\SummerCoop.Launcher." + identity))
            {
                bool owns;
                try { owns = instance.WaitOne(0); }
                catch (AbandonedMutexException) { owns = true; }
                if (!owns)
                {
                    try { new SteamLaunchInbox(LauncherSettings.ConfigPath()).Send(request); return 0; }
                    catch (Exception error) { MessageBox.Show("Не удалось передать приглашение открытому лаунчеру: " + error.Message, "SummerCoop"); return 1; }
                }
                try { Application.Run(new MainForm(request)); return 0; }
                finally { instance.ReleaseMutex(); }
            }
        }

        private static int CommandLine(string[] args)
        {
            try
            {
                Dictionary<string, string> options = new Dictionary<string, string>();
                foreach (string arg in args)
                {
                    int at = arg.IndexOf('=');
                    string name = at < 0 ? arg : arg.Substring(0, at);
                    if (name != "--detect" && name != "--print-args" && name != "--install" && name != "--keep-other-mods" && name != "--log" && name != "--mode" && name != "--game" && name != "--profile" && name != "--name" && name != "--address" && name != "--port" && name != "--password" && name != "--lobby")
                        throw new ArgumentException("Unknown argument: " + name);
                    options.Add(name, at < 0 ? "" : arg.Substring(at + 1));
                }
                if (options.ContainsKey("--detect"))
                {
                    List<string> games = SteamDiscovery.Discover();
                    foreach (string game in games) Console.WriteLine(game);
                    string logFile;
                    if (options.TryGetValue("--log", out logFile) && logFile.Length != 0) File.WriteAllLines(logFile, games.ToArray(), Encoding.UTF8);
                    return 0;
                }
                if (options.ContainsKey("--install")) return Install(options);
                if (options.ContainsKey("--print-args"))
                {
                    LauncherSettings s = FromOptions(options);
                    List<string> errors = s.Validate(true);
                    if (errors.Count != 0) throw new ArgumentException(errors[0]);
                    Console.WriteLine(GameArgs.ToCommandLine(GameArgs.Build(s)));
                    return 0;
                }
                throw new ArgumentException("Use --detect [--log=PATH], --print-args --mode=host-lan|join-lan|host-steam|join-steam|solo --game=PATH [--profile=PATH] [--name=NAME] [--address=IP] [--port=PORT] [--password=PWD] [--lobby=ID] or --install --game=PATH [--profile=PATH] [--keep-other-mods] [--log=PATH]");
            }
            catch (Exception e) { Console.Error.WriteLine("Ошибка: " + e.Message); return 1; }
        }

        // Setup is built into the launcher; this keeps the automation contract
        // of the former standalone installer (--install --game=PATH ...).
        private static int Install(Dictionary<string, string> options)
        {
            string logFile; options.TryGetValue("--log", out logFile);
            List<string> lines = new List<string>();
            Action<string> log = delegate(string line) { lines.Add(line); Console.WriteLine(line); };
            try
            {
                string gamePath, profile;
                if (!options.TryGetValue("--game", out gamePath) || gamePath.Trim().Length == 0)
                {
                    List<string> games = SteamDiscovery.Discover();
                    if (games.Count != 1) throw new IOException("Укажите --game=PATH: найдена не одна игровая папка.");
                    gamePath = games[0];
                }
                if (!options.TryGetValue("--profile", out profile) || profile.Trim().Length == 0) profile = Installer.DefaultProfile;
                ModPayload payload = ModPayload.Shared();
                new Installer(new InstallerServices()).Install(gamePath, profile, payload.Value, !options.ContainsKey("--keep-other-mods"), log);
                log("Готово. Запустите игру через лаунчер; меню SummerCoop открывается клавишей F6.");
                return 0;
            }
            catch (Exception e) { log("Ошибка: " + e.Message); return 1; }
            finally
            {
                ModPayload.ReleaseShared();
                if (logFile != null) File.WriteAllLines(logFile, lines.ToArray(), Encoding.UTF8);
            }
        }

        private static LauncherSettings FromOptions(Dictionary<string, string> options)
        {
            LauncherSettings s = LauncherSettings.Load(LauncherSettings.ConfigPath());
            string v;
            if (options.TryGetValue("--game", out v)) s.GamePath = v;
            if (options.TryGetValue("--profile", out v)) s.ProfilePath = v;
            if (options.TryGetValue("--name", out v)) s.PlayerName = v;
            if (options.TryGetValue("--address", out v)) s.Address = v;
            if (options.TryGetValue("--port", out v)) { int p; if (int.TryParse(v, out p)) s.Port = p; }
            if (options.TryGetValue("--password", out v)) s.Password = v;
            if (options.TryGetValue("--lobby", out v)) s.LobbyId = v;
            if (options.TryGetValue("--mode", out v))
            {
                if (v == "host-lan") s.Mode = ConnectionMode.HostLan;
                else if (v == "join-lan") s.Mode = ConnectionMode.JoinLan;
                else if (v == "host-steam") s.Mode = ConnectionMode.HostSteam;
                else if (v == "join-steam") s.Mode = ConnectionMode.JoinSteam;
                else if (v == "solo") s.Mode = ConnectionMode.Solo;
                else throw new ArgumentException("Unknown --mode=" + v);
            }
            return s;
        }
    }
}
