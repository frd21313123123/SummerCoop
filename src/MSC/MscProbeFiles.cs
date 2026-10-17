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
        public static void WriteAllText(string path, string value)
        {
            // File.Replace can race an open from another thread even when the
            // reader shares DELETE. Serialize local diagnostic readers/writers;
            // other game processes still receive complete atomic snapshots.
            lock (publication) AtomicFile.Write(path, Encoding.UTF8.GetBytes(value));
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
