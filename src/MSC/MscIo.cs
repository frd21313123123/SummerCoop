using System;
using SummerCoop.Core;

namespace MWCoop
{
    public static class MscIo
    {
        static BackgroundIo worker = new BackgroundIo("SummerCoop file IO", 256);
        static bool stopped;
        public static void Start() { if (stopped) { worker = new BackgroundIo("SummerCoop file IO", 256); stopped = false; } }
        static float nextError;
        public static int Pending { get { return worker.Pending; } }
        public static double OldestMilliseconds { get { return worker.OldestMilliseconds; } }
        public static bool TryQueue(Action operation) { return worker.TryPost(operation); }
        public static bool Shutdown() { bool done = worker.Complete(5000); if (done) stopped = true; return done; }
        public static void Update()
        {
            if (UnityEngine.Time.realtimeSinceStartup < nextError) return;
            nextError = UnityEngine.Time.realtimeSinceStartup + 10;
            Exception error = worker.TakeError();
            if (error != null) UnityEngine.Debug.LogWarning("SummerCoop background IO: " + error);
        }
    }
}
