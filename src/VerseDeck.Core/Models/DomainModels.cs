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
    bool MobileHaptics,
    string GameAction = "",
    string Response = "");

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
    bool WelcomeSoundEnabled = true,
    string GameFolder = "",
    bool CopilotEnabled = false,
    string CopilotVoice = "",
    string CopilotPack = "sobria",
    double CopilotVolume = 0.8,
    bool CopilotVoiceOnly = false,
    string CopilotMutedCategories = "",
    bool CopilotGreeting = false,
    bool Animations = true);

public sealed record KeyPressAction(string Key, IReadOnlyList<string> Modifiers, int PressDurationMs)
{
    // Some game actions only trigger while the key is held; it is still one key, pressed once.
    public const int MaxPressDurationMs = 2000;

    public static readonly IReadOnlyList<string> AllowedModifiers =
        ["Ctrl", "Control", "Shift", "Alt", "LCtrl", "RCtrl", "LShift", "RShift", "LAlt", "RAlt"];

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

        // Anything else held with the key would turn one press into a chord of several keys.
        var unknown = Modifiers.FirstOrDefault(m => !AllowedModifiers.Contains(m, StringComparer.OrdinalIgnoreCase));
        if (unknown is not null)
        {
            throw new InvalidOperationException($"'{unknown}' is not a modifier. Use Ctrl, Shift or Alt.");
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
    Task DeleteButtonAsync(long buttonId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VoiceCommand>> GetVoiceCommandsAsync(CancellationToken cancellationToken = default);
    Task<VoiceCommand> SaveVoiceCommandAsync(VoiceCommand command, CancellationToken cancellationToken = default);
    Task AddCommandLogAsync(string source, string command, string result, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CommandLogEntry>> GetRecentCommandLogAsync(int count, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Checklist>> GetChecklistsAsync(long profileId, CancellationToken cancellationToken = default);
    Task<Checklist> SaveChecklistAsync(Checklist checklist, CancellationToken cancellationToken = default);
    Task DeleteChecklistAsync(long checklistId, CancellationToken cancellationToken = default);
    Task<LogbookNote> AddNoteAsync(LogbookNote note, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LogbookNote>> GetNotesAsync(int count, CancellationToken cancellationToken = default);
    Task DeleteNoteAsync(long noteId, CancellationToken cancellationToken = default);
}

public interface IInputSender
{
    Task SendAsync(KeyPressAction action, CancellationToken cancellationToken = default);
}

public interface IVoiceCommandService : IDisposable
{
    event EventHandler<VoiceRecognizedEventArgs>? CommandRecognized;
    event EventHandler<string>? Diagnostic;

    /// <summary>Everything the engine heard but did not turn into a command, with the reason.</summary>
    event EventHandler<RecognitionHeard>? Heard;

    /// <summary>
    /// Free dictation competing with the commands, so conversation is not taken for one. It costs about ten
    /// times the CPU per utterance and tens of megabytes, so it is only worth it when always listening.
    /// Applied on the next start.
    /// </summary>
    bool UseDiscardModel { get; set; }

    /// <summary>Checklist names the companion grammar listens for. Applied on the next start.</summary>
    IReadOnlyList<string> ChecklistNames { get; set; }

    /// <summary>Minimum confidence for checklist, timer and note phrases.</summary>
    double CompanionMinimumConfidence { get; set; }

    /// <summary>A checklist, timer or note phrase was heard.</summary>
    event EventHandler<CompanionRecognizedEventArgs>? CompanionRecognized;
    bool IsRunning { get; }
    void PauseRecognition();
    void ResumeRecognition();
    void SetInputGate(bool isOpen, TimeSpan? releaseGrace = null);
    Task StartAsync(IReadOnlyList<VoiceCommand> commands, IReadOnlyList<DeckButton> buttons, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public enum CompanionKind { StartChecklist, Done, Skip, Repeat, CancelChecklist, Timer, CancelTimers, Note }

/// <summary>A request to the copilot itself rather than to a module: checklists, timers and notes.</summary>
public sealed record CompanionCommand(CompanionKind Kind, string? Name = null, TimeSpan? Duration = null, string? Label = null, string? Text = null);

public sealed record ChecklistStep(long Id, int Position, string Text, long? ButtonId);

public sealed record Checklist(long Id, long ProfileId, string Name, IReadOnlyList<ChecklistStep> Steps);

public sealed record LogbookNote(long Id, DateTimeOffset CreatedAt, string ProfileName, string ShipName, string Text, string Source);

/// <summary>What happened to something the microphone heard.</summary>
public enum RecognitionOutcome { Executed, Discarded, LowConfidence, GateClosed, CopilotSpeaking, Repeated, ModuleGone, Offline, Failed, NothingToDo }

public sealed record CompanionRecognizedEventArgs(CompanionCommand Command, string Text, double Confidence, DateTimeOffset? HeardAt);

public sealed record RecognitionHeard(string Text, double Confidence, RecognitionOutcome Outcome, DateTimeOffset At);

public sealed class VoiceRecognizedEventArgs : EventArgs
{
    public VoiceRecognizedEventArgs(VoiceCommand command, DeckButton button, double confidence, DateTimeOffset? heardAt = null)
    {
        Command = command;
        Button = button;
        Confidence = confidence;
        HeardAt = heardAt;
    }

    /// <summary>When the utterance began, which is earlier than when the recogniser reports it.</summary>
    public DateTimeOffset? HeardAt { get; }

    public VoiceCommand Command { get; }
    public DeckButton Button { get; }
    public double Confidence { get; }
}
