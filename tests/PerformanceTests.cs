using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using SummerCoop.Core;

internal static class PerformanceTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run(Action<string, Action> test)
    {
        test("Frame measurements use elapsed time, nearest-rank tails and collection counts", delegate {
            var frames = new FrameMeasurements(4);
            frames.Add(10, 1, 0, 1, 2, 3); frames.Add(20, 2, 1, 2, 0, 0); frames.Add(30, 3, 2, 0, 0, 0);
            Check(frames.AverageFps == 50 && frames.Percentile(.5, false) == 20 && frames.Percentile(.95, false) == 30, "Biased FPS or percentile");
            Check(frames.Percentile(.95, true) == 3 && frames.Collections == 3 && frames.At(0).Waiting == 2, "Mod/queue/GC samples lost");
        });
        test("Frame measurements reset at scene/warmup boundaries and expose overflow", delegate {
            var frames = new FrameMeasurements(2);
            frames.Add(double.NaN, 0, 0, 0, 0, 0); frames.Add(0, 0, 0, 0, 0, 0);
            frames.Add(101, 3, 2, 0, 0, 0); frames.Add(40, 2, 0, 0, 0, 0); frames.Add(10, 1, 0, 0, 0, 0);
            Check(frames.Count == 2 && frames.Overflow && frames.Above(100) == 1 && frames.Above(33) == 2, "Unbounded or hidden sample loss");
            frames.Reset(); frames.Add(5, 1, 0, 0, 0, 0);
            Check(frames.Count == 1 && !frames.Overflow && frames.Collections == 0 && frames.Percentile(1, false) == 5, "Old scene survived reset");
        });
        test("Performance exports retain invariant numbers and JSON escaping", delegate {
            CultureInfo previous = Thread.CurrentThread.CurrentCulture;
            try {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("ru-RU");
                var frames = new FrameMeasurements(2); frames.Add(12.5, .5, 0, 0, 0, 0);
                var csv = new StringWriter(); frames.WriteCsv(csv); var json = new StringWriter(); frames.WriteJson(json);
                Check(csv.ToString().Contains("0,12.5,12.5,0.5") && json.ToString().Contains("\"p95Ms\":12.5"), "Locale corrupted exports");
                Check(FrameMeasurements.Quote("a\"\n\\") == "\"a\\\"\\u000a\\\\\"", "Unescaped JSON metadata");
            } finally { Thread.CurrentThread.CurrentCulture = previous; }
        });
        test("Background IO is bounded, ordered and drained before shutdown", delegate {
            var entered = new ManualResetEvent(false); var release = new ManualResetEvent(false);
            var seen = new List<int>(); var worker = new BackgroundIo("IO test", 2);
            try {
                Check(worker.TryPost(delegate { entered.Set(); if (!release.WaitOne(5000)) throw new Exception("Test timed out"); seen.Add(0); }), "Initial IO refused");
                Check(entered.WaitOne(5000), "Worker not started");
                Check(worker.TryPost(delegate { seen.Add(1); }) && worker.TryPost(delegate { seen.Add(2); }), "Queue filled early");
                Check(!worker.TryPost(delegate { seen.Add(99); }), "Queue exceeded bound");
                release.Set(); Check(worker.Complete(5000), "IO did not drain");
                Check(seen.Count == 3 && seen[0] == 0 && seen[1] == 1 && seen[2] == 2, "IO order changed");
                Check(!worker.TryPost(delegate { }), "Shutdown accepted new IO");
            } finally { release.Set(); worker.Complete(5000); entered.Close(); release.Close(); }
        });
        test("A failed background write does not discard later operations", delegate {
            bool completed = false; var worker = new BackgroundIo("IO failure test", 4);
            worker.TryPost(delegate { throw new IOException("test disk failure"); });
            worker.TryPost(delegate { completed = true; });
            Check(worker.Complete(5000) && completed && worker.TakeError() is IOException, "IO failure stopped the queue or disappeared");
        });
    }
}
