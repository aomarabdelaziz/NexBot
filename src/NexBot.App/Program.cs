namespace NexBot.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var form = new MainForm();
        // Hidden: "NexBot.exe --screenshots <folder>" saves one picture of every page (used by the guide in docs/).
        if (args.Length == 2 && args[0] == "--screenshots")
            form.Shown += async (_, _) => await form.CaptureAllPagesAsync(args[1]);
        Application.Run(form);
    }
}
