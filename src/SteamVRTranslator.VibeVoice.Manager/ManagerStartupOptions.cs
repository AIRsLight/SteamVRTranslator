namespace SteamVRTranslator.VibeVoice.Manager;

public sealed record ManagerStartupOptions(
    Uri ServiceUri,
    string ApiKey,
    bool StartLocalService)
{
    public static ManagerStartupOptions Parse(IEnumerable<string> arguments)
    {
        var values = arguments.ToArray();
        var serviceUrl = ReadValue(values, "--service-url") ??
                         Environment.GetEnvironmentVariable("VIBEVOICE_MANAGER_SERVICE_URL") ??
                         "http://127.0.0.1:5090";
        var apiKey = ReadValue(values, "--api-key") ??
                     Environment.GetEnvironmentVariable("VIBEVOICE_MANAGER_API_KEY") ??
                     string.Empty;
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var serviceUri) ||
            serviceUri.Scheme is not ("http" or "https"))
        {
            serviceUri = new Uri("http://127.0.0.1:5090");
        }

        return new ManagerStartupOptions(
            serviceUri,
            apiKey,
            !values.Contains("--no-start-service", StringComparer.OrdinalIgnoreCase));
    }

    private static string? ReadValue(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }
}
