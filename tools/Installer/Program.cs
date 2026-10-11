using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace SummerCoop.Setup
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length != 0) return CommandLine(args);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm()); return 0;
        }
        private static int CommandLine(string[] args)
        {
            string logFile = null; List<string> lines = new List<string>();
            Action<string> log = delegate(string line) { lines.Add(line); Console.WriteLine(line); };
            try
            {
                Dictionary<string, string> options = new Dictionary<string, string>();
                foreach (string arg in args)
                {
                    int at = arg.IndexOf('='); string name = at < 0 ? arg : arg.Substring(0, at);
                    if (name != "--install" && name != "--detect" && name != "--game" && name != "--profile" && name != "--log" && name != "--keep-other-mods") throw new ArgumentException("Unknown argument: " + name);
                    if ((name == "--game" || name == "--profile" || name == "--log") && at < 0) throw new ArgumentException("Use " + name + "=PATH");
                    options.Add(name, at < 0 ? "" : arg.Substring(at + 1));
                }
                options.TryGetValue("--log", out logFile);
                if (options.ContainsKey("--detect")) { foreach (string game in SteamDiscovery.Discover()) log(game); return 0; }
                if (!options.ContainsKey("--install")) throw new ArgumentException("Use --detect or --install --game=PATH [--profile=PATH] [--log=PATH] [--keep-other-mods]");
                string gamePath, profile;
                if (!options.TryGetValue("--game", out gamePath))
                {
                    List<string> games = SteamDiscovery.Discover();
                    if (games.Count != 1) throw new IOException("Укажите --game=PATH: найдена не одна игровая папка.");
                    gamePath = games[0];
                }
                if (!options.TryGetValue("--profile", out profile)) profile = Installer.DefaultProfile;
                using (ExtractedPayload extracted = new ExtractedPayload())
                { new Installer(new InstallerServices()).Install(gamePath, profile, extracted.Value, !options.ContainsKey("--keep-other-mods"), log); }
                log("Готово. Запустите игру через Steam; меню SummerCoop открывается клавишей F6."); return 0;
            }
            catch (Exception e) { log("Ошибка: " + e.Message); return 1; }
            finally { if (logFile != null) File.WriteAllLines(logFile, lines.ToArray(), Encoding.UTF8); }
        }
    }
    internal sealed class ExtractedPayload : IDisposable
    {
        internal Payload Value;
        private readonly string directory;
        internal ExtractedPayload()
        {
            directory = SafePath.Under(Path.GetTempPath(), "SummerCoop-Setup-" + Guid.NewGuid().ToString("N"));
            SafePath.NoLinks(directory); Directory.CreateDirectory(directory);
            try { Value = Payload.Extract(directory); } catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            // Only remove our verified unique temporary directory; never the selected game/profile.
            if (!Directory.Exists(directory)) return;
            SafePath.NoLinks(directory);
            foreach (string child in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories)) SafePath.NoLinks(child);
            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories)) SafePath.NoLinks(file);
            Directory.Delete(directory, true);
        }
    }
    internal sealed class SetupForm : Form
    {
        private readonly ComboBox game = new ComboBox();
        private readonly TextBox profile = new TextBox();
        private readonly CheckBox disable = new CheckBox();
        private readonly Button browse = new Button(), install = new Button();
        private readonly TextBox output = new TextBox();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly Label status = new Label();
        private bool busy;
        internal SetupForm()
        {
            Text = "Установка SummerCoop"; ClientSize = new Size(690, 555);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Segoe UI", 10); BackColor = Color.White;
            Label title = new Label { Text = "SummerCoop для My Summer Car", AutoSize = true, Location = new Point(24, 22), Font = new Font("Segoe UI", 17, FontStyle.Bold) }; Controls.Add(title);
            Controls.Add(new Label { Text = "Закройте игру. Установщик найдёт её в библиотеках Steam.", AutoSize = true, Location = new Point(26, 65) });
            Controls.Add(new Label { Text = "Папка игры", AutoSize = true, Location = new Point(26, 102) });
            game.SetBounds(26, 127, 530, 28); game.DropDownStyle = ComboBoxStyle.DropDown; Controls.Add(game);
            browse.Text = "Обзор…"; browse.SetBounds(565, 126, 98, 30); browse.Click += Browse; Controls.Add(browse);
            Controls.Add(new Label { Text = "Профиль мира хоста (при обновлении)", AutoSize = true, Location = new Point(26, 172) });
            profile.Text = Installer.DefaultProfile; profile.SetBounds(26, 197, 637, 28); Controls.Add(profile);
            disable.Text = "Отключить остальные моды, сохранив их в резервной папке"; disable.Checked = true; disable.SetBounds(26, 241, 637, 30); Controls.Add(disable);
            LinkLabel loader = new LinkLabel { Text = "Установить MSCLoader: официальный сайт", AutoSize = true, Location = new Point(26, 283) };
            loader.LinkClicked += delegate { try { Process.Start(new ProcessStartInfo("https://github.com/piotrulos/MSCModLoader/releases/tag/v1.3.5") { UseShellExecute = true }); } catch (Exception e) { Append(e.Message); } }; Controls.Add(loader);
            status.Text = "Поиск установленной игры…"; status.SetBounds(26, 322, 637, 27); Controls.Add(status);
            output.Multiline = true; output.ReadOnly = true; output.ScrollBars = ScrollBars.Vertical; output.SetBounds(26, 354, 637, 119); Controls.Add(output);
            progress.SetBounds(26, 492, 448, 24); Controls.Add(progress);
            install.Text = "Установить"; install.SetBounds(493, 484, 170, 40); install.Click += Install; Controls.Add(install); AcceptButton = install;
            Shown += delegate
            {
                try
                {
                    List<string> games = SteamDiscovery.Discover(); foreach (string found in games) game.Items.Add(found);
                    if (games.Count != 0) { game.SelectedIndex = 0; status.Text = "Игра найдена. Проверьте папку и нажмите «Установить»."; }
                    else status.Text = "Игра не найдена. Выберите её папку через «Обзор».";
                }
                catch (Exception e) { status.Text = "Выберите папку игры через «Обзор»."; Append(e.Message); }
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) { e.Cancel = true; status.Text = "Дождитесь завершения установки или восстановления файлов."; } };
        }
        private void Browse(object sender, EventArgs args)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            { dialog.Description = "Папка с mysummercar.exe"; dialog.ShowNewFolderButton = false; if (Directory.Exists(game.Text)) dialog.SelectedPath = game.Text; if (dialog.ShowDialog(this) == DialogResult.OK) game.Text = dialog.SelectedPath; }
        }
        private void Append(string line)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Append), line); return; }
            output.AppendText(line + Environment.NewLine);
        }
        private void Install(object sender, EventArgs args)
        {
            string selectedGame = game.Text.Trim(), selectedProfile = profile.Text.Trim(); bool disableOthers = disable.Checked;
            if (selectedGame.Length == 0 || selectedProfile.Length == 0) { status.Text = "Укажите папку игры и каталог профиля."; return; }
            busy = true; game.Enabled = profile.Enabled = browse.Enabled = disable.Enabled = install.Enabled = false;
            output.Clear(); status.Text = "Установка…"; progress.Style = ProgressBarStyle.Marquee;
            BackgroundWorker worker = new BackgroundWorker();
            worker.DoWork += delegate(object s, DoWorkEventArgs e)
            { using (ExtractedPayload extracted = new ExtractedPayload()) e.Result = new Installer(new InstallerServices()).Install(selectedGame, selectedProfile, extracted.Value, disableOthers, Append); };
            worker.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                busy = false; game.Enabled = profile.Enabled = browse.Enabled = disable.Enabled = install.Enabled = true; progress.Style = ProgressBarStyle.Blocks;
                if (e.Error != null)
                {
                    status.Text = "Установка не завершена."; Append(e.Error.Message);
                    if (e.Error is UnauthorizedAccessException) Append("Нет прав записи в папку игры. Запустите установщик от имени администратора.");
                }
                else
                {
                    progress.Value = 100; status.Text = "Готово! Запустите игру через Steam.";
                    Append("Меню SummerCoop открывается клавишей F6. Передайте друзьям этот же установщик.");
                }
                worker.Dispose();
            };
            worker.RunWorkerAsync();
        }
    }
}
