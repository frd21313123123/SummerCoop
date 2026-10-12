using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;

namespace SummerCoop.Game
{
    public static class NativeAssembly
    {
        // Wheel, trim and other variant slots have HandChild2/3/... too.
        public static bool Accepts(IEnumerable<FsmString> variables, string partName)
        {
            foreach (FsmString variable in variables)
                if (variable.Name.StartsWith("HandChild", StringComparison.Ordinal) && variable.Value == partName) return true;
            return false;
        }
    }
}
