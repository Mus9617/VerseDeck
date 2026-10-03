using System.Net;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Writers;
using VerseDeck.Core.Models;
using VerseDeck.Speech;

namespace VerseDeck.Tests;

public static class SpeechFixture
{
    public static readonly VoiceInfo Voice = VoiceCatalog.Load().Find(VoiceCatalog.DefaultVoiceId)!;

    public static string TempFolder(string prefix)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"versedeck-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static DeckButton Button(string name, string gameAction = "", string response = "", int pressMs = 60, long id = 1)
    {
        return new DeckButton(id, 1, name, "power", "#49E7FF", "Flight", new KeyPressAction("N", [], pressMs), false, true, gameAction, response);
    }

    public static SpeechAudio Tone(double seconds = 0.5, int sampleRate = 22050)
    {
        var samples = new float[(int)(seconds * sampleRate)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(0.2 * Math.Sin(i * 0.05));
        }

        return new SpeechAudio(samples, sampleRate);
    }
}

public class VoiceCatalogTests
{
    private static readonly VoiceCatalog Catalog = VoiceCatalog.Load();

    [Fact]
    public void Load_HasSixVoices_FullyDescribed()
    {
        Assert.Equal(6, Catalog.Voices.Count);
        Assert.Equal(6, Catalog.Voices.Select(v => v.Id).Distinct().Count());
        Assert.All(Catalog.Voices, v =>
        {
            Assert.Matches("^[0-9a-f]{64}$", v.Sha256);
            Assert.StartsWith("https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/", v.Url);
            Assert.EndsWith($"{v.Folder}.tar.bz2", v.Url);
            Assert.True(v.Bytes > 1_000_000);
            Assert.False(string.IsNullOrWhiteSpace(v.Name));
            Assert.False(string.IsNullOrWhiteSpace(v.License));
            Assert.Contains(v.Engine, new[] { "piper", "kokoro" });
        });
    }

    [Fact]
    public void DefaultVoice_Exists()
    {
        Assert.NotNull(Catalog.Find(VoiceCatalog.DefaultVoiceId));
        Assert.Null(Catalog.Find("nope"));
        Assert.Null(Catalog.Find(null));
    }

    [Fact]
    public void VoicesSharingAFolder_ShareTheDownload()
    {
        var sharvard = Catalog.Voices.Where(v => v.Id.StartsWith("piper-sharvard")).ToList();

        Assert.Equal(2, sharvard.Count);
        Assert.Single(sharvard.Select(v => v.Sha256).Distinct());
        Assert.Equal([0, 1], sharvard.Select(v => v.Speaker));
    }

    [Fact]
    public void Store_VoiceIsInstalled_OnlyWhenItsModelFileExists()
    {
        var root = SpeechFixture.TempFolder("voices");
        try
        {
            var store = new VoiceStore(root);
            Assert.False(store.IsInstalled(SpeechFixture.Voice));

            Directory.CreateDirectory(store.FolderOf(SpeechFixture.Voice));
            Assert.False(store.IsInstalled(SpeechFixture.Voice));

            File.WriteAllText(Path.Combine(store.FolderOf(SpeechFixture.Voice), SpeechFixture.Voice.Model), "x");
            File.WriteAllText(Path.Combine(store.FolderOf(SpeechFixture.Voice), VoiceStore.TokensFile), "x");
            Assert.True(store.IsInstalled(SpeechFixture.Voice));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class PhraseCacheTests : IDisposable
{
    private readonly string _root = SpeechFixture.TempFolder("cache");
    private readonly VoiceInfo _other = VoiceCatalog.Load().Find("piper-claude-mx")!;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Store_ThenTryGet_ReturnsSameFile_WithDuration()
    {
        var cache = new PhraseCache(_root);

        var stored = cache.Store(SpeechFixture.Voice, "Tren de aterrizaje.", SpeechFixture.Tone(0.5));
        var found = cache.TryGet(SpeechFixture.Voice, "Tren de aterrizaje.");

        Assert.Equal(stored.Path, found!.Path);
        Assert.Equal(0.5, found.Duration.TotalSeconds, 2);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void DifferentVoiceOrText_IsADifferentEntry()
    {
        var cache = new PhraseCache(_root);
        cache.Store(SpeechFixture.Voice, "Luces.", SpeechFixture.Tone());

        Assert.Null(cache.TryGet(_other, "Luces."));
        Assert.Null(cache.TryGet(SpeechFixture.Voice, "luces."));
        Assert.NotNull(cache.TryGet(SpeechFixture.Voice, "Luces."));
    }

    [Fact]
    public void StoredFile_IsAValidPcmWav()
    {
        var cache = new PhraseCache(_root);
        var stored = cache.Store(SpeechFixture.Voice, "Motores.", SpeechFixture.Tone(0.25, 24000));

        var bytes = File.ReadAllBytes(stored.Path);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(bytes, 8, 8));
        Assert.Equal(1, BitConverter.ToInt16(bytes, 20));
        Assert.Equal(1, BitConverter.ToInt16(bytes, 22));
        Assert.Equal(24000, BitConverter.ToInt32(bytes, 24));
        Assert.Equal(16, BitConverter.ToInt16(bytes, 34));
        Assert.Equal(bytes.Length - 44, BitConverter.ToInt32(bytes, 40));
        Assert.Equal(6000 * 2, bytes.Length - 44);
    }

    [Fact]
    public void LoudSamples_AreClamped_NotWrapped()
    {
        var cache = new PhraseCache(_root);
        var stored = cache.Store(SpeechFixture.Voice, "x", new SpeechAudio([2f, -2f], 22050));

        var bytes = File.ReadAllBytes(stored.Path);

        Assert.Equal(short.MaxValue, BitConverter.ToInt16(bytes, 44));
        Assert.Equal(-short.MaxValue, BitConverter.ToInt16(bytes, 46));
    }

    [Fact]
    public void Clear_RemovesEveryPhrase()
    {
        var cache = new PhraseCache(_root);
        cache.Store(SpeechFixture.Voice, "a", SpeechFixture.Tone());
        cache.Store(_other, "b", SpeechFixture.Tone());

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Null(cache.TryGet(SpeechFixture.Voice, "a"));
    }

    [Fact]
    public void MissingFolder_IsEmpty_NotAnError()
    {
        var cache = new PhraseCache(Path.Combine(_root, "never-created"));

        Assert.Equal(0, cache.Count);
        Assert.Null(cache.TryGet(SpeechFixture.Voice, "a"));
        cache.Clear();
    }
}

public class ResponsePackTests
{
    private static readonly IReadOnlyList<ResponsePack> Packs = ResponsePack.LoadAll();
    private static ResponsePack Sobria => Packs.First(p => p.Id == "sobria");

    private static ResponseSelector Selector(int seed = 1) => new(Sobria, new Random(seed));

    [Fact]
    public void LoadAll_HasThreePacks_SobriaFirst_EachComplete()
    {
        Assert.Equal(["sobria", "caracter", "militar"], Packs.Select(p => p.Id));
        Assert.All(Packs, p =>
        {
            Assert.NotEmpty(p.Greeting);
            Assert.NotEmpty(p.Generic);
            Assert.NotEmpty(p.Hold);
            Assert.NotEmpty(p.Failed);
            Assert.NotEmpty(p.NoKey);
            Assert.NotEmpty(p.Profile);
            Assert.All(p.Generic.Concat(p.Hold).Concat(p.Profile), t => Assert.Contains("{nombre}", t));
            Assert.All(p.Actions.Values, variants => Assert.NotEmpty(variants));
        });
    }

    [Fact]
    public void EveryPack_CoversTheSameActions()
    {
        var expected = Sobria.Actions.Keys.OrderBy(k => k).ToList();

        Assert.Equal(31, expected.Count);
        Assert.All(Packs, p => Assert.Equal(expected, p.Actions.Keys.OrderBy(k => k)));
    }

    [Fact]
    public void ForModule_CustomText_WinsOverEverything()
    {
        var button = SpeechFixture.Button("Landing Gear", "landing_gear", "A sus pies.");

        Assert.Equal("A sus pies.", Selector().ForModule(button));
    }

    [Fact]
    public void ForModule_Silent_ReturnsNull()
    {
        Assert.Null(Selector().ForModule(SpeechFixture.Button("Landing Gear", "landing_gear", "-")));
    }

    [Fact]
    public void ForModule_LinkedAction_UsesThePackPhrase()
    {
        var phrase = Selector().ForModule(SpeechFixture.Button("Mi tren", "landing_gear"));

        Assert.Contains(phrase, Sobria.Actions["landing_gear"]);
    }

    [Fact]
    public void ForModule_UnlinkedDefaultModule_UsesItsNameAlias()
    {
        var phrase = Selector().ForModule(SpeechFixture.Button("Star Map"));

        Assert.Equal("Mapa estelar.", phrase);
    }

    [Fact]
    public void ForModule_UnknownModule_UsesTheGenericTemplate()
    {
        var phrase = Selector().ForModule(SpeechFixture.Button("Minar"));

        Assert.Contains(phrase, new[] { "Minar, hecho.", "Minar." });
    }

    [Fact]
    public void ForModule_UnknownLongPressModule_UsesTheHoldTemplate()
    {
        Assert.Equal("Minar, manteniendo.", Selector().ForModule(SpeechFixture.Button("Minar", pressMs: 1000)));
    }

    [Fact]
    public void ForModule_FileOnlyAction_FallsBackToTheModuleName()
    {
        var phrase = Selector().ForModule(SpeechFixture.Button("Lights", "spaceship_movement/v_something"));

        Assert.Contains(phrase, Sobria.Actions["headlights"]);
    }

    [Fact]
    public void ForModule_NeverRepeatsTheSameVariantTwiceInARow()
    {
        var selector = Selector(7);
        var button = SpeechFixture.Button("Landing Gear", "landing_gear");
        var previous = selector.ForModule(button);

        for (var i = 0; i < 40; i++)
        {
            var next = selector.ForModule(button);
            Assert.NotEqual(previous, next);
            previous = next;
        }
    }

    [Fact]
    public void ForModule_CustomVariants_AreSplitAndRotated()
    {
        var selector = Selector();
        var button = SpeechFixture.Button("Lights", response: " Luces. | Hecho, luces. ");

        var said = Enumerable.Range(0, 10).Select(_ => selector.ForModule(button)).ToHashSet();

        Assert.Equal(new HashSet<string?> { "Luces.", "Hecho, luces." }, said);
    }

    [Fact]
    public void SingleVariant_IsAllowedToRepeat()
    {
        var selector = Selector();
        var button = SpeechFixture.Button("Star Map");

        Assert.Equal(selector.ForModule(button), selector.ForModule(button));
    }

    [Fact]
    public void EventPhrases_ComeFromThePack()
    {
        var selector = Selector();

        Assert.Equal("No he podido enviarlo.", selector.Failed());
        Assert.Equal("Esa acción no tiene tecla en el juego.", selector.NoKey());
        Assert.Equal("Perfil Combate.", selector.Profile("Combate"));
        Assert.Equal("Sistemas listos.", selector.Greeting());
    }

    [Fact]
    public void AllFor_ListsEveryVariantThatCouldBeSaid_Once()
    {
        DeckButton[] buttons =
        [
            SpeechFixture.Button("Landing Gear", "landing_gear", id: 1),
            SpeechFixture.Button("Otro tren", "landing_gear", id: 2),
            SpeechFixture.Button("Minar", id: 3),
            SpeechFixture.Button("Callado", response: "-", id: 4)
        ];

        var all = Selector().AllFor(buttons);

        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Contains("Tren de aterrizaje.", all);
        Assert.Contains("Tren, hecho.", all);
        Assert.Contains("Minar, hecho.", all);
        Assert.Contains("Minar.", all);
        Assert.Contains("No he podido enviarlo.", all);
        Assert.Contains("Sistemas listos.", all);
        Assert.DoesNotContain(all, t => t.Contains("Callado"));
    }

    [Fact]
    public void NoDefaultPhrase_ClaimsAShipState()
    {
        string[] stateWords = ["desplegado", "retraído", "encendid", "apagad", "activad", "desactivad", "abiert", "cerrad"];

        var claims = Packs.SelectMany(p => p.Actions.Values.SelectMany(v => v))
            .Where(t => stateWords.Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase)));

        Assert.Empty(claims);
    }
}

public sealed class VoiceInstallerTests : IDisposable
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>Cancels the given source after the first chunk has been read, like a user pressing Cancel mid-download.</summary>
    private sealed class CancelAfterFirstRead(byte[] data, CancellationTokenSource source) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer[..Math.Min(buffer.Length, 16)], cancellationToken);
            source.Cancel();
            return read;
        }
    }

    private readonly string _root = SpeechFixture.TempFolder("install");
    private readonly VoiceStore _store;

    public VoiceInstallerTests()
    {
        _store = new VoiceStore(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static byte[] Archive(params (string Path, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var writer = WriterFactory.OpenWriter(output, ArchiveType.Tar, new WriterOptions(CompressionType.BZip2)))
        {
            foreach (var (path, content) in entries)
            {
                using var data = new MemoryStream(Encoding.UTF8.GetBytes(content));
                writer.Write(path, data, DateTime.UtcNow);
            }
        }

        return output.ToArray();
    }

    private static byte[] VoiceArchive(VoiceInfo voice)
    {
        return Archive(
            ($"{voice.Folder}/{voice.Model}", "model"),
            ($"{voice.Folder}/tokens.txt", "tokens"),
            ($"{voice.Folder}/espeak-ng-data/phontab", "data"));
    }

    private static VoiceInfo WithHashOf(byte[] archive)
    {
        return SpeechFixture.Voice with { Sha256 = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant(), Bytes = archive.Length };
    }

    private VoiceInstaller Installer(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new HttpClient(new FakeHandler(respond)), _store);

    private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private string[] LeftoverEntries() => Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
        .Where(p => !p.EndsWith(".descargas"))
        .ToArray();

    [Fact]
    public async Task Install_ExtractsTheVoice_AndReportsFullProgress()
    {
        var archive = VoiceArchive(SpeechFixture.Voice);
        var voice = WithHashOf(archive);
        var progress = new List<double>();

        await Installer(_ => Ok(archive)).InstallAsync(voice, new SyncProgress(progress.Add), CancellationToken.None);

        Assert.True(_store.IsInstalled(voice));
        Assert.True(File.Exists(Path.Combine(_store.FolderOf(voice), "espeak-ng-data", "phontab")));
        Assert.Equal(1, progress.Last());
        Assert.Equal(progress.OrderBy(p => p), progress);
    }

    [Fact]
    public async Task Install_WrongHash_Throws_AndLeavesNothing()
    {
        var archive = VoiceArchive(SpeechFixture.Voice);

        await Assert.ThrowsAsync<InvalidDataException>(() => Installer(_ => Ok(archive)).InstallAsync(SpeechFixture.Voice, null, CancellationToken.None));

        Assert.False(_store.IsInstalled(SpeechFixture.Voice));
        Assert.Empty(LeftoverEntries());
    }

    [Fact]
    public async Task Install_HttpError_Throws_AndLeavesNothing()
    {
        var installer = Installer(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<HttpRequestException>(() => installer.InstallAsync(SpeechFixture.Voice, null, CancellationToken.None));

        Assert.Empty(LeftoverEntries());
    }

    [Fact]
    public async Task Install_CancelledMidDownload_LeavesNothing()
    {
        var archive = VoiceArchive(SpeechFixture.Voice);
        var voice = WithHashOf(archive);
        using var cancel = new CancellationTokenSource();
        var installer = Installer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CancelAfterFirstRead(archive, cancel)) });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(voice, null, cancel.Token));

        Assert.False(_store.IsInstalled(voice));
        Assert.Empty(LeftoverEntries());
    }

    [Fact]
    public async Task Install_ArchiveWithoutTheModel_Throws_AndLeavesNothing()
    {
        var archive = Archive(($"{SpeechFixture.Voice.Folder}/readme.txt", "nothing useful"));
        var voice = WithHashOf(archive);

        await Assert.ThrowsAsync<InvalidDataException>(() => Installer(_ => Ok(archive)).InstallAsync(voice, null, CancellationToken.None));

        Assert.False(_store.IsInstalled(voice));
        Assert.Empty(LeftoverEntries());
    }

    [Fact]
    public async Task Install_EntryEscapingTheFolder_IsRejected()
    {
        var archive = Archive(
            ($"{SpeechFixture.Voice.Folder}/{SpeechFixture.Voice.Model}", "model"),
            ("../../escaped.txt", "evil"));
        var voice = WithHashOf(archive);

        await Assert.ThrowsAsync<InvalidDataException>(() => Installer(_ => Ok(archive)).InstallAsync(voice, null, CancellationToken.None));

        Assert.False(_store.IsInstalled(voice));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
    }

    [Fact]
    public async Task Install_OverAnExistingVoice_ReplacesIt()
    {
        var archive = VoiceArchive(SpeechFixture.Voice);
        var voice = WithHashOf(archive);
        Directory.CreateDirectory(_store.FolderOf(voice));
        File.WriteAllText(Path.Combine(_store.FolderOf(voice), "old.txt"), "stale");

        await Installer(_ => Ok(archive)).InstallAsync(voice, null, CancellationToken.None);

        Assert.True(_store.IsInstalled(voice));
        Assert.False(File.Exists(Path.Combine(_store.FolderOf(voice), "old.txt")));
    }

    [Fact]
    public async Task FailedInstall_CanBeRetried()
    {
        var archive = VoiceArchive(SpeechFixture.Voice);
        var voice = WithHashOf(archive);
        var attempt = 0;
        var installer = Installer(_ => ++attempt == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Ok(archive));

        await Assert.ThrowsAsync<HttpRequestException>(() => installer.InstallAsync(voice, null, CancellationToken.None));
        await installer.InstallAsync(voice, null, CancellationToken.None);

        Assert.True(_store.IsInstalled(voice));
    }

    [Fact]
    public async Task Remove_DeletesTheVoiceFolder()
    {
        var archive = VoiceArchive(SpeechFixture.Voice);
        var voice = WithHashOf(archive);
        var installer = Installer(_ => Ok(archive));
        await installer.InstallAsync(voice, null, CancellationToken.None);

        installer.Remove(voice);

        Assert.False(_store.IsInstalled(voice));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
