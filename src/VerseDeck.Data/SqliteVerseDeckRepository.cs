using System.Text.Json;
using Microsoft.Data.Sqlite;
using VerseDeck.Core.Models;

namespace VerseDeck.Data;

public sealed class SqliteVerseDeckRepository : IVerseDeckRepository
{
    private readonly string _connectionString;

    public SqliteVerseDeckRepository(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
        CREATE TABLE IF NOT EXISTS Profiles (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            ShipName TEXT NOT NULL,
            Role TEXT NOT NULL,
            IsActive INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS Ships (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL UNIQUE
        );
        CREATE TABLE IF NOT EXISTS Panels (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ProfileId INTEGER NOT NULL,
            Name TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS Buttons (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ProfileId INTEGER NOT NULL,
            Name TEXT NOT NULL,
            Icon TEXT NOT NULL,
            AccentColor TEXT NOT NULL,
            Category TEXT NOT NULL,
            ActionKey TEXT NOT NULL,
            ActionModifiers TEXT NOT NULL,
            PressDurationMs INTEGER NOT NULL,
            RequiresConfirmation INTEGER NOT NULL,
            MobileHaptics INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS VoiceCommands (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ButtonId INTEGER NOT NULL,
            Phrase TEXT NOT NULL,
            MinimumConfidence REAL NOT NULL,
            Enabled INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS KeyBindings (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Source TEXT NOT NULL,
            ActionName TEXT NOT NULL,
            BoundKey TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS MarketRuns (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            CreatedAt TEXT NOT NULL,
            Commodity TEXT NOT NULL,
            BuyLocation TEXT NOT NULL,
            SellLocation TEXT NOT NULL,
            Scu REAL NOT NULL,
            BuyPrice REAL NOT NULL,
            SellPrice REAL NOT NULL,
            ExtraCosts REAL NOT NULL
        );
        CREATE TABLE IF NOT EXISTS Settings (
            Key TEXT PRIMARY KEY,
            Value TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS ConnectedDevices (
            Id TEXT PRIMARY KEY,
            Name TEXT NOT NULL,
            RemoteAddress TEXT NOT NULL,
            ConnectedAt TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS CommandLog (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            CreatedAt TEXT NOT NULL,
            Source TEXT NOT NULL,
            Command TEXT NOT NULL,
            Result TEXT NOT NULL
        );
        """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureDefaultDeckAsync(connection, cancellationToken);
    }

    public async Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Key, Value FROM Settings";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return new AppSettings(
            int.Parse(values.GetValueOrDefault("MobilePort", "4785")),
            values.GetValueOrDefault("PairingPin", "2468"),
            bool.Parse(values.GetValueOrDefault("VoiceEnabled", "False")),
            values.GetValueOrDefault("Theme", "ColdBlue"),
            double.Parse(values.GetValueOrDefault("VoiceMinimumConfidence", "0.40")),
            values.GetValueOrDefault("VoiceActivationMode", "PushToTalk"),
            values.GetValueOrDefault("PushToTalkDevice", "Keyboard"),
            values.GetValueOrDefault("PushToTalkBinding", "F13"),
            bool.Parse(values.GetValueOrDefault("CommandSoundEnabled", "True")),
            bool.Parse(values.GetValueOrDefault("WelcomeSoundEnabled", "True")));
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await UpsertSetting(connection, "MobilePort", settings.MobilePort.ToString(), cancellationToken);
        await UpsertSetting(connection, "PairingPin", settings.PairingPin, cancellationToken);
        await UpsertSetting(connection, "VoiceEnabled", settings.VoiceEnabled.ToString(), cancellationToken);
        await UpsertSetting(connection, "Theme", settings.Theme, cancellationToken);
        await UpsertSetting(connection, "VoiceMinimumConfidence", settings.VoiceMinimumConfidence.ToString("0.00"), cancellationToken);
        await UpsertSetting(connection, "VoiceActivationMode", settings.VoiceActivationMode, cancellationToken);
        await UpsertSetting(connection, "PushToTalkDevice", settings.PushToTalkDevice, cancellationToken);
        await UpsertSetting(connection, "PushToTalkBinding", settings.PushToTalkBinding, cancellationToken);
        await UpsertSetting(connection, "CommandSoundEnabled", settings.CommandSoundEnabled.ToString(), cancellationToken);
        await UpsertSetting(connection, "WelcomeSoundEnabled", settings.WelcomeSoundEnabled.ToString(), cancellationToken);
    }

    public async Task<IReadOnlyList<Profile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, ShipName, Role, IsActive FROM Profiles ORDER BY IsActive DESC, Name";
        var results = new List<Profile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new Profile(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4) == 1));
        }

        return results;
    }

    public async Task<Profile> SaveProfileAsync(Profile profile, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        if (profile.IsActive)
        {
            var deactivate = connection.CreateCommand();
            deactivate.CommandText = "UPDATE Profiles SET IsActive = 0";
            await deactivate.ExecuteNonQueryAsync(cancellationToken);
        }

        var command = connection.CreateCommand();
        if (profile.Id == 0)
        {
            command.CommandText = "INSERT INTO Profiles (Name, ShipName, Role, IsActive) VALUES ($name, $ship, $role, $active); SELECT last_insert_rowid();";
        }
        else
        {
            command.CommandText = "UPDATE Profiles SET Name=$name, ShipName=$ship, Role=$role, IsActive=$active WHERE Id=$id; SELECT $id;";
            command.Parameters.AddWithValue("$id", profile.Id);
        }

        command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.AddWithValue("$ship", profile.ShipName);
        command.Parameters.AddWithValue("$role", profile.Role);
        command.Parameters.AddWithValue("$active", profile.IsActive ? 1 : 0);
        var id = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? profile.Id);
        return profile with { Id = id };
    }

    public async Task<IReadOnlyList<DeckButton>> GetButtonsAsync(long profileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
        SELECT Id, ProfileId, Name, Icon, AccentColor, Category, ActionKey, ActionModifiers, PressDurationMs, RequiresConfirmation, MobileHaptics
        FROM Buttons WHERE ProfileId=$profileId ORDER BY Name
        """;
        command.Parameters.AddWithValue("$profileId", profileId);
        var results = new List<DeckButton>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var modifiers = JsonSerializer.Deserialize<List<string>>(reader.GetString(7)) ?? [];
            results.Add(new DeckButton(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), new KeyPressAction(reader.GetString(6), modifiers, reader.GetInt32(8)),
                reader.GetInt32(9) == 1, reader.GetInt32(10) == 1));
        }

        return results;
    }

    public async Task<DeckButton> SaveButtonAsync(DeckButton button, CancellationToken cancellationToken = default)
    {
        button.Action.Validate();
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        if (button.Id == 0)
        {
            command.CommandText = """
            INSERT INTO Buttons (ProfileId, Name, Icon, AccentColor, Category, ActionKey, ActionModifiers, PressDurationMs, RequiresConfirmation, MobileHaptics)
            VALUES ($profileId, $name, $icon, $accent, $category, $key, $modifiers, $duration, $confirm, $haptics);
            SELECT last_insert_rowid();
            """;
        }
        else
        {
            command.CommandText = """
            UPDATE Buttons SET ProfileId=$profileId, Name=$name, Icon=$icon, AccentColor=$accent, Category=$category,
            ActionKey=$key, ActionModifiers=$modifiers, PressDurationMs=$duration, RequiresConfirmation=$confirm, MobileHaptics=$haptics
            WHERE Id=$id; SELECT $id;
            """;
            command.Parameters.AddWithValue("$id", button.Id);
        }

        command.Parameters.AddWithValue("$profileId", button.ProfileId);
        command.Parameters.AddWithValue("$name", button.Name);
        command.Parameters.AddWithValue("$icon", button.Icon);
        command.Parameters.AddWithValue("$accent", button.AccentColor);
        command.Parameters.AddWithValue("$category", button.Category);
        command.Parameters.AddWithValue("$key", button.Action.Key);
        command.Parameters.AddWithValue("$modifiers", JsonSerializer.Serialize(button.Action.Modifiers));
        command.Parameters.AddWithValue("$duration", button.Action.PressDurationMs);
        command.Parameters.AddWithValue("$confirm", button.RequiresConfirmation ? 1 : 0);
        command.Parameters.AddWithValue("$haptics", button.MobileHaptics ? 1 : 0);
        var id = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? button.Id);
        return button with { Id = id };
    }

    public async Task DeleteButtonAsync(long buttonId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var deleteVoice = connection.CreateCommand();
        deleteVoice.Transaction = (SqliteTransaction)transaction;
        deleteVoice.CommandText = "DELETE FROM VoiceCommands WHERE ButtonId=$buttonId";
        deleteVoice.Parameters.AddWithValue("$buttonId", buttonId);
        await deleteVoice.ExecuteNonQueryAsync(cancellationToken);

        var deleteButton = connection.CreateCommand();
        deleteButton.Transaction = (SqliteTransaction)transaction;
        deleteButton.CommandText = "DELETE FROM Buttons WHERE Id=$buttonId";
        deleteButton.Parameters.AddWithValue("$buttonId", buttonId);
        await deleteButton.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<VoiceCommand>> GetVoiceCommandsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ButtonId, Phrase, MinimumConfidence, Enabled FROM VoiceCommands ORDER BY Phrase";
        var results = new List<VoiceCommand>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new VoiceCommand(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetDouble(3), reader.GetInt32(4) == 1));
        }

        return results;
    }

    public async Task<VoiceCommand> SaveVoiceCommandAsync(VoiceCommand voiceCommand, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        if (voiceCommand.Id == 0)
        {
            command.CommandText = "INSERT INTO VoiceCommands (ButtonId, Phrase, MinimumConfidence, Enabled) VALUES ($buttonId, $phrase, $confidence, $enabled); SELECT last_insert_rowid();";
        }
        else
        {
            command.CommandText = "UPDATE VoiceCommands SET ButtonId=$buttonId, Phrase=$phrase, MinimumConfidence=$confidence, Enabled=$enabled WHERE Id=$id; SELECT $id;";
            command.Parameters.AddWithValue("$id", voiceCommand.Id);
        }

        command.Parameters.AddWithValue("$buttonId", voiceCommand.ButtonId);
        command.Parameters.AddWithValue("$phrase", voiceCommand.Phrase);
        command.Parameters.AddWithValue("$confidence", voiceCommand.MinimumConfidence);
        command.Parameters.AddWithValue("$enabled", voiceCommand.Enabled ? 1 : 0);
        var id = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? voiceCommand.Id);
        return voiceCommand with { Id = id };
    }

    public async Task AddCommandLogAsync(string source, string commandText, string result, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO CommandLog (CreatedAt, Source, Command, Result) VALUES ($created, $source, $command, $result)";
        command.Parameters.AddWithValue("$created", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$command", commandText);
        command.Parameters.AddWithValue("$result", result);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CommandLogEntry>> GetRecentCommandLogAsync(int count, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, CreatedAt, Source, Command, Result FROM CommandLog ORDER BY Id DESC LIMIT $count";
        command.Parameters.AddWithValue("$count", count);
        var results = new List<CommandLogEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new CommandLogEntry(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        return results;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task UpsertSetting(SqliteConnection connection, string key, string value, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Settings (Key, Value) VALUES ($key, $value) ON CONFLICT(Key) DO UPDATE SET Value=$value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureDefaultDeckAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var repo = new SqliteVerseDeckRepository(connection.DataSource);
        var profiles = await repo.GetProfilesAsync(cancellationToken);
        var profile = profiles.FirstOrDefault(p => p.IsActive);
        if (profile is null)
        {
            profile = await repo.SaveProfileAsync(new Profile(0, "Global", "Starter Ship", "Exploracion", true), cancellationToken);
            await repo.SaveSettingsAsync(new AppSettings(4785, Random.Shared.Next(1000, 9999).ToString(), false, "ColdBlue", 0.40), cancellationToken);
        }

        await ApplyOneTimeDefaultsAsync(connection, cancellationToken);

        var existingButtons = await repo.GetButtonsAsync(profile.Id, cancellationToken);
        var existingVoice = await repo.GetVoiceCommandsAsync(cancellationToken);

        foreach (var preset in DefaultButtonPresets)
        {
            var button = existingButtons.FirstOrDefault(b => b.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
            if (button is null)
            {
                button = await repo.SaveButtonAsync(new DeckButton(
                    0,
                    profile.Id,
                    preset.Name,
                    preset.Icon,
                    preset.AccentColor,
                    preset.Category,
                    preset.Action,
                    preset.RequiresConfirmation,
                    true), cancellationToken);
            }

            foreach (var phrase in preset.Phrases)
            {
                if (!existingVoice.Any(v => v.ButtonId == button.Id && v.Phrase.Equals(phrase, StringComparison.OrdinalIgnoreCase)))
                {
                    await repo.SaveVoiceCommandAsync(new VoiceCommand(0, button.Id, phrase, 0.40, true), cancellationToken);
                }
            }
        }
    }

    private static async Task ApplyOneTimeDefaultsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var confirmationReset = connection.CreateCommand();
        confirmationReset.CommandText = "SELECT Value FROM Settings WHERE Key='ConfirmationDefaultsV3'";
        if (await confirmationReset.ExecuteScalarAsync(cancellationToken) is null)
        {
            var update = connection.CreateCommand();
            update.CommandText = """
            UPDATE Buttons SET RequiresConfirmation = 0;
            INSERT INTO Settings (Key, Value) VALUES ('ConfirmationDefaultsV3', 'Done');
            """;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        var confidenceReset = connection.CreateCommand();
        confidenceReset.CommandText = "SELECT Value FROM Settings WHERE Key='VoiceConfidenceDefaultsV4'";
        if (await confidenceReset.ExecuteScalarAsync(cancellationToken) is null)
        {
            var update = connection.CreateCommand();
            update.CommandText = """
            UPDATE VoiceCommands SET MinimumConfidence = 0.40 WHERE MinimumConfidence > 0.40;
            INSERT INTO Settings (Key, Value) VALUES ('VoiceConfidenceDefaultsV4', 'Done');
            INSERT INTO Settings (Key, Value) VALUES ('VoiceMinimumConfidence', '0.40')
            ON CONFLICT(Key) DO UPDATE SET Value='0.40';
            """;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static readonly IReadOnlyList<DefaultButtonPreset> DefaultButtonPresets =
    [
        new("Flight Ready", "power", "#35F2A2", "Flight", new KeyPressAction("R", ["Alt"], 60), false, ["encender nave", "preparar nave", "nave lista", "flight ready", "ship ready"]),
        new("Power Toggle", "power", "#FFD166", "Flight", new KeyPressAction("U", [], 60), false, ["activar energia", "apagar energia", "cortar energia", "power toggle", "power on", "power off"]),
        new("Engines Toggle", "engine", "#FF8A3D", "Flight", new KeyPressAction("I", [], 60), false, ["encender motores", "apagar motores", "activar motores", "desactivar motores", "engines on", "engines off", "toggle engines"]),
        new("Landing Gear", "gear", "#29D9FF", "Flight", KeyPressAction.DefaultLandingGear, false, ["sacar tren de aterrizaje", "guardar tren de aterrizaje", "bajar tren", "subir tren", "landing gear", "gear down", "gear up"]),
        new("Lights", "lights", "#F7E967", "Systems", new KeyPressAction("L", [], 60), false, ["activar luces", "activar luzes", "apagar luces", "apagar luzes", "lights on", "lights off", "toggle lights"]),
        new("Radar Ping", "radar", "#65E4FF", "Scan", new KeyPressAction("TAB", [], 60), false, ["activar radar", "ping radar", "escanear radar", "radar ping", "scan ping"]),
        new("Scan Mode", "scan", "#54D6A7", "Scan", new KeyPressAction("V", [], 60), false, ["activar escaner", "modo escaner", "iniciar escaneo", "scan mode", "scanner mode"]),
        new("Quantum Mode", "quantum", "#B78CFF", "Navigation", new KeyPressAction("B", [], 60), false, ["activar salto", "modo quantum", "activar quantum", "preparar salto", "quantum mode", "activate quantum", "quantum jump"]),
        new("Star Map", "map", "#4CC9F0", "Navigation", new KeyPressAction("F2", [], 60), false, ["abrir mapa", "abrir mapa estelar", "cerrar mapa", "star map", "open map"]),
        new("Shields", "shield", "#2EF6D1", "Combat", new KeyPressAction("O", [], 60), false, ["activar escudos", "apagar escudos", "subir escudos", "shields", "shields on", "shields off"]),
        new("Weapons", "weapons", "#FF4D6D", "Combat", new KeyPressAction("P", [], 60), false, ["activar armas", "guardar armas", "armar nave", "weapons", "weapons on", "weapons off"]),
        new("Doors", "doors", "#F77F00", "Utility", new KeyPressAction("K", [], 60), false, ["abrir puertas", "cerrar puertas", "open doors", "close doors"]),
        new("Cargo", "cargo", "#A7C957", "Utility", new KeyPressAction("J", [], 60), false, ["abrir carga", "panel de carga", "cargo", "cargo panel"]),
        new("Comms", "comms", "#90DBF4", "Utility", new KeyPressAction("F11", [], 60), false, ["abrir comunicaciones", "abrir comms", "comunicaciones", "open comms", "communications"]),
        new("Eject", "eject", "#FF2E2E", "Emergency", new KeyPressAction("Y", ["Alt"], 60), false, ["eyectar", "eyeccion", "eject"]),
        new("Self Destruct", "warning", "#FF2E2E", "Emergency", new KeyPressAction("BACKSPACE", ["Alt"], 60), false, ["autodestruccion", "cancelar nave", "self destruct"])
    ];

    private sealed record DefaultButtonPreset(
        string Name,
        string Icon,
        string AccentColor,
        string Category,
        KeyPressAction Action,
        bool RequiresConfirmation,
        IReadOnlyList<string> Phrases);
}
