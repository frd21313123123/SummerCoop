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
            "RayTracing", "Gfx", "Vin", "TaxiCustomer", "CutterHitch", "MapRender", "MapPanel", "Clearing"
        };
        sealed class Failure { public int Count; public float Retry; public string Message; }
        static readonly Dictionary<string, Failure> failures = new Dictionary<string, Failure>();
        public static IEnumerable<string> Failures
        {
            get { foreach (KeyValuePair<string, Failure> item in failures) yield return item.Key + ": " + item.Value.Message; }
        }
        public static string Argument(string key)
        {
            string[] args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, key);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
        }
        public static void PrepareEnvironment()
        {
            string root = Argument("--summercoop-profile");
            Root = Path.GetFullPath(root.Length > 0 ? root : Path.Combine(Application.persistentDataPath, "SummerCoop"));
            string data = Path.Combine(Root, "mwcoop"); Directory.CreateDirectory(data);
            Environment.SetEnvironmentVariable("MWCOOP_DIR", data);
            Environment.SetEnvironmentVariable("MWCOOP_DATA", data);
        }
        public static void Configure()
        {
            if (Root == null) PrepareEnvironment();
            Config.Override("Coop", "Langue", "ru");
            Config.Override("Coop", "Mode", "solo");
            Config.Override("Coop", "Ajouts", "0");
            Config.Override("Coop", "RayTracing", "0");
            string name = Argument("--summercoop-name"); if (name.Length > 0) Config.Override("Coop", "Pseudo", name);
            string port = Argument("--summercoop-host-lan");
            bool steamHost = Argument("--summercoop-host-steam") == "yes";
            string steamLobby = Argument("--summercoop-join-steam"); if (steamLobby.Length == 0) steamLobby = Argument("+connect_lobby");
            bool guest = Argument("--summercoop-join-lan").Length > 0 || steamLobby.Length > 0;
            AutoStart = port.Length > 0 || steamHost || guest;
            Config.Override("Coop", "Mode", guest ? "client" : AutoStart ? "host" : "solo");
            Config.Override("Coop", "Reseau", steamHost || steamLobby.Length > 0 ? "steam" : "ip");
            Config.Override("Coop", "Port", port.Length > 0 ? port : Argument("--summercoop-port").Length > 0 ? Argument("--summercoop-port") : "27842");
            Config.Override("Coop", "Adresse", Argument("--summercoop-join-lan"));
            Config.Override("Coop", "Salon", steamLobby);
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
        }
        public static bool Supports(Type module) { return module == null || !excluded.Contains(module.Name); }
        public static void Run(Action action)
        {
            if (!Supports(action.Method.DeclaringType)) return;
            if (action.Method.DeclaringType == typeof(Studio) && action.Method.Name == "Update" && !Menu.Open) return;
            string key = action.Method.DeclaringType.Name + "." + action.Method.Name;
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
        public static void SceneChanged() { failures.Clear(); MscParts.Reset(); }
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
            GUILayout.Label("SummerCoop " + Version.Text + " / MWCoop → My Summer Car");
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
