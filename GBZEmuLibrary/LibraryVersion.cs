using System.Reflection;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Exposes the semantic version embedded in the GBZEmuLibrary assembly.
    /// </summary>
    public static class LibraryVersion
    {
        /// <summary>
        /// Gets the semantic version assigned by GBZEmuLibrary.csproj at build time.
        /// </summary>
        public static string Current { get; } = ResolveCurrent();

        private static string ResolveCurrent()
        {
            var assembly = typeof(LibraryVersion).Assembly;
            var attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            return attribute?.InformationalVersion ?? assembly.GetName().Version.ToString(3);
        }
    }
}
