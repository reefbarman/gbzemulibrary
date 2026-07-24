namespace GBZEmuFrontend;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(FrontendOptions.Usage);
            return 0;
        }

        try
        {
            var options = FrontendOptions.Parse(args);
            var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var settingsStore = new FrontendSettingsStore(applicationData);
            var loadedSettings = settingsStore.Load();
            if (!string.IsNullOrWhiteSpace(loadedSettings.Diagnostic))
            {
                Console.Error.WriteLine(loadedSettings.Diagnostic);
            }

            using var frontend = new Frontend();
            frontend.Run(options, settingsStore, loadedSettings);
            return 0;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(FrontendOptions.Usage);
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
