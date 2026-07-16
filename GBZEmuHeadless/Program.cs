namespace GBZEmuHeadless;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(HeadlessOptions.Usage);
            return 0;
        }

        try
        {
            var options = HeadlessOptions.Parse(args);
            var reportPath = new HeadlessRunner().Run(options);
            Console.WriteLine($"Wrote {reportPath}");
            return 0;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(HeadlessOptions.Usage);
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
