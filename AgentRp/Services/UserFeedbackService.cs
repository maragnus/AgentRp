namespace AgentRp.Services;

public sealed class UserFeedbackService : IUserFeedbackService
{
    private readonly List<ToastMessage> _messages = [];

    public event Action? Changed;

    public IReadOnlyList<ToastMessage> Messages => _messages;

    public void ShowBackgroundSuccess(string message, string title) => Add(message, title, ToastIntent.Success);

    public void ShowBackgroundError(string message, string title) => Add(message, title, ToastIntent.Error);

    public void ShowBackgroundInfo(string message, string title) => Add(message, title, ToastIntent.Info);

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
