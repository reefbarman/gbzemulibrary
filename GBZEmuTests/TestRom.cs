namespace GBZEmuTests;

internal sealed class TestRom : IDisposable
{
    public string Path { get; }

    private TestRom(string path)
    {
        Path = path;
    }

    public static TestRom Create(params byte[] program)
    {
        var bytes = new byte[0x8000];
        Array.Copy(program, 0, bytes, 0x100, program.Length);
        bytes[0x147] = 0x00;
        bytes[0x148] = 0x00;
        bytes[0x149] = 0x00;

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gbzemu-{Guid.NewGuid():N}.gb");
        File.WriteAllBytes(path, bytes);
        return new TestRom(path);
    }

    public void Dispose()
    {
        File.Delete(Path);
    }
}
