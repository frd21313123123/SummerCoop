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
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
    static int Main(string[] encoded)
    {
        string pidFile = null; IntPtr desktop = IntPtr.Zero, nativeProcess = IntPtr.Zero;
        try
        {
            if (encoded.Length != 5 && encoded.Length != 6) throw new ArgumentException("Expected executable, arguments, working directory, desktop, PID file and optional timeout seconds.");
            string[] args = Array.ConvertAll(encoded, delegate(string value) { return Encoding.Unicode.GetString(Convert.FromBase64String(value)); });
            pidFile = args[4];
            SetErrorMode(3); // No modal crash dialog on either desktop.
            desktop = CreateDesktop(args[3], IntPtr.Zero, IntPtr.Zero, 0, 0x00ff, IntPtr.Zero);
            if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)), Desktop = "WinSta0\\" + args[3], Flags = 1, Show = 0 };
            ProcessInfo info;
            if (!CreateProcess(args[0], new StringBuilder("\"" + args[0] + "\" " + args[1]), IntPtr.Zero, IntPtr.Zero, false, 0x400, IntPtr.Zero, args[2], ref startup, out info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            nativeProcess = info.Process; CloseHandle(info.Thread);
            File.WriteAllText(pidFile, info.Id.ToString());
            using (var child = Process.GetProcessById(info.Id))
            {
                // A hung test must not leave a hidden game running indefinitely.
                int timeout = args.Length == 6 ? int.Parse(args[5]) : 660;
                if (timeout < 30 || timeout > 7200) throw new ArgumentOutOfRangeException("timeout");
                if (!child.WaitForExit(timeout * 1000)) { child.Kill(); child.WaitForExit(); return 2; }
                uint code;
                if (!GetExitCodeProcess(nativeProcess, out code)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return unchecked((int)code);
            }
        }
        catch (Exception e) { if (pidFile != null) File.WriteAllText(pidFile + ".error", e.ToString()); return 1; }
        finally { if (nativeProcess != IntPtr.Zero) CloseHandle(nativeProcess); if (desktop != IntPtr.Zero) CloseDesktop(desktop); }
    }
}
