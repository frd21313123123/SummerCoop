using System;
using System.IO;
using MSCLoader;
using UnityEngine;
using HutongGames.PlayMaker;

namespace SummerCoop.Game
{
    public sealed class SummerCoopMod : Mod
    {
        public override string ID { get { return "SummerCoop"; } }
        public override string Name { get { return "SummerCoop"; } }
        public override string Author { get { return "SummerCoop contributors"; } }
        public override string Version { get { return Core.Protocol.ModVersion; } }
        public override void ModSetup()
        {
            SetupFunction(Setup.OnMenuLoad, OnMenu);
            SetupFunction(Setup.PreLoad, Pre);
            SetupFunction(Setup.OnLoad, Loaded);
        }
        private void OnMenu()
        {
            ModConsole.Print("SummerCoop " + Version);
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--summercoop-probe") >= 0)
            {
                GameObject go = new GameObject("SummerCoop.Probe");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<GameProbe>();
            }
            else { CoopRuntime.Ensure(); CoopRuntime.Instance.Menu(); }
        }
        private void Pre() { if (CoopRuntime.Instance != null) CoopRuntime.Instance.PreLoad(); }
        private void Loaded() { if (CoopRuntime.Instance != null) CoopRuntime.Instance.Loaded(); }
    }

    public sealed class GameProbe : MonoBehaviour
    {
        private float began;
        private bool loading, dumped;
        private void Start() { began = Time.realtimeSinceStartup; }
        private void Update()
        {
            if (!loading && Time.realtimeSinceStartup - began > 2)
            {
                loading = true;
                Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, "probe-save"));
                Type pathType = typeof(ES2).Assembly.GetType("ES2FilenameData", true);
                System.Reflection.FieldInfo pathField = pathType.GetField("persistentDataPath", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (pathField == null) throw new InvalidOperationException("Save isolation hook missing");
                pathField.SetValue(null, Path.Combine(Environment.CurrentDirectory, "probe-save") + Path.DirectorySeparatorChar);
                Application.LoadLevel("GAME");
            }
            if (loading && !dumped && Application.loadedLevelName == "GAME" && Time.realtimeSinceStartup - began > 45)
            {
                dumped = true;
                string file = Path.Combine(Environment.CurrentDirectory, "probe-fsms.txt");
                using (StreamWriter w = new StreamWriter(file))
                {
                    foreach (PlayMakerFSM f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                    {
                        if (!f.Fsm.Initialized) continue;
                        w.WriteLine("FSM\t" + PathOf(f.transform) + "\t" + f.FsmName + "\t" + f.ActiveStateName);
                        foreach (FsmState s in f.FsmStates)
                        {
                            w.Write("STATE\t" + s.Name + "\t");
                            foreach (FsmStateAction a in s.Actions) w.Write(a.GetType().FullName + ",");
                            w.WriteLine();
                            foreach (FsmTransition t in s.Transitions) w.WriteLine("TRANS\t" + t.EventName + "\t" + t.ToState);
                            foreach (FsmStateAction a in s.Actions)
                            {
                                foreach (System.Reflection.FieldInfo field in a.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                                {
                                    object v = field.GetValue(a);
                                    if (v is NamedVariable || v is FsmEvent || v is FsmOwnerDefault || v is string || v is bool || v is int || v is float) w.WriteLine("ACTION\t" + a.GetType().Name + "\t" + field.Name + "\t" + (v == null ? "" : v.ToString()));
                                }
                            }
                        }
                        foreach (NamedVariable v in f.FsmVariables.GetAllNamedVariables()) w.WriteLine("VAR\t" + v.GetType().Name + "\t" + v.Name + "\t" + v.ToString());
                    }
                    w.WriteLine("GLOBALS");
                    foreach (NamedVariable v in FsmVariables.GlobalVariables.GetAllNamedVariables()) if (!v.Name.StartsWith("WWW", StringComparison.Ordinal)) w.WriteLine("VAR\t" + v.GetType().Name + "\t" + v.Name + "\t" + v.ToString());
                }
                Application.Quit();
            }
        }
        public static string PathOf(Transform t)
        {
            string result = t.name + "[" + t.GetSiblingIndex() + "]";
            while (t.parent != null) { t = t.parent; result = t.name + "[" + t.GetSiblingIndex() + "]/" + result; }
            return result;
        }
    }
}
