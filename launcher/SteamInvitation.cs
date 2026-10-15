using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SummerCoop.Launcher
{
    internal sealed class SteamInvitation
    {
        internal const uint AppId = 516750;
        internal readonly ulong LobbyId, SenderId;
        internal readonly string SenderName;
        internal readonly DateTime ReceivedUtc;

        internal SteamInvitation(ulong lobbyId, ulong senderId, string senderName)
        {
            if (lobbyId == 0) throw new ArgumentException("Steam передал пустой номер лобби.");
            LobbyId = lobbyId;
            SenderId = senderId;
            SenderName = CleanName(senderName);
            ReceivedUtc = DateTime.UtcNow;
        }

        internal static bool TryLobby(string value, out ulong lobby)
        {
            return ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out lobby) && lobby != 0;
        }

        private static string CleanName(string name)
        {
            StringBuilder clean = new StringBuilder();
            foreach (char c in name ?? "") if (!char.IsControl(c) && clean.Length < 80) clean.Append(c);
            return clean.Length == 0 ? "Друг в Steam" : clean.ToString();
        }

        public override string ToString() { return SenderName + " · лобби " + LobbyId.ToString(CultureInfo.InvariantCulture); }
    }

    // Steam's %command% passes the original game EXE followed by +connect_lobby.
    // We extract data only; forwarded arguments can never select an executable to run.
    internal sealed class SteamLaunchRequest
    {
        internal ulong LobbyId;
        internal string GamePath = "";

        internal static bool TryParse(string[] args, out SteamLaunchRequest request)
        {
            request = new SteamLaunchRequest();
            if (args.Length == 0) return true;
            int first = 0;
            try
            {
                if (Path.IsPathRooted(args[0]) && string.Equals(Path.GetFileName(args[0]), "mysummercar.exe", StringComparison.OrdinalIgnoreCase))
                {
                    request.GamePath = Path.GetDirectoryName(Path.GetFullPath(args[0]));
                    first = 1;
                }
            }
            catch (Exception) { return false; }
            bool recognized = first != 0;
            for (int i = first; i < args.Length; i++)
            {
                string value = null;
                if (args[i] == "+connect_lobby" || args[i] == "--summercoop-join-steam")
                {
                    if (++i >= args.Length) throw new ArgumentException("В приглашении Steam отсутствует номер лобби.");
                    value = args[i];
                }
                else if (args[i].StartsWith("+connect_lobby=", StringComparison.Ordinal)) value = args[i].Substring(15);
                if (value == null)
                {
                    // Other Unity/game options in %command% are not launcher commands.
                    if (first == 0) return false;
                    continue;
                }
                ulong lobby;
                if (!SteamInvitation.TryLobby(value, out lobby)) throw new ArgumentException("Неверный номер лобби в приглашении Steam.");
                if (request.LobbyId != 0 && request.LobbyId != lobby) throw new ArgumentException("Steam передал разные номера лобби.");
                request.LobbyId = lobby;
                recognized = true;
            }
            return recognized;
        }

        internal string Encode()
        {
            if (GamePath.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0 || GamePath.Length > 2048)
                throw new ArgumentException("Неверный путь игры.");
            return "SummerCoop.Steam.1\n" + LobbyId.ToString(CultureInfo.InvariantCulture) + "\n" + GamePath;
        }

        internal static SteamLaunchRequest Decode(string text)
        {
            if (text == null || text.Length > 4096) return null;
            string[] fields = text.Split('\n');
            ulong lobby;
            if (fields.Length != 3 || fields[0] != "SummerCoop.Steam.1" ||
                !ulong.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out lobby) ||
                fields[2].IndexOfAny(new[] { '\r', '\0' }) >= 0) return null;
            if (fields[2].Length > 2048) return null;
            return new SteamLaunchRequest { LobbyId = lobby, GamePath = fields[2] };
        }
    }

    // Per-user mailbox for a second launcher started by Steam. Atomic rename makes
    // startup races harmless; the first window drains it on its UI timer.
    internal sealed class SteamLaunchInbox
    {
        private readonly string directory;
        internal SteamLaunchInbox(string configPath) { directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)), "steam-inbox"); }

        internal void Send(SteamLaunchRequest request)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            string temporary = path + ".tmp";
            try
            {
                File.WriteAllText(temporary, request.Encode(), new UTF8Encoding(false));
                File.Move(temporary, path + ".invite");
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal List<SteamLaunchRequest> Drain(DateTime nowUtc)
        {
            List<SteamLaunchRequest> requests = new List<SteamLaunchRequest>();
            if (!Directory.Exists(directory)) return requests;
            string[] paths = Directory.GetFiles(directory, "*.invite");
            Array.Sort(paths, StringComparer.Ordinal);
            int count = 0;
            foreach (string path in paths)
            {
                if (++count > 64) break;
                try
                {
                    FileInfo file = new FileInfo(path);
                    if (file.Length <= 8192 && file.LastWriteTimeUtc <= nowUtc.AddMinutes(1) && file.LastWriteTimeUtc >= nowUtc.AddMinutes(-10))
                    {
                        SteamLaunchRequest request = SteamLaunchRequest.Decode(File.ReadAllText(path, Encoding.UTF8));
                        if (request != null) requests.Add(request);
                    }
                    File.Delete(path);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return requests;
        }
    }
}
