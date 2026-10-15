using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Keeps test windows on a separate desktop without ever switching the user's
// desktop. Unity 5 cannot load MSC reliably with its null graphics device.
class TestDesktopHost
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int Size;
        public string Reserved, Desktop, Title;
        public int X, Y, Width, Height, CharsX, CharsY, Fill, Flags;
        public short Show, ReservedSize;
        public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInfo { public IntPtr Process, Thread; public int Id, ThreadId; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, int flags, int access, IntPtr security);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, int flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
    static int Main(string[] encoded)
    {
        string pidFile = null; IntPtr desktop = IntPtr.Zero;
        try
        {
            if (encoded.Length != 5) throw new ArgumentException("Expected executable, arguments, working directory, desktop and PID file.");
            string[] args = Array.ConvertAll(encoded, delegate(string value) { return Encoding.Unicode.GetString(Convert.FromBase64String(value)); });
            pidFile = args[4];
            SetErrorMode(3); // No modal crash dialog on either desktop.
            desktop = CreateDesktop(args[3], IntPtr.Zero, IntPtr.Zero, 0, 0x00ff, IntPtr.Zero);
            if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)), Desktop = "WinSta0\\" + args[3], Flags = 1, Show = 0 };
            ProcessInfo info;
            if (!CreateProcess(args[0], new StringBuilder("\"" + args[0] + "\" " + args[1]), IntPtr.Zero, IntPtr.Zero, false, 0x400, IntPtr.Zero, args[2], ref startup, out info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(info.Thread); CloseHandle(info.Process);
            File.WriteAllText(pidFile, info.Id.ToString());
            using (var child = Process.GetProcessById(info.Id))
            {
                // A hung test must not leave a hidden game running indefinitely.
                if (!child.WaitForExit(660000)) { child.Kill(); child.WaitForExit(); return 2; }
                return child.ExitCode;
            }
        }
        catch (Exception e) { if (pidFile != null) File.WriteAllText(pidFile + ".error", e.ToString()); return 1; }
        finally { if (desktop != IntPtr.Zero) CloseDesktop(desktop); }
    }
}
