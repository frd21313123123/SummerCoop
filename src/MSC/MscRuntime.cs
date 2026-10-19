using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HutongGames.PlayMaker;
using MWCoop.Net;
using SummerCoop.Core;
using UnityEngine;
using Session = MWCoop.Net.Session;

namespace MWCoop
{
    public static class MscRuntime
    {
        public static readonly bool Enabled = true;
        public static string Root { get; private set; }
        public static string BuildIdentity { get; private set; }
        public static bool AutoStart { get; private set; }
        // MWC-only locations/addons cannot be made into MSC mechanics by renaming
        // a string. Keep their source, but never activate them in the MSC world.
        static readonly HashSet<string> excluded = new HashSet<string>(StringComparer.Ordinal) {
            "Ajouts", "Kauppa", "KauppaOsto", "KauppaVideo", "KauppaFilmi", "KauppaKalusteet",
            "MuseoRakennus", "Frost", "TeimoSled", "ModSecondMachtwagen", "ModMachtwagen300D",
            "ModRam1500", "ModJonne", "ModTowCart", "ModJackStands", "ModCheatbox", "MscMods",
            "RayTracing", "Gfx", "Vin", "TaxiCustomer", "CutterHitch", "MapRender", "MapPanel", "Clearing", "Parked", "PhoneKeys", "Races", "Rally", "Kartturi"
        };
        sealed class Failure { public int Count; public float Retry; public string Message; }
        static readonly Dictionary<string, Failure> failures = new Dictionary<string, Failure>();
        static readonly string[] arguments = Environment.GetCommandLineArgs();
        static readonly Dictionary<MethodInfo, Invocation> invocations = new Dictionary<MethodInfo, Invocation>();
        public sealed class Invocation
        {
            internal Action Action;
            internal string Key;
            internal bool Supported;
        }
        public static Invocation Prepare(Action action)
        {
            return new Invocation { Action = action, Key = action.Method.DeclaringType.Name + "." + action.Method.Name, Supported = Supports(action.Method.DeclaringType) };
        }
        public static IEnumerable<string> Failures
        {
            get { foreach (KeyValuePair<string, Failure> item in failures) yield return item.Key + ": " + item.Value.Message; }
        }
        public static string Argument(string key)
        {
            int i = Array.IndexOf(arguments, key);
            return i >= 0 && i + 1 < arguments.Length ? arguments[i + 1] : "";
        }
        public static bool HasArgument(string key)
        {
            return Array.IndexOf(arguments, key) >= 0;
        }
        public static void TouchLoaderTrace()
        {
            try
            {
                string text = DateTime.UtcNow.ToString("o") + "\r\nSummerCoop loaded successfully\r\n";
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string[] dirs = new[] {
                    Path.Combine(localApp, "MWCoop"),
                    Path.Combine(localApp, "SummerCoop"),
                    Path.Combine(Environment.CurrentDirectory, "MWCoop"),
                    Path.Combine(Environment.CurrentDirectory, "SummerCoop"),
                    Root != null ? Path.Combine(Root, "mwcoop") : null
                };
                foreach (string d in dirs)
                {
                    if (string.IsNullOrEmpty(d)) continue;
                    try
                    {
                        Directory.CreateDirectory(d);
                        File.WriteAllText(Path.Combine(d, "dernier-lancement.txt"), text);
                    }
                    catch {}
                }
            }
            catch {}
        }
        public static void PrepareEnvironment()
        {
            string root = Argument("--summercoop-profile");
            Root = Path.GetFullPath(root.Length > 0 ? root : Path.Combine(Application.persistentDataPath, "SummerCoop"));
            string data = Path.Combine(Root, "mwcoop"); Directory.CreateDirectory(data);
            Environment.SetEnvironmentVariable("MWCOOP_DIR", data);
            Environment.SetEnvironmentVariable("MWCOOP_DATA", data);
            string gameDir = Path.GetDirectoryName(Application.dataPath);
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] sources = new[] {
                !string.IsNullOrEmpty(gameDir) ? Path.Combine(gameDir, "MWCoop\\lancement.ini") : null,
                !string.IsNullOrEmpty(gameDir) ? Path.Combine(gameDir, "SummerCoop\\lancement.ini") : null,
                Path.Combine(Environment.CurrentDirectory, "MWCoop\\lancement.ini"),
                Path.Combine(Environment.CurrentDirectory, "SummerCoop\\lancement.ini"),
                !string.IsNullOrEmpty(localApp) ? Path.Combine(localApp, "MWCoop\\lancement.ini") : null,
                Path.Combine(Application.persistentDataPath, "SummerCoop\\mwcoop\\lancement.ini")
            };
            string target = Path.Combine(data, "lancement.ini");
            foreach (string s in sources)
            {
                if (!string.IsNullOrEmpty(s) && File.Exists(s))
                {
                    try { File.Copy(s, target, true); } catch {}
                    break;
                }
            }
            MscLauncher.Initialize(gameDir, data, target);
            TouchLoaderTrace();
        }
        public static void Configure()
        {
            if (Root == null) PrepareEnvironment();
            Config.Override("Coop", "Langue", "ru");
            Config.Override("Coop", "Ajouts", "0");
            Config.Override("Coop", "RayTracing", "0");

            string mode = Config.Get("Arg", "mode", Config.Get("Coop", "Mode", "")).ToLowerInvariant();
            string rawReseau = Config.Get("Coop", "Reseau", "ip").ToLowerInvariant();
            string rawHostSteam = Config.Get("Coop", "HoteSteam", "");
            string rawAddress = Config.Get("Arg", "adresse", Config.Get("Coop", "Adresse", ""));
            string rawPort = Config.Get("Arg", "port", Config.Get("Coop", "Port", ""));
            string rawName = Config.Get("Coop", "Pseudo", "");

            string name = Argument("--summercoop-name");
            if (name.Length > 0) Config.Override("Coop", "Pseudo", name);
            else if (rawName.Length > 0) Config.Override("Coop", "Pseudo", rawName);

            string port = Argument("--summercoop-host-lan");
            bool steamHost = Argument("--summercoop-host-steam") == "yes" || (mode == "hote" && rawReseau == "steam");
            string steamLobby = Argument("--summercoop-join-steam");
            if (steamLobby.Length == 0) steamLobby = Argument("+connect_lobby");
            if (steamLobby.Length == 0 && mode == "invite" && rawReseau == "steam") steamLobby = Config.Get("Coop", "Salon", "");

            bool guest = Argument("--summercoop-join-lan").Length > 0 || steamLobby.Length > 0 || mode == "invite" || mode == "client";
            bool host = port.Length > 0 || steamHost || mode == "hote" || mode == "host";
            string partie = Config.Get("Arg", "partie", Config.Get("Coop", "Partie", "")).ToLowerInvariant();
            bool auto = HasArgument("--summercoop-auto") || Config.HasArg("auto") || partie == "continuer" || partie == "nouvelle";
            AutoStart = host || guest || auto;
            CarColor.NewGame = !guest && partie == "nouvelle";

            Config.Override("Coop", "Mode", guest ? "client" : (host || AutoStart) && mode != "solo" ? "host" : "solo");
            Config.Override("Coop", "Reseau", steamHost || steamLobby.Length > 0 || rawReseau == "steam" ? "steam" : "ip");

            string resolvedPort = port.Length > 0 ? port : Argument("--summercoop-port").Length > 0 ? Argument("--summercoop-port") : rawPort.Length > 0 ? rawPort : "27842";
            Config.Override("Coop", "Port", resolvedPort);

            string resolvedAddr = Argument("--summercoop-join-lan").Length > 0 ? Argument("--summercoop-join-lan") : rawAddress;
            Config.Override("Coop", "Adresse", resolvedAddr);
            if (steamLobby.Length > 0) Config.Override("Coop", "Salon", steamLobby);
            if (rawHostSteam.Length > 0) Config.Override("Coop", "HoteSteam", rawHostSteam);
            Config.Override("Coop", "MotDePasseMSC", Argument("--summercoop-password"));

            MscProfile.Activate(Root, guest, Argument("--summercoop-import-save") == "yes");
            BuildIdentity = Codec.Hash(Codec.Make(delegate(BinaryWriter w) {
                Codec.Text(w, "My Summer Car / MWCoop adapter 1");
                foreach (string file in new[] { "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll", "PlayMaker.dll", "UnityEngine.dll" })
                    Codec.Text(w, Codec.Hash(File.ReadAllBytes(Path.Combine(Path.Combine(Application.dataPath, "Managed"), file))));
                Codec.Text(w, Codec.Hash(File.ReadAllBytes(typeof(MscRuntime).Assembly.Location)));
                Codec.Text(w, Codec.Hash(File.ReadAllBytes(typeof(Protocol).Assembly.Location)));
            }));
        }
        public static void ConfigureGame()
        {
            if (!MscProfile.Isolated) throw new InvalidOperationException("MSC profile has not been isolated");
            FsmVariables globals = FsmVariables.GlobalVariables;
            FsmString slot = globals.FindFsmString("SaveSlot"); if (slot != null) slot.Value = "defaultES2File.txt";
            foreach (string key in new[] { "OptionsPermaDeath", "PlayerPermaDeath", "PlayerInMenu", "PlayerStop", "PlayerSleeps", "PlayerSeated", "PlayerCarControl", "PlayerVanControl", "PlayerMuscleControl" })
            { FsmBool value = globals.FindFsmBool(key); if (value != null) value.Value = false; }
            foreach (string key in new[] { "PlayerFirstName", "PlayerLastName", "PlayerName" })
            { FsmString value = globals.FindFsmString(key); if (value != null && value.Value.Length == 0) value.Value = "Coop"; }
            FsmBool songImported = globals.FindFsmBool("SongImported");
            if (songImported != null) songImported.Value = true;
            try
            {
                PlayMakerFSM gfx = Game.FindFsm("Systems/Options", "GFX");
                if (gfx != null)
                {
                    FsmBool si = gfx.FsmVariables.FindFsmBool("SongImport");
                    if (si != null) si.Value = true;
                }
            }
            catch {}
        }
        public static bool Supports(Type module) { return module == null || !excluded.Contains(module.Name); }
        public static void Run(Action action)
        {
            Invocation invocation;
            if (!invocations.TryGetValue(action.Method, out invocation))
                invocations[action.Method] = invocation = Prepare(action);
            // Metadata is reusable; the caller's delegate target is not.
            Run(invocation, action);
        }
        public static void Run(Invocation invocation) { Run(invocation, invocation.Action); }
        static void Run(Invocation invocation, Action action)
        {
            if (!invocation.Supported) return;
            string key = invocation.Key;
            Failure failure;
            if (failures.TryGetValue(key, out failure) && Time.realtimeSinceStartup < failure.Retry) return;
            try { action(); failures.Remove(key); }
            catch (Exception e)
            {
                if (failure == null) failures[key] = failure = new Failure();
                failure.Message = e.Message; failure.Count++;
                failure.Retry = Time.realtimeSinceStartup + Mathf.Min(60, 1 << Math.Min(failure.Count, 6));
                Log.Error("MSC compatibility: " + key + " (retry in " + (failure.Retry - Time.realtimeSinceStartup) + "s): " + e);
            }
        }
        public static void SceneChanged() { failures.Clear(); MscBattery.Reset(); MscParts.Reset(); MscVehicleControls.Reset(); MscPlatforms.Reset(); MscBrewing.Reset(); MscSleep.Reset(); MscIsland.Reset(); MscFireworks.Reset(); MscFluids.Reset(); MscToolCases.Reset(); }
    }
    // The original engine still supplies F10, T, the roster, looks and sync audit.
    // F6 supplies the MSC connection settings when launched without a launcher.
    public sealed class MscConnectPanel : MonoBehaviour
    {
        bool visible;
        string name = Environment.UserName, address = "127.0.0.1", port = "27842", password = "", error = "", lobby = "";
        bool steam, import;
        void Start() { visible = !MscRuntime.AutoStart; }
        void Update() { if (Input.GetKeyDown(KeyCode.F6)) visible = !visible; }
        void OnGUI()
        {
            if (!visible) return;
            GUILayout.BeginArea(new Rect(20, 20, Mathf.Min(490, Screen.width - 40), Screen.height - 40), GUI.skin.box);
            GUILayout.Label("SummerCoop " + Version.Text);
            GUILayout.Label("F10 — игроки, внешность, чат, синхронизация. T — чат.");
            if (Application.loadedLevelName != "GAME")
            {
                GUILayout.Label("Имя"); name = GUILayout.TextField(name, 24);
                steam = GUILayout.Toggle(steam, "Через Steam");
                GUILayout.Label(steam ? "Steam lobby ID" : "IP хоста");
                if (steam) lobby = GUILayout.TextField(lobby, 20); else address = GUILayout.TextField(address, 255);
                GUILayout.Label("Порт"); port = GUILayout.TextField(port, 5);
                GUILayout.Label("Пароль"); password = GUILayout.PasswordField(password, '*', 64);
                import = GUILayout.Toggle(import, "Начать из копии сохранения SummerCoop / одиночной игры");
                if (GUILayout.Button("Создать игру")) Launch(false);
                if (GUILayout.Button("Подключиться")) Launch(true);
            }
            GUILayout.Label("Сессия: " + Session.Status);
            GUILayout.Label("Сохранение: " + MscProfile.SaveDirectory);
            foreach (string message in MscRuntime.Failures) GUILayout.Label("Требует проверки: " + message);
            if (error.Length > 0) GUILayout.Label(error);
            if (GUILayout.Button("Скрыть (F6)")) visible = false;
            GUILayout.EndArea();
        }
        void Launch(bool guest)
        {
            try
            {
                int parsed; if (!int.TryParse(port, out parsed) || parsed < 1024 || parsed > 65535) throw new ArgumentException("Порт должен быть от 1024 до 65535");
                if (name.Trim().Length == 0) throw new ArgumentException("Введите имя");
                ulong steamLobby; if (guest && steam && !ulong.TryParse(lobby, out steamLobby)) throw new ArgumentException("Нужен номер Steam lobby");
                Session.Stop(); MscProfile.Activate(MscRuntime.Root, guest, import);
                Config.Override("Coop", "Mode", guest ? "client" : "host"); Config.Override("Coop", "Reseau", steam ? "steam" : "ip");
                Config.Override("Coop", "Pseudo", name); Config.Override("Coop", "Adresse", address); Config.Override("Coop", "Port", port);
                Config.Override("Coop", "Salon", lobby); Config.Override("Coop", "MotDePasseMSC", password);
                MscFlow.Requested = true; Session.Start(); visible = false; error = "";
            }
            catch (Exception e) { error = e.Message; }
        }
    }
}
