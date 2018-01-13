using System;

namespace GBZEmuLibrary
{
    //TODO alot of these helpers only help with understanding
    //If optimisation is needed this is the first place to optimize
    //Maybe with lookup tables?
    internal class Helpers
    {
        public static bool TestBit(byte reg, int bit)
        {
            return (reg & (1 << bit)) != 0;
        }

        public static bool TestBit(ushort reg, int bit)
        {
            return (reg & (1 << bit)) != 0;
        }

        public static void SetBit(ref byte reg, int bit, bool val)
        {
            if (val)
            {
                reg |= (byte)(1 << bit);
            }
            else
            {
                reg &= (byte)~(1 << bit);
            }
        }

        public static int GetBit(byte data, int bit)
        {
            return (data & (1 << bit)) != 0 ? 1 : 0;
        }

        public static int GetBitsIsolated(byte data, int startBit, int numBits)
        {
            return (data & (((1 << numBits) - 1) << startBit)) >> startBit;
        }

        public static byte GetBits(byte data, int numBits)
        {
            return (byte)(data & ((1 << numBits) - 1));
        }

        public static void ResetLowBits(ref byte data, int numBits)
        {
            data &= (byte)((byte.MaxValue + 1) - Math.Pow(2, numBits));
        }

        public static void ResetHighBits(ref byte data, int numBits)
        {
            data &= (byte)(Math.Pow(2, 8 - numBits) - 1);
        }

        public static void NoOp()
        {

        }
    }
}
