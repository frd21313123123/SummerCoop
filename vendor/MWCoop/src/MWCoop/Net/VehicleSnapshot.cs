using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MWCoop.Net
{
    // Body of modes 0 (parked), 1 (driver) and 2 (coasting/towed). Keep the
    // upstream layout: missing cabin heat is NaN, not an invalid motion sample.
    // This decoder is shared by the game receiver and standalone regressions.
    public sealed class VehicleSnapshot
    {
        public Vector3 Position, Velocity, AngularVelocity, DriverHead;
        public Quaternion Rotation;
        public float Rpm, Throttle, Steering, Heat = float.NaN, Timestamp = -1f;
        public int SoundMask;
        public readonly List<float> Sounds = new List<float>();

        // A new driver may take over. Delayed parked/coasting samples from the
        // previous owner must not move a vehicle that is already driven elsewhere.
        public static bool AcceptMotion(int mode, int sender, int currentOwner, int seatedDriver = -1)
        {
            if (mode == 1) return seatedDriver < 0 || sender == seatedDriver;
            return currentOwner < 0 || sender == currentOwner;
        }

        public static VehicleSnapshot Read(NetReader reader, int mode)
        {
            if (mode < 0 || mode > 2) throw new IOException("Unknown vehicle motion mode");
            var state = new VehicleSnapshot {
                Position = reader.Vec(), Rotation = reader.Quat(),
                Velocity = reader.Vec(), AngularVelocity = reader.Vec()
            };
            if (mode != 0)
            {
                state.Rpm = reader.F32(); state.Throttle = reader.F32(); state.Steering = reader.F32();
                state.Heat = reader.OptionalF32(); state.SoundMask = reader.U16();
                for (int i = 0; i < 14; i++)
                    if ((state.SoundMask & (1 << i)) != 0) { state.Sounds.Add(reader.F32()); state.Sounds.Add(reader.F32()); }
            }
            if (mode == 1 && reader.More) state.DriverHead = reader.Vec();
            if (reader.More) state.Timestamp = reader.F32();
            if (reader.More) throw new IOException("Trailing vehicle motion data");
            return state;
        }
    }

    // Keep each sender's watermark across ownership changes and releases. A late
    // driving packet must not resurrect a driver or undo a newer parked pose.
    public sealed class VehicleMotionOrder
    {
        readonly Dictionary<int, float> timestamps = new Dictionary<int, float>();
        public void Forget(int sender) { timestamps.Remove(sender); }
        public bool Accept(int sender, float timestamp)
        {
            if (float.IsNaN(timestamp) || float.IsInfinity(timestamp)) return false;
            if (timestamp < 0f) return true; // Older packets without a timestamp.
            float previous;
            if (timestamps.TryGetValue(sender, out previous) && timestamp <= previous) return false;
            timestamps[sender] = timestamp;
            return true;
        }
    }
}
