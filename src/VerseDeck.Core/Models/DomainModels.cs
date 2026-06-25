namespace VerseDeck.Core.Models;

public sealed record Profile(
    long Id,
    string Name,
    string ShipName,
    string Role,
    bool IsActive);

public sealed record DeckButton(
    long Id,
    long ProfileId,
    string Name,
    string Icon,
    string AccentColor,
    string Category,
    KeyPressAction Action,
    bool RequiresConfirmation,
    bool MobileHaptics);

public sealed record VoiceCommand(
    long Id,
    long ButtonId,
    string Phrase,
    double MinimumConfidence,
    bool Enabled);

public sealed record CommandLogEntry(
    long Id,
    DateTimeOffset CreatedAt,
    string Source,
    string Command,
    string Result);

public sealed record ConnectedDevice(
    string Id,
    string Name,
    string RemoteAddress,
    DateTimeOffset ConnectedAt);

public sealed record AppSettings(
    int MobilePort,
    string PairingPin,
    bool VoiceEnabled,
    string Theme,
    double VoiceMinimumConfidence,
    string VoiceActivationMode = "PushToTalk",
    string PushToTalkDevice = "Keyboard",
    string PushToTalkBinding = "F13",
    bool CommandSoundEnabled = true,
    bool WelcomeSoundEnabled = true);

public sealed record KeyPressAction(string Key, IReadOnlyList<string> Modifiers, int PressDurationMs)
{
    public const int MaxPressDurationMs = 250;

    public static KeyPressAction DefaultLandingGear => new("N", Array.Empty<string>(), 60);

    public KeyPressAction Validate()
    {
        if (string.IsNullOrWhiteSpace(Key))
        {
            throw new InvalidOperationException("A key press action must have one key.");
        }

        if (PressDurationMs is < 20 or > MaxPressDurationMs)
        {
            throw new InvalidOperationException($"Press duration must be between 20 and {MaxPressDurationMs} ms.");
        }

        if (Modifiers.Count > 3)
        {
            throw new InvalidOperationException("A key press action can have at most three modifiers.");
        }

        return this;
    }
}

public interface IVerseDeckRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Profile>> GetProfilesAsync(CancellationToken cancellationToken = default);
    Task<Profile> SaveProfileAsync(Profile profile, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeckButton>> GetButtonsAsync(long profileId, CancellationToken cancellationToken = default);
    Task<DeckButton> SaveButtonAsync(DeckButton button, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VoiceCommand>> GetVoiceCommandsAsync(CancellationToken cancellationToken = default);
    Task<VoiceCommand> SaveVoiceCommandAsync(VoiceCommand command, CancellationToken cancellationToken = default);
    Task AddCommandLogAsync(string source, string command, string result, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CommandLogEntry>> GetRecentCommandLogAsync(int count, CancellationToken cancellationToken = default);
}

public interface IInputSender
{
    Task SendAsync(KeyPressAction action, CancellationToken cancellationToken = default);
}

public interface IVoiceCommandService : IDisposable
{
    event EventHandler<VoiceRecognizedEventArgs>? CommandRecognized;
    bool IsRunning { get; }
    Task StartAsync(IReadOnlyList<VoiceCommand> commands, IReadOnlyList<DeckButton> buttons, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed class VoiceRecognizedEventArgs : EventArgs
{
    public VoiceRecognizedEventArgs(VoiceCommand command, DeckButton button, double confidence)
    {
        Command = command;
        Button = button;
        Confidence = confidence;
    }

    public VoiceCommand Command { get; }
    public DeckButton Button { get; }
    public double Confidence { get; }
}
