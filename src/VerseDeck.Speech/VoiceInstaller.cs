using System.Security.Cryptography;
using SharpCompress.Readers;

namespace VerseDeck.Speech;

public interface IVoiceInstaller
{
    Task InstallAsync(VoiceInfo voice, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Downloads a voice model on the player's request. This is the only code in VerseDeck's copilot that
/// uses the network, and a voice only counts as installed once the whole archive verified and extracted.
/// </summary>
public sealed class VoiceInstaller : IVoiceInstaller
{
    private const string DownloadsFolder = ".descargas";
    private const string StagingPrefix = ".tmp-";
    private const string ReplacedPrefix = ".old-";

    private readonly HttpClient _http;
    private readonly VoiceStore _store;

    public VoiceInstaller(HttpClient http, VoiceStore store)
    {
        _http = http;
        _store = store;
    }

    public async Task InstallAsync(VoiceInfo voice, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var downloads = Path.Combine(_store.Root, DownloadsFolder);
        var archive = Path.Combine(downloads, $"{voice.Folder}.{Guid.NewGuid():N}.tar.bz2");
        var staging = Path.Combine(_store.Root, $"{StagingPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(downloads);
        try
        {
            await DownloadAsync(voice, archive, progress, cancellationToken);
            await Task.Run(() => Extract(archive, staging, cancellationToken), cancellationToken);

            var extracted = Path.Combine(staging, voice.Folder);
            if (!File.Exists(Path.Combine(extracted, voice.Model)) || !File.Exists(Path.Combine(extracted, VoiceStore.TokensFile)))
            {
                throw new InvalidDataException("El archivo descargado no contiene el modelo de voz esperado.");
            }

            Swap(extracted, _store.FolderOf(voice));
            progress?.Report(1);
        }
        finally
        {
            TryDelete(() => File.Delete(archive));
            TryDelete(() => Directory.Delete(staging, recursive: true));
        }
    }

    public void Remove(VoiceInfo voice)
    {
        var folder = _store.FolderOf(voice);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Removes what an interrupted install left behind. Call once at startup, before any install.</summary>
    public void CleanLeftovers()
    {
        if (!Directory.Exists(_store.Root))
        {
            return;
        }

        foreach (var folder in Directory.EnumerateDirectories(_store.Root).ToList())
        {
            var name = Path.GetFileName(folder);
            if (name.StartsWith(StagingPrefix, StringComparison.Ordinal) || name.StartsWith(ReplacedPrefix, StringComparison.Ordinal))
            {
                TryDelete(() => Directory.Delete(folder, recursive: true));
            }
        }

        var downloads = Path.Combine(_store.Root, DownloadsFolder);
        if (Directory.Exists(downloads))
        {
            foreach (var file in Directory.EnumerateFiles(downloads).ToList())
            {
                TryDelete(() => File.Delete(file));
            }
        }
    }

    // The old voice is moved aside first, so a failed move never leaves a half-removed folder that looks installed.
    private void Swap(string extracted, string target)
    {
        string? replaced = null;
        if (Directory.Exists(target))
        {
            replaced = Path.Combine(_store.Root, $"{ReplacedPrefix}{Guid.NewGuid():N}");
            Directory.Move(target, replaced);
        }

        try
        {
            Directory.Move(extracted, target);
        }
        catch
        {
            if (replaced is not null && !Directory.Exists(target))
            {
                Directory.Move(replaced, target);
            }

            throw;
        }

        if (replaced is not null)
        {
            TryDelete(() => Directory.Delete(replaced, recursive: true));
        }
    }

    private async Task DownloadAsync(VoiceInfo voice, string archive, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(voice.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"El servidor respondio {(int)response.StatusCode} al descargar la voz.");
        }

        var total = response.Content.Headers.ContentLength ?? voice.Bytes;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var file = File.Create(archive))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                done += read;

                // The rest of the bar is the extraction, reported by the caller as 1.
                progress?.Report(total > 0 ? Math.Min(0.9, 0.9 * done / total) : 0);
            }
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!actual.Equals(voice.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("La descarga no coincide con la voz publicada (SHA-256 distinto). No se ha instalado nada.");
        }
    }

    private static void Extract(string archive, string staging, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(staging);
        using var stream = File.OpenRead(archive);
        using var reader = ReaderFactory.OpenReader(stream);
        var buffer = new byte[81920];
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = reader.Entry;
            if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key) || !string.IsNullOrEmpty(entry.LinkTarget))
            {
                continue;
            }

            // An archive must not be able to write outside its own folder.
            var destination = Path.GetFullPath(Path.Combine(staging, entry.Key.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("El archivo de voz contiene rutas fuera de su carpeta. No se ha instalado nada.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var output = File.Create(destination);
            using var input = reader.OpenEntryStream();

            // Copied in pieces so that Cancel is noticed inside a model of hundreds of megabytes.
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
            }
        }
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Whatever cannot be removed now is removed by CleanLeftovers at the next start.
        }
    }
}
