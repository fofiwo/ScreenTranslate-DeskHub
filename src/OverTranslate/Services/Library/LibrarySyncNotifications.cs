namespace OverTranslate.Services.Library;

internal static class LibrarySyncNotifications
{
    public static event Action? Changed;
    public static void NotifyChanged() => Changed?.Invoke();
}
