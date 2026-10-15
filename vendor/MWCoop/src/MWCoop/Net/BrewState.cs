using System;
using System.IO;

namespace MWCoop.Net
{
    // Persistent contents only. The native bucket has transient divisions by
    // zero in Math*/SugarWaterRatio, which must never enter a network packet.
    public struct BrewState
    {
        public float Alcohol, Sugar, Sweetness, Time, Vinegar, Water, Yeast;
        public bool Finished, LidOn;
        public void Write(NetWriter w)
        {
            Validate();
            w.F32(Alcohol).F32(Sugar).F32(Sweetness).F32(Time).F32(Vinegar).F32(Water).F32(Yeast).Bool(Finished).Bool(LidOn);
        }
        public static BrewState Read(NetReader r)
        {
            var state = new BrewState { Alcohol = r.F32(), Sugar = r.F32(), Sweetness = r.F32(), Time = r.F32(), Vinegar = r.F32(), Water = r.F32(), Yeast = r.F32(), Finished = r.Bool(), LidOn = r.Bool() };
            state.Validate(); return state;
        }
        void Validate()
        {
            if (!Range(Alcohol, .151f) || !Range(Sugar, 30.1f) || !Range(Sweetness, 32f) || !Range(Time, 1000000f)
                || !Range(Vinegar, 30.1f) || !Range(Water, 31f) || !Range(Yeast, 10.1f)) throw new IOException("Invalid native kilju contents");
        }
        static bool Range(float value, float max) { return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0 && value <= max; }
    }
}
