using System;
using System.Collections.Generic;
using System.IO;
using SummerCoop.Core;

namespace MWCoop
{
    // Reuse SummerCoop's protection against empty ES2 output during native saves.
    public static class MscSaveGuard
    {
        static Dictionary<string, byte[]> before;
        public static void Begin()
        {
            if (!MscProfile.Isolated) throw new InvalidOperationException("Native save requires isolated MSC profile");
            before = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (string name in NativeSaveBundle.Names) { string path = Path.Combine(MscProfile.SaveDirectory, name); if (File.Exists(path)) before[name] = File.ReadAllBytes(path); }
        }
        public static void End()
        {
            if (before == null) return;
            foreach (KeyValuePair<string, byte[]> item in before)
            {
                string path = Path.Combine(MscProfile.SaveDirectory, item.Key);
                if (item.Value.Length > 0 && (!File.Exists(path) || new FileInfo(path).Length == 0))
                { AtomicFile.Write(path, item.Value); Log.Warn("MSC preserved previous nonempty save: " + item.Key); }
            }
            before = null;
        }
        public static void Rollback()
        {
            if (before == null) return;
            foreach (KeyValuePair<string, byte[]> item in before) AtomicFile.Write(Path.Combine(MscProfile.SaveDirectory, item.Key), item.Value);
            before = null;
        }
    }
}
