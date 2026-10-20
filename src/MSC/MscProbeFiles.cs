using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using SummerCoop.Core;

namespace MWCoop
{
    // Functional QA processes publish clocks/snapshots only after closing the
    // temporary file. Ordinary gameplay never calls these diagnostic helpers.
    internal static class MscProbeFiles
    {
        static readonly object publication = new object();
        static T Snapshot<T>(string path, Func<T> run)
        {
            // Unity's Mono can open the replacement with exclusive sharing.
            // Coordinate the two native QA processes as well as local threads.
            string key = Codec.Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()));
            lock (publication)
            using (var gate = new Mutex(false, "Local\\SummerCoop.QA." + key))
            {
                bool held = false;
                try
                {
                    try { held = gate.WaitOne(5000); }
                    catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new IOException("Timed out waiting for QA snapshot: " + path);
                    return run();
                }
                finally { if (held) gate.ReleaseMutex(); }
            }
        }
        public static void WriteAllText(string path, string value)
        {
            Snapshot(path, delegate { AtomicFile.Write(path, Encoding.UTF8.GetBytes(value)); return true; });
        }
        public static void WriteAllLines(string path, string[] lines)
        {
            WriteAllText(path, lines.Length == 0 ? "" : string.Join(System.Environment.NewLine, lines) + System.Environment.NewLine);
        }
        public static string ReadAllText(string path)
        {
            return Snapshot(path, delegate {
                using (var file = OpenSnapshot(path))
                using (var reader = new StreamReader(file, Encoding.UTF8, true)) return reader.ReadToEnd();
            });
        }
        public static string[] ReadAllLines(string path)
        {
            return Snapshot(path, delegate {
                var result = new List<string>();
                using (var file = OpenSnapshot(path))
                using (var reader = new StreamReader(file, Encoding.UTF8, true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null) result.Add(line);
                }
                return result.ToArray();
            });
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
