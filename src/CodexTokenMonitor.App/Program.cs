namespace CodexTokenMonitor.App;

internal static class Program
{
    private const string SingleInstanceMutexName = "Local\\CodexTokenMonitor.SingleInstance";
    private const string ActivationEventName = "Local\\CodexTokenMonitor.Activate";

    [STAThread]
    private static void Main(string[] args)
    {
        using var singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        using var activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationEventName);
        if (!createdNew)
        {
            if (!args.Contains("--start-hidden", StringComparer.OrdinalIgnoreCase) &&
                !args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase))
            {
                activationEvent.Set();
            }

            return;
        }

        ApplicationConfiguration.Initialize();
        using var application = TrayApplicationContext.Create(args, activationEvent);
        Application.Run(application);
    }
}
