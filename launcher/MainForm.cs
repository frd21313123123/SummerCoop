using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using SummerCoop.Setup;

namespace SummerCoop.Launcher
{
    internal sealed partial class MainForm : Form
    {
        // Official MSCLoader download page, offered when the loader is missing.
        internal const string LoaderDownloadUrl = "https://github.com/piotrulos/MSCModLoader/releases";
        private readonly LauncherSettings settings;
        private readonly string configPath;
        private readonly StringBuilder activity = new StringBuilder();
        private readonly ISteamInviteClient steamClient;
        private readonly SteamLaunchInbox steamInbox;
        private readonly Timer steamTimer = new Timer();
        private readonly Dictionary<ulong, DateTime> dismissedInvites = new Dictionary<ulong, DateTime>();
        private ComboBox cmbInvites;
        private Control inviteRow;
        private Label lblSteam;
        private bool lastLaunchSucceeded;
        private DateTime nextInboxUtc;
        private bool busy;
        private bool loading;

        private readonly Panel content = new MwScenePanel();
        private Button navHome, navSettings, navLogs;
        private Panel pageHome, pageSettings, pageLogs;
        private ComboBox cmbMode, cmbGame, cmbMethod;
        private TextBox txtName, txtAddress, txtPort, txtPassword, txtLobby, txtProfile, txtLogs;
        private CheckBox chkImport, chkDisable, chkClose;
        private Label lblModeDesc, lblGame, lblMod, lblStatus;
        private Button btnPlay, btnInstall;
        private Control addressRow, portRow, passwordRow, lobbyRow;

        internal MainForm() : this((SteamLaunchRequest)null) { }
        internal MainForm(SteamLaunchRequest request) : this(LauncherSettings.ConfigPath(), new SteamInviteClient(), request) { }

        // An isolated settings path lets GUI checks avoid the user's real configuration.
        internal MainForm(string settingsPath) : this(settingsPath, null, null) { }

        internal MainForm(string settingsPath, ISteamInviteClient invites, SteamLaunchRequest request)
        {
            configPath = settingsPath;
            steamClient = invites;
            steamInbox = new SteamLaunchInbox(settingsPath);
            settings = LauncherSettings.Load(configPath);
            if (settings.GamePath.Length == 0)
            {
                try
                {
                    List<string> games = SteamDiscovery.Discover();
                    if (games.Count != 0) settings.GamePath = games[0];
                }
                catch (Exception) { }
            }
            Text = "SummerCoop — My Summer Car";
            ClientSize = new Size(1280, 800);
            MinimumSize = new Size(1024, 640);
            FormBorderStyle = FormBorderStyle.None;
            DoubleBuffered = true;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", 14, FontStyle.Regular, GraphicsUnit.Pixel);
            MwStyle.Dark = settings.DarkTheme;
            ForeColor = MwStyle.Ink;
            BackColor = Color.FromArgb(4, 8, 22);
            content.Dock = DockStyle.Fill;
            Controls.Add(content);
            BuildNavigation();
            BuildStatus();
            pageHome = MakePage();
            pageSettings = MakePage();
            pageLogs = MakePage();
            BuildHome();
            BuildSettings();
            BuildLogs();
            LoadSettingsToUI();
            RefreshStatuses();
            ShowPage(pageHome);
            Resize += delegate { LayoutView(); };
            Shown += delegate {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                if (Width > area.Width || Height > area.Height) Size = new Size(Math.Max(MinimumSize.Width, Math.Min(Width, area.Width)), Math.Max(MinimumSize.Height, Math.Min(Height, area.Height)));
                LayoutView();
            };
            if (steamClient != null) steamClient.Invitation += ReceiveInvitation;
            steamTimer.Interval = 250;
            steamTimer.Tick += delegate { TickSteam(); };
            if (steamClient != null) Shown += delegate { TickSteam(); steamTimer.Start(); };
            if (request != null) ReceiveSteamLaunch(request);
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (busy) { e.Cancel = true; AppendHome("Дождитесь завершения установки."); return; }
                SaveUIToSettings(true);
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ModPayload.ReleaseShared();
                steamTimer.Stop();
                steamTimer.Dispose();
                if (steamClient != null) { steamClient.Invitation -= ReceiveInvitation; steamClient.Dispose(); }
            }
            if (disposing && content.BackgroundImage != null)
            {
                Image background = content.BackgroundImage;
                content.BackgroundImage = null;
                background.Dispose();
            }
            base.Dispose(disposing);
        }

        private void LoadSettingsToUI()
        {
            loading = true;
            try
            {
                try { foreach (string game in SteamDiscovery.Discover()) if (!cmbGame.Items.Contains(game)) cmbGame.Items.Add(game); }
                catch (Exception) { }
                cmbGame.Text = settings.GamePath;
                txtProfile.Text = settings.ProfilePath;
                chkDisable.Checked = settings.DisableOtherMods;
                chkClose.Checked = settings.CloseOnLaunch;
                chkImport.Checked = settings.ImportSave;
                cmbMethod.SelectedIndex = (int)settings.LaunchMethod;
                cmbMode.SelectedIndex = (int)settings.Mode;
                txtName.Text = settings.PlayerName;
                txtAddress.Text = settings.Address;
                txtPort.Text = settings.Port.ToString();
                txtPassword.Text = settings.Password;
                txtLobby.Text = settings.LobbyId;
            }
            finally { loading = false; }
            RefreshModeHint();
        }

        private bool SaveUIToSettings(bool saveFile)
        {
            settings.GamePath = cmbGame.Text.Trim();
            settings.ProfilePath = txtProfile.Text.Trim();
            settings.DisableOtherMods = chkDisable.Checked;
            settings.CloseOnLaunch = chkClose.Checked;
            settings.ImportSave = chkImport.Checked;
            settings.LaunchMethod = (LaunchMethod)Math.Max(0, cmbMethod.SelectedIndex);
            settings.Mode = (ConnectionMode)Math.Max(0, cmbMode.SelectedIndex);
            settings.PlayerName = txtName.Text.Trim();
            settings.Address = txtAddress.Text.Trim();
            int port;
            settings.Port = int.TryParse(txtPort.Text.Trim(), out port) ? port : 0;
            settings.Password = txtPassword.Text;
            settings.LobbyId = txtLobby.Text.Trim();
            if (saveFile)
            {
                try { settings.Save(configPath); }
                catch (Exception e) { AppendHome("Не удалось сохранить настройки: " + e.Message); return false; }
            }
            return true;
        }

        private void RefreshModeHint()
        {
            ConnectionMode mode = (ConnectionMode)Math.Max(0, cmbMode.SelectedIndex);
            bool lan = mode == ConnectionMode.HostLan || mode == ConnectionMode.JoinLan;
            pageHome.SuspendLayout();
            addressRow.Visible = mode == ConnectionMode.JoinLan;
            portRow.Visible = passwordRow.Visible = lan;
            lobbyRow.Visible = mode == ConnectionMode.JoinSteam;
            lblModeDesc.Text = GameArgs.Describe(mode);
            chkImport.Enabled = mode == ConnectionMode.Solo || mode == ConnectionMode.HostLan || mode == ConnectionMode.HostSteam;
            btnPlay.Text = mode == ConnectionMode.HostLan || mode == ConnectionMode.HostSteam ? "Создать игру" :
                mode == ConnectionMode.JoinLan || mode == ConnectionMode.JoinSteam ? "Присоединиться" : "Играть";
            bool steam = mode == ConnectionMode.HostSteam || mode == ConnectionMode.JoinSteam;
            netSteam.Selected = steam;
            netLan.Selected = !steam;
            netSteam.Invalidate(); netLan.Invalidate();
            pageHome.ResumeLayout(true);
        }

        private void RefreshStatuses()
        {
            SaveUIToSettings(false);
            string game = settings.GamePath;
            bool found = HasGameFile(game, "mysummercar.exe");
            bool loader = found && HasGameFile(game, @"mysummercar_Data\Managed\MSCLoader.dll");
            lblGame.Text = !found ? "Выберите папку My Summer Car в настройках." :
                !loader ? "Установите MSCLoader в папку игры — кнопка «Скачать MSCLoader» ниже." : "My Summer Car найдена · MSCLoader установлен";
            lblGame.ForeColor = loader ? MwStyle.Ok : MwStyle.Warn;
            bool installed = found && HasGameFile(game, @"Mods\SummerCoop.dll") &&
                HasGameFile(game, @"Mods\References\SummerCoop.Core.dll");
            lblMod.Text = installed ? "Мод установлен. Перед запуском лаунчер проверит его версию." : "Мод не установлен — лаунчер установит его автоматически при запуске.";
            btnInstall.Text = installed ? "Обновить мод" : "Установить мод";
            btnInstall.Enabled = !busy && loader;
            if (btnLoader != null) btnLoader.Visible = found && !loader;
            if (gameFooter != null) gameFooter.Text = found ? "✓ My Summer Car" : "My Summer Car · выберите папку";
        }

        private static bool HasGameFile(string game, string relativePath)
        {
            try { return game.Length != 0 && File.Exists(Path.Combine(game, relativePath)); }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }

        private void AppendHome(string line)
        {
            if (lblStatus.InvokeRequired) { lblStatus.BeginInvoke(new Action<string>(AppendHome), line); return; }
            activity.AppendLine(line);
            lblStatus.Text = line;
            if (pageLogs.Visible) RefreshLogs();
        }

        private void AppendLaunch(string line) { AppendHome(line); }

        private void TickSteam()
        {
            if (IsDisposed || busy) return;
            if (steamClient != null)
            {
                steamClient.Update(cmbGame.Text.Trim());
                lblSteam.Text = steamClient.Status;
                lblSteam.ForeColor = steamClient.Ready ? MwStyle.Ok : MwStyle.Warn;
            }
            if (DateTime.UtcNow >= nextInboxUtc)
            {
                nextInboxUtc = DateTime.UtcNow.AddSeconds(1);
                try { foreach (SteamLaunchRequest request in steamInbox.Drain(DateTime.UtcNow)) ReceiveSteamLaunch(request); }
                catch (Exception e)
                {
                    nextInboxUtc = DateTime.UtcNow.AddSeconds(10);
                    AppendHome("Не удалось прочитать приглашение: " + e.Message);
                }
            }
            for (int i = cmbInvites.Items.Count - 1; i >= 0; i--)
                if (((SteamInvitation)cmbInvites.Items[i]).ReceivedUtc < DateTime.UtcNow.AddMinutes(-30)) cmbInvites.Items.RemoveAt(i);
            inviteRow.Visible = cmbInvites.Items.Count != 0;
            if (cmbInvites.Items.Count != 0 && cmbInvites.SelectedIndex < 0) cmbInvites.SelectedIndex = 0;
        }

        private void ReceiveSteamLaunch(SteamLaunchRequest request)
        {
            if (request.GamePath.Length != 0 && HasGameFile(request.GamePath, "mysummercar.exe")) cmbGame.Text = request.GamePath;
            if (request.LobbyId != 0) ReceiveInvitation(new SteamInvitation(request.LobbyId, 0, "Приглашение из Steam"));
            else FocusLauncher();
        }

        private void FocusLauncher()
        {
            if (!IsHandleCreated) return;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            Activate();
        }

        private void ReceiveInvitation(SteamInvitation invitation)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<SteamInvitation>(ReceiveInvitation), invitation); return; }
            DateTime dismissed;
            if (dismissedInvites.TryGetValue(invitation.LobbyId, out dismissed) && dismissed > DateTime.UtcNow.AddSeconds(-3)) return;
            for (int i = 0; i < cmbInvites.Items.Count; i++)
            {
                SteamInvitation existing = (SteamInvitation)cmbInvites.Items[i];
                if (existing.LobbyId != invitation.LobbyId) continue;
                if (invitation.SenderId != 0) cmbInvites.Items[i] = invitation;
                return;
            }
            if (cmbInvites.Items.Count >= 16) cmbInvites.Items.RemoveAt(0);
            cmbInvites.Items.Add(invitation);
            cmbInvites.SelectedIndex = cmbInvites.Items.Count - 1;
            inviteRow.Visible = true;
            inviteRow.BringToFront();
            ShowPage(pageHome);
            AppendHome(invitation.SenderName + " приглашает вас в My Summer Car.");
            FocusLauncher();
        }

        private void DismissInvitation() { DismissInvitation(cmbInvites.SelectedItem as SteamInvitation); }

        private void DismissInvitation(SteamInvitation invitation)
        {
            if (invitation == null) return;
            dismissedInvites[invitation.LobbyId] = DateTime.UtcNow;
            if (dismissedInvites.Count > 64)
            {
                List<ulong> expired = new List<ulong>();
                foreach (KeyValuePair<ulong, DateTime> item in dismissedInvites)
                    if (item.Value < DateTime.UtcNow.AddSeconds(-3)) expired.Add(item.Key);
                foreach (ulong lobby in expired) dismissedInvites.Remove(lobby);
            }
            cmbInvites.Items.Remove(invitation);
            if (cmbInvites.Items.Count != 0) cmbInvites.SelectedIndex = 0;
            inviteRow.Visible = cmbInvites.Items.Count != 0;
        }

        private void AcceptInvitation()
        {
            if (busy) { AppendHome("Дождитесь завершения установки."); return; }
            SteamInvitation invitation = cmbInvites.SelectedItem as SteamInvitation;
            if (invitation == null) return;
            cmbMode.SelectedIndex = (int)ConnectionMode.JoinSteam;
            cmbMethod.SelectedIndex = (int)LaunchMethod.Steam;
            txtLobby.Text = invitation.LobbyId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // The launch may install or update the mod first, so the accepted
            // invite is dismissed only after the game actually started.
            PlayAccepted(delegate { if (!IsDisposed) DismissInvitation(invitation); });
        }

        private void Play()
        {
            PlayAccepted(null);
        }

        // onLaunched runs only after the game actually started, so accepted
        // Steam invites stay visible while the mod is being installed.
        private void PlayAccepted(Action onLaunched)
        {
            lastLaunchSucceeded = false;
            if (busy) { AppendHome("Дождитесь завершения установки."); return; }
            SaveUIToSettings(false);
            List<string> errors = settings.Validate(true);
            if (errors.Count != 0)
            {
                AppendHome(errors[0]);
                if (settings.GamePath.Length == 0 || settings.ProfilePath.Length == 0) ShowPage(pageSettings);
                else
                {
                    ShowPage(pageLobby);
                    if (settings.PlayerName.Length == 0 || settings.PlayerName.Length > 48) txtName.Focus();
                    else if ((settings.Mode == ConnectionMode.HostLan || settings.Mode == ConnectionMode.JoinLan) && (settings.Port < 1024 || settings.Port > 65535)) txtPort.Focus();
                    else if (settings.Mode == ConnectionMode.JoinLan) txtAddress.Focus();
                    else if (settings.Mode == ConnectionMode.JoinSteam) txtLobby.Focus();
                }
                return;
            }
            try { new Installer(new InstallerServices()).ValidateGame(settings.GamePath); }
            catch (Exception e) { AppendHome(e.Message); ShowPage(pageSettings); return; }
            if (!SaveUIToSettings(true)) return;
            // Setup is built into the launcher: the embedded pair is compared
            // with the installed DLLs and the game starts only afterwards.
            StartInstall(false, delegate
            {
                LaunchGame();
                if (lastLaunchSucceeded && onLaunched != null) onLaunched();
            });
        }

        private void LaunchGame()
        {
            List<string> args = GameArgs.Build(settings);
            try
            {
                InstallerServices services = new InstallerServices();
                if (services.IsGameRunning(settings.GamePath)) throw new IOException("Закройте My Summer Car перед подключением. Приглашение останется в лаунчере.");
                bool steamMode = settings.Mode == ConnectionMode.HostSteam || settings.Mode == ConnectionMode.JoinSteam;
                if (steamClient != null) steamClient.Update(settings.GamePath);
                if (steamMode && (steamClient == null || !steamClient.Ready))
                    throw new IOException(steamClient == null ? "Откройте Steam перед подключением." : steamClient.Status);
                using (Process launched = Process.Start(GameArgs.Launch(settings, FindSteamExe(), steamClient != null && steamClient.Ready))) { }
                lastLaunchSucceeded = true;
                activity.AppendLine("Запуск: " + GameArgs.ToDisplayCommandLine(args));
                AppendHome("Игра запущена. Меню SummerCoop — F10, подключение — F6.");
                if (settings.CloseOnLaunch) Close();
            }
            catch (Exception e) { AppendHome(e.Message); }
        }

        private static string FindSteamExe()
        {
            List<string> roots = new List<string>();
            try
            {
                object v = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe", null);
                if (v is string) roots.Add((string)v);
            }
            catch (Exception) { }
            try
            {
                object v = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null);
                if (v is string) roots.Add(Path.Combine((string)v, "steam.exe"));
            }
            catch (Exception) { }
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Steam\steam.exe"));
            foreach (string candidate in roots)
            {
                try { if (candidate != null && File.Exists(candidate)) return candidate; }
                catch (Exception) { }
            }
            return null;
        }

        private void InstallMod()
        {
            if (busy) return;
            SaveUIToSettings(false);
            string game = settings.GamePath, profile = settings.ProfilePath;
            if (game.Length == 0 || profile.Length == 0)
            {
                AppendHome("Укажите папку игры и сохранение мира в настройках.");
                ShowPage(pageSettings);
                return;
            }
            if (!SaveUIToSettings(true)) return;
            StartInstall(true, null);
        }

        // Runs the shared installer in the background. Automatic launches pass
        // force=false, so a pair that already matches the embedded payload is
        // left untouched; the manual button reinstalls unconditionally.
        private void StartInstall(bool force, Action continuation)
        {
            string game = settings.GamePath, profile = settings.ProfilePath;
            bool disable = settings.DisableOtherMods;
            busy = true;
            pageHome.Enabled = pageLobby.Enabled = pageSettings.Enabled = false;
            btnInstall.Enabled = btnPlay.Enabled = false;
            AppendHome(force ? "Установка…" : "Проверка установленной сборки…");
            BackgroundWorker worker = new BackgroundWorker();
            worker.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                ModPayload payload = ModPayload.Shared();
                Installer installer = new Installer(new InstallerServices());
                if (!force && installer.IsCurrent(game, payload.Value)) return;
                if (!force) AppendHome("Установленный мод отличается от сборки лаунчера. Обновление…");
                installer.Install(game, profile, payload.Value, disable, AppendHome);
            };
            worker.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                busy = false;
                pageHome.Enabled = pageLobby.Enabled = pageSettings.Enabled = true;
                btnPlay.Enabled = true;
                RefreshStatuses();
                worker.Dispose();
                if (e.Error != null)
                {
                    AppendHome("Установка не завершена: " + e.Error.Message);
                    if (e.Error is UnauthorizedAccessException) AppendHome("Нет прав записи. Запустите лаунчер от имени администратора.");
                    return;
                }
                if (continuation != null) continuation();
                else AppendHome("Готово! Можно нажимать «Играть».");
            };
            worker.RunWorkerAsync();
        }

        private void RefreshLogs()
        {
            if (txtLogs == null) return;
            SaveUIToSettings(false);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("SummerCoop " + LauncherInfo.ModVersion);
            sb.AppendLine("Режим: " + GameArgs.Describe(settings.Mode));
            sb.AppendLine("Игра: " + settings.GamePath);
            sb.AppendLine("Профиль: " + settings.ProfilePath);
            sb.AppendLine();
            sb.AppendLine("Действия лаунчера");
            sb.Append(activity);
            sb.AppendLine();
            foreach (string path in LogFiles())
            {
                sb.AppendLine("== " + path + " ==");
                try
                {
                    if (!File.Exists(path)) { sb.AppendLine("(нет файла)"); sb.AppendLine(); continue; }
                    string[] lines = File.ReadAllLines(path);
                    int from = Math.Max(0, lines.Length - 150);
                    sb.AppendLine("(всего строк: " + lines.Length + ", показаны последние " + (lines.Length - from) + ")");
                    for (int i = from; i < lines.Length; i++) sb.AppendLine(lines[i]);
                }
                catch (Exception e) { sb.AppendLine("Не читается: " + e.Message); }
                sb.AppendLine();
            }
            txtLogs.Text = sb.ToString();
        }

        private List<string> LogFiles()
        {
            List<string> files = new List<string>();
            try
            {
                string profile = settings.ProfilePath;
                if (profile.Length != 0)
                {
                    files.Add(Path.Combine(profile, "errors.log"));
                    string parent = Path.GetDirectoryName(profile.TrimEnd(Path.DirectorySeparatorChar));
                    if (parent != null) files.Add(Path.Combine(parent, "errors.log"));
                }
                if (settings.GamePath.Length != 0)
                {
                    files.Add(Path.Combine(settings.GamePath, @"mysummercar_Data\output_log.txt"));
                }
            }
            catch (Exception) { }
            return files;
        }

        private void OpenFolder(string path)
        {
            try
            {
                string target = path;
                if (File.Exists(target)) target = Path.GetDirectoryName(target);
                if (!Directory.Exists(target)) { AppendHome("Нет папки: " + path); return; }
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception e) { AppendHome(e.Message); }
        }
    }
}
