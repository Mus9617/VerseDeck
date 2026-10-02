using System.Security.Cryptography;
using SharpCompress.Readers;

namespace VerseDeck.Speech;

/// <summary>
/// Downloads a voice model on the player's request. This is the only code in VerseDeck's copilot that
/// uses the network, and a voice only counts as installed once the whole archive verified and extracted.
/// </summary>
public interface IVoiceInstaller
{
    Task InstallAsync(VoiceInfo voice, IProgress<double>? progress, CancellationToken cancellationToken);
}

public sealed class VoiceInstaller : IVoiceInstaller
{
    private readonly HttpClient _http;
    private readonly VoiceStore _store;

    public VoiceInstaller(HttpClient http, VoiceStore store)
    {
        _http = http;
        _store = store;
    }

    public async Task InstallAsync(VoiceInfo voice, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var downloads = Path.Combine(_store.Root, ".descargas");
        var archive = Path.Combine(downloads, $"{voice.Folder}.{Guid.NewGuid():N}.tar.bz2");
        var staging = Path.Combine(_store.Root, $".tmp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(downloads);
        try
        {
            await DownloadAsync(voice, archive, progress, cancellationToken);
            await Task.Run(() => Extract(archive, staging, cancellationToken), cancellationToken);

            var extracted = Path.Combine(staging, voice.Folder);
            if (!File.Exists(Path.Combine(extracted, voice.Model)))
            {
                throw new InvalidDataException("El archivo descargado no contiene el modelo de voz esperado.");
            }

            var target = _store.FolderOf(voice);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.Move(extracted, target);
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

                // The last few percent are the extraction, reported by the caller as 1.
                progress?.Report(total > 0 ? Math.Min(0.95, 0.95 * done / total) : 0);
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
            input.CopyTo(output);
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
            // Leftover temporary files are harmless and are overwritten by name on the next attempt.
        }
    }
}
