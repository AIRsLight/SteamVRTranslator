namespace SteamVRTranslator.App.Speech;

public static class WasapiErrorClassifier
{
    private const int AccessDeniedHResult = unchecked((int)0x80070005);

    public static bool IsMicrophoneAccessDenied(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception.HResult == AccessDeniedHResult)
        {
            return true;
        }

        if (exception is AggregateException aggregateException &&
            aggregateException.InnerExceptions.Any(IsMicrophoneAccessDenied))
        {
            return true;
        }

        return exception.InnerException is not null &&
               IsMicrophoneAccessDenied(exception.InnerException);
    }
}
