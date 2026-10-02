namespace VerseDeck.App.Services;

public interface IDialogService
{
    bool Confirm(string title, string message);
}

public interface IAudioFeedback
{
    void PlayWelcome();
    void PlayCommand();
}

public interface IDebugLog
{
    string Path { get; }
    void Write(string message);
}
