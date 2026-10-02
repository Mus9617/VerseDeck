using Microsoft.Data.Sqlite;
using VerseDeck.Data;

namespace VerseDeck.Tests;

public sealed class TempDatabase : IAsyncDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"versedeck-test-{Guid.NewGuid():N}.db");

    public async Task<SqliteVerseDeckRepository> CreateAsync()
    {
        var repository = new SqliteVerseDeckRepository(Path);
        await repository.InitializeAsync();
        return repository;
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    public ValueTask DisposeAsync()
    {
        // Only this database's pool: clearing every pool would close connections other test classes are using.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path }.ToString()))
        {
            SqliteConnection.ClearPool(connection);
        }

        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
            // A leftover temp file must not fail the test run.
        }

        return ValueTask.CompletedTask;
    }
}
