using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VerseDeck.App.Services;
using VerseDeck.Core.Models;

namespace VerseDeck.App.ViewModels;

public sealed partial class ProfileViewModel : ObservableObject
{
    private readonly DeckSession _session;
    private readonly IStatusSink _status;

    [ObservableProperty]
    private Profile? _selectedProfile;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _shipName = string.Empty;

    public ProfileViewModel(DeckSession session, IStatusSink status)
    {
        _session = session;
        _status = status;
        _session.Changed += (_, _) => Sync();
    }

    public ObservableCollection<Profile> Profiles { get; } = [];
    public IReadOnlyList<string> ShipNames => ShipCatalog.Names;

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (SelectedProfile is null)
        {
            _status.Error("Selecciona un perfil guardado para cargarlo.");
            return;
        }

        try
        {
            var name = SelectedProfile.Name;
            await _session.ActivateProfileAsync(SelectedProfile.Id);
            _status.Info($"Perfil cargado: {name}");
        }
        catch (Exception ex)
        {
            _status.Error(ex.Message);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var isNew = !_session.Profiles.Any(p => p.Name.Equals(Name.Trim(), StringComparison.OrdinalIgnoreCase));
            await _session.SaveProfileAsync(Name, ShipName ?? string.Empty);
            _status.Info(isNew ? "Perfil nuevo creado" : "Perfil guardado");
        }
        catch (Exception ex)
        {
            _status.Error(ex.Message);
        }
    }

    private void Sync()
    {
        Profiles.Clear();
        foreach (var profile in _session.Profiles)
        {
            Profiles.Add(profile);
        }

        var active = _session.ActiveProfile;
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == active?.Id);
        Name = active?.Name ?? string.Empty;
        ShipName = active?.ShipName ?? string.Empty;
    }
}

public sealed partial class ActivityViewModel : ObservableObject
{
    private readonly IVerseDeckRepository _repository;

    public ActivityViewModel(IVerseDeckRepository repository)
    {
        _repository = repository;
    }

    public ObservableCollection<string> Entries { get; } = [];

    public async Task RefreshAsync()
    {
        try
        {
            var lines = (await _repository.GetRecentCommandLogAsync(8))
                .Select(item => $"{item.CreatedAt:HH:mm:ss}  [{item.Source}]  {item.Command}: {item.Result}")
                .ToList();
            if (lines.SequenceEqual(Entries))
            {
                return;
            }

            Entries.Clear();
            foreach (var line in lines)
            {
                Entries.Add(line);
            }
        }
        catch
        {
            // The activity list is informative; a failed refresh must not interrupt the deck.
        }
    }
}
