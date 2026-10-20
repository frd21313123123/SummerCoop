using System;
using System.Reflection;
using Harmony;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MWCoop
{
    // Only isolated, explicitly requested automation may change desktop/audio
    // behaviour. The normal launcher and multiplayer session never enable this.
    public sealed class MscTestEnvironment : MonoBehaviour
    {
        static bool installed;
        public static int SuppressedCursorActions { get; private set; }
        public static bool Active { get { return installed; } }
        // Unity 5.0.0f4's deprecated Screen.lockCursor getter is inverted.
        // Cursor.lockState is the native state actually used by its setter.
        public static bool CursorUnlocked { get { return Cursor.lockState == CursorLockMode.None; } }
        static bool MusicClock { get { return MscRuntime.Argument("--summercoop-test-summerfixes").Length > 0; } }
        public static bool AudioMuted { get { return AudioListener.volume == 0 && (MusicClock || AudioListener.pause); } }

        public static void Install()
        {
            if (installed || MscRuntime.Argument("--summercoop-test-report").Length == 0) return;
            // Patch the actions before loading GAME: freeing the cursor only at
            // the end of a frame still lets a native action recenter it first.
            var harmony = HarmonyInstance.Create("SummerCoop.TestDesktop");
            var prefix = new HarmonyMethod(typeof(MscTestEnvironment).GetMethod("FreeCursorAction", BindingFlags.Static | BindingFlags.NonPublic));
            foreach (Type type in new[] { typeof(MSCLoader.Mod).Assembly.GetType("MSCLoader.SetMouseCursorFix", true), typeof(HutongGames.PlayMaker.Actions.SetMouseCursor), typeof(HutongGames.PlayMaker.Actions.SetCursorMode) })
                harmony.Patch(type.GetMethod("OnEnter", BindingFlags.Instance | BindingFlags.Public), prefix, null);
            installed = true;
            Enforce();
            var guard = new GameObject("SummerCoop test desktop guard");
            UnityEngine.Object.DontDestroyOnLoad(guard);
            guard.AddComponent<MscTestEnvironment>();
        }
        static bool FreeCursorAction(FsmStateAction __instance)
        {
            SuppressedCursorActions++;
            Enforce();
            __instance.Finish();
            return false;
        }
        static void Enforce()
        {
            Screen.lockCursor = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            AudioListener.pause = !MusicClock;
            AudioListener.volume = 0;
            Application.runInBackground = true;
        }
        void Awake() { Enforce(); }
        void Update() { Enforce(); }
        void LateUpdate() { Enforce(); }
        void OnLevelWasLoaded(int level) { Enforce(); }
    }
}
