using HutongGames.PlayMaker;
namespace MWCoop
{
    public static class MscWeather
    {
        public static FsmBool Rain(PlayMakerFSM forecast)
        {
            if (forecast == null) return null;
            return forecast.FsmVariables.FindFsmBool("Raining") ?? forecast.FsmVariables.FindFsmBool("Rain") ?? forecast.FsmVariables.FindFsmBool("RainYes");
        }
        public static bool Raining(PlayMakerFSM forecast) { FsmBool value = Rain(forecast); return value != null && value.Value; }
        public static void Apply(PlayMakerFSM forecast, bool raining) { FsmBool value = Rain(forecast); if (value != null) value.Value = raining; }
        public static void ApplyPlayerRain(PlayMakerFSM rain, bool raining)
        {
            FsmBool value = rain != null ? rain.FsmVariables.FindFsmBool("RainYes") : null;
            if (value == null) return;
            value.Value = raining;
            // MSC's heavy-rain loop watches CloudYes, not RainYes. Explicitly
            // start its normal dry-weather fade when rain is switched off.
            string state = rain.ActiveStateName;
            if (!raining && rain.Fsm.Initialized && state != "No rain" && state != "Rain stops" && state != "Cloud off 2")
                Game.SetState(rain, "Rain stops");
        }
    }
}
