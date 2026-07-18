using System;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Excludes immutable configuration or runtime infrastructure from the versioned machine-state payload.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    internal sealed class SaveStateIgnoreAttribute : Attribute
    {
    }
}
