using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using Harmony;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerCoop.Core;
using UnityEngine;

// Built as a separate MSCLoader mod. Does not reference/load SummerCoop or MWCoop.
namespace SummerCoop.PerformanceOnly
{
    public sealed class StandaloneFrameMod : Mod
    {
        public override string ID { get { return "SummerCoopPerformanceProbe"; } }
        public override string Name { get { return "Passive performance probe"; } }
        public override string Author { get { return "SummerCoop"; } }
        public override string Version { get { return "1"; } }
        public override void ModSetup()
        {
            if (FrameProbe.Argument("--summercoop-profile").Length == 0) throw new InvalidOperationException("Probe requires an isolated profile");
            var harmony = HarmonyInstance.Create("SummerCoop.PassiveProbe.Cursor");
            var prefix = new HarmonyMethod(typeof(FrameProbe).GetMethod("CursorAction", BindingFlags.Static | BindingFlags.Public));
            foreach (Type type in new[] { typeof(Mod).Assembly.GetType("MSCLoader.SetMouseCursorFix", true), typeof(HutongGames.PlayMaker.Actions.SetMouseCursor), typeof(HutongGames.PlayMaker.Actions.SetCursorMode) })
                harmony.Patch(type.GetMethod("OnEnter", BindingFlags.Instance | BindingFlags.Public), prefix, null);
            SetupFunction(Setup.OnMenuLoad, Menu);
        }
        void Menu()
        {
            if (FrameProbe.Instance != null) return;
            var root = new GameObject("Passive frame probe"); UnityEngine.Object.DontDestroyOnLoad(root); root.AddComponent<FrameProbe>();
        }
    }
    public sealed class FrameProbe : MonoBehaviour
    {
        public static FrameProbe Instance;
        static readonly string[] arguments = Environment.GetCommandLineArgs();
        public static string Argument(string key) { int i = Array.IndexOf(arguments, key); return i >= 0 && i + 1 < arguments.Length ? arguments[i + 1] : ""; }
        static int Integer(string key, int fallback, int min, int max) { int value; return int.TryParse(Argument(key), out value) ? Math.Max(min, Math.Min(max, value)) : fallback; }
        readonly Stopwatch clock = new Stopwatch();
        readonly WaitForEndOfFrame end = new WaitForEndOfFrame();
        FrameMeasurements frames;
        float began, ready = -1, measured = -1;
        int warmup, duration, lastGc, oldLimit, oldVsync;
        double previous;
        long memoryStart;
        bool loading, recording, finished;
        public static bool CursorAction(FsmStateAction __instance) { Enforce(); __instance.Finish(); return false; }
        static void Enforce() { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; AudioListener.pause = true; AudioListener.volume = 0; Application.runInBackground = true; }
        void Start()
        {
            Instance = this; Enforce();
            string profile = Path.GetFullPath(Argument("--summercoop-profile")); Directory.CreateDirectory(profile);
            typeof(ES2).Assembly.GetType("ES2FilenameData", true).GetField("persistentDataPath", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).SetValue(null, profile + Path.DirectorySeparatorChar);
            warmup = Integer("--summercoop-test-warmup", 60, 0, 600); duration = Integer("--summercoop-test-duration", 600, 10, 3600);
            frames = new FrameMeasurements(Math.Min(2000000, duration * 500 + 1));
            oldLimit = Application.targetFrameRate; oldVsync = QualitySettings.vSyncCount; Application.targetFrameRate = -1; QualitySettings.vSyncCount = 0;
            began = Time.realtimeSinceStartup; clock.Start(); StartCoroutine(Measure());
        }
        void Update()
        {
            Enforce();
            if (loading || Application.loadedLevelName != "MainMenu" || Time.realtimeSinceStartup - began < 1) return;
            FsmString slot = FsmVariables.GlobalVariables.FindFsmString("SaveSlot"); if (slot != null) slot.Value = "defaultES2File.txt";
            foreach (string key in new[] { "OptionsPermaDeath", "PlayerPermaDeath", "PlayerInMenu", "PlayerStop", "PlayerSleeps", "PlayerSeated", "PlayerCarControl", "PlayerVanControl", "PlayerMuscleControl" })
            { FsmBool value = FsmVariables.GlobalVariables.FindFsmBool(key); if (value != null) value.Value = false; }
            FsmBool imported = FsmVariables.GlobalVariables.FindFsmBool("SongImported"); if (imported != null) imported.Value = true;
            loading = true; Application.LoadLevel("GAME");
        }
        void LateUpdate() { Enforce(); }
        void OnLevelWasLoaded(int level)
        {
            if (finished) return;
            Application.targetFrameRate = -1; QualitySettings.vSyncCount = 0;
        }
        IEnumerator Measure()
        {
            while (!finished)
            {
                yield return end;
                float now = Time.realtimeSinceStartup;
                if (Application.loadedLevelName != "GAME")
                {
                    if (recording || now - began > 600) { Finish(false, "Scene changed or GAME load timed out"); continue; }
                    ready = -1; continue;
                }
                if (ready < 0) ready = now;
                if (now - ready < warmup) continue;
                double current = clock.Elapsed.TotalMilliseconds; int gc = GC.CollectionCount(0);
                if (!recording) { recording = true; measured = now; previous = current; lastGc = gc; memoryStart = GC.GetTotalMemory(false); continue; }
                frames.Add(current - previous, 0, gc - lastGc, 0, 0, 0); previous = current; lastGc = gc;
                if (frames.Overflow) Finish(false, "Frame sample capacity exceeded");
                else if (now - measured >= duration) Finish(true, "");
            }
        }
        void Finish(bool valid, string failure)
        {
            finished = true;
            string output = Argument("--summercoop-test-report");
            string prefix = Path.Combine(Path.GetDirectoryName(output), Path.GetFileNameWithoutExtension(output));
            using (var csv = new StreamWriter(prefix + "-frames.csv", false, new UTF8Encoding(false))) frames.WriteCsv(csv);
            using (var json = new StreamWriter(prefix + ".json", false, new UTF8Encoding(false)))
            {
                json.Write("{\"valid\":" + (valid ? "true" : "false") + ",\"failure\":" + FrameMeasurements.Quote(failure)
                    + ",\"engine\":\"MSC + MSCLoader + passive probe\",\"role\":\"vanilla\",\"players\":1,\"width\":" + Screen.width + ",\"height\":" + Screen.height
                    + ",\"frameLimit\":" + Application.targetFrameRate + ",\"vsync\":" + QualitySettings.vSyncCount + ",\"quality\":" + QualitySettings.GetQualityLevel()
                    + ",\"shadowDistance\":" + FrameMeasurements.Number(QualitySettings.shadowDistance) + ",\"lodBias\":" + FrameMeasurements.Number(QualitySettings.lodBias)
                    + ",\"warmupSeconds\":" + warmup + ",\"requestedSeconds\":" + duration
                    + ",\"processor\":" + FrameMeasurements.Quote(SystemInfo.processorType) + ",\"graphics\":" + FrameMeasurements.Quote(SystemInfo.graphicsDeviceName)
                    + ",\"memoryStartBytes\":" + memoryStart + ",\"memoryEndBytes\":" + GC.GetTotalMemory(false) + ",");
                frames.WriteJson(json); json.Write("}");
            }
            File.WriteAllLines(output, new[] { "Performance=" + (valid ? "COMPLETE" : "FAILED"), "Failure=" + failure, "AverageFps=" + FrameMeasurements.Number(frames.AverageFps) });
            Application.targetFrameRate = oldLimit; QualitySettings.vSyncCount = oldVsync; Application.Quit();
        }
        void OnDestroy() { Application.targetFrameRate = oldLimit; QualitySettings.vSyncCount = oldVsync; }
    }
}
