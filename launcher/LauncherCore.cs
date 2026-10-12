using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SummerCoop.Launcher
{
    public enum ConnectionMode
    {
        Solo = 0,
        HostLan = 1,
        JoinLan = 2,
        HostSteam = 3,
        JoinSteam = 4
    }

    public enum LaunchMethod
    {
        Auto = 0,
        DirectExe = 1,
        Steam = 2
    }

    public sealed class LauncherSettings
    {
        public string GamePath = "";
        public string ProfilePath = "";
        public string PlayerName = "Player";
        public string Address = "127.0.0.1";
        public int Port = 27842;
        public string Password = "";
        public string LobbyId = "";
        public ConnectionMode Mode = ConnectionMode.HostLan;
        public LaunchMethod LaunchMethod = LaunchMethod.Auto;
        public bool DisableOtherMods = true;
        public bool CloseOnLaunch;
        public bool ImportSave;

        public static string DefaultProfile()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"AppData\LocalLow\Amistech\My Summer Car\SummerCoop\world");
        }

        public static string ConfigPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"SummerCoop\launcher.ini");
        }

        public List<string> Validate(bool requireConnection)
        {
            List<string> errors = new List<string>();
            if (GamePath == null || GamePath.Trim().Length == 0) errors.Add("Укажите папку игры.");
            if (PlayerName == null || PlayerName.Trim().Length == 0) errors.Add("Укажите имя игрока.");
            else if (PlayerName.Trim().Length > 48) errors.Add("Имя длиннее 48 символов.");
            if ((Mode == ConnectionMode.HostLan || Mode == ConnectionMode.JoinLan) && (Port < 1024 || Port > 65535))
                errors.Add("Порт должен быть от 1024 до 65535.");
            if (!Enum.IsDefined(typeof(ConnectionMode), Mode)) errors.Add("Выберите режим подключения.");
            if (ProfilePath == null || ProfilePath.Trim().Length == 0) errors.Add("Укажите профиль мира.");
            if (!requireConnection) return errors;
            if (Mode == ConnectionMode.JoinLan && (Address == null || Address.Trim().Length == 0)) errors.Add("Укажите IP хоста.");
            if (Mode == ConnectionMode.JoinSteam)
            {
                ulong id;
                if (LobbyId == null || !ulong.TryParse(LobbyId.Trim(), out id) || id == 0) errors.Add("Нужен номер лобби Steam.");
            }
            return errors;
        }

        public void Save(string path)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[Launcher]");
            sb.AppendLine("GamePath=" + GamePath);
            sb.AppendLine("ProfilePath=" + ProfilePath);
            sb.AppendLine("PlayerName=" + PlayerName);
            sb.AppendLine("Address=" + Address);
            sb.AppendLine("Port=" + Port);
            sb.AppendLine("Password=" + Password);
            sb.AppendLine("LobbyId=" + LobbyId);
            sb.AppendLine("Mode=" + (int)Mode);
            sb.AppendLine("LaunchMethod=" + (int)LaunchMethod);
            sb.AppendLine("DisableOtherMods=" + (DisableOtherMods ? "1" : "0"));
            sb.AppendLine("CloseOnLaunch=" + (CloseOnLaunch ? "1" : "0"));
            sb.AppendLine("ImportSave=" + (ImportSave ? "1" : "0"));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        public static LauncherSettings Load(string path)
        {
            LauncherSettings s = new LauncherSettings();
            s.ProfilePath = DefaultProfile();
            if (!File.Exists(path)) return s;
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string section = "";
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                if (line.StartsWith("[") && line.EndsWith("]")) { section = line; continue; }
                int at = line.IndexOf('=');
                if (at <= 0) continue;
                if (section == "[Launcher]") values[line.Substring(0, at).Trim()] = line.Substring(at + 1).Trim();
            }
            string v;
            if (values.TryGetValue("GamePath", out v)) s.GamePath = v;
            if (values.TryGetValue("ProfilePath", out v) && v.Length != 0) s.ProfilePath = v;
            if (values.TryGetValue("PlayerName", out v) && v.Length != 0) s.PlayerName = v;
            if (values.TryGetValue("Address", out v) && v.Length != 0) s.Address = v;
            if (values.TryGetValue("Port", out v)) { int p; if (int.TryParse(v, out p)) s.Port = p; }
            if (values.TryGetValue("Password", out v)) s.Password = v;
            if (values.TryGetValue("LobbyId", out v)) s.LobbyId = v;
            if (values.TryGetValue("Mode", out v)) { int m; if (int.TryParse(v, out m) && m >= 0 && m <= 4) s.Mode = (ConnectionMode)m; }
            if (values.TryGetValue("LaunchMethod", out v)) { int m; if (int.TryParse(v, out m) && m >= 0 && m <= 2) s.LaunchMethod = (LaunchMethod)m; }
            if (values.TryGetValue("DisableOtherMods", out v)) s.DisableOtherMods = v != "0";
            if (values.TryGetValue("CloseOnLaunch", out v)) s.CloseOnLaunch = v == "1";
            if (values.TryGetValue("ImportSave", out v)) s.ImportSave = v == "1";
            return s;
        }
    }

    public static class LauncherInfo
    {
        public const string ModVersion = "0.1.9-alpha";
        public const int ProtocolVersion = 2;
        public const int DefaultPort = 27842;
    }

    public static class GameArgs
    {
        public static string PortText(int port)
        {
            if (port < 1024 || port > 65535) throw new ArgumentException("Порт должен быть от 1024 до 65535");
            return port.ToString();
        }

        // Аргументы mysummercar.exe. Порядок стабилен ради тестов и Steam -applaunch.
        public static List<string> Build(LauncherSettings s)
        {
            if (s == null) throw new ArgumentNullException("s");
            List<string> errors = s.Validate(true);
            if (errors.Count != 0) throw new ArgumentException(errors[0]);
            List<string> args = new List<string>();
            args.Add("--summercoop-profile");
            args.Add(s.ProfilePath.Trim());
            string name = s.PlayerName.Trim();
            if (name.Length != 0) { args.Add("--summercoop-name"); args.Add(name); }
            if (s.ImportSave && (s.Mode == ConnectionMode.Solo || s.Mode == ConnectionMode.HostLan || s.Mode == ConnectionMode.HostSteam))
                { args.Add("--summercoop-import-save"); args.Add("yes"); }
            switch (s.Mode)
            {
                case ConnectionMode.HostLan:
                    args.Add("--summercoop-host-lan");
                    args.Add(PortText(s.Port));
                    if (s.Password.Length != 0) { args.Add("--summercoop-password"); args.Add(s.Password); }
                    break;
                case ConnectionMode.JoinLan:
                    args.Add("--summercoop-join-lan");
                    args.Add(s.Address.Trim());
                    args.Add("--summercoop-port");
                    args.Add(PortText(s.Port));
                    if (s.Password.Length != 0) { args.Add("--summercoop-password"); args.Add(s.Password); }
                    break;
                case ConnectionMode.HostSteam:
                    args.Add("--summercoop-host-steam");
                    args.Add("yes");
                    break;
                case ConnectionMode.JoinSteam:
                    args.Add("--summercoop-join-steam");
                    args.Add(s.LobbyId.Trim());
                    break;
                default:
                    break;
            }
            return args;
        }

        public static string Quote(string value)
        {
            StringBuilder result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1);
                else result.Append('\\', slashes);
                result.Append(c);
                slashes = 0;
            }
            result.Append('\\', slashes * 2).Append('"');
            return result.ToString();
        }

        public static string ToCommandLine(List<string> args)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < args.Count; i++)
            {
                if (i != 0) sb.Append(' ');
                string a = args[i];
                if (a.Length == 0 || a.IndexOfAny(new char[] { ' ', '\t', '"', '\'' }) >= 0) sb.Append(Quote(a));
                else sb.Append(a);
            }
            return sb.ToString();
        }

        public static string ToDisplayCommandLine(List<string> args)
        {
            List<string> display = new List<string>(args);
            for (int i = 0; i + 1 < display.Count; i++)
                if (display[i] == "--summercoop-password") display[++i] = "***";
            return ToCommandLine(display);
        }

        public static string Describe(ConnectionMode mode)
        {
            switch (mode)
            {
                case ConnectionMode.HostLan: return "Вы создаёте мир. Друзьям понадобятся ваш IP, порт и пароль, если он задан.";
                case ConnectionMode.JoinLan: return "Введите адрес и порт, которые сообщил создатель игры.";
                case ConnectionMode.HostSteam: return "Вы создаёте мир. Пригласите друзей через Steam в игре.";
                case ConnectionMode.JoinSteam: return "Введите номер лобби, который сообщил создатель игры.";
                default: return "Игра запустится без подключения. Создать игру или присоединиться можно через F6.";
            }
        }
    }
}
