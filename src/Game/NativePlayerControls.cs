using HutongGames.PlayMaker;
using SummerCoop.Core;
using UnityEngine;

namespace SummerCoop.Game
{
    public static class NativePlayerControls
    {
        public static bool Read(string name, out float value)
        {
            FsmFloat variable = FsmVariables.GlobalVariables.FindFsmFloat(name);
            value = variable == null ? 0 : variable.Value; return variable != null;
        }
        public static void SetNeed(string name, float value)
        {
            foreach (string need in PlayerControls.Needs) if (name == need)
            {
                FsmFloat variable = FsmVariables.GlobalVariables.FindFsmFloat(name);
                if (variable != null) variable.Value = PlayerControls.ClampPercent(value);
                return;
            }
        }
        public static void ClearNeeds() { foreach (string need in PlayerControls.Needs) SetNeed(need, 0); }
        public static int ClearEffects()
        {
            int count = 0;
            foreach (string name in PlayerControls.Effects)
            {
                FsmFloat variable = FsmVariables.GlobalVariables.FindFsmFloat(name);
                if (variable != null) { variable.Value = 0; count++; }
            }
            GameObject player = GameObject.Find("PLAYER");
            if (player != null) foreach (PlayMakerFSM fsm in player.GetComponentsInChildren<PlayMakerFSM>(true))
                if (fsm.FsmName == "Hangover") count += ClearHangoverVariables(fsm.FsmVariables);
            return count;
        }
        public static int ClearHangoverVariables(FsmVariables variables)
        {
            int count = 0;
            // The spelling is the native game's spelling, including "Strenght".
            foreach (string name in new[] { "HangoverStrenght", "HangoverStrenghtMinus", "TimeLeft", "X", "Y", "Z" })
            {
                FsmFloat value = variables.FindFsmFloat(name); if (value != null) { value.Value = 0; count++; }
            }
            return count;
        }
    }
}
