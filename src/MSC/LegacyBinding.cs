using HutongGames.PlayMaker;
using UnityEngine;

// Minimal boundary for the existing, tested native mount repair. The legacy runtime
// and its transport are deliberately not loaded alongside MWCoop.
namespace SummerCoop.Game
{
    public sealed class FsmBinding { public PlayMakerFSM Component; }
    public static class WorldRegistry
    {
        public static bool PersonalObject(GameObject go)
        {
            if (go == null) return false;
            for (Transform t = go.transform; t != null; t = t.parent)
                if (t.name == "PLAYER" || t.name == "FPSCamera" || t.name.StartsWith("SummerCoop")) return true;
            return false;
        }
    }
}
