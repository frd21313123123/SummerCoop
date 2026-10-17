using System.Diagnostics;
using UnityEngine;

namespace MWCoop
{
    // Shared main-thread budget for background discovery, never for gameplay messages.
    public static class MscMaintenance
    {
        static int frame = -1;
        static long spent;
        public static bool Available
        {
            get
            {
                if (frame != Time.frameCount) { frame = Time.frameCount; spent = 0; }
                return spent < Stopwatch.Frequency / 1000;
            }
        }
        public static long Begin() { return Stopwatch.GetTimestamp(); }
        public static void End(long began) { spent += Stopwatch.GetTimestamp() - began; }
        public static double RemainingMilliseconds { get { bool available = Available; return available ? 1.0 - spent * 1000.0 / Stopwatch.Frequency : 0; } }
    }
}
