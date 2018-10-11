using System.Runtime.InteropServices;

namespace GBZEmuLibrary
{
    [StructLayout(LayoutKind.Explicit)]
    internal struct Registers
    {
        [FieldOffset(0)] public ushort AF;
        [FieldOffset(0)] public byte   F;
        [FieldOffset(1)] public byte   A;

        [FieldOffset(2)] public ushort BC;
        [FieldOffset(2)] public byte   C;
        [FieldOffset(3)] public byte   B;

        [FieldOffset(4)] public ushort DE;
        [FieldOffset(4)] public byte   E;
        [FieldOffset(5)] public byte   D;

        [FieldOffset(6)] public ushort HL;
        [FieldOffset(6)] public byte   L;
        [FieldOffset(7)] public byte   H;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct StackPointer
    {
        [FieldOffset(0)] public ushort SP;
        [FieldOffset(0)] public byte   Lo;
        [FieldOffset(1)] public byte   Hi;
    }
}