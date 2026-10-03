using VerseDeck.Core.Models;

namespace VerseDeck.App.Services;

/// <summary>Single source of truth for the active profile, its buttons and its voice phrases.</summary>
public sealed class DeckSession
{
    private readonly IVerseDeckRepository _repository;

    public DeckSession(IVerseDeckRepository repository)
    {
        _repository = repository;
    }

    public AppSettings Settings { get; private set; } = new(4785, "2468", false, ThemeService.Auto, 0.40);
    public Profile? ActiveProfile { get; private set; }
    public IReadOnlyList<Profile> Profiles { get; private set; } = [];
    public IReadOnlyList<DeckButton> Buttons { get; private set; } = [];
    public IReadOnlyList<VoiceCommand> VoiceCommands { get; private set; } = [];
    public IReadOnlyList<Checklist> Checklists { get; private set; } = [];

    public event EventHandler? Changed;

    public async Task LoadAsync()
    {
        await _repository.InitializeAsync();
        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        Settings = await _repository.GetSettingsAsync();
        Profiles = await _repository.GetProfilesAsync();
        ActiveProfile = Profiles.FirstOrDefault(p => p.IsActive) ?? Profiles.FirstOrDefault();
        Buttons = ActiveProfile is null ? [] : await _repository.GetButtonsAsync(ActiveProfile.Id);
        var buttonIds = Buttons.Select(b => b.Id).ToHashSet();
        VoiceCommands = (await _repository.GetVoiceCommandsAsync()).Where(v => buttonIds.Contains(v.ButtonId)).ToList();
        Checklists = ActiveProfile is null ? [] : await _repository.GetChecklistsAsync(ActiveProfile.Id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        await _repository.SaveSettingsAsync(settings);
        await ReloadAsync();
    }

    public async Task ActivateProfileAsync(long profileId)
    {
        var profile = Profiles.FirstOrDefault(p => p.Id == profileId)
            ?? throw new InvalidOperationException("Selecciona un perfil guardado para cargarlo.");
        await _repository.SaveProfileAsync(profile with { IsActive = true });
        await _repository.AddCommandLogAsync("Windows", "Perfil", $"Cargado: {profile.Name}");
        await ReloadAsync();
    }

    public async Task<Profile> SaveProfileAsync(string name, string shipName)
    {
        name = name.Trim();
        shipName = shipName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("El nombre del perfil no puede estar vacio.");
        }

        if (string.IsNullOrWhiteSpace(shipName))
        {
            throw new InvalidOperationException("La nave del perfil no puede estar vacia.");
        }

        var sourceButtons = Buttons;
        var sourceVoiceCommands = VoiceCommands;
        var sourceChecklists = Checklists;
        var existing = Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var saved = await _repository.SaveProfileAsync(existing is null
            ? new Profile(0, name, shipName, "General", true)
            : existing with { ShipName = shipName, IsActive = true });

        // A new profile starts as a copy of the deck that was on screen.
        if (existing is null)
        {
            var clonedIds = await CloneDeckAsync(sourceButtons, sourceVoiceCommands, saved.Id);

            // Checklists come along, pointing at the new profile's copies of their modules.
            foreach (var checklist in sourceChecklists)
            {
                var steps = checklist.Steps
                    .Select(s => s with { Id = 0, ButtonId = s.ButtonId is { } id && clonedIds.TryGetValue(id, out var cloned) ? cloned : null })
                    .ToList();
                await _repository.SaveChecklistAsync(new Checklist(0, saved.Id, checklist.Name, steps));
            }
        }

        await _repository.AddCommandLogAsync("Windows", "Perfil", existing is null ? $"Creado: {saved.Name}" : $"Guardado: {saved.Name}");
        await ReloadAsync();
        return saved;
    }

    public async Task<DeckButton> SaveButtonAsync(DeckButton button)
    {
        var saved = await _repository.SaveButtonAsync(button);
        await ReloadAsync();
        return saved;
    }

    public async Task<Checklist> SaveChecklistAsync(Checklist checklist)
    {
        if (string.IsNullOrWhiteSpace(checklist.Name))
        {
            throw new InvalidOperationException("La checklist necesita un nombre.");
        }

        if (Checklists.Any(c => c.Id != checklist.Id && c.Name.Equals(checklist.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Ya hay una checklist con ese nombre.");
        }

        var saved = await _repository.SaveChecklistAsync(checklist with { Name = checklist.Name.Trim(), ProfileId = ActiveProfile?.Id ?? checklist.ProfileId });
        await ReloadAsync();
        return saved;
    }

    public async Task DeleteChecklistAsync(long checklistId)
    {
        await _repository.DeleteChecklistAsync(checklistId);
        await ReloadAsync();
    }

    /// <summary>Saves several modules and reloads once, so listeners see a single change.</summary>
    public async Task SaveButtonsAsync(IEnumerable<DeckButton> buttons)
    {
        foreach (var button in buttons)
        {
            await _repository.SaveButtonAsync(button);
        }

        await ReloadAsync();
    }

    public async Task DeleteButtonAsync(long buttonId)
    {
        await _repository.DeleteButtonAsync(buttonId);
        await ReloadAsync();
    }

    public async Task SaveVoicePhraseAsync(long buttonId, string phrase, double confidence)
    {
        phrase = phrase.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(phrase))
        {
            throw new InvalidOperationException("La frase de voz no puede estar vacia.");
        }

        var existing = VoiceCommands.FirstOrDefault(c => c.ButtonId == buttonId && c.Phrase.Equals(phrase, StringComparison.OrdinalIgnoreCase));
        await _repository.SaveVoiceCommandAsync(new VoiceCommand(existing?.Id ?? 0, buttonId, phrase, Math.Clamp(confidence, 0.1, 0.98), true));
        await ReloadAsync();
    }

    private async Task<Dictionary<long, long>> CloneDeckAsync(IReadOnlyList<DeckButton> sourceButtons, IReadOnlyList<VoiceCommand> sourceVoiceCommands, long targetProfileId)
    {
        var clonedIds = new Dictionary<long, long>();
        foreach (var button in sourceButtons)
        {
            var cloned = await _repository.SaveButtonAsync(button with { Id = 0, ProfileId = targetProfileId });
            clonedIds[button.Id] = cloned.Id;
        }

        foreach (var command in sourceVoiceCommands)
        {
            if (clonedIds.TryGetValue(command.ButtonId, out var clonedId))
            {
                await _repository.SaveVoiceCommandAsync(command with { Id = 0, ButtonId = clonedId });
            }
        }

        return clonedIds;
    }
}
