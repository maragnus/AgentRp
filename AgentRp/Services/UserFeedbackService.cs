namespace AgentRp.Services;

public sealed class UserFeedbackService : IUserFeedbackService
{
    private readonly List<ToastMessage> _messages = [];

    public event Action? Changed;

    public IReadOnlyList<ToastMessage> Messages => _messages;

    public void ShowBackgroundError(string message, string title) => Add(message, title, ToastIntent.Error);

    public void ShowBackgroundError(Exception exception, string fallbackMessage, string title) =>
        ShowBackgroundError(UserFacingErrorMessageBuilder.Build(fallbackMessage, exception), title);

    public void ShowBackgroundWarning(string message, string title) => Add(message, title, ToastIntent.Warning);

    public void Dismiss(Guid id)
    {
        _messages.RemoveAll(x => x.Id == id);
        Changed?.Invoke();
    }

    private void Add(string message, string title, ToastIntent intent)
    {
        _messages.Add(new ToastMessage(Guid.NewGuid(), title, message, intent, DateTime.UtcNow));
        Changed?.Invoke();
    }
}
