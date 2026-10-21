using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MWCoop
{
    // Functional QA processes publish clocks/snapshots only after closing the
    // temporary file. Ordinary gameplay never calls these diagnostic helpers.
    internal static class MscProbeFiles
    {
        static readonly object publication = new object();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileEx(string source, string destination, int flags);
        public static void WriteAllText(string path, string value)
        {
            // Ephemeral QA IPC needs a complete publication, without forcing
            // physical disk durability and stalling Unity's simulation frame.
            // Native rename also avoids old Mono's File.Replace sharing window.
            lock (publication)
            {
                string target = Path.GetFullPath(path), temp = target + ".pending";
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) file.Write(bytes, 0, bytes.Length);
                for (int attempt = 0; ; attempt++)
                {
                    if (MoveFileEx(temp, target, 1)) return;
                    int error = Marshal.GetLastWin32Error();
                    if (attempt >= 50) throw new IOException("QA snapshot rename failed (" + error + "): " + target);
                    Thread.Sleep(2);
                }
            }
        }
        public static void WriteAllLines(string path, string[] lines)
        {
            WriteAllText(path, lines.Length == 0 ? "" : string.Join(System.Environment.NewLine, lines) + System.Environment.NewLine);
        }
        public static string ReadAllText(string path)
        {
            lock (publication)
                using (var file = OpenSnapshot(path))
                using (var reader = new StreamReader(file, Encoding.UTF8, true)) return reader.ReadToEnd();
        }
        public static string[] ReadAllLines(string path)
        {
            lock (publication)
            {
                var result = new List<string>();
                using (var file = OpenSnapshot(path))
                using (var reader = new StreamReader(file, Encoding.UTF8, true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null) result.Add(line);
                }
                return result.ToArray();
            }
        }
        static FileStream OpenSnapshot(string path)
        {
            // Windows can briefly expose the rename boundary as not-found.
            // Retry opening; never accept a partly written clock or snapshot.
            for (int attempt = 0; ; attempt++)
            {
                try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
                catch (IOException) { if (attempt >= 8) throw; Thread.Sleep(1); }
            }
        }
    }
}
