using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace SummerCoop.Setup
{
    internal sealed class Vdf
    {
        internal readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, Vdf> Children = new Dictionary<string, Vdf>(StringComparer.OrdinalIgnoreCase);
        private static string Token(string text, ref int at)
        {
            while (at < text.Length)
            {
                if (Char.IsWhiteSpace(text[at])) { at++; continue; }
                if (text[at] == '/' && at + 1 < text.Length && text[at + 1] == '/')
                { while (at < text.Length && text[at] != '\n') at++; continue; }
                break;
            }
            if (at == text.Length) return null;
            char first = text[at++];
            if (first == '{' || first == '}') return first.ToString();
            StringBuilder value = new StringBuilder();
            if (first == '"')
            {
                while (at < text.Length)
                {
                    char c = text[at++];
                    if (c == '"') return value.ToString();
                    if (c == '\\' && at < text.Length && (text[at] == '\\' || text[at] == '"')) c = text[at++];
                    value.Append(c);
                }
                throw new FormatException("Unclosed Steam VDF string.");
            }
            value.Append(first);
            while (at < text.Length && !Char.IsWhiteSpace(text[at]) && text[at] != '{' && text[at] != '}') value.Append(text[at++]);
            return value.ToString();
        }
        private static Vdf Read(string text, ref int at, bool nested)
        {
            Vdf result = new Vdf(); string key;
            while ((key = Token(text, ref at)) != null)
            {
                if (key == "}") { if (!nested) throw new FormatException("Unexpected VDF brace."); return result; }
                string value = Token(text, ref at);
                if (key == "{" || value == null || value == "}") throw new FormatException("Invalid Steam VDF.");
                if (value == "{") result.Children[key] = Read(text, ref at, true); else result.Values[key] = value;
            }
            if (nested) throw new FormatException("Unclosed Steam VDF object.");
            return result;
        }
        internal static Vdf Parse(string text) { int at = 0; return Read(text, ref at, false); }
    }

    internal static class SteamDiscovery
    {
        internal static List<string> Discover()
        {
            List<string> roots = new List<string>();
            foreach (string key in new string[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam" })
            {
                try
                {
                    object value = Registry.GetValue(key, key.StartsWith("HKEY_CURRENT_USER", StringComparison.Ordinal) ? "SteamPath" : "InstallPath", null);
                    if (value is string) roots.Add((string)value);
                }
                catch (System.Security.SecurityException) { }
            }
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            return Discover(roots);
        }
        internal static List<string> Discover(IEnumerable<string> roots)
        {
            HashSet<string> libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots)
            {
                if (String.IsNullOrWhiteSpace(root)) continue;
                libraries.Add(Path.GetFullPath(root));
                try
                {
                    string file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                    if (!File.Exists(file)) continue;
                    Vdf all = Vdf.Parse(File.ReadAllText(file)), folders;
                    if (!all.Children.TryGetValue("libraryfolders", out folders)) continue;
                    foreach (Vdf entry in folders.Children.Values)
                    { string path; if (entry.Values.TryGetValue("path", out path)) libraries.Add(Path.GetFullPath(path)); }
                    foreach (KeyValuePair<string, string> entry in folders.Values)
                    { int number; if (Int32.TryParse(entry.Key, out number)) libraries.Add(Path.GetFullPath(entry.Value)); }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (FormatException) { }
                catch (ArgumentException) { }
            }
            List<string> games = new List<string>();
            foreach (string library in libraries)
            {
                try
                {
                    string common = Path.Combine(library, "steamapps", "common"), manifest = Path.Combine(library, "steamapps", "appmanifest_516750.acf");
                    string name = "My Summer Car";
                    if (File.Exists(manifest))
                    {
                        Vdf state;
                        if (Vdf.Parse(File.ReadAllText(manifest)).Children.TryGetValue("AppState", out state))
                        { string value; if (state.Values.TryGetValue("installdir", out value)) name = value; }
                    }
                    string game = SafePath.Under(common, name);
                    if (File.Exists(Path.Combine(game, "mysummercar.exe")) && Directory.Exists(Path.Combine(game, "mysummercar_Data"))) games.Add(game);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (FormatException) { }
                catch (ArgumentException) { }
            }
            games.Sort(StringComparer.OrdinalIgnoreCase); return games;
        }
    }

    internal static class SafePath
    {
        internal static string Under(string root, string relative)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(Path.Combine(prefix, relative));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Путь выходит за пределы выбранной папки.");
            return target;
        }
        internal static void NoLinks(string path)
        {
            string full = Path.GetFullPath(path);
            for (string part = full; !String.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
                if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Выберите обычную папку без ссылок и junction: " + part);
        }
        internal static string Hash(string path)
        {
            using (FileStream file = File.OpenRead(path)) using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "");
        }
    }

    internal sealed class Payload
    {
        internal static readonly string[] Files = { "SummerCoop.dll", @"References\SummerCoop.Core.dll", @"References\SummerCoop.ProfileUpdate.exe" };
        internal readonly Dictionary<string, string> Hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal string DirectoryPath;
        internal void Verify()
        {
            foreach (string file in Files)
            {
                string expected, path = SafePath.Under(DirectoryPath, file); SafePath.NoLinks(path);
                if (!Hashes.TryGetValue(file, out expected) || !File.Exists(path) || !String.Equals(expected, SafePath.Hash(path), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Повреждён файл установщика: " + file + ". Скачайте установщик заново.");
            }
        }
        internal static Payload Extract(string directory)
        {
            Payload payload = new Payload(); payload.DirectoryPath = directory;
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream manifest = assembly.GetManifestResourceStream("SummerCoop.Payload.SHA256"))
            {
                if (manifest == null) throw new IOException("В установщике отсутствует манифест файлов.");
                using (StreamReader reader = new StreamReader(manifest))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length < 67 || line.Substring(64, 2) != "  ") throw new IOException("Повреждён манифест установщика.");
                        payload.Hashes.Add(line.Substring(66), line.Substring(0, 64));
                    }
                }
            }
            for (int i = 0; i < Files.Length; i++)
            {
                string path = SafePath.Under(directory, Files[i]); SafePath.NoLinks(path);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (Stream source = assembly.GetManifestResourceStream("SummerCoop.Payload." + i))
                {
                    if (source == null) throw new IOException("В установщике отсутствует файл " + Files[i]);
                    using (FileStream destination = new FileStream(path, FileMode.CreateNew)) source.CopyTo(destination);
                }
            }
            payload.Verify(); return payload;
        }
    }

    internal class InstallerServices
    {
        internal virtual bool IsGameRunning(string game)
        {
            foreach (Process process in Process.GetProcessesByName("mysummercar"))
                using (process)
                {
                    try { if (String.Equals(process.MainModule.FileName, Path.Combine(game, "mysummercar.exe"), StringComparison.OrdinalIgnoreCase)) return true; }
                    catch (InvalidOperationException) { if (!process.HasExited) return true; }
                    catch (System.ComponentModel.Win32Exception) { return true; }
                }
            return false;
        }
        // Windows CommandLineToArgvW quoting, including paths with spaces and trailing backslashes.
        internal static string Quote(string value)
        {
            StringBuilder result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1); else result.Append('\\', slashes);
                result.Append(c); slashes = 0;
            }
            result.Append('\\', slashes * 2).Append('"'); return result.ToString();
        }
        internal virtual void Migrate(string game, string profile, Payload payload, Action<string> log)
        {
            string[] args = { game, profile, Path.Combine(game, @"Mods\SummerCoop.dll"), Path.Combine(game, @"Mods\References\SummerCoop.Core.dll"), payload.DirectoryPath, "--same-schema-v2" };
            StringBuilder command = new StringBuilder(); foreach (string arg in args) command.Append(Quote(arg)).Append(' ');
            ProcessStartInfo info = new ProcessStartInfo(SafePath.Under(payload.DirectoryPath, Payload.Files[2]), command.ToString());
            info.UseShellExecute = false; info.CreateNoWindow = true; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
            info.WorkingDirectory = Path.Combine(payload.DirectoryPath, "References");
            StringBuilder output = new StringBuilder(); object gate = new object();
            using (Process child = new Process())
            {
                child.StartInfo = info;
                DataReceivedEventHandler receive = delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (gate) output.AppendLine(e.Data); };
                child.OutputDataReceived += receive; child.ErrorDataReceived += receive;
                child.Start(); child.BeginOutputReadLine(); child.BeginErrorReadLine(); child.WaitForExit();
                log(output.ToString().Trim());
                if (child.ExitCode != 0) throw new IOException("Обновление мира отклонено. Проверьте прежние DLL и версию игры.\r\n" + output);
            }
        }
    }

    internal sealed class InstallResult
    {
        internal string BackupPath;
        internal int DisabledMods;
    }

    internal sealed class Installer
    {
        internal static string DefaultProfile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"AppData\LocalLow\Amistech\My Summer Car\SummerCoop\world"); } }
        private readonly InstallerServices services;
        internal Installer(InstallerServices services) { this.services = services; }
        internal void ValidateGame(string game)
        {
            SafePath.NoLinks(game);
            if (!File.Exists(Path.Combine(game, "mysummercar.exe")) || !Directory.Exists(Path.Combine(game, "mysummercar_Data")))
                throw new IOException("Выберите папку My Summer Car, в которой находится mysummercar.exe.");
            if (!File.Exists(Path.Combine(game, @"mysummercar_Data\Managed\MSCLoader.dll")))
                throw new IOException("Сначала установите MSCLoader с каталогом модов Game Folder. Ссылка на официальный загрузчик есть в этом окне.");
            string config = Path.Combine(game, "doorstop_config.ini");
            if (File.Exists(config))
            {
                string section = "";
                foreach (string raw in File.ReadAllLines(config))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal)) { section = line; continue; }
                    int split = line.IndexOf('=');
                    if (section.Equals("[MSCLoader]", StringComparison.OrdinalIgnoreCase) && split > 0 && line.Substring(0, split).Trim().Equals("mods", StringComparison.OrdinalIgnoreCase))
                    {
                        string location = line.Substring(split + 1).Split(';', '#')[0].Trim();
                        if (!location.Equals("GF", StringComparison.OrdinalIgnoreCase)) throw new IOException("В MSCLoader выбран другой каталог модов. Выберите Game Folder в установщике MSCLoader.");
                    }
                    if (section.Equals("[General]", StringComparison.OrdinalIgnoreCase) && split > 0 && line.Substring(0, split).Trim().Equals("enabled", StringComparison.OrdinalIgnoreCase)
                        && line.Substring(split + 1).Split(';', '#')[0].Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("MSCLoader отключён в doorstop_config.ini. Включите загрузчик перед установкой мода.");
                }
            }
            if (services.IsGameRunning(game)) throw new IOException("Закройте My Summer Car перед установкой.");
        }
        // The launcher checks this before launching, so an up-to-date pair is
        // left untouched and the embedded payload only touches the game when
        // the installed DLLs are missing or belong to another build.
        internal bool IsCurrent(string gamePath, Payload payload)
        {
            string mods = SafePath.Under(Path.GetFullPath(gamePath), "Mods");
            for (int i = 0; i < 2; i++)
            {
                string destination = SafePath.Under(mods, Payload.Files[i]);
                if (!File.Exists(destination)) return false;
                SafePath.NoLinks(destination);
                if (!SafePath.Hash(destination).Equals(payload.Hashes[Payload.Files[i]], StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        internal InstallResult Install(string gamePath, string profilePath, Payload payload, bool disableOthers, Action<string> log)
        {
            string game = Path.GetFullPath(gamePath), profile = Path.GetFullPath(profilePath);
            ValidateGame(game); SafePath.NoLinks(profile); payload.Verify();
            string mods = SafePath.Under(game, "Mods"); SafePath.NoLinks(mods);
            string backupRoot = SafePath.Under(game, "SummerCoop-disabled-mods"); SafePath.NoLinks(backupRoot);
            // A per-game lock prevents two installers from sharing a partially updated DLL pair.
            Directory.CreateDirectory(backupRoot);
            using (FileStream installLock = new FileStream(Path.Combine(backupRoot, "setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                List<string> destinations = new List<string>();
                for (int i = 0; i < 2; i++) destinations.Add(SafePath.Under(mods, Payload.Files[i]));
                List<string> others = new List<string>();
                if (disableOthers && Directory.Exists(mods)) FindOtherMods(mods, mods, destinations, others);
                foreach (string destination in destinations) SafePath.NoLinks(destination);
                bool same = IsCurrent(game, payload);
                bool hasWorld = File.Exists(Path.Combine(profile, "world.scw"));
                if (hasWorld && !same && (!File.Exists(destinations[0]) || !File.Exists(destinations[1])))
                    throw new IOException("Для обновления существующего мира нужны прежние DLL SummerCoop. Восстановите их из резервной копии.");
                string backup = SafePath.Under(backupRoot, "setup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(backup);
                Dictionary<string, string> saved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in destinations)
                {
                    string copy = SafePath.Under(backup, "previous\\" + file.Substring(mods.Length + 1));
                    if (File.Exists(file)) CopyVerified(file, copy); else copy = null;
                    saved.Add(file, copy);
                }
                foreach (string file in others)
                {
                    string copy = SafePath.Under(backup, "disabled\\" + file.Substring(mods.Length + 1));
                    CopyVerified(file, copy); saved.Add(file, copy);
                }
                Dictionary<string, string> profileSaved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (hasWorld && !same)
                {
                    foreach (string file in Directory.GetFiles(profile))
                    {
                        SafePath.NoLinks(file); string copy = SafePath.Under(backup, "profile\\" + Path.GetFileName(file));
                        CopyVerified(file, copy); profileSaved.Add(file, copy);
                    }
                }
                log("Резервная копия: " + backup);
                List<string> touched = new List<string>(); bool migrationStarted = false;
                try
                {
                    ValidateGame(game);
                    if (hasWorld && !same)
                    { log("Проверка и обновление совместимого мира. Это может занять несколько минут…"); migrationStarted = true; services.Migrate(game, profile, payload, log); }
                    ValidateGame(game);
                    foreach (string file in others) { touched.Add(file); File.Delete(file); }
                    for (int i = 0; i < 2; i++)
                    { touched.Add(destinations[i]); CopyVerified(SafePath.Under(payload.DirectoryPath, Payload.Files[i]), destinations[i]); }
                    File.WriteAllText(Path.Combine(backup, "installation.txt"), "SummerCoop setup\r\nGame: " + game + "\r\nProfile: " + profile + "\r\nDisabled DLLs: " + others.Count + "\r\n", Encoding.UTF8);
                    log("Установлены обе DLL SummerCoop. Отключено сторонних DLL: " + others.Count + ".");
                    return new InstallResult { BackupPath = backup, DisabledMods = others.Count };
                }
                catch (Exception original)
                {
                    List<string> failures = new List<string>();
                    foreach (string file in touched)
                    {
                        try { if (saved[file] != null) CopyVerified(saved[file], file); else if (File.Exists(file)) File.Delete(file); }
                        catch (Exception e) { failures.Add(file + ": " + e.Message); }
                    }
                    if (migrationStarted)
                    {
                        foreach (string file in Directory.GetFiles(profile))
                            if (!profileSaved.ContainsKey(file)) { try { File.Delete(file); } catch (Exception e) { failures.Add(file + ": " + e.Message); } }
                        foreach (KeyValuePair<string, string> entry in profileSaved)
                        { try { CopyVerified(entry.Value, entry.Key); } catch (Exception e) { failures.Add(entry.Key + ": " + e.Message); } }
                    }
                    if (failures.Count != 0) throw new IOException(original.Message + "\r\nЧасть файлов не удалось восстановить. Резервная копия: " + backup + "\r\n" + String.Join("\r\n", failures.ToArray()), original);
                    log("Установка отменена; изменённые DLL и файлы мира восстановлены. Резервная копия: " + backup);
                    throw;
                }
            }
        }
        private static void FindOtherMods(string root, string directory, List<string> own, List<string> others)
        {
            SafePath.NoLinks(directory);
            foreach (string file in Directory.GetFiles(directory, "*.dll"))
            {
                SafePath.NoLinks(file); string name = Path.GetFileName(file);
                if (own.Exists(delegate(string path) { return path.Equals(file, StringComparison.OrdinalIgnoreCase); })) continue;
                if (directory.Equals(root, StringComparison.OrdinalIgnoreCase) && (name.Equals("MSCLoader_Console.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("MSCLoader_Settings.dll", StringComparison.OrdinalIgnoreCase))) continue;
                others.Add(file);
            }
            foreach (string child in Directory.GetDirectories(directory))
            {
                // Assets and configuration are inert without their mod DLL; keep them for restoration.
                string name = Path.GetFileName(child);
                if (directory.Equals(root, StringComparison.OrdinalIgnoreCase) && (name.Equals("Assets", StringComparison.OrdinalIgnoreCase) || name.Equals("Config", StringComparison.OrdinalIgnoreCase))) continue;
                FindOtherMods(root, child, own, others);
            }
        }
        private static void CopyVerified(string source, string destination)
        {
            SafePath.NoLinks(source); SafePath.NoLinks(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(source, destination, true);
            if (!SafePath.Hash(source).Equals(SafePath.Hash(destination), StringComparison.OrdinalIgnoreCase)) throw new IOException("Не совпала контрольная сумма: " + destination);
        }
    }
}
