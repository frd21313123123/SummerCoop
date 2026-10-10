using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SummerCoop.Game
{
    public static class NativeSave
    {
        public static void Capture(WorldRegistry registry)
        {
            if (registry == null) return;
            NativeHooks.Saving = true;
            try
            {
                foreach (FsmBinding b in registry.Fsms.Values)
                {
                    if (b.Component == null) continue;
                    HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
                    foreach (FsmTransition transition in b.Component.FsmGlobalTransitions)
                    {
                        if (transition.EventName != "SAVEGAME") continue;
                        FsmState state = b.Component.Fsm.GetState(transition.ToState);
                        while (state != null && visited.Add(state.Name))
                        {
                            foreach (FsmStateAction a in state.Actions)
                            {
                                string name = a.GetType().Name;
                                if (name.StartsWith("Save", StringComparison.Ordinal) || name == "ArrayListEasySave" || name.StartsWith("Get", StringComparison.Ordinal) || name == "BuildString" || name == "ConvertIntToString") a.OnEnter();
                            }
                            FsmState next = null;
                            foreach (FsmTransition t in state.Transitions) if (t.EventName == "FINISHED" && t.ToState.IndexOf("save", StringComparison.OrdinalIgnoreCase) >= 0) next = b.Component.Fsm.GetState(t.ToState);
                            state = next;
                        }
                    }
                }
                foreach (ES2Auto mesh in UnityEngine.Object.FindObjectsOfType<ES2Auto>()) if (!string.IsNullOrEmpty(mesh.saveFile) && !string.IsNullOrEmpty(mesh.uniqueTag)) mesh.Save();
            }
            finally { NativeHooks.Saving = false; }
        }
        public static void Capture(WorldRegistry registry, string directory)
        {
            Dictionary<string, byte[]> previous = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (string name in SaveIsolation.NativeFiles) { string path = Path.Combine(directory, name); if (File.Exists(path)) previous[name] = File.ReadAllBytes(path); }
            try
            {
                Capture(registry);
                foreach (string name in SaveIsolation.NativeFiles)
                {
                    string path = Path.Combine(directory, name); byte[] before;
                    if (File.Exists(path) && new FileInfo(path).Length == 0 && previous.TryGetValue(name, out before) && before.Length > 0)
                    {
                        SummerCoop.Core.AtomicFile.Write(path, before);
                        Debug.LogWarning("SummerCoop preserved previous nonempty native file: " + name);
                    }
                }
            }
            catch
            {
                foreach (var file in previous) SummerCoop.Core.AtomicFile.Write(Path.Combine(directory, file.Key), file.Value);
                throw;
            }
        }
    }
}
