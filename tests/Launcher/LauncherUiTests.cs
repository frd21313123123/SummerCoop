using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using SummerCoop.Launcher;

internal static class LauncherUiTests
{
    private sealed class FakeSteam : ISteamInviteClient
    {
        public event Action<SteamInvitation> Invitation;
        public bool Ready { get { return true; } }
        public string Status { get { return "Steam подключён · приглашения появятся здесь."; } }
        public bool Disposed;
        public void Update(string path) { }
        public void Dispose() { Disposed = true; }
        internal void Invite(ulong lobby, string name) { if (Invitation != null) Invitation(new SteamInvitation(lobby, 42, name)); }
    }
    private static T Field<T>(MainForm form, string name)
    {
        return (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
    }

    private static object Call(MainForm form, string name, params object[] args)
    {
        MethodInfo match = null;
        foreach (MethodInfo method in typeof(MainForm).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (method.Name != name || method.GetParameters().Length != args.Length) continue;
            if (match != null) throw new Exception("Overloaded test helper call: " + name);
            match = method;
        }
        if (match == null) throw new Exception("Test helper cannot find: " + name);
        return match.Invoke(form, args);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Render(MainForm form, string path)
    {
        Application.DoEvents();
        CheckLayout(form);
        using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(path);
        }
    }

    private static void CheckGlassClip(MainForm form)
    {
        MwScenePanel scene = Field<MwScenePanel>(form, "content");
        MwGlassPanel card = Field<MwGlassPanel>(form, "rail");
        using (Bitmap bitmap = new Bitmap(card.Width, card.Height))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Magenta);
            Rectangle clip = new Rectangle(8, 8, 18, 18);
            g.SetClip(clip);
            scene.Glass(g, card);
            Check(bitmap.GetPixel(80, 80).ToArgb() == Color.Magenta.ToArgb(),
                "Glass painting escaped the child repaint clip");
            Check(bitmap.GetPixel(16, 16).ToArgb() != Color.Magenta.ToArgb(),
                "Glass repaint did not draw inside the child clip");
        }
    }

    private static void CheckLayout(Control parent)
    {
        List<Control> children = new List<Control>();
        foreach (Control child in parent.Controls)
        {
            if (!child.Visible) continue;
            if (parent is TableLayoutPanel)
            {
                ScrollableControl scroll = parent as ScrollableControl;
                Rectangle available = scroll != null && scroll.AutoScroll ? scroll.DisplayRectangle : parent.ClientRectangle;
                Check(child.Right <= available.Right && child.Bottom <= available.Bottom,
                    "Clipped control: " + child.Text + " " + child.Bounds + " in " + parent.GetType().Name + " " + parent.ClientSize);
                foreach (Control other in children)
                    Check(!child.Bounds.IntersectsWith(other.Bounds), "Overlapping controls: " + child.Text + " / " + other.Text);
                children.Add(child);
            }
            CheckLayout(child);
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string directory = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(directory);
        string config = Path.Combine(directory, "ui-test.ini");
        try
        {
            LauncherSettings initial = new LauncherSettings();
            initial.ProfilePath = Path.Combine(directory, "world");
            initial.PlayerName = "Saved Player";
            initial.Password = "Saved password";
            initial.ImportSave = true;
            initial.Mode = ConnectionMode.JoinSteam;
            initial.LobbyId = "123456";
            initial.Save(config);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (MainForm form = new MainForm(config))
            {
                // Render the real controls without displaying a window to the user.
                form.ShowInTaskbar = false;
                form.Opacity = 0;
                form.Show();
                CheckGlassClip(form);
                ComboBox mode = Field<ComboBox>(form, "cmbMode");
                Check(mode.SelectedIndex == (int)ConnectionMode.JoinSteam, "saved mode changed while loading");
                Check(Field<TextBox>(form, "txtName").Text == "Saved Player", "saved name lost while loading");
                Check(Field<TextBox>(form, "txtPassword").Text == "Saved password", "saved password lost while loading");
                Check(Field<CheckBox>(form, "chkImport").Checked, "saved import lost while loading");
                Render(form, Path.Combine(directory, "play-steam.png"));
                Field<MwButton>(form, "netLan").PerformClick();
                Field<MwButton>(form, "hostTile").PerformClick();
                Check(mode.SelectedIndex == (int)ConnectionMode.HostLan && Field<Panel>(form, "pageLobby").Visible, "home LAN host action selected the wrong mode");
                Call(form, "ShowPage", Field<Panel>(form, "pageHome"));
                Field<MwButton>(form, "netSteam").PerformClick();
                Field<MwButton>(form, "joinTile").PerformClick();
                Check(mode.SelectedIndex == (int)ConnectionMode.JoinSteam && Field<Panel>(form, "pageLobby").Visible, "home Steam join action selected the wrong mode");
                Call(form, "ShowPage", Field<Panel>(form, "pageLobby"));

                string profile = Path.Combine(directory, "edited world");
                Field<TextBox>(form, "txtProfile").Text = profile;
                Field<TextBox>(form, "txtName").Text = "Edited Player";
                Field<TextBox>(form, "txtPort").Text = "invalid";
                foreach (ConnectionMode value in new ConnectionMode[] { ConnectionMode.JoinLan, ConnectionMode.HostSteam, ConnectionMode.HostLan, ConnectionMode.Solo, ConnectionMode.JoinSteam })
                {
                    mode.SelectedIndex = (int)value;
                    Application.DoEvents();
                    bool lan = value == ConnectionMode.HostLan || value == ConnectionMode.JoinLan;
                    Check(Field<Control>(form, "addressRow").Visible == (value == ConnectionMode.JoinLan), "wrong address visibility");
                    Check(Field<Control>(form, "portRow").Visible == lan, "wrong port visibility");
                    Check(Field<Control>(form, "passwordRow").Visible == lan, "wrong password visibility");
                    Check(Field<Control>(form, "lobbyRow").Visible == (value == ConnectionMode.JoinSteam), "wrong lobby visibility");
                    Check(Field<TextBox>(form, "txtName").Text == "Edited Player", "mode change reset player name");
                    Check(Field<TextBox>(form, "txtProfile").Text == profile, "mode change reset profile");
                    Check(Field<TextBox>(form, "txtPort").Text == "invalid", "mode change reset port");
                    CheckLayout(form);
                }
                mode.SelectedIndex = (int)ConnectionMode.JoinLan;
                Field<TextBox>(form, "txtPort").Text = "27842";
                Call(form, "SaveUIToSettings", true);
                LauncherSettings saved = LauncherSettings.Load(config);
                Check(saved.ImportSave && saved.PlayerName == "Edited Player" && saved.ProfilePath == profile && saved.Mode == ConnectionMode.JoinLan, "edited values were not saved");
                Render(form, Path.Combine(directory, "play-lan.png"));
                form.ClientSize = new Size(1024, 640);
                Render(form, Path.Combine(directory, "play-minimum.png"));
                foreach (string page in new string[] { "pageSettings", "pageLogs", "pageGuide", "pageNotes" })
                {
                    Call(form, "ShowPage", Field<Panel>(form, page));
                    if (page == "pageLogs") Call(form, "RefreshLogs");
                    Render(form, Path.Combine(directory, page + "-minimum.png"));
                }
                // A bad port must be explained on the play page even after navigation.
                Field<TextBox>(form, "txtPort").Text = "bad";
                Field<ComboBox>(form, "cmbGame").Text = directory;
                Call(form, "Play");
                Check(Field<Label>(form, "lblStatus").Text.Contains("Порт"), "bad port silently reused previous value");
                Check(Field<Panel>(form, "pageLobby").Visible, "connection error sent to an unrelated page");
                Check(Field<TextBox>(form, "txtPort").Focused, "invalid port not focused");
                Field<ComboBox>(form, "cmbGame").Text = "\"invalid path\"";
                Check(Field<Label>(form, "lblGame").Text.Contains("Выберите папку"), "invalid game path not explained");
                // The launcher offers the loader download and says that the mod
                // itself is installed automatically before a launch. The folder
                // is rebuilt from scratch so an earlier run cannot leave the
                // loader DLL behind and hide the hint.
                string fakeGame = Path.Combine(directory, "fixture-game");
                if (Directory.Exists(fakeGame)) Directory.Delete(fakeGame, true);
                Directory.CreateDirectory(Path.Combine(fakeGame, "mysummercar_Data"));
                File.WriteAllText(Path.Combine(fakeGame, "mysummercar.exe"), "fixture");
                Field<ComboBox>(form, "cmbGame").Text = fakeGame;
                Call(form, "RefreshStatuses");
                Check(Field<Label>(form, "lblGame").Text.Contains("Установите MSCLoader"), "missing loader not explained");
                Check(Field<MwButton>(form, "btnLoader").Visible, "loader download hidden");
                Directory.CreateDirectory(Path.Combine(fakeGame, @"mysummercar_Data\Managed"));
                File.WriteAllText(Path.Combine(fakeGame, @"mysummercar_Data\Managed\MSCLoader.dll"), "loader");
                Call(form, "RefreshStatuses");
                Check(!Field<MwButton>(form, "btnLoader").Visible, "loader download still visible");
                Check(Field<Label>(form, "lblMod").Text.Contains("не установлен"), "missing mod not explained");
                form.Close();
            }
            FakeSteam steam = new FakeSteam();
            using (MainForm form = new MainForm(config, steam, new SteamLaunchRequest { LobbyId = 123456 }))
            {
                form.ShowInTaskbar = false;
                form.Opacity = 0;
                form.Show();
                ComboBox pending = Field<ComboBox>(form, "cmbInvites");
                Check(pending.Items.Count == 1 && Field<Control>(form, "inviteRow").Visible, "startup invite not visible");
                steam.Invite(123456, "Steam Friend");
                Check(pending.Items.Count == 1 && pending.Items[0].ToString().Contains("Steam Friend"), "callback/command invite duplicated or sender not updated");
                steam.Invite(987654, "Second Friend");
                Check(pending.Items.Count == 2 && ((SteamInvitation)pending.SelectedItem).LobbyId == 987654, "second invite replaced first");
                Field<ComboBox>(form, "cmbMode").SelectedIndex = (int)ConnectionMode.HostLan;
                Check(Field<TextBox>(form, "txtLobby").Text != "987654", "invite joined without consent");
                // Invalid game folder guarantees this check cannot launch a game.
                Field<ComboBox>(form, "cmbGame").Text = "";
                Call(form, "AcceptInvitation");
                Check(Field<ComboBox>(form, "cmbMode").SelectedIndex == (int)ConnectionMode.JoinSteam, "accept did not select Steam guest");
                Check(Field<ComboBox>(form, "cmbMethod").SelectedIndex == (int)LaunchMethod.Steam, "accept retained direct launch setting");
                Check(Field<TextBox>(form, "txtLobby").Text == "987654", "accept targeted a different lobby");
                Check(pending.Items.Count == 2, "failed launch discarded invite");
                Call(form, "ShowPage", Field<Panel>(form, "pageHome"));
                form.ClientSize = new Size(1024, 640);
                Render(form, Path.Combine(directory, "steam-invitation-minimum.png"));
                Call(form, "ToggleTheme");
                Render(form, Path.Combine(directory, "home-light.png"));
                Check(!LauncherSettings.Load(config).DarkTheme, "theme preference was not saved");
                Call(form, "DismissInvitation");
                steam.Invite(987654, "Duplicate callback");
                Check(pending.Items.Count == 1, "dismissed callback immediately reappeared");
                Call(form, "DismissInvitation");
                Check(pending.Items.Count == 0 && !Field<Control>(form, "inviteRow").Visible, "dismiss did not hide invite");
                new SteamLaunchInbox(config).Send(new SteamLaunchRequest { LobbyId = 7654321 });
                typeof(MainForm).GetField("nextInboxUtc", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, DateTime.MinValue);
                Call(form, "TickSteam");
                Check(pending.Items.Count == 1 && ((SteamInvitation)pending.SelectedItem).LobbyId == 7654321, "second launcher invite not displayed");
                form.Close();
            }
            Check(steam.Disposed, "Steam callback client not disposed");
            Console.WriteLine("PASS launcher GUI: settings, modes, validation, invitation callbacks, startup/forwarded invites, exact lobby selection, dismissal, failed launch retention, MSCLoader hints and minimum-size layouts");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (File.Exists(config)) File.Delete(config); }
    }
}
