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
    internal sealed class MainForm : Form
    {
        private readonly LauncherSettings settings;
        private readonly string configPath;
        private readonly StringBuilder activity = new StringBuilder();
        private bool busy;
        private bool loading;

        private readonly Panel content = new Panel();
        private Button navHome, navSettings, navLogs;
        private Panel pageHome, pageSettings, pageLogs;
        private ComboBox cmbMode, cmbGame, cmbMethod;
        private TextBox txtName, txtAddress, txtPort, txtPassword, txtLobby, txtProfile, txtLogs;
        private CheckBox chkImport, chkDisable, chkClose;
        private Label lblModeDesc, lblGame, lblMod, lblStatus;
        private Button btnPlay, btnInstall;
        private Control addressRow, portRow, passwordRow, lobbyRow;

        internal MainForm() : this(LauncherSettings.ConfigPath()) { }

        // An isolated settings path lets GUI checks avoid the user's real configuration.
        internal MainForm(string settingsPath)
        {
            configPath = settingsPath;
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
            ClientSize = new Size(1000, 700);
            MinimumSize = new Size(820, 660);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10);
            ForeColor = Color.FromArgb(30, 44, 55);
            BackColor = Color.FromArgb(24, 34, 44);

            content.Dock = DockStyle.Fill;
            content.Padding = new Padding(28, 24, 28, 24);
            try { content.BackgroundImage = BackgroundArt.Load(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)); }
            catch (Exception) { }
            content.BackgroundImageLayout = ImageLayout.Zoom;
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
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (busy) { e.Cancel = true; AppendHome("Дождитесь завершения установки."); return; }
                SaveUIToSettings(true);
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && content.BackgroundImage != null)
            {
                Image background = content.BackgroundImage;
                content.BackgroundImage = null;
                background.Dispose();
            }
            base.Dispose(disposing);
        }

        private void BuildNavigation()
        {
            TableLayoutPanel bar = new TableLayoutPanel();
            bar.Dock = DockStyle.Top;
            bar.Height = 76;
            bar.Padding = new Padding(28, 14, 28, 14);
            bar.BackColor = Color.FromArgb(20, 30, 40);
            bar.ColumnCount = 4;
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            Label title = new Label();
            title.Text = "SummerCoop  /  " + LauncherInfo.ModVersion;
            title.Font = new Font("Segoe UI", 16, FontStyle.Bold);
            title.ForeColor = Color.White;
            title.Dock = DockStyle.Fill;
            title.TextAlign = ContentAlignment.MiddleLeft;
            bar.Controls.Add(title, 0, 0);
            navHome = MakeButton("Играть", false);
            navSettings = MakeButton("Настройки", false);
            navLogs = MakeButton("Диагностика", false);
            Button[] buttons = { navHome, navSettings, navLogs };
            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i].Dock = DockStyle.Fill;
                buttons[i].ForeColor = Color.White;
                bar.Controls.Add(buttons[i], i + 1, 0);
            }
            navHome.Click += delegate { ShowPage(pageHome); RefreshStatuses(); };
            navSettings.Click += delegate { ShowPage(pageSettings); };
            navLogs.Click += delegate { ShowPage(pageLogs); RefreshLogs(); };
            Controls.Add(bar);
        }

        private void BuildStatus()
        {
            lblStatus = new Label();
            lblStatus.Dock = DockStyle.Bottom;
            lblStatus.Height = 48;
            lblStatus.Padding = new Padding(28, 0, 28, 0);
            lblStatus.BackColor = Color.FromArgb(20, 30, 40);
            lblStatus.ForeColor = Color.FromArgb(220, 230, 240);
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            lblStatus.AutoEllipsis = true;
            lblStatus.Text = "Выберите режим и нажмите «Играть». Меню в игре — F6.";
            Controls.Add(lblStatus);
        }

        private Panel MakePage()
        {
            Panel page = new Panel();
            page.Dock = DockStyle.Fill;
            page.BackColor = Color.Transparent;
            page.AutoScroll = true;
            page.Visible = false;
            content.Controls.Add(page);
            return page;
        }

        private TableLayoutPanel Card(Panel page, string title)
        {
            TableLayoutPanel card = new TableLayoutPanel();
            card.Dock = DockStyle.Top;
            card.AutoSize = true;
            card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            card.ColumnCount = 1;
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.BackColor = Color.FromArgb(248, 250, 251);
            card.Padding = new Padding(24, 18, 24, 18);
            page.Controls.Add(card);
            Label heading = MakeLabel(title);
            heading.Font = new Font("Segoe UI", 18, FontStyle.Bold);
            heading.Margin = new Padding(0, 0, 0, 14);
            Add(card, heading);
            return card;
        }

        private static void Add(TableLayoutPanel table, Control control)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(control, 0, row);
        }

        private static Label MakeLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Dock = DockStyle.Fill;
            label.Margin = new Padding(0, 0, 0, 10);
            return label;
        }

        private static Button MakeButton(string text, bool primary)
        {
            Button button = new Button();
            button.Text = text;
            button.AutoSize = true;
            button.MinimumSize = new Size(140, 40);
            button.Padding = new Padding(12, 4, 12, 4);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = primary ? Color.FromArgb(195, 76, 34) : Color.FromArgb(224, 231, 235);
            button.ForeColor = primary ? Color.White : Color.FromArgb(30, 44, 55);
            button.Font = new Font("Segoe UI", 10, primary ? FontStyle.Bold : FontStyle.Regular);
            button.Margin = new Padding(0, 4, 12, 0);
            button.Cursor = Cursors.Hand;
            return button;
        }

        private static Control Field(TableLayoutPanel card, string caption, Control input)
        {
            TableLayoutPanel row = new TableLayoutPanel();
            row.AutoSize = true;
            row.Dock = DockStyle.Fill;
            row.Margin = new Padding(0, 0, 0, 10);
            row.ColumnCount = 2;
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Label label = MakeLabel(caption);
            label.Anchor = AnchorStyles.Left;
            label.Margin = new Padding(0, 4, 8, 4);
            row.Controls.Add(label, 0, 0);
            input.Dock = DockStyle.Fill;
            input.Margin = Padding.Empty;
            row.Controls.Add(input, 1, 0);
            Add(card, row);
            return row;
        }

        private static ComboBox Choice(params string[] items)
        {
            ComboBox choice = new ComboBox();
            choice.DropDownStyle = ComboBoxStyle.DropDownList;
            choice.Items.AddRange(items);
            return choice;
        }

        private static CheckBox Option(string text)
        {
            CheckBox option = new CheckBox();
            option.Text = text;
            option.AutoSize = true;
            option.Margin = new Padding(0, 0, 0, 10);
            return option;
        }

        private void BuildHome()
        {
            TableLayoutPanel card = Card(pageHome, "Игра с друзьями");
            cmbMode = Choice("Выбрать подключение в игре (F6)", "Создать игру · LAN / VPN", "Присоединиться · LAN / VPN", "Создать игру · Steam", "Присоединиться · Steam");
            Field(card, "Как играть", cmbMode);
            cmbMode.SelectedIndexChanged += delegate { if (!loading) RefreshModeHint(); };
            lblModeDesc = MakeLabel("");
            lblModeDesc.ForeColor = Color.FromArgb(75, 93, 106);
            Add(card, lblModeDesc);
            txtName = new TextBox();
            txtName.MaxLength = 48;
            Field(card, "Имя игрока", txtName);
            txtAddress = new TextBox();
            addressRow = Field(card, "Адрес хоста", txtAddress);
            txtPort = new TextBox();
            portRow = Field(card, "Порт", txtPort);
            txtPassword = new TextBox();
            txtPassword.UseSystemPasswordChar = true;
            passwordRow = Field(card, "Пароль (если есть)", txtPassword);
            txtLobby = new TextBox();
            lobbyRow = Field(card, "Номер лобби", txtLobby);

            lblGame = MakeLabel("");
            lblGame.Margin = new Padding(0, 12, 0, 8);
            Add(card, lblGame);
            lblMod = MakeLabel("");
            Add(card, lblMod);
            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.AutoSize = true;
            actions.Dock = DockStyle.Fill;
            actions.Margin = Padding.Empty;
            btnPlay = MakeButton("Играть", true);
            btnPlay.MinimumSize = new Size(200, 46);
            btnPlay.Click += delegate { Play(); };
            btnInstall = MakeButton("Установить мод", false);
            btnInstall.Click += delegate { InstallMod(); };
            actions.Controls.Add(btnPlay);
            actions.Controls.Add(btnInstall);
            Add(card, actions);
        }

        private void BuildSettings()
        {
            TableLayoutPanel card = Card(pageSettings, "Настройки");
            cmbGame = new ComboBox();
            Field(card, "Папка игры", PathPicker(cmbGame, "Выберите папку с mysummercar.exe"));
            cmbGame.TextChanged += delegate { if (!loading) RefreshStatuses(); };
            txtProfile = new TextBox();
            Field(card, "Сохранение мира", PathPicker(txtProfile, "Выберите папку сохранения мира"));
            Label profileHint = MakeLabel("Мир хранится отдельно от одиночного сохранения.");
            profileHint.ForeColor = Color.FromArgb(75, 93, 106);
            Add(card, profileHint);
            chkImport = Option("Начать новый мир с копии одиночного сохранения");
            Add(card, chkImport);
            chkDisable = Option("Отключить другие моды при установке");
            Add(card, chkDisable);
            chkClose = Option("Закрывать лаунчер после запуска игры");
            Add(card, chkClose);
            cmbMethod = Choice("Автоматически", "Напрямую", "Через Steam");
            Field(card, "Способ запуска", cmbMethod);
            Button save = MakeButton("Сохранить", true);
            save.Click += delegate { if (SaveUIToSettings(true)) { RefreshStatuses(); AppendHome("Настройки сохранены."); } };
            Add(card, save);
        }

        private Control PathPicker(Control input, string description)
        {
            TableLayoutPanel picker = new TableLayoutPanel();
            picker.AutoSize = true;
            picker.ColumnCount = 2;
            picker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            picker.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            input.Dock = DockStyle.Fill;
            input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            input.Margin = new Padding(0, 4, 10, 4);
            picker.Controls.Add(input, 0, 0);
            Button browse = MakeButton("Обзор…", false);
            browse.MinimumSize = new Size(100, 32);
            browse.Margin = Padding.Empty;
            browse.Click += delegate
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog())
                {
                    dialog.Description = description;
                    if (Directory.Exists(input.Text)) dialog.SelectedPath = input.Text;
                    if (dialog.ShowDialog(this) == DialogResult.OK) input.Text = dialog.SelectedPath;
                }
            };
            picker.Controls.Add(browse, 1, 0);
            return picker;
        }

        private void BuildLogs()
        {
            TableLayoutPanel card = Card(pageLogs, "Диагностика");
            txtLogs = new TextBox();
            txtLogs.Multiline = true;
            txtLogs.ReadOnly = true;
            txtLogs.ScrollBars = ScrollBars.Both;
            txtLogs.WordWrap = false;
            txtLogs.Dock = DockStyle.Fill;
            txtLogs.Height = 320;
            txtLogs.Font = new Font("Consolas", 9);
            Add(card, txtLogs);
            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.AutoSize = true;
            actions.Margin = Padding.Empty;
            Button refresh = MakeButton("Обновить", false);
            refresh.Click += delegate { RefreshLogs(); };
            Button profile = MakeButton("Папка сохранения", false);
            profile.Click += delegate { OpenFolder(txtProfile.Text); };
            Button game = MakeButton("Папка игры", false);
            game.Click += delegate { OpenFolder(cmbGame.Text); };
            actions.Controls.Add(refresh);
            actions.Controls.Add(profile);
            actions.Controls.Add(game);
            Add(card, actions);
        }

        private void ShowPage(Panel page)
        {
            pageHome.Visible = page == pageHome;
            pageSettings.Visible = page == pageSettings;
            pageLogs.Visible = page == pageLogs;
            if (page == pageHome) AcceptButton = btnPlay;
            else AcceptButton = null;
            Highlight(navHome, page == pageHome);
            Highlight(navSettings, page == pageSettings);
            Highlight(navLogs, page == pageLogs);
        }

        private static void Highlight(Button button, bool active)
        {
            button.BackColor = active ? Color.FromArgb(195, 76, 34) : Color.FromArgb(35, 49, 62);
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
            pageHome.ResumeLayout(true);
        }

        private void RefreshStatuses()
        {
            SaveUIToSettings(false);
            string game = settings.GamePath;
            bool found = HasGameFile(game, "mysummercar.exe");
            bool loader = found && HasGameFile(game, @"mysummercar_Data\Managed\MSCLoader.dll");
            lblGame.Text = !found ? "Выберите папку My Summer Car в настройках." :
                !loader ? "Установите MSCLoader 1.3.5 в папку игры." : "My Summer Car найдена · MSCLoader установлен";
            lblGame.ForeColor = loader ? Color.FromArgb(40, 112, 77) : Color.FromArgb(161, 72, 33);
            bool installed = found && HasGameFile(game, @"Mods\SummerCoop.dll") &&
                HasGameFile(game, @"Mods\References\SummerCoop.Core.dll");
            lblMod.Text = installed ? "Мод установлен. Для совместной игры используйте одну сборку." : "Установите мод перед первым запуском.";
            btnInstall.Text = installed ? "Обновить мод" : "Установить мод";
            btnInstall.Enabled = !busy && loader;
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

        private void Play()
        {
            if (busy) { AppendHome("Дождитесь завершения установки."); return; }
            SaveUIToSettings(false);
            List<string> errors = settings.Validate(true);
            if (errors.Count != 0)
            {
                AppendHome(errors[0]);
                if (settings.GamePath.Length == 0 || settings.ProfilePath.Length == 0) ShowPage(pageSettings);
                else
                {
                    ShowPage(pageHome);
                    if (settings.PlayerName.Length == 0 || settings.PlayerName.Length > 48) txtName.Focus();
                    else if ((settings.Mode == ConnectionMode.HostLan || settings.Mode == ConnectionMode.JoinLan) && (settings.Port < 1024 || settings.Port > 65535)) txtPort.Focus();
                    else if (settings.Mode == ConnectionMode.JoinLan) txtAddress.Focus();
                    else if (settings.Mode == ConnectionMode.JoinSteam) txtLobby.Focus();
                }
                return;
            }
            try { new Installer(new InstallerServices()).ValidateGame(settings.GamePath); }
            catch (Exception e) { AppendHome(e.Message); ShowPage(pageSettings); return; }
            if (!File.Exists(Path.Combine(settings.GamePath, @"Mods\SummerCoop.dll")) ||
                !File.Exists(Path.Combine(settings.GamePath, @"Mods\References\SummerCoop.Core.dll")))
            {
                AppendHome("Нажмите «Установить мод» перед запуском.");
                btnInstall.Focus();
                return;
            }
            if (!SaveUIToSettings(true)) return;
            List<string> args = GameArgs.Build(settings);
            try
            {
                InstallerServices services = new InstallerServices();
                if (services.IsGameRunning(settings.GamePath)) throw new IOException("Закройте My Summer Car перед запуском.");
                Process.Start(GameArgs.Launch(settings, FindSteamExe()));
                activity.AppendLine("Запуск: " + GameArgs.ToDisplayCommandLine(args));
                AppendHome("Игра запущена. Меню SummerCoop в игре — F6.");
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
            bool disable = settings.DisableOtherMods;
            if (game.Length == 0 || profile.Length == 0)
            {
                AppendHome("Укажите папку игры и сохранение мира в настройках.");
                ShowPage(pageSettings);
                return;
            }
            if (!SaveUIToSettings(true)) return;
            busy = true;
            pageHome.Enabled = pageSettings.Enabled = false;
            btnInstall.Enabled = btnPlay.Enabled = false;
            AppendHome("Установка…");
            BackgroundWorker worker = new BackgroundWorker();
            worker.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                using (LauncherPayload payload = new LauncherPayload())
                    e.Result = new Installer(new InstallerServices()).Install(game, profile, payload.Value, disable, AppendHome);
            };
            worker.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                busy = false;
                pageHome.Enabled = pageSettings.Enabled = true;
                btnPlay.Enabled = true;
                RefreshStatuses();
                if (e.Error != null)
                {
                    AppendHome("Установка не завершена: " + e.Error.Message);
                    if (e.Error is UnauthorizedAccessException) AppendHome("Нет прав записи. Запустите от имени администратора.");
                }
                else
                {
                    AppendHome("Готово! Можно нажимать «Играть».");
                }
                worker.Dispose();
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

        private sealed class LauncherPayload : IDisposable
        {
            internal Payload Value;
            private readonly string directory;
            internal LauncherPayload()
            {
                directory = SafePath.Under(Path.GetTempPath(), "SummerCoop-Launcher-" + Guid.NewGuid().ToString("N"));
                SafePath.NoLinks(directory);
                Directory.CreateDirectory(directory);
                try { Value = Payload.Extract(directory); }
                catch { Dispose(); throw; }
            }
            public void Dispose()
            {
                if (!Directory.Exists(directory)) return;
                SafePath.NoLinks(directory);
                foreach (string child in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories)) SafePath.NoLinks(child);
                foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories)) SafePath.NoLinks(file);
                Directory.Delete(directory, true);
            }
        }
    }
}
