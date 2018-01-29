using System;

namespace GBZEmuLibrary
{
    [Flags]
    public enum BootMode
    {
        DMG   = 1,
        GBC   = 2,
        Skip  = 4,
        Force = 8,
        Short = 16
    }

    internal static class BootModeHelper
    {
        public static bool IsSet(this BootMode val, BootMode flag)
        {
            return (val & flag) == flag;
        }
    }
}
