using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using MWCoop.Net;
using SummerCoop.Core;
using UnityEngine;
using Session = MWCoop.Net.Session;

namespace MWCoop
{
    // Passive end-of-frame collection; no functional probes or scene mutations.
    public sealed class MscPerformanceProbe : MonoBehaviour
    {
        public static bool Requested { get { return MscRuntime.Argument("--summercoop-test-performance") == "yes"; } }
        static MscPerformanceProbe instance;
        sealed class Module { public double Total, Maximum; public int Calls; }
        readonly Dictionary<string, Module> modules = new Dictionary<string, Module>();
        readonly Stopwatch timer = new Stopwatch();
        readonly WaitForEndOfFrame end = new WaitForEndOfFrame();
        FrameMeasurements measurements;
        int expected, warmup, duration, oldLimit, oldVsync, lastGc, peakReliable, peakWaiting, peakReordered;
        float began, readyAt = -1, measuredAt = -1;
        double previous;
        long memoryStart, memoryEnd;
        long heap;
        float nextHeap;
        int peakIo, peakSnapshots;
        double oldestDiscovery, oldestIo;
        struct Health { public double Seconds, DiscoveryAge, IoAge; public long Heap; public int Discovery, Snapshots, IoPending; }
        readonly List<Health> health = new List<Health>();
        bool recording, finished;
        string output, failure = "";
        public static bool Recording { get { return instance != null && instance.recording; } }
        public static bool PartsProfiling { get; private set; }
        public static void ModuleTime(string name, double milliseconds)
        {
            if (!Recording || name.StartsWith("[", StringComparison.Ordinal) && !name.StartsWith("[MSC parts ", StringComparison.Ordinal)) return;
            Module item;
            if (!instance.modules.TryGetValue(name, out item)) instance.modules[name] = item = new Module();
            item.Total += milliseconds; item.Maximum = Math.Max(item.Maximum, milliseconds); item.Calls++;
        }
        static int Integer(string name, int fallback, int minimum, int maximum)
        {
            int value;
            return int.TryParse(MscRuntime.Argument(name), out value) ? Math.Max(minimum, Math.Min(maximum, value)) : fallback;
        }
        void Start()
        {
            instance = this;
            PartsProfiling = MscRuntime.Argument("--summercoop-test-parts-profile") == "yes";
            output = MscRuntime.Argument("--summercoop-test-report");
            warmup = Integer("--summercoop-test-warmup", 60, 0, 600);
            duration = Integer("--summercoop-test-duration", 600, 10, 3600);
            expected = Integer("--summercoop-test-players", 2, 1, 8);
            // 500 measured frames/s for the requested window; bounded even if uncapped FPS is higher.
            measurements = new FrameMeasurements(Math.Min(2000000, duration * 500 + 1));
            health.Capacity = duration / 10 + 2;
            oldLimit = Application.targetFrameRate; oldVsync = QualitySettings.vSyncCount;
            Application.targetFrameRate = -1; QualitySettings.vSyncCount = 0;
            began = Time.realtimeSinceStartup; timer.Start();
            StartCoroutine(Measure());
        }
        bool Ready()
        {
            if (!PlayerSync.InGame || Session.Players.Count < expected) return false;
            foreach (PlayerInfo player in Session.Players.Values) if (player.Level != 1) return false;
            return true;
        }
        IEnumerator Measure()
        {
            while (!finished) { yield return end; Tick(); }
        }
        void Tick()
        {
            float now = Time.realtimeSinceStartup;
            double current = timer.Elapsed.TotalMilliseconds;
            int gc = GC.CollectionCount(0);
            if (!Ready())
            {
                if (recording) { failure = "Participant disconnected or scene changed during measurement"; Finish(false); return; }
                readyAt = -1;
                if (now - began > 600 + warmup) { failure = "Timed out waiting for GAME participants"; Finish(false); }
                return;
            }
            if (readyAt < 0) readyAt = now;
            if (now - readyAt < warmup) return;
            if (!recording)
            {
                measurements.Reset(); modules.Clear(); measuredAt = now;
                memoryStart = heap = GC.GetTotalMemory(false); nextHeap = now + 10; recording = true; previous = current; lastGc = gc;
                return; // Never include the scene activation / warm-up boundary frame.
            }
            int reliable = 0, waiting = 0, reordered = 0;
            if (Session.T != null) foreach (Peer peer in Session.T.Peers)
            { reliable += peer.unacked.Count; waiting += peer.waiting.Count; reordered += peer.early.Count; }
            peakReliable = Math.Max(peakReliable, reliable); peakWaiting = Math.Max(peakWaiting, waiting); peakReordered = Math.Max(peakReordered, reordered);
            int io = MscIo.Pending, snapshots = Props.SnapshotPending + WorldFsms.SnapshotPending;
            double ioAge = MscIo.OldestMilliseconds, discoveryAge = Math.Max(MscParts.DiscoveryAge, Props.DiscoveryAge);
            if (now >= nextHeap)
            {
                nextHeap = now + 10; heap = GC.GetTotalMemory(false);
                health.Add(new Health { Seconds = now - measuredAt, Heap = heap, Discovery = MscParts.DiscoveryPending + Props.DiscoveryPending,
                    DiscoveryAge = discoveryAge, Snapshots = snapshots, IoPending = io, IoAge = ioAge });
            }
            peakIo = Math.Max(peakIo, io); peakSnapshots = Math.Max(peakSnapshots, snapshots);
            oldestIo = Math.Max(oldestIo, ioAge); oldestDiscovery = Math.Max(oldestDiscovery, discoveryAge);
            measurements.Add(current - previous, Perf.FrameMilliseconds, gc - lastGc, reliable, waiting, reordered);
            previous = current; lastGc = gc;
            if (measurements.Overflow) { failure = "Frame sample capacity exceeded"; Finish(false); }
            else if (now - measuredAt >= duration) Finish(true);
        }
        void Finish(bool complete)
        {
            if (finished) return;
            finished = true; recording = false; memoryEnd = GC.GetTotalMemory(false);
            Application.targetFrameRate = oldLimit; QualitySettings.vSyncCount = oldVsync;
            string prefix = Path.Combine(Path.GetDirectoryName(output) ?? "", Path.GetFileNameWithoutExtension(output));
            try
            {
                try { File.WriteAllLines(prefix + "-integrity.tsv", MscParts.IntegrityLines()); }
                catch (Exception e) { Log.Warn("End-of-test integrity inspection failed: " + e); }
                using (var csv = new StreamWriter(prefix + "-frames.csv", false, new UTF8Encoding(false))) measurements.WriteCsv(csv);
                using (var csv = new StreamWriter(prefix + "-health.csv", false, new UTF8Encoding(false)))
                {
                    csv.WriteLine("elapsed_s,heap_bytes,discovery_pending,discovery_age_s,snapshot_pending,io_pending,io_age_ms");
                    foreach (Health sample in health) csv.WriteLine(FrameMeasurements.Number(sample.Seconds) + "," + sample.Heap + "," + sample.Discovery
                        + "," + FrameMeasurements.Number(sample.DiscoveryAge) + "," + sample.Snapshots + "," + sample.IoPending + "," + FrameMeasurements.Number(sample.IoAge));
                }
                using (var json = new StreamWriter(prefix + ".json", false, new UTF8Encoding(false)))
                {
                    json.Write("{\"valid\":" + (complete && !measurements.Overflow ? "true" : "false") + ",\"failure\":" + FrameMeasurements.Quote(failure)
                        + ",\"engine\":\"MWCoop MSC\",\"role\":" + FrameMeasurements.Quote(Session.IsHost ? "host" : "client")
                        + ",\"modVersion\":" + FrameMeasurements.Quote(Version.Text) + ",\"netVersion\":" + Session.NetVersion
                        + ",\"buildIdentity\":" + FrameMeasurements.Quote(MscRuntime.BuildIdentity)
                        + ",\"players\":" + Session.Players.Count + ",\"expectedPlayers\":" + expected
                        + ",\"warmupSeconds\":" + warmup + ",\"requestedSeconds\":" + duration
                        + ",\"width\":" + Screen.width + ",\"height\":" + Screen.height
                        + ",\"processor\":" + FrameMeasurements.Quote(SystemInfo.processorType)
                        + ",\"graphics\":" + FrameMeasurements.Quote(SystemInfo.graphicsDeviceName)
                        + ",\"memoryMb\":" + SystemInfo.systemMemorySize + ",\"unity\":" + FrameMeasurements.Quote(Application.unityVersion)
                        + ",\"memoryStartBytes\":" + memoryStart + ",\"memoryEndBytes\":" + memoryEnd
                        + ",\"reliablePeak\":" + peakReliable + ",\"waitingPeak\":" + peakWaiting + ",\"reorderedPeak\":" + peakReordered
                        + ",\"ioPeak\":" + peakIo + ",\"snapshotPeak\":" + peakSnapshots + ",\"discoveryOldestSeconds\":" + FrameMeasurements.Number(oldestDiscovery)
                        + ",\"ioOldestMs\":" + FrameMeasurements.Number(oldestIo) + ",\"droppedLogs\":" + Log.DroppedEntries
                        + ",\"partWorkload\":" + FrameMeasurements.Quote(MscParts.Workload)
                        + ",\"partsProfiling\":" + (PartsProfiling ? "true" : "false") + ",");
                    measurements.WriteJson(json);
                    json.Write(",\"modules\":{"); bool first = true;
                    foreach (var item in modules)
                    {
                        if (!first) json.Write(','); first = false;
                        json.Write(FrameMeasurements.Quote(item.Key) + ":{\"msPerFrame\":" + FrameMeasurements.Number(item.Value.Total / Math.Max(1, measurements.Count))
                            + ",\"maxMs\":" + FrameMeasurements.Number(item.Value.Maximum) + ",\"calls\":" + item.Value.Calls + "}");
                    }
                    json.Write("}}");
                }
                File.WriteAllLines(output, new[] { "Performance=" + (complete ? "COMPLETE" : "FAILED"), "Failure=" + failure,
                    "Players=" + Session.Players.Count, "Frames=" + measurements.Count, "AverageFps=" + FrameMeasurements.Number(measurements.AverageFps),
                    "FrameP95ms=" + FrameMeasurements.Number(measurements.Percentile(.95, false)), "FrameMaxms=" + FrameMeasurements.Number(measurements.Percentile(1, false)) });
            }
            catch (Exception e) { Log.Error("Performance export failed: " + e); }
            // A host gives guests time to finish/export before closing the transport.
            StartCoroutine(Quit());
        }
        IEnumerator Quit() { if (Session.IsHost) yield return new WaitForSeconds(12); Application.Quit(); }
        void OnDestroy()
        {
            if (!finished) { Application.targetFrameRate = oldLimit; QualitySettings.vSyncCount = oldVsync; }
            if (instance == this) instance = null;
        }
    }
}
