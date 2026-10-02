# Voz del copiloto — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que VerseDeck conteste con una voz neuronal local al ejecutar un comando, con interruptores general, por categoría y por módulo.

**Architecture:** Un proyecto sin interfaz, `VerseDeck.Speech`, contiene el catálogo de voces, el instalador, el motor sherpa-onnx, la caché de frases y las personalidades. En la app, `CopilotService` escucha a `ButtonExecutor` y a `DeckSession`, decide si toca hablar, saca el WAV de la caché (o lo genera en segundo plano) y lo reproduce con NAudio. Nada de esto está en el camino de la pulsación.

**Tech Stack:** .NET 8, org.k2fsa.sherpa.onnx, SharpCompress, NAudio, WPF, CommunityToolkit.Mvvm, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-02-copiloto-voz-design.md`

## Global Constraints

- Gratis y local. Red solo en `VoiceInstaller`, y solo por orden del usuario.
- La voz nunca retrasa ni impide una pulsación: `CopilotService` reacciona a eventos y no se espera desde `ButtonExecutor`.
- Voz natural, sin efectos.
- Frases por defecto que confirman la orden, no un estado de la nave.
- Copiloto desactivado por defecto; actualizar la app no cambia nada audible.
- Voz por defecto del catálogo: `piper-davefx`.
- ViewModels sin tipos de WPF. Textos de interfaz en español sin tildes; las frases habladas sí llevan tildes.
- Rama `copiloto-voz`. Verificación: `dotnet build VerseDeck.slnx`, `dotnet test tests/VerseDeck.Tests`.
- En los comandos de PowerShell de esta máquina no puede aparecer la palabra suelta `del`.

## Review Focus

1. **Descarga interrumpida o corrupta**: no queda una voz a medias que parezca instalada. Test en Tarea 3.
2. **Motor que falla o tarda**: el comando sale igual y a tiempo. Test en Tarea 5.
3. **Ráfaga de comandos**: solo se oye la última respuesta; una generación lenta de una frase anterior no suena después. Test en Tarea 5.
4. **El copiloto se oye a sí mismo** en modo de escucha continua: no se ejecuta un comando reconocido mientras habla. Test en Tarea 5.
5. **Base de datos anterior**: columna y ajustes nuevos sin perder nada, copiloto apagado. Test en Tarea 1.

---

### Task 1: Persistencia

**Files:** `DomainModels.cs`, `SqliteVerseDeckRepository.cs`, `RepositoryTests.cs`

**Interfaces:**
- `DeckButton(..., string GameAction = "", string Response = "")`. Columna `Buttons.Response TEXT NOT NULL DEFAULT ''`.
- `AppSettings` gana, al final y con estos valores por defecto: `bool CopilotEnabled = false`, `string CopilotVoice = ""`, `string CopilotPack = "sobria"`, `double CopilotVolume = 0.8`, `bool CopilotVoiceOnly = false`, `string CopilotMutedCategories = ""`, `bool CopilotGreeting = false`.

- [ ] Tests: `Response_RoundTrips`, `CopilotSettings_RoundTrip_UnderSpanishCulture` (volumen 0.65), `DatabaseWithoutResponseColumn_IsUpgraded`, `FreshDatabase_HasCopilotDisabled`.
- [ ] Implementar, suite en verde, commit `feat: persist copilot settings and module responses`.

### Task 2: Catálogo, caché y personalidades

**Files:** crear `src/VerseDeck.Speech/` (`VerseDeck.Speech.csproj`, `voices.json`, `VoiceCatalog.cs`, `PhraseCache.cs`, `Packs/sobria.json`, `Packs/militar.json`, `Packs/caracter.json`, `ResponsePack.cs`), tests `SpeechTests.cs`

**Interfaces:**
```csharp
public sealed record VoiceInfo(string Id, string Name, string Engine, string Folder, string Model, int Speaker, string Url, long Bytes, string Sha256, string License);
public sealed class VoiceCatalog { public static VoiceCatalog Load(); IReadOnlyList<VoiceInfo> Voices; VoiceInfo? Find(string id); public const string DefaultVoiceId = "piper-davefx"; }
public sealed class VoiceStore(string root) { string FolderOf(VoiceInfo v); bool IsInstalled(VoiceInfo v); }
public sealed record SpeechAudio(float[] Samples, int SampleRate) { TimeSpan Duration; }
public sealed record CachedPhrase(string Path, TimeSpan Duration);
public sealed class PhraseCache(string root)
{
    CachedPhrase? TryGet(VoiceInfo voice, string text);
    CachedPhrase Store(VoiceInfo voice, string text, SpeechAudio audio);   // WAV PCM 16 bits
    int Count { get; }  void Clear();
}
public sealed class ResponsePack { static IReadOnlyList<ResponsePack> LoadAll(); string Id; string Name; ... }
public sealed class ResponseSelector(ResponsePack pack, Random random)
{
    string? ForModule(DeckButton button);   // null si Response == "-"
    string Failed(); string NoKey(); string Profile(string name); string Greeting();
    IReadOnlyList<string> AllFor(IEnumerable<DeckButton> buttons);  // todo lo que podria decirse, para precalentar
}
```
- Voces: `piper-davefx`, `piper-sharvard-0`, `piper-sharvard-1`, `piper-claude-mx`, `kokoro-dora` (hablante 28), `kokoro-alex` (hablante 29). Tamaño y SHA-256 medidos sobre los archivos reales.
- Orden de `ForModule`: texto propio (variantes con `|`); frases del pack por `GameAction`; por nombre de módulo por defecto; genérica con `{nombre}` (plantilla de mantener si `PressDurationMs >= 500`). Sin repetir variante dos veces seguidas para el mismo módulo.

- [ ] Tests: catálogo completo y con hashes de 64 hex; caché ida y vuelta, claves distintas por voz y texto, `Clear`, WAV con cabecera y duración correctas; packs cargan los tres con todas las secciones; orden de fuentes; `-` devuelve null; sin repetición inmediata; `AllFor` incluye cada variante.
- [ ] Implementar, suite, commit `feat: voice catalog, phrase cache and response packs`.

### Task 3: Instalador de voces

**Files:** `src/VerseDeck.Speech/VoiceInstaller.cs`, tests `VoiceInstallerTests.cs`

**Interfaces:**
```csharp
public sealed class VoiceInstaller(HttpClient http, VoiceStore store)
{
    Task InstallAsync(VoiceInfo voice, IProgress<double>? progress, CancellationToken ct);
    void Remove(VoiceInfo voice);
}
```
- Descarga a `<root>\.descargas\<carpeta>.tar.bz2` calculando SHA-256 al vuelo; hash distinto → borra y lanza `InvalidDataException` con mensaje en español. Extrae con SharpCompress a `<root>\.tmp-<guid>`, comprueba que cada entrada queda dentro de esa carpeta, y mueve `<tmp>\<carpeta>` a `<root>\<carpeta>` al final. Cancelación o error: no queda nada bajo `<root>\<carpeta>`.

- [ ] Tests con `HttpMessageHandler` falso y un `.tar.bz2` creado en el propio test: instala; hash incorrecto; cancelación a mitad; error HTTP; reinstalar sobre una existente; entrada con `..\` rechazada; progreso llega a 1.
- [ ] Implementar, suite, commit `feat: voice installer with hash check`.

### Task 4: Motor y reproductor reales

**Files:** `src/VerseDeck.Speech/SherpaTtsEngine.cs`, `src/VerseDeck.App/Services/NAudioPlayer.cs`

**Interfaces:**
```csharp
public interface ITtsEngine : IDisposable { Task<SpeechAudio> SynthesizeAsync(VoiceInfo voice, string text, CancellationToken ct); }
public interface IAudioPlayer { void Play(string wavPath, double volume); void Stop(); }
```
- `SherpaTtsEngine`: un modelo cargado a la vez (por carpeta), carga y síntesis en `Task.Run` bajo un `SemaphoreSlim`; 2 hilos; velocidad 1.0.
- `NAudioPlayer`: `WaveOutEvent` + `AudioFileReader`; `Play` corta lo anterior; nunca lanza.

- [ ] Comprobación real fuera de la batería normal: un test con variable de entorno que genera un WAV con la voz ya descargada en el scratchpad y comprueba duración > 0,5 s.
- [ ] Commit `feat: sherpa-onnx engine and audio player`.

### Task 5: `CopilotService`

**Files:** `src/VerseDeck.App/Services/CopilotService.cs`, `SpeechGuard.cs`; modificar `ButtonExecutor.cs` (evento con origen, evento de fallo, bloqueo y supresión de sonido), `VoiceViewModel.cs` (consulta a `SpeechGuard`); tests `CopilotServiceTests.cs`

**Interfaces:**
```csharp
public sealed record ExecutedEventArgs(DeckButton Button, string Source);
// ButtonExecutor: event EventHandler<ExecutedEventArgs>? Sent; event EventHandler<ExecutedEventArgs>? Failed;
//   Func<DeckButton, string?>? BlockReason { get; set; }   // motivo => no se envia, resultado Failed
//   Func<DeckButton, string, bool>? WillBeAnswered { get; set; }  // true => no suena system.mp3
public sealed class SpeechGuard { void BlockUntil(DateTimeOffset until); void Clear(); bool Blocks(DateTimeOffset now); }
public sealed class CopilotService
{
    bool Muted { get; set; }
    VoiceInfo? ActiveVoice { get; }            // la elegida si esta instalada
    bool WillAnswer(DeckButton button, string source);
    Task SayAsync(string text);                // cache -> reproducir; si falta, generar y reproducir si sigue siendo la ultima
    Task WarmUpAsync();                        // genera lo que falte del deck activo
    Task Pending { get; }                      // ultima reaccion en segundo plano
    event EventHandler? Changed;
}
```
- Un módulo vinculado cuyo estado es "sin tecla" o "no enviable" ya no envía su tecla antigua: `BlockReason` devuelve el motivo, el ejecutor responde `Failed` y el copiloto dice la frase de "sin tecla".
- `SpeechGuard.BlockUntil(ahora + duración + 300 ms)` al empezar a sonar; `VoiceViewModel` descarta reconocimientos bloqueados solo cuando el modo no es push-to-talk.
- Frase de perfil al cambiar de perfil activo (no en la primera carga); saludo en `StartAsync` si `CopilotGreeting`.

- [ ] Tests con `FakeTtsEngine` y `FakeAudioPlayer`: contesta cuando toca; cada interruptor (general, silencio, solo voz, categoría, módulo `-`); sin voz instalada no habla; frase en caché suena sin llamar al motor; frase nueva se genera una vez y se guarda; ráfaga → solo suena la última; motor que lanza → sin sonido y `ExecuteAsync` devuelve `Sent`; motor lento no retrasa `ExecuteAsync`; `WillBeAnswered` suprime `system.mp3`; módulo sin tecla no envía y dice la frase; fallo de envío dice la frase de fallo; perfil; saludo; guardia bloquea reconocimiento en escucha continua y no en PTT; `WarmUpAsync` genera todo lo que falta y nada más; volumen llega al reproductor.
- [ ] Implementar, suite, commit `feat: copilot service`.

### Task 6: ViewModels

**Files:** `CopilotViewModel.cs`; modificar `ShellViewModel.cs`, `ModuleEditorViewModel.cs`, `Harness.cs`; tests `CopilotViewModelTests.cs`

**Interfaces:** `CopilotViewModel`: `Enabled`, `VoiceOnly`, `Greeting`, `Volume`, `Packs`/`SelectedPack`, `Voices` (`VoiceRowViewModel`: `Name`, `Details`, `IsInstalled`, `IsSelected`, `IsBusy`, `Progress`, `InstallCommand`, `UseCommand`, `CancelCommand`), `Categories` (`CategoryToggle`: `Name`, `Speaks`), `CacheText`, `TestCommand`, `WarmUpCommand`, `ClearCacheCommand`, `PackSample`. Shell: `Copilot`, sección `Copiloto`, `IsMuted` enlazado a `CopilotService.Muted`. Editor: `ResponseModes`, `SelectedResponseMode`, `ResponseText`.

- [ ] Tests: ajustes se guardan al cambiar; instalar marca la fila y la selecciona si no había voz; fallo de instalación muestra error y deja la fila sin instalar; cancelar; `Use` cambia de voz; categorías se guardan como lista; probar habla aunque esté desactivado; editor guarda los tres modos; texto propio vacío se rechaza.
- [ ] Implementar, suite, commit `feat: copilot view model and module response editor`.

### Task 7: Vistas

**Files:** `Views/CopilotView.xaml(.cs)`, `MainWindow.xaml` (raíl, sección, botón de silencio en cabecera), `ModuleEditorView.xaml`, `Themes/Controls.xaml` (`Slider`, `ProgressBar`), `Themes/Icons.xaml` (`Icon.nav.copilot`), `App.xaml.cs`, `ThemeResourceTests.cs`

- [ ] Vista con cuatro tarjetas (Voz, Personalidad, Cuándo habla, Caché). Composición real en `App.xaml.cs`.
- [ ] Comprobación en vivo con datos desechables: instalar `piper-davefx` desde la app (descarga real), activar, pulsar un módulo y comprobar en el log que se generó y reprodujo; segunda pulsación sale de caché; captura de la sección.
- [ ] Commit `feat: copilot section`.

### Task 8: Cierre

- [ ] README (sección Copiloto, licencias de voces y dependencias), spec a "implementado", revisión final de la rama, correcciones con test, commit.
