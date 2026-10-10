using System.IO;
using System.Runtime.InteropServices;

namespace SummerCoop.Core
{
    // Unity 5's Mono BinaryWriter allocates a GetBytes array for every float/double.
    // Its integer writes already use the writer's reusable buffer and the same little-endian format.
    internal sealed class WireWriter : BinaryWriter
    {
        [StructLayout(LayoutKind.Explicit)]
        private struct SingleBits { [FieldOffset(0)] public float Value; [FieldOffset(0)] public int Bits; }
        [StructLayout(LayoutKind.Explicit)]
        private struct DoubleBits { [FieldOffset(0)] public double Value; [FieldOffset(0)] public long Bits; }
        public WireWriter(Stream stream) : base(stream) { }
        public override void Write(float value) { SingleBits bits = new SingleBits { Value = value }; base.Write(bits.Bits); }
        public override void Write(double value) { DoubleBits bits = new DoubleBits { Value = value }; base.Write(bits.Bits); }
    }
}
