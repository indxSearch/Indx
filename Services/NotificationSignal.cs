namespace IndxServer.Services
{
    /// <summary>
    /// Says a notification was stored, and for whom, so an open console shows it at once: the bell
    /// (NotificationPanel) read its count only when the page loaded and when it was opened, so a
    /// notification made in the background (a support reply, an expired rule) waited for a reload.
    /// One per instance; NotificationService raises it after saving.
    /// </summary>
    public sealed class NotificationSignal
    {
        public event Action<string>? Created;

        public void Raise(string userId) => Created?.Invoke(userId);
    }
}
