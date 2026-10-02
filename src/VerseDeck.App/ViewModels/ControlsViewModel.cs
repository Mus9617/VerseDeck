using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VerseDeck.App.Services;
using VerseDeck.Game;

namespace VerseDeck.App.ViewModels;

public sealed record LinkRow(string Module, string Action, string KeyText, string StatusText, string StatusKey, string Detail);

public sealed record RebindRow(string ActionMap, string Action, string Input, bool InCatalog);

public sealed record PresetLinkRow(string Module, string Action, string CurrentKey, string NewKey);

public sealed record GameActionChoice(string Id, string Label);

public static class BindStatusText
{
    public static string Text(BindStatus status, string gameVersion) => status switch
    {
        BindStatus.YourKey => "Tu tecla",
        BindStatus.Default => $"Por defecto ({gameVersion})",
        BindStatus.NoKey => "Sin tecla en el juego",
        BindStatus.NotSendable => "No enviable",
        _ => "Conflicto"
    };

    public static string Key(BindStatus status) => status switch
    {
        BindStatus.YourKey or BindStatus.Default => "Positive",
        BindStatus.Conflict => "Warning",
        _ => "Danger"
    };

    /// <summary>Short warning shown on the module tile, empty when the link is usable.</summary>
    public static string TileWarning(BindStatus? status) => status switch
    {
        BindStatus.NoKey => "SIN TECLA",
        BindStatus.NotSendable or BindStatus.Conflict => "REVISAR",
        _ => string.Empty
    };
}

public sealed partial class ControlsViewModel : ObservableObject
{
    private readonly DeckSession _session;
    private readonly ControlSync _sync;
    private readonly GameActionCatalog _catalog;
    private readonly IStatusSink _status;
    private bool _folderLoaded;

    [ObservableProperty]
    private string _gameFolderText = "No detectada";

    [ObservableProperty]
    private string _folderInput = string.Empty;

    [ObservableProperty]
    private string _fileStatus = "Sin leer";

    [ObservableProperty]
    private bool _hasPresetPreview;

    public ControlsViewModel(DeckSession session, ControlSync sync, GameActionCatalog catalog, IStatusSink status)
    {
        _session = session;
        _sync = sync;
        _catalog = catalog;
        _status = status;
        CatalogVersion = $"Catalogo de acciones para Star Citizen {catalog.GameVersion}";
        _sync.Changed += (_, _) => Refresh();
    }

    public string CatalogVersion { get; }
    public ObservableCollection<LinkRow> Links { get; } = [];
    public ObservableCollection<RebindRow> Rebinds { get; } = [];
    public ObservableCollection<PresetLinkRow> PresetPreview { get; } = [];

    [RelayCommand]
    private async Task SyncAsync()
    {
        await _sync.SyncAsync();
        if (_sync.Install is null)
        {
            _status.Error(_sync.FileStatus);
        }
        else
        {
            _status.Info("Controles sincronizados");
        }
    }

    [RelayCommand]
    private async Task SaveFolderAsync()
    {
        var folder = FolderInput.Trim().Trim('"');
        if (folder.Length > 0 && !GameInstallLocator.IsChannelFolder(folder))
        {
            _status.Error("Esa carpeta no es de Star Citizen. Indica la carpeta LIVE, por ejemplo D:\\RSI\\StarCitizen\\LIVE.");
            return;
        }

        await _session.SaveSettingsAsync(_session.Settings with { GameFolder = folder });
        await _sync.Pending;
        FolderInput = folder;
        _status.Info(folder.Length == 0 ? "Carpeta del juego: deteccion automatica" : "Carpeta del juego guardada");
    }

    [RelayCommand]
    private async Task LinkPresetsAsync()
    {
        var updates = _session.Buttons
            .Where(b => string.IsNullOrWhiteSpace(b.GameAction) && _catalog.PresetLinkFor(b.Name) is not null)
            .Select(b => b with { GameAction = _catalog.PresetLinkFor(b.Name)! })
            .ToList();
        if (updates.Count == 0)
        {
            _status.Info("No hay modulos por defecto sin vincular.");
            return;
        }

        await _session.SaveButtonsAsync(updates);
        await _sync.Pending;
        _status.Info($"{updates.Count} modulos vinculados a acciones del juego");
    }

    private void Refresh()
    {
        var install = _sync.Install;
        GameFolderText = install is null ? "No detectada" : $"{install.ChannelFolder}  ({install.Source})";
        FileStatus = _sync.FileStatus;
        if (!_folderLoaded)
        {
            FolderInput = _session.Settings.GameFolder;
            _folderLoaded = true;
        }

        Links.Clear();
        foreach (var link in _sync.Links.OrderBy(l => l.ModuleName, StringComparer.OrdinalIgnoreCase))
        {
            Links.Add(new LinkRow(link.ModuleName, link.ActionLabel, link.KeyText, BindStatusText.Text(link.Status, _catalog.GameVersion), BindStatusText.Key(link.Status), link.Detail));
        }

        Rebinds.Clear();
        foreach (var rebind in _sync.Rebinds)
        {
            Rebinds.Add(new RebindRow(rebind.ActionMap, rebind.Action, rebind.Input.Raw, _catalog.FindByActionName(rebind.Action) is not null));
        }

        PresetPreview.Clear();
        foreach (var button in _session.Buttons.Where(b => string.IsNullOrWhiteSpace(b.GameAction)).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
        {
            var action = _catalog.Find(_catalog.PresetLinkFor(button.Name));
            if (action is null)
            {
                continue;
            }

            var resolution = BindingResolver.Resolve(action.Id, _catalog, _sync.Rebinds);
            PresetPreview.Add(new PresetLinkRow(
                button.Name,
                action.Label,
                ActionText.Display(button.Action),
                resolution.Action is null ? "sin tecla en el juego" : ActionText.Display(resolution.Action)));
        }

        HasPresetPreview = PresetPreview.Count > 0;
    }
}
