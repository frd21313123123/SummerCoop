using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using SummerCoop.Launcher;

internal static class LauncherUiTests
{
    private static T Field<T>(MainForm form, string name)
    {
        return (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
    }

    private static object Call(MainForm form, string name, params object[] args)
    {
        return typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, args);
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

    private static void CheckLayout(Control parent)
    {
        List<Control> children = new List<Control>();
        foreach (Control child in parent.Controls)
        {
            if (!child.Visible) continue;
            if (parent is TableLayoutPanel)
            {
                Check(child.Right <= parent.ClientSize.Width && child.Bottom <= parent.ClientSize.Height,
                    "Clipped control: " + child.Text + " in " + parent.GetType().Name);
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
                ComboBox mode = Field<ComboBox>(form, "cmbMode");
                Check(mode.SelectedIndex == (int)ConnectionMode.JoinSteam, "saved mode changed while loading");
                Check(Field<TextBox>(form, "txtName").Text == "Saved Player", "saved name lost while loading");
                Check(Field<TextBox>(form, "txtPassword").Text == "Saved password", "saved password lost while loading");
                Check(Field<CheckBox>(form, "chkImport").Checked, "saved import lost while loading");
                Render(form, Path.Combine(directory, "play-steam.png"));

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
                form.ClientSize = new Size(820, 660);
                Render(form, Path.Combine(directory, "play-minimum.png"));
                foreach (string page in new string[] { "pageSettings", "pageLogs" })
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
                Check(Field<Panel>(form, "pageHome").Visible, "connection error sent to an unrelated page");
                Check(Field<TextBox>(form, "txtPort").Focused, "invalid port not focused");
                Field<ComboBox>(form, "cmbGame").Text = "\"invalid path\"";
                Check(Field<Label>(form, "lblGame").Text.Contains("Выберите папку"), "invalid game path not explained");
                form.Close();
            }
            Console.WriteLine("PASS launcher GUI: saved settings, all modes, contextual fields, edits, validation and minimum-size layouts");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (File.Exists(config)) File.Delete(config); }
    }
}
