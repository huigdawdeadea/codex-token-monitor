namespace CodexTokenMonitor.App;

internal static class Program
{
    private const string SingleInstanceMutexName = "Local\\CodexTokenMonitor.SingleInstance";
    private const string ActivationEventName = "Local\\CodexTokenMonitor.Activate";

    [STAThread]
    private static void Main(string[] args)
    {
        var instanceSuffix = args
            .FirstOrDefault(argument => argument.StartsWith("--validation-instance=", StringComparison.OrdinalIgnoreCase))?
            .Split('=', 2)[1];
        var mutexName = string.IsNullOrWhiteSpace(instanceSuffix)
            ? SingleInstanceMutexName
            : $"{SingleInstanceMutexName}.{SanitizeInstanceSuffix(instanceSuffix)}";
        var eventName = string.IsNullOrWhiteSpace(instanceSuffix)
            ? ActivationEventName
            : $"{ActivationEventName}.{SanitizeInstanceSuffix(instanceSuffix)}";
        using var singleInstance = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        using var activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            eventName);
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

    private static string SanitizeInstanceSuffix(string value) => new(
        value.Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_').Take(32).ToArray());
}
