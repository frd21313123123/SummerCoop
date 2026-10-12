using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using SummerCoop.Core;

namespace SummerCoop.Game
{
    public sealed class NativeKeys
    {
        private readonly PersonalKeys keys;
        public NativeKeys(string root, string nativeDirectory, string world, string identity)
        {
            Dictionary<string, int> initial = new Dictionary<string, int>();
            foreach (string name in PersonalKeys.Names)
            {
                string tag = FileTag(nativeDirectory, name);
                initial[name] = ES2.Exists(tag) ? Math.Max(0, Math.Min(2, ES2.Load<int>(tag))) : (name == "PlayerKeyHome" || name == "PlayerKeySatsuma" || name == "PlayerKeyRuscko" ? 1 : 0);
            }
            keys = new PersonalKeys(Path.Combine(root, "personal-keys"), world, identity, initial);
            keys.Save();
            // Overlay only this participant's six key tags before the native database loads.
            if (File.Exists(Path.Combine(nativeDirectory, "defaultES2File.txt")))
                foreach (string name in PersonalKeys.Names) ES2.Save<int>(keys.Get(name), FileTag(nativeDirectory, name));
        }
        private static string FileTag(string directory, string name) { return Path.Combine(directory, "defaultES2File.txt") + "?tag=Key" + name.Substring("PlayerKey".Length); }
        public void Capture()
        {
            foreach (string name in PersonalKeys.Names)
            {
                FsmInt variable = FsmVariables.GlobalVariables.FindFsmInt(name);
                if (variable != null) keys.Set(name, Math.Max(0, Math.Min(2, variable.Value)));
            }
            keys.Save();
        }
        public void RestoreGlobals()
        {
            foreach (string name in PersonalKeys.Names)
            {
                FsmInt variable = FsmVariables.GlobalVariables.FindFsmInt(name);
                if (variable != null) variable.Value = keys.Get(name);
            }
        }
    }
}
