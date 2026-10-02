using System.Xml;
using System.Xml.Linq;

namespace VerseDeck.Game;

public sealed record GameRebind(string ActionMap, string Action, ScInput Input, int MultiTap);

public sealed record ActionMapsFile(bool Ok, string? Error, IReadOnlyList<GameRebind> Rebinds);

/// <summary>Reads the player's rebinds. The file only holds what the player changed, never the defaults.</summary>
public static class ActionMapsReader
{
    public static ActionMapsFile Read(string path)
    {
        try
        {
            // The game may be writing the file; never ask for exclusive access.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);
            if (document.Root?.Name.LocalName != "ActionMaps")
            {
                return Failed("El archivo no es un actionmaps.xml de Star Citizen.");
            }

            var profiles = document.Root.Elements("ActionProfiles").ToList();
            var profile = profiles.FirstOrDefault(p => (string?)p.Attribute("profileName") == "default") ?? profiles.FirstOrDefault();
            if (profile is null)
            {
                return Failed("El archivo no contiene ningun perfil de controles.");
            }

            var rebinds =
                from map in profile.Elements("actionmap")
                from action in map.Elements("action")
                from rebind in action.Elements("rebind")
                select new GameRebind(
                    (string?)map.Attribute("name") ?? string.Empty,
                    (string?)action.Attribute("name") ?? string.Empty,
                    ScInput.Parse((string?)rebind.Attribute("input")),
                    int.TryParse((string?)rebind.Attribute("multiTap"), out var multiTap) ? multiTap : 1);
            return new ActionMapsFile(true, null, rebinds.ToList());
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Failed("No se encuentra actionmaps.xml.");
        }
        catch (XmlException)
        {
            return Failed("actionmaps.xml esta incompleto o no se puede leer como XML.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed($"No se puede abrir actionmaps.xml: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Reading the player's file must never take the app down, whatever it contains.
            return Failed($"No se pudo interpretar actionmaps.xml: {ex.Message}");
        }
    }

    private static ActionMapsFile Failed(string error) => new(false, error, []);
}
