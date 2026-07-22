using System.Reflection;
using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies the host-visible library semantic version contract.
/// </summary>
public sealed class LibraryVersionTests
{
    [Fact]
    public void CurrentMatchesAssemblyInformationalVersion()
    {
        var attribute = typeof(Emulator).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        Assert.NotNull(attribute);
        Assert.Matches(@"^\d+\.\d+\.\d+$", LibraryVersion.Current);
        Assert.Equal(attribute.InformationalVersion, LibraryVersion.Current);
    }
}
