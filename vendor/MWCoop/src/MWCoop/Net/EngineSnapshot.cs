using System;
using System.Collections.Generic;
using System.IO;

namespace MWCoop.Net
{
    public sealed class EngineSnapshot
    {
        public const int State = 0, Pull = 1, Start = 2, Stop = 3, Shift = 4;
        public string Vehicle = "", NativeState = "";
        public int Sequence, Kind;
        public bool Starting, Accessory, ShutOff, Running;
        public float Rpm, Thrust, Gear;
        public float BatteryCharge = -1;
        public void Write(NetWriter w)
        {
            w.Str(Vehicle).I32(Sequence).U8(Kind).Str(NativeState).Bool(Starting).Bool(Accessory).Bool(ShutOff).Bool(Running).F32(Rpm).F32(Thrust).F32(Gear).F32(BatteryCharge);
        }
        public static EngineSnapshot Read(NetReader r)
        {
            var s = new EngineSnapshot { Vehicle = r.Str(), Sequence = r.I32(), Kind = r.U8(), NativeState = r.Str(),
                Starting = r.Bool(), Accessory = r.Bool(), ShutOff = r.Bool(), Running = r.Bool(), Rpm = r.F32(), Thrust = r.F32(), Gear = r.F32(), BatteryCharge = r.F32() };
            if (r.More || s.Vehicle.Length == 0 || s.Vehicle.Length > 100 || s.NativeState.Length > 80 || s.Sequence <= 0
                || s.Kind < State || s.Kind > Shift || s.Rpm < 0 || s.Rpm > 50000 || Math.Abs(s.Thrust) > 10000 || Math.Abs(s.Gear) > 100000 || s.BatteryCharge < -1 || s.BatteryCharge > 10000)
                throw new IOException("Invalid engine state");
            if (s.Kind == Shift && s.Gear != -10 && s.Gear != 64 && s.Gear != 10000) throw new IOException("Invalid boat gear");
            return s;
        }
        public bool SamePhase(EngineSnapshot other)
        {
            return other != null && NativeState == other.NativeState && Starting == other.Starting && Accessory == other.Accessory
                && ShutOff == other.ShutOff && Running == other.Running && Gear == other.Gear;
        }
    }
    public sealed class EngineOrder
    {
        readonly Dictionary<int, int> latest = new Dictionary<int, int>();
        public bool Accept(int actor, int sequence)
        {
            int previous;
            if (sequence <= 0 || latest.TryGetValue(actor, out previous) && sequence <= previous) return false;
            latest[actor] = sequence; return true;
        }
        public void Forget(int actor) { latest.Remove(actor); }
    }
}
