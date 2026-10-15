using System;
using System.IO;
using UnityEngine;

namespace MWCoop.Net
{
    // Appended to PlayerState: 0 = no support; otherwise the vehicle table
    // index + 1, followed by feet/head/yaw in that vehicle's reference frame.
    public struct SupportPose
    {
        public int Vehicle;
        public Vector3 Feet, Head;
        public float Yaw;
        public bool Attached { get { return Vehicle > 0; } }
        public int VehicleIndex { get { return Vehicle - 1; } }
        public void Write(NetWriter writer)
        {
            Validate(); writer.U8(Vehicle);
            if (Attached) writer.Vec(Feet).Vec(Head).F32(Yaw);
        }
        public static SupportPose Read(NetReader reader)
        {
            SupportPose pose = new SupportPose { Vehicle = reader.U8() };
            if (pose.Attached) { pose.Feet = reader.Vec(); pose.Head = reader.Vec(); pose.Yaw = reader.F32(); }
            pose.Validate();
            if (reader.More) throw new IOException("Trailing player support data");
            return pose;
        }
        void Validate()
        {
            if (Vehicle < 0 || Vehicle > 255) throw new IOException("Invalid player support index");
            if (!Attached) return;
            if (!Finite(Feet.x) || !Finite(Feet.y) || !Finite(Feet.z) || !Finite(Head.x) || !Finite(Head.y) || !Finite(Head.z) || !Finite(Yaw)
                || Math.Abs(Feet.x) > 32 || Math.Abs(Feet.y) > 32 || Math.Abs(Feet.z) > 32
                || Math.Abs(Head.x) > 32 || Math.Abs(Head.y) > 32 || Math.Abs(Head.z) > 32 || Math.Abs(Yaw) > 360)
                throw new IOException("Invalid player support pose");
        }
        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }
}
