using System.IO;
using System.Runtime.InteropServices;

namespace MWCoop
{
    // Mono's BinaryWriter allocates a byte array for every floating point value.
    // Preview files retain the same little-endian bits without those temporary arrays.
    public sealed class MscPreviewWriter : BinaryWriter
    {
        [StructLayout(LayoutKind.Explicit)]
        struct SingleBits { [FieldOffset(0)] public float Value; [FieldOffset(0)] public int Bits; }
        [StructLayout(LayoutKind.Explicit)]
        struct DoubleBits { [FieldOffset(0)] public double Value; [FieldOffset(0)] public long Bits; }
        public MscPreviewWriter(Stream stream) : base(stream) { }
        public override void Write(float value) { base.Write(new SingleBits { Value = value }.Bits); }
        public override void Write(double value) { base.Write(new DoubleBits { Value = value }.Bits); }
    }
}
