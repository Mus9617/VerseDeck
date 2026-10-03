using System.Text.Json;
using VerseDeck.Core.Models;

namespace VerseDeck.Game;

public sealed record GameAction(string Id, string Label, string ActionMap, IReadOnlyList<string> ActionNames, string? DefaultInput, int PressMs, string Group = "General");

/// <summary>
/// VerseDeck's own table of game actions and their default keyboard keys. The game does not publish its
/// defaults in any readable file, so this is maintained by hand for one game version. Keys come from two
/// public 4.10 guides that agree; where they disagree the action has no default. Some internal names are
/// best effort: a wrong one only means a rebind of that action is not noticed, and the default key is used.
/// </summary>
public sealed class GameActionCatalog
{
    private readonly Dictionary<string, string> _presetLinks;

    private GameActionCatalog(string gameVersion, IReadOnlyList<GameAction> actions, Dictionary<string, string> presetLinks)
    {
        GameVersion = gameVersion;
        Actions = actions;
        _presetLinks = presetLinks;
    }

    public string GameVersion { get; }
    public IReadOnlyList<GameAction> Actions { get; }

    public static GameActionCatalog Load()
    {
        using var stream = typeof(GameActionCatalog).Assembly.GetManifestResourceStream("VerseDeck.Game.game-actions.json")
            ?? throw new InvalidOperationException("game-actions.json is missing from the assembly.");
        var file = JsonSerializer.Deserialize<CatalogFile>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("game-actions.json is empty.");
        var actions = file.Actions
            .Select(a => new GameAction(a.Id, a.Label, a.ActionMap, a.Actions, a.Default, a.PressMs, string.IsNullOrWhiteSpace(a.Group) ? "General" : a.Group))
            .ToList();
        return new GameActionCatalog(file.GameVersion, actions, new Dictionary<string, string>(file.PresetLinks, StringComparer.OrdinalIgnoreCase));
    }

    public GameAction? Find(string? id)
    {
        return Actions.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public GameAction? FindByActionName(string actionName)
    {
        return Actions.FirstOrDefault(a => a.ActionNames.Contains(actionName, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The catalog action a default VerseDeck module corresponds to, by module name.</summary>
    public string? PresetLinkFor(string moduleName)
    {
        return _presetLinks.GetValueOrDefault(moduleName.Trim());
    }

    private sealed record CatalogFile(string GameVersion, List<CatalogAction> Actions, Dictionary<string, string> PresetLinks);

    private sealed record CatalogAction(string Id, string Label, string ActionMap, List<string> Actions, string? Default, int PressMs, string? Group);
}

public enum BindStatus { YourKey, Default, NoKey, NotSendable, Conflict }

/// <summary>The key VerseDeck should send for a game action; Action is null when nothing can be sent.</summary>
public sealed record Resolution(BindStatus Status, KeyPressAction? Action, string Detail);

public static class BindingResolver
{
    private const int DefaultPressMs = 60;

    /// <param name="gameAction">A catalog id, or "map/action" for an action that only exists in the player's file.</param>
    public static Resolution Resolve(string gameAction, GameActionCatalog catalog, IReadOnlyList<GameRebind> rebinds)
    {
        var entry = catalog.Find(gameAction);
        IReadOnlyList<string> names;
        string map;
        if (entry is not null)
        {
            names = entry.ActionNames;
            map = entry.ActionMap;
        }
        else if (gameAction.Split('/', 2) is [var fileMap, var fileAction])
        {
            names = [fileAction];
            map = fileMap;
        }
        else
        {
            return new Resolution(BindStatus.NoKey, null, "La accion no esta en el catalogo ni en tu archivo.");
        }

        // Joystick and gamepad rebinds do not change which keyboard key triggers the action.
        // For catalog actions the map is not required to match: maps have been renamed between game versions.
        var candidates = rebinds
            .Where(r => names.Contains(r.Action, StringComparer.OrdinalIgnoreCase))
            .Where(r => entry is not null || r.ActionMap.Equals(map, StringComparison.OrdinalIgnoreCase))
            .Where(r => r.Input.Device is ScDevice.Keyboard or ScDevice.Mouse or ScDevice.Unknown)
            // An action of the same name in another map must not win over the one in the expected map.
            .OrderByDescending(r => r.ActionMap.Equals(map, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            if (entry?.DefaultInput is null)
            {
                return new Resolution(BindStatus.NoKey, null, entry is null
                    ? "La accion ya no esta en tu archivo."
                    : "El juego no trae tecla por defecto: asignala en Opciones > Keybindings.");
            }

            var defaultInput = ScInput.Parse($"kb1_{entry.DefaultInput}");
            if (!defaultInput.TryToKeyPress(entry.PressMs, out var defaultAction))
            {
                return new Resolution(BindStatus.NotSendable, null, "La tecla por defecto del catalogo no se puede enviar.");
            }

            return ConflictsWith(defaultInput.Raw, map, names, rebinds)
                ? new Resolution(BindStatus.Conflict, defaultAction, "Otra accion de tu archivo usa la misma tecla.")
                : new Resolution(BindStatus.Default, defaultAction, $"Tecla por defecto del juego {catalog.GameVersion}.");
        }

        var pressMs = entry?.PressMs ?? DefaultPressMs;
        var sendable = candidates.FirstOrDefault(c => c.MultiTap <= 1 && c.Input.TryToKeyPress(pressMs, out _));
        if (sendable is null)
        {
            var first = candidates[0];
            if (first.Input.IsUnbound)
            {
                return new Resolution(BindStatus.NoKey, null, "Has dejado esta accion sin tecla en el juego.");
            }

            return new Resolution(BindStatus.NotSendable, null, first.Input.Device == ScDevice.Mouse
                ? "Esta asignada a un boton de raton; VerseDeck solo envia teclado."
                : first.MultiTap > 1
                    ? "Requiere doble pulsacion; VerseDeck envia una sola."
                    : $"VerseDeck no conoce la tecla '{first.Input.Raw}'.");
        }

        sendable.Input.TryToKeyPress(pressMs, out var action);
        return ConflictsWith(sendable.Input.Raw, sendable.ActionMap, names, rebinds)
            ? new Resolution(BindStatus.Conflict, action, "Otra accion de tu archivo usa la misma tecla.")
            : new Resolution(BindStatus.YourKey, action, "Tecla leida de tu actionmaps.xml.");
    }

    private static bool ConflictsWith(string raw, string map, IReadOnlyList<string> ownNames, IReadOnlyList<GameRebind> rebinds)
    {
        return rebinds.Any(r =>
            r.ActionMap.Equals(map, StringComparison.OrdinalIgnoreCase)
            && !ownNames.Contains(r.Action, StringComparer.OrdinalIgnoreCase)
            && r.Input.Device == ScDevice.Keyboard
            && !r.Input.IsUnbound
            && r.Input.Raw.Trim().Equals(raw.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
