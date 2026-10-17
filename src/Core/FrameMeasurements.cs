using System;
using System.Globalization;
using System.IO;

namespace SummerCoop.Core
{
    // Bounded, allocation-free while recording. Statistics/export happen after recording.
    public sealed class FrameMeasurements
    {
        public struct Sample
        {
            public double Milliseconds, ModMilliseconds;
            public int Collections, Reliable, Waiting, Reordered;
        }
        readonly Sample[] samples;
        public int Count { get; private set; }
        public bool Overflow { get; private set; }
        public FrameMeasurements(int capacity)
        {
            if (capacity < 1 || capacity > 2000000) throw new ArgumentOutOfRangeException("capacity");
            samples = new Sample[capacity];
        }
        public void Reset() { Count = 0; Overflow = false; }
        public void Add(double milliseconds, double modMilliseconds, int collections, int reliable, int waiting, int reordered)
        {
            if (milliseconds <= 0 || double.IsNaN(milliseconds) || double.IsInfinity(milliseconds)) return;
            if (Count == samples.Length) { Overflow = true; return; }
            samples[Count++] = new Sample { Milliseconds = milliseconds, ModMilliseconds = modMilliseconds,
                Collections = Math.Max(0, collections), Reliable = reliable, Waiting = waiting, Reordered = reordered };
        }
        public Sample At(int index) { if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException("index"); return samples[index]; }
        public double ElapsedMilliseconds { get { double sum = 0; for (int i = 0; i < Count; i++) sum += samples[i].Milliseconds; return sum; } }
        public double AverageFps { get { double elapsed = ElapsedMilliseconds; return elapsed > 0 ? Count * 1000.0 / elapsed : 0; } }
        public double Percentile(double fraction, bool mod)
        {
            if (fraction < 0 || fraction > 1 || double.IsNaN(fraction)) throw new ArgumentOutOfRangeException("fraction");
            if (Count == 0) return 0;
            double[] sorted = new double[Count];
            for (int i = 0; i < Count; i++) sorted[i] = mod ? samples[i].ModMilliseconds : samples[i].Milliseconds;
            Array.Sort(sorted);
            return sorted[Math.Max(0, (int)Math.Ceiling(fraction * Count) - 1)];
        }
        public int Above(double milliseconds) { int count = 0; for (int i = 0; i < Count; i++) if (samples[i].Milliseconds > milliseconds) count++; return count; }
        public int Collections { get { int count = 0; for (int i = 0; i < Count; i++) count += samples[i].Collections; return count; } }
        public static string Number(double value) { return value.ToString("0.######", CultureInfo.InvariantCulture); }
        public static string Quote(string value)
        {
            var result = new System.Text.StringBuilder("\"");
            foreach (char c in value ?? "")
            {
                if (c == '\\' || c == '"') result.Append('\\').Append(c);
                else if (c < 32) result.Append("\\u").Append(((int)c).ToString("x4"));
                else result.Append(c);
            }
            return result.Append('"').ToString();
        }
        public void WriteCsv(TextWriter writer)
        {
            writer.WriteLine("frame,elapsed_ms,frame_ms,mod_ms,gc_collections,reliable_pending,reliable_waiting,reordered_pending");
            double elapsed = 0;
            for (int i = 0; i < Count; i++)
            {
                Sample s = samples[i]; elapsed += s.Milliseconds;
                writer.WriteLine(i + "," + Number(elapsed) + "," + Number(s.Milliseconds) + "," + Number(s.ModMilliseconds)
                    + "," + s.Collections + "," + s.Reliable + "," + s.Waiting + "," + s.Reordered);
            }
        }
        // An object fragment, so callers can add environment and module evidence.
        public void WriteJson(TextWriter writer)
        {
            writer.Write("\"frames\":" + Count + ",\"overflow\":" + (Overflow ? "true" : "false")
                + ",\"seconds\":" + Number(ElapsedMilliseconds / 1000) + ",\"averageFps\":" + Number(AverageFps)
                + ",\"p50Ms\":" + Number(Percentile(.5, false)) + ",\"p95Ms\":" + Number(Percentile(.95, false))
                + ",\"p99Ms\":" + Number(Percentile(.99, false)) + ",\"maxMs\":" + Number(Percentile(1, false))
                + ",\"modP95Ms\":" + Number(Percentile(.95, true)) + ",\"modMaxMs\":" + Number(Percentile(1, true))
                + ",\"above33Ms\":" + Above(33) + ",\"above50Ms\":" + Above(50) + ",\"above100Ms\":" + Above(100)
                + ",\"gcCollections\":" + Collections);
        }
    }
}
