using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VerseDeck.App.Services;
using VerseDeck.Speech;

namespace VerseDeck.App.ViewModels;

public sealed record PackChoice(string Id, string Name);

public sealed partial class VoiceRowViewModel : ObservableObject
{
    private readonly CopilotViewModel _owner;

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    public VoiceRowViewModel(CopilotViewModel owner, VoiceInfo voice)
    {
        _owner = owner;
        Voice = voice;
        Details = $"{voice.Bytes / 1_048_576} MB  -  {voice.License}";
    }

    public VoiceInfo Voice { get; }
    public string Name => Voice.Name;
    public string Details { get; }

    [RelayCommand]
    private Task InstallAsync() => _owner.InstallAsync(this);

    [RelayCommand]
    private Task UseAsync() => _owner.UseAsync(this);

    [RelayCommand]
    private void Cancel() => _owner.CancelInstall(this);
}

public sealed partial class CategoryToggle : ObservableObject
{
    private readonly Action _changed;

    [ObservableProperty]
    private bool _speaks;

    public CategoryToggle(string name, bool speaks, Action changed)
    {
        Name = name;
        _speaks = speaks;
        _changed = changed;
    }

    public string Name { get; }

    partial void OnSpeaksChanged(bool value) => _changed();
}

public sealed partial class CopilotViewModel : ObservableObject
{
    private readonly DeckSession _session;
    private readonly CopilotService _copilot;
    private readonly IVoiceInstaller _installer;
    private readonly IStatusSink _status;
    private readonly Dictionary<VoiceRowViewModel, CancellationTokenSource> _downloads = [];
    private bool _syncing;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private bool _voiceOnly;

    [ObservableProperty]
    private bool _greeting;

    [ObservableProperty]
    private double _volume = 0.8;

    [ObservableProperty]
    private PackChoice? _selectedPack;

    [ObservableProperty]
    private string _packSample = string.Empty;

    [ObservableProperty]
    private string _cacheText = string.Empty;

    [ObservableProperty]
    private string _statusLine = string.Empty;

    public CopilotViewModel(DeckSession session, CopilotService copilot, IVoiceInstaller installer, IStatusSink status)
    {
        _session = session;
        _copilot = copilot;
        _installer = installer;
        _status = status;
        Packs = copilot.Packs.Select(p => new PackChoice(p.Id, p.Name)).ToList();
        foreach (var voice in copilot.Voices.Voices)
        {
            Voices.Add(new VoiceRowViewModel(this, voice));
        }

        _session.Changed += (_, _) => Sync();
        _copilot.Changed += (_, _) => Sync();
    }

    public IReadOnlyList<PackChoice> Packs { get; }
    public ObservableCollection<VoiceRowViewModel> Voices { get; } = [];
    public ObservableCollection<CategoryToggle> Categories { get; } = [];

    /// <summary>The last save or warm-up started by a change, so callers can wait for it.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private async Task TestAsync()
    {
        if (_copilot.ActiveVoice is null)
        {
            _status.Error("Descarga y elige una voz antes de probarla.");
            return;
        }

        await _copilot.TestAsync();
    }

    [RelayCommand]
    private async Task WarmUpAsync()
    {
        if (_copilot.ActiveVoice is null)
        {
            _status.Error("Descarga y elige una voz antes de generar frases.");
            return;
        }

        await _copilot.WarmUpAsync();
        _status.Info($"Frases listas: {_copilot.CachedPhrases}");
    }

    [RelayCommand]
    private void ClearCache()
    {
        _copilot.ClearCache();
        _status.Info("Cache de frases vaciada");
    }

    internal async Task InstallAsync(VoiceRowViewModel row)
    {
        if (row.IsBusy)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _downloads[row] = cancellation;
        row.IsBusy = true;
        row.Progress = 0;
        try
        {
            await _installer.InstallAsync(row.Voice, new Progress<double>(value => row.Progress = value), cancellation.Token);
            _status.Info($"Voz instalada: {row.Name}");

            // The first voice to arrive becomes the copilot's voice without a second click.
            if (_copilot.ActiveVoice is null)
            {
                await UseAsync(row);
            }
        }
        catch (OperationCanceledException)
        {
            _status.Info("Descarga cancelada");
        }
        catch (Exception ex)
        {
            _status.Error($"No se pudo instalar la voz: {ex.Message}");
        }
        finally
        {
            _downloads.Remove(row);
            cancellation.Dispose();
            row.IsBusy = false;
            Sync();
        }
    }

    internal async Task UseAsync(VoiceRowViewModel row)
    {
        if (!_copilot.Store.IsInstalled(row.Voice))
        {
            _status.Error("Esa voz aun no esta descargada.");
            return;
        }

        await _session.SaveSettingsAsync(_session.Settings with { CopilotVoice = row.Voice.Id });
        Pending = _copilot.WarmUpAsync();
    }

    internal void CancelInstall(VoiceRowViewModel row)
    {
        if (_downloads.TryGetValue(row, out var cancellation))
        {
            cancellation.Cancel();
        }
    }

    partial void OnEnabledChanged(bool value) => QueueSave(warmUp: value);

    partial void OnVoiceOnlyChanged(bool value) => QueueSave(warmUp: false);

    partial void OnGreetingChanged(bool value) => QueueSave(warmUp: false);

    partial void OnVolumeChanged(double value) => QueueSave(warmUp: false);

    partial void OnSelectedPackChanged(PackChoice? value) => QueueSave(warmUp: true);

    private void QueueSave(bool warmUp)
    {
        if (!_syncing)
        {
            Pending = SaveAsync(warmUp);
        }
    }

    private async Task SaveAsync(bool warmUp)
    {
        try
        {
            await _session.SaveSettingsAsync(_session.Settings with
            {
                CopilotEnabled = Enabled,
                CopilotVoiceOnly = VoiceOnly,
                CopilotGreeting = Greeting,
                CopilotVolume = Math.Clamp(Volume, 0, 1),
                CopilotPack = SelectedPack?.Id ?? _session.Settings.CopilotPack,
                CopilotMutedCategories = string.Join(",", Categories.Where(c => !c.Speaks).Select(c => c.Name))
            });
            if (warmUp && _copilot.CanSpeak)
            {
                await _copilot.WarmUpAsync();
            }
        }
        catch (Exception ex)
        {
            _status.Error(ex.Message);
        }
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            var settings = _session.Settings;
            Enabled = settings.CopilotEnabled;
            VoiceOnly = settings.CopilotVoiceOnly;
            Greeting = settings.CopilotGreeting;
            Volume = settings.CopilotVolume;
            SelectedPack = Packs.FirstOrDefault(p => p.Id.Equals(settings.CopilotPack, StringComparison.OrdinalIgnoreCase)) ?? Packs[0];

            var pack = _copilot.Packs.First(p => p.Id == SelectedPack.Id);
            PackSample = string.Join("   ", pack.Actions["landing_gear"].Take(1).Concat(pack.Actions["power_thrusters"].Take(1)).Concat(pack.Failed.Take(1)));

            var active = _copilot.ActiveVoice;
            foreach (var row in Voices)
            {
                row.IsInstalled = _copilot.Store.IsInstalled(row.Voice);
                row.IsSelected = active?.Id == row.Voice.Id;
            }

            var muted = CopilotService.MutedCategories(settings);
            var names = ModuleStyle.Categories
                .Concat(_session.Buttons.Select(b => b.Category))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!names.SequenceEqual(Categories.Select(c => c.Name)))
            {
                Categories.Clear();
                foreach (var name in names)
                {
                    Categories.Add(new CategoryToggle(name, true, () => QueueSave(warmUp: false)));
                }
            }

            foreach (var category in Categories)
            {
                category.Speaks = !muted.Contains(category.Name, StringComparer.OrdinalIgnoreCase);
            }

            CacheText = string.IsNullOrEmpty(_copilot.WarmUpStatus)
                ? $"Frases generadas: {_copilot.CachedPhrases}"
                : _copilot.WarmUpStatus;
            StatusLine = active is null
                ? "Sin voz: descarga una y el copiloto podra hablar."
                : settings.CopilotEnabled
                    ? $"Activo con la voz {active.Name}."
                    : $"Voz lista ({active.Name}). Activa el copiloto para que conteste.";
        }
        finally
        {
            _syncing = false;
        }
    }
}
