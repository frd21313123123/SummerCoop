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
    }
}
