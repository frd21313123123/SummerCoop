using System;
using System.IO;
using SummerCoop.Setup;

namespace SummerCoop.Launcher
{
    // The launcher is the only setup tool: the mod package is embedded in its
    // EXE and extracted once per process. The interface, the automatic update
    // before a launch and the command line all install from this one verified
    // copy, so the payload is released only when the launcher exits.
    internal sealed class ModPayload : IDisposable
    {
        private static readonly object gate = new object();
        private static ModPayload shared;
        private readonly string directory;
        internal readonly Payload Value;

        private ModPayload()
        {
            directory = SafePath.Under(Path.GetTempPath(), "SummerCoop-" + Guid.NewGuid().ToString("N"));
            SafePath.NoLinks(directory);
            Directory.CreateDirectory(directory);
            try { Value = Payload.Extract(directory); }
            catch { Dispose(); throw; }
        }

        internal static ModPayload Shared()
        {
            lock (gate)
            {
                if (shared == null) shared = new ModPayload();
                return shared;
            }
        }

        internal static void ReleaseShared()
        {
            ModPayload payload;
            lock (gate) { payload = shared; shared = null; }
            if (payload != null) payload.Dispose();
        }

        public void Dispose()
        {
            // Only remove our verified unique temporary directory; never the
            // selected game or profile.
            if (!Directory.Exists(directory)) return;
            SafePath.NoLinks(directory);
            foreach (string child in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories)) SafePath.NoLinks(child);
            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories)) SafePath.NoLinks(file);
            Directory.Delete(directory, true);
        }
    }
}
