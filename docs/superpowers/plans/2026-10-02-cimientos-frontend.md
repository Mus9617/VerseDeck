# Cimientos y frontend — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reorganizar VerseDeck en MVVM con interfaz vectorial y temas por fabricante, corrigiendo los fallos conocidos, sin perder ninguna función actual.

**Architecture:** Los servicios (`DeckSession`, `ButtonExecutor`, `ThemeService`) concentran el estado y la ejecución; los ViewModels, sin tipos de WPF, exponen datos y comandos; las vistas son `UserControl` que solo enlazan. Los temas son `ResourceDictionary` intercambiables que definen los mismos tokens.

**Tech Stack:** .NET 8, WPF, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, ASP.NET Core (Kestrel), System.Speech, QRCoder, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-02-cimientos-frontend-design.md` (y `2026-10-02-roadmap.md` para la línea de TOS).

## Global Constraints

- Una acción humana produce como máximo una pulsación. Nunca secuencias, bucles ni auto-repetición.
- Los ViewModels no usan tipos de WPF (`Brush`, `MessageBox`, `Dispatcher`, `Visibility`).
- Composición manual en `App.xaml.cs`, sin contenedor de dependencias.
- Solo dependencias gratuitas y locales. Paquetes nuevos permitidos: `CommunityToolkit.Mvvm`, `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`.
- Tipografías OFL incluidas: Barlow, Barlow Condensed, JetBrains Mono.
- Sin integración con Stream Deck.
- Textos de interfaz en español, sin tildes, como el resto de la app.
- Estilo de código: el de los archivos existentes (namespaces de archivo, records, llaves siempre).
- `git` no está en el PATH. Usar `C:\Users\music\AppData\Local\GitHubDesktop\app-3.6.5\resources\app\git\cmd\git.exe` (abajo, `git`).
- Comandos de verificación: `dotnet build VerseDeck.slnx` y `dotnet test tests/VerseDeck.Tests`.

## Review Focus

1. **Base de datos de v1.1 ya existente**: al actualizar no se añaden ni duplican módulos y los borrados por el usuario no vuelven. Test en Tarea 1.
2. **Tecla no soportada escrita en el editor** (por ejemplo `ñ` o `F99`): mensaje en el pie, nada se guarda, la app sigue. Test en Tarea 6.
3. **Puerto móvil ocupado**: el servidor no arranca, el indicador LAN sigue en OFFLINE y el pie muestra el error; se puede reintentar. Test en Tarea 7.
4. **Sin reconocedor de voz o sin micrófono**: activar voz deja el estado en OFFLINE con mensaje, sin excepción. Test en Tarea 7.
5. **Nombre de nave libre o en otro formato** (`drake cutlass`, `Mi nave`, vacío): tema por prefijo sin distinguir mayúsculas; lo desconocido usa Neutral. Test en Tarea 5.

## Estructura de archivos

```
src/VerseDeck.Core/Models/DomainModels.cs        modificar: interfaces
src/VerseDeck.Data/SqliteVerseDeckRepository.cs  modificar: siembra, cultura
src/VerseDeck.Input/KeyInputBuilder.cs           crear: entradas con scan code
src/VerseDeck.Input/WindowsInputSender.cs        modificar
src/VerseDeck.MobileServer/MobilePanelServer.cs  modificar: emparejado
src/VerseDeck.MobileServer/PairingGuard.cs       crear: tokens y bloqueo
src/VerseDeck.MobileServer/MobilePage.cs         crear: HTML
src/VerseDeck.Voice/WindowsSpeechCommandService.cs modificar: interfaz
src/VerseDeck.App/Services/*.cs                  crear (Tareas 4 y 5)
src/VerseDeck.App/ViewModels/*.cs                crear (Tareas 6 y 7)
src/VerseDeck.App/Themes/*.xaml, Fonts/*.ttf     crear (Tarea 8)
src/VerseDeck.App/Views/*.xaml(.cs)              crear (Tarea 9)
src/VerseDeck.App/MainWindow.xaml(.cs), App.xaml(.cs)  reescribir (Tarea 9)
tests/VerseDeck.Tests/                           crear
```

---

### Task 1: Proyecto de tests y correcciones del repositorio

**Files:**
- Create: `tests/VerseDeck.Tests/VerseDeck.Tests.csproj`, `tests/VerseDeck.Tests/TempDatabase.cs`, `tests/VerseDeck.Tests/RepositoryTests.cs`
- Modify: `VerseDeck.slnx`, `src/VerseDeck.Core/Models/DomainModels.cs`, `src/VerseDeck.Data/SqliteVerseDeckRepository.cs`

**Interfaces:**
- Produces: `IVerseDeckRepository.DeleteButtonAsync(long buttonId, CancellationToken ct = default)`; `TempDatabase : IAsyncDisposable` con `string Path` y `Task<SqliteVerseDeckRepository> CreateAsync()` (repositorio ya inicializado sobre archivo temporal; `SqliteConnection.ClearAllPools()` al liberar).

- [ ] **Step 1:** Crear el proyecto de tests (`net8.0-windows`, `UseWPF=true`, xUnit, referencia a todos los proyectos de `src`) y añadirlo a `VerseDeck.slnx` bajo la carpeta `/tests/`.
- [ ] **Step 2: Tests que fallan** en `RepositoryTests.cs`:

```csharp
[Fact] public async Task Initialize_SeedsSixteenDefaultButtons_Once()
// tras InitializeAsync dos veces: 16 botones en el perfil activo

[Fact] public async Task DeletedDefaultButton_DoesNotReappear_AfterReinitialize()
// borrar "Lights", InitializeAsync de nuevo => sigue sin existir

[Fact] public async Task RenamedDefaultButton_IsNotDuplicated_AfterReinitialize()
// renombrar "Lights" a "Luces", InitializeAsync => 16 botones, ninguno "Lights"

[Fact] public async Task LegacyDatabaseWithButtons_IsMarkedSeeded_WithoutAddingButtons()
// crear BD, borrar el ajuste DefaultDeckSeededV1 y el boton "Cargo",
// InitializeAsync => 15 botones y el ajuste vuelve a existir

[Fact] public async Task Settings_RoundTrip_UnderSpanishCulture()
// CultureInfo.CurrentCulture = es-ES; guardar confianza 0.55 => leer 0.55

[Fact] public async Task Settings_OutOfRangeConfidence_FallsBackToDefault()
// escribir Value='40' en VoiceMinimumConfidence => GetSettingsAsync da 0.40
```

- [ ] **Step 3:** `dotnet test tests/VerseDeck.Tests` → fallan los seis.
- [ ] **Step 4:** Implementar en `SqliteVerseDeckRepository`:
  - `EnsureDefaultDeckAsync` solo siembra si no existe el ajuste `DefaultDeckSeededV1`; si no existe pero el perfil activo ya tiene botones, solo escribe el ajuste. Tras sembrar escribe `DefaultDeckSeededV1 = Done`.
  - Lectura y escritura de números con `CultureInfo.InvariantCulture`. Al leer `VoiceMinimumConfidence`, si no se puede interpretar o queda fuera de `0.1–0.98`, usar `0.40`.
  - Añadir `DeleteButtonAsync` a `IVerseDeckRepository`.
- [ ] **Step 5:** `dotnet test tests/VerseDeck.Tests` → pasan.
- [ ] **Step 6:** `git add -A; git commit -m "fix: seed default deck once and store settings culture-invariant"`

---

### Task 2: Pulsación con scan codes

**Files:**
- Create: `src/VerseDeck.Input/KeyInputBuilder.cs`, `tests/VerseDeck.Tests/KeyInputBuilderTests.cs`
- Modify: `src/VerseDeck.Input/WindowsInputSender.cs`, `src/VerseDeck.Input/KeyMap.cs` (mensaje de error: `F1-F24`)

**Interfaces:**
- Produces:
```csharp
public readonly record struct KeyStroke(ushort VirtualKey, ushort ScanCode, bool Extended, bool KeyUp);
public static class KeyInputBuilder
{
    // scanCodeOf: inyectable para test; en produccion MapVirtualKey(vk, MAPVK_VK_TO_VSC)
    public static IReadOnlyList<KeyStroke> Down(KeyPressAction action, Func<ushort, ushort> scanCodeOf);
    public static IReadOnlyList<KeyStroke> Up(KeyPressAction action, Func<ushort, ushort> scanCodeOf);
}
```

- [ ] **Step 1: Tests que fallan:**

```csharp
[Fact] public void Down_OrdersModifiersThenKey()
// Alt+R => [ALT down, R down]
[Fact] public void Up_ReleasesKeyThenModifiersReversed()
// Ctrl,Alt+R => [R up, ALT up, CTRL up]
[Theory] [InlineData("LEFT")] [InlineData("UP")] [InlineData("INSERT")] [InlineData("DELETE")] [InlineData("HOME")] [InlineData("END")]
public void NavigationKeys_AreExtended(string key)
[Fact] public void LetterKey_IsNotExtended()
[Fact] public void UnsupportedKey_Throws() // "ñ" => InvalidOperationException
```

- [ ] **Step 2:** `dotnet test tests/VerseDeck.Tests --filter KeyInputBuilder` → fallan.
- [ ] **Step 3:** Implementar `KeyInputBuilder`. En `WindowsInputSender.SendAsync`: enviar `Down` con `SendInput`, `await Task.Delay(PressDurationMs)`, enviar `Up` (también si se cancela, en `finally`). Flags: `KEYEVENTF_SCANCODE (0x0008)`, `KEYEVENTF_EXTENDEDKEY (0x0001)`, `KEYEVENTF_KEYUP (0x0002)`; `wVk = 0` cuando hay scan code. Si `scanCodeOf` devuelve 0 (botones de ratón, F13–F24 en algunos teclados), enviar por tecla virtual como hoy.
- [ ] **Step 4:** Tests pasan. Comprobación manual: con Notepad en primer plano, el módulo "Landing Gear" escribe una sola `n`.
- [ ] **Step 5:** `git commit -m "fix: send key presses as held scan codes"`

---

### Task 3: Emparejado seguro del panel móvil

**Files:**
- Create: `src/VerseDeck.MobileServer/PairingGuard.cs`, `src/VerseDeck.MobileServer/MobilePage.cs`, `tests/VerseDeck.Tests/PairingGuardTests.cs`, `tests/VerseDeck.Tests/MobileServerTests.cs`
- Modify: `src/VerseDeck.MobileServer/MobilePanelServer.cs`

**Interfaces:**
- Produces:
```csharp
public sealed class PairingGuard(string pin, Func<DateTimeOffset> now)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(1);
    public PairResult TryPair(string remoteAddress, string? pin); // Ok(token) | WrongPin | LockedOut
    public bool IsValidToken(string? token);
}
```
- Rutas: `POST /api/pair {pin}` → `200 {token}` | `401` | `429`. `GET /api/buttons`, `POST /api/press`, `/ws?token=` exigen token (`Authorization: Bearer` o query) y origen de red privada; sin token `401`, fuera de LAN `403`.
- `POST /api/press` y el mensaje WS responden `{ok:false,error:"Confirmacion requerida"}` con `400` si el módulo requiere confirmación y llega sin ella.
- `MobilePanelServer.StartAsync` deja propagar el error si el puerto está ocupado y deja `IsRunning == false`.

- [ ] **Step 1: Tests que fallan.** `PairingGuardTests` con reloj falso: PIN correcto da token válido; cinco fallos bloquean; el sexto intento con PIN correcto devuelve `LockedOut`; pasado un minuto vuelve a aceptar; el bloqueo es por IP. `MobileServerTests` arrancando el servidor en puerto libre (`TcpListener` en puerto 0) con repositorio temporal y un `IInputSender` falso que cuenta llamadas:

```csharp
[Fact] public async Task Buttons_WithoutToken_Returns401()
[Fact] public async Task Pair_WrongPin_Returns401()
[Fact] public async Task Press_WithToken_SendsExactlyOneKeyPress()
[Fact] public async Task Press_ConfirmationRequiredButMissing_Returns400_AndSendsNothing()
[Fact] public async Task RootPage_DoesNotContainPin()
[Fact] public async Task Start_OnBusyPort_Throws_AndIsNotRunning()
```

- [ ] **Step 2:** Tests fallan.
- [ ] **Step 3:** Implementar. La página (`MobilePage.Html`) muestra un formulario de PIN, guarda el token en `sessionStorage`, pinta los botones con `textContent` y `createElement` (nada de `innerHTML` con datos) y conserva la vibración y el `confirm` actuales.
- [ ] **Step 4:** Tests pasan.
- [ ] **Step 5:** `git commit -m "fix: require paired token on every mobile panel route"`

---

### Task 4: Servicios de aplicación

**Files:**
- Create en `src/VerseDeck.App/Services/`: `DeckSession.cs`, `ButtonExecutor.cs`, `IDialogService.cs`, `IAudioFeedback.cs`, `AudioFeedback.cs`, `DebugLog.cs`
- Create: `tests/VerseDeck.Tests/Fakes.cs`, `tests/VerseDeck.Tests/DeckSessionTests.cs`, `tests/VerseDeck.Tests/ButtonExecutorTests.cs`
- Modify: `src/VerseDeck.App/VerseDeck.App.csproj` (añadir `CommunityToolkit.Mvvm`)

**Interfaces:**
- Produces:
```csharp
public interface IDialogService { bool Confirm(string title, string message); }
public interface IAudioFeedback { void PlayWelcome(); void PlayCommand(); }
public interface IDebugLog { void Write(string message); string Path { get; } }

public sealed class DeckSession(IVerseDeckRepository repository)
{
    public AppSettings Settings { get; }
    public Profile? ActiveProfile { get; }
    public IReadOnlyList<Profile> Profiles { get; }
    public IReadOnlyList<DeckButton> Buttons { get; }
    public IReadOnlyList<VoiceCommand> VoiceCommands { get; } // solo las del perfil activo
    public event EventHandler? Changed;
    public Task LoadAsync();                       // InitializeAsync + recarga
    public Task ReloadAsync();
    public Task SaveSettingsAsync(AppSettings settings);
    public Task ActivateProfileAsync(long profileId);
    public Task<Profile> SaveProfileAsync(string name, string shipName); // crea clonando el deck activo si el nombre es nuevo
    public Task<DeckButton> SaveButtonAsync(DeckButton button);
    public Task DeleteButtonAsync(long buttonId);
    public Task SaveVoicePhraseAsync(long buttonId, string phrase, double confidence);
}

public enum ExecuteResult { Sent, Cancelled, Failed }
public sealed class ButtonExecutor(IInputSender sender, IVerseDeckRepository repository,
    IDialogService dialogs, IAudioFeedback audio, IDebugLog log, Func<AppSettings> settings)
{
    public event EventHandler<DeckButton>? Sent;
    public string? LastError { get; }
    public Task<ExecuteResult> ExecuteAsync(DeckButton button, string source);
}
```
- `Fakes.cs`: `FakeInputSender` (lista de acciones recibidas, `ThrowOnSend`), `FakeDialogService` (`Answer`), `FakeAudio`, `FakeLog`.

- [ ] **Step 1: Tests que fallan:**

```csharp
// DeckSessionTests (repositorio temporal real)
[Fact] public async Task Load_SelectsActiveProfile_AndItsButtons()
[Fact] public async Task SaveProfile_NewName_ClonesButtonsAndPhrases()   // 16 botones y sus frases en el nuevo
[Fact] public async Task SaveProfile_ExistingName_UpdatesShip_WithoutCloning()
[Fact] public async Task VoiceCommands_OnlyIncludeActiveProfile()
[Fact] public async Task EveryMutation_RaisesChangedOnce()

// ButtonExecutorTests
[Fact] public async Task Execute_SendsOnePress_LogsAndPlaysSound()
[Fact] public async Task Execute_ConfirmationDeclined_SendsNothing()     // => Cancelled
[Fact] public async Task Execute_SenderThrows_ReturnsFailed_WithLastError()
[Fact] public async Task Execute_CommandSoundDisabled_DoesNotPlay()
```

- [ ] **Step 2:** Tests fallan.
- [ ] **Step 3:** Implementar. `AudioFeedback` envuelve los dos `MediaPlayer` actuales (`ready.mp3` a 0.72, `system.mp3` a 0.62) y nunca lanza. `DebugLog` escribe en `%AppData%\VerseDeck Companion\debug.log` y nunca lanza.
- [ ] **Step 4:** Tests pasan.
- [ ] **Step 5:** `git commit -m "feat: add deck session and button executor services"`

---

### Task 5: Fabricantes y servicio de temas

**Files:**
- Create: `src/VerseDeck.App/Services/ThemeService.cs`, `tests/VerseDeck.Tests/ThemeServiceTests.cs`
- Modify: `src/VerseDeck.App/ShipCatalog.cs`

**Interfaces:**
- Produces:
```csharp
public enum ThemeId { Neutral, Drake, Origin, Aegis, Anvil }
public static class ShipCatalog { public static ThemeId ThemeFor(string? shipName); /* Names se conserva */ }
public sealed class ThemeService(Action<ThemeId> apply)
{
    public const string Auto = "Auto";
    public ThemeId Current { get; }
    public IReadOnlyList<string> Choices { get; } // "Auto","Neutral","Drake","Origin","Aegis","Anvil"
    public void Update(string themeSetting, string? shipName);
}
```
- El valor antiguo del ajuste `Theme` (`ColdBlue`) y cualquier valor desconocido se tratan como `Auto`.

- [ ] **Step 1: Tests que fallan:**

```csharp
[Theory]
[InlineData("Drake Cutlass Black", ThemeId.Drake)]
[InlineData("drake cutlass", ThemeId.Drake)]
[InlineData("Origin 890 Jump", ThemeId.Origin)]
[InlineData("Aegis Gladius", ThemeId.Aegis)]
[InlineData("Anvil Carrack", ThemeId.Anvil)]
[InlineData("RSI Polaris", ThemeId.Neutral)]
[InlineData("Mi nave", ThemeId.Neutral)]
[InlineData("", ThemeId.Neutral)]
[InlineData(null, ThemeId.Neutral)]
public void ThemeFor_UsesManufacturerPrefix(string? ship, ThemeId expected)

[Fact] public void Update_Auto_FollowsShip()
[Fact] public void Update_FixedTheme_IgnoresShip()
[Fact] public void Update_LegacyColdBlue_BehavesAsAuto()
[Fact] public void Update_SameTheme_DoesNotReapply()
```

- [ ] **Step 2–4:** Fallan, implementar, pasan.
- [ ] **Step 5:** `git commit -m "feat: resolve theme from ship manufacturer"`

---

### Task 6: ViewModels de deck, editor, perfil y actividad

**Files:**
- Create en `src/VerseDeck.App/ViewModels/`: `DeckViewModel.cs`, `ModuleTileViewModel.cs`, `ModuleEditorViewModel.cs`, `ProfileViewModel.cs`, `ActivityViewModel.cs`, `IStatusSink.cs`
- Create: `tests/VerseDeck.Tests/DeckViewModelTests.cs`, `ModuleEditorViewModelTests.cs`, `ProfileViewModelTests.cs`

**Interfaces:**
- Consumes: `DeckSession`, `ButtonExecutor`, `IDialogService` (Tarea 4).
- Produces:
```csharp
public interface IStatusSink { void Info(string message); void Error(string message); }

public sealed partial class ModuleTileViewModel : ObservableObject
// Id, Name, Category, KeyText ("Alt+R"), IconKey, AccentKey ("Accent"|"Positive"|"Danger"),
// VoiceSummary, [ObservableProperty] bool isSelected, bool justSent

public sealed record ModuleGroup(string Category, IReadOnlyList<ModuleTileViewModel> Tiles);

public sealed partial class DeckViewModel : ObservableObject
// ObservableCollection<ModuleGroup> Groups; bool IsEditMode; ModuleTileViewModel? Selected;
// IAsyncRelayCommand<ModuleTileViewModel> PressCommand

public sealed partial class ModuleEditorViewModel : ObservableObject
// Name, Category, Icon, Frame ("Cyan"|"Green"|"Red"), Key, Modifiers, RequiresConfirmation,
// NewPhrase, Confidence, ObservableCollection<string> Phrases,
// Categories, Icons, Frames (listas fijas actuales),
// CreateCommand, SaveCommand, DeleteCommand, AddPhraseCommand; void Load(DeckButton? button)

public sealed partial class ProfileViewModel : ObservableObject
// Profiles, SelectedProfile, Name, ShipName, ShipNames, LoadCommand, SaveCommand

public sealed partial class ActivityViewModel : ObservableObject
// ObservableCollection<string> Entries; Task RefreshAsync() (8 ultimas)
```
- Orden de categorías: Flight, Navigation, Scan, Combat, Utility, Emergency, Systems, Custom, y luego cualquier otra alfabéticamente. Los grupos vacíos no se muestran.
- `AccentKey`: `Danger` para Emergency y Combat o marco Red; `Positive` para Scan y Utility o marco Green; `Accent` en el resto (misma regla que `FrameNameFor` actual).
- `JustSent` se pone a `true` al recibir `ButtonExecutor.Sent` y vuelve a `false` a los 400 ms.

- [ ] **Step 1: Tests que fallan** (repositorio temporal + fakes):

```csharp
// DeckViewModelTests
[Fact] public async Task Groups_FollowCategoryOrder_AndSkipEmpty()
[Fact] public async Task Press_InNormalMode_ExecutesButton()
[Fact] public async Task Press_InEditMode_SelectsWithoutExecuting()
[Fact] public async Task SessionChanged_RebuildsGroups_KeepingSelection()

// ModuleEditorViewModelTests
[Fact] public async Task Save_UnsupportedKey_ReportsError_AndDoesNotPersist()   // Key = "ñ"; tambien "F99"
[Fact] public async Task Save_EmptyName_ReportsError()
[Fact] public async Task Create_DefaultsKeyToF13_WhenEmpty()
[Fact] public async Task Delete_Declined_KeepsModule()
[Fact] public async Task Delete_Confirmed_RemovesModuleAndPhrases()
[Fact] public async Task AddPhrase_ClampsConfidence_Between010And098()
[Fact] public async Task AddPhrase_ParsesCommaDecimal_UnderSpanishCulture()   // "0,55" => 0.55

// ProfileViewModelTests
[Fact] public async Task Save_NewName_CreatesProfile_AndActivatesIt()
[Fact] public async Task Save_EmptyNameOrShip_ReportsError()
[Fact] public async Task Load_SwitchesActiveProfile()
```

- [ ] **Step 2–4:** Fallan, implementar, pasan. La validación de tecla usa `KeyMap.ToVirtualKey` antes de guardar. Los errores van a `IStatusSink.Error`; ningún comando deja escapar excepciones.
- [ ] **Step 5:** `git commit -m "feat: add deck, editor, profile and activity view models"`

---

### Task 7: ViewModels de voz, enlace móvil y shell

**Files:**
- Create en `src/VerseDeck.App/ViewModels/`: `VoiceViewModel.cs`, `MobileLinkViewModel.cs`, `SettingsViewModel.cs`, `ShellViewModel.cs`
- Create: `src/VerseDeck.App/Services/IPttMonitor.cs`, `IMobileLink.cs`, `MobileLink.cs`
- Create: `tests/VerseDeck.Tests/VoiceViewModelTests.cs`, `MobileLinkViewModelTests.cs`, `ShellViewModelTests.cs`
- Modify: `src/VerseDeck.Core/Models/DomainModels.cs`, `src/VerseDeck.Voice/WindowsSpeechCommandService.cs`, `src/VerseDeck.App/PttInputMonitor.cs`

**Interfaces:**
- `IVoiceCommandService` gana `void PauseRecognition()`, `void ResumeRecognition()`, `void SetInputGate(bool isOpen, TimeSpan? releaseGrace = null)`, `event EventHandler<string>? Diagnostic`.
- Produces:
```csharp
public interface IPttMonitor
{
    event EventHandler<bool>? PressedChanged;
    void Start(string deviceType, string binding); void Stop();
    bool AnyInputPressed(); bool TryDetectPressed(out PttBinding binding);
}
public interface IMobileLink
{
    bool IsRunning { get; } string Url { get; } int ConnectedCount { get; }
    Task StartAsync(int port, string pin); Task StopAsync();
}
public enum LinkState { Offline, Armed, Online }

public sealed partial class VoiceViewModel : ObservableObject
// State (LinkState), StatusText, Mode ("PushToTalk"|"ManualToggle"), PttDevice, PttBinding,
// Confidence, IsDetecting; StartCommand, StopCommand, SaveSettingsCommand, DetectPttCommand
public sealed partial class MobileLinkViewModel : ObservableObject
// State, Url, PinText, DevicesText, byte[]? QrPng; StartCommand, StopCommand
public sealed partial class SettingsViewModel : ObservableObject
// WelcomeSound, CommandSound, Theme, ThemeChoices, OpenDebugConsoleCommand
public sealed partial class ShellViewModel : ObservableObject, IStatusSink
// Section ("Deck"|"Voz"|"Movil"|"Ajustes"), StatusText, StatusIsError,
// ProfileTitle, ShipTitle; hijos: Deck, Editor, Profile, Activity, Voice, Mobile, Settings
// Task InitializeAsync(); Task ShutdownAsync()
```
- `VoiceViewModel` recibe un `Func<Action, Task>` para volver al hilo de interfaz y un `Func<TimeSpan, Action, IDisposable>` como temporizador, de modo que no toca `Dispatcher`. Gracia al soltar PTT: 2,5 s (como hoy).
- El QR se expone como PNG en bytes; la vista lo convierte en imagen.

- [ ] **Step 1: Tests que fallan** con `FakeVoiceService`, `FakePttMonitor`, `FakeMobileLink`:

```csharp
// VoiceViewModelTests
[Fact] public async Task Start_PushToTalk_StartsPaused_AndArmsMonitor()         // State == Armed
[Fact] public async Task Start_ManualToggle_ListensImmediately()                // State == Online
[Fact] public async Task Start_NoRecognizerAvailable_StaysOffline_WithMessage() // fake no arranca
[Fact] public async Task Start_ServiceThrows_StaysOffline_WithMessage()
[Fact] public async Task SessionChanged_WhileRunning_RestartsEngineWithNewPhrases()
[Fact] public async Task SessionChanged_WhileRunningInPtt_RestartsAndPausesAgain()
[Fact] public async Task PttPress_OpensGate_Release_ClosesAfterGrace()
[Fact] public async Task Recognized_ExecutesButtonWithSourceVoice()
[Fact] public async Task SaveSettings_EmptyPttBinding_ReportsError()

// MobileLinkViewModelTests
[Fact] public async Task Start_SetsOnline_UrlAndQr()
[Fact] public async Task Start_PortBusy_StaysOffline_ReportsError_AndCanRetry()
[Fact] public async Task Stop_ClearsUrlAndQr()

// ShellViewModelTests
[Fact] public async Task Initialize_LoadsSession_AppliesTheme_AndPlaysWelcomeWhenEnabled()
[Fact] public async Task ProfileChange_UpdatesTitles_AndTheme()
[Fact] public void Error_SetsStatusIsError_Info_ClearsIt()
```

- [ ] **Step 2–4:** Fallan, implementar, pasan. `MobileLink` adapta `MobilePanelServer`; `PttInputMonitor` implementa `IPttMonitor` (los métodos estáticos pasan a ser de instancia).
- [ ] **Step 5:** `git commit -m "feat: add voice, mobile link and shell view models"`

---

### Task 8: Temas, tipografías e iconos

**Files:**
- Create en `src/VerseDeck.App/Themes/`: `Neutral.xaml`, `Drake.xaml`, `Origin.xaml`, `Aegis.xaml`, `Anvil.xaml`, `Controls.xaml`, `Icons.xaml`
- Create: `src/VerseDeck.App/Fonts/` con `Barlow-Regular.ttf`, `Barlow-SemiBold.ttf`, `BarlowCondensed-SemiBold.ttf`, `BarlowCondensed-Bold.ttf`, `JetBrainsMono-Medium.ttf` y sus `OFL.txt`
- Create: `tests/VerseDeck.Tests/ThemeResourceTests.cs`
- Modify: `src/VerseDeck.App/VerseDeck.App.csproj` (fuentes como `Resource`)

**Interfaces:**
- Produces: claves de recurso que cada tema define, todas obligatorias:
  `Bg`, `Surface`, `SurfaceRaised`, `Line`, `Text`, `TextMuted`, `Accent`, `OnAccent`, `Positive`, `Danger`, `Warning` (brushes); `Radius` (`CornerRadius`); `Stroke` (`Thickness`); `HeadingFont`, `BodyFont`, `MonoFont` (`FontFamily`); `HeadingCase` (`CharacterCasing`).
- `Icons.xaml`: una `Geometry` por icono con clave `Icon.<clave>` para las 16 claves de `ModuleIcons` (`power`, `engine`, `gear`, `flight`, `quantum`, `map`, `radar`, `scan`, `shield`, `weapons`, `cargo`, `comms`, `doors`, `eject`, `warning`, `lights`), más `Icon.nav.deck`, `Icon.nav.voice`, `Icon.nav.mobile`, `Icon.nav.settings`.
- `Controls.xaml`: estilos implícitos para `Button`, `TextBox`, `ComboBox`, `CheckBox`, `ScrollBar`, `ListBox`, y con clave `ModuleTile`, `NavButton`, `KeyCap`, `SectionHeader`, `StatusChip`. Solo `DynamicResource` para los tokens.

Valores de los temas:

| Token | Neutral | Drake | Origin | Aegis | Anvil |
|---|---|---|---|---|---|
| Bg | #0E1317 | #121110 | #F3F1EC | #0D1014 | #12150F |
| Surface | #161D23 | #1C1A17 | #FFFFFF | #171C22 | #1B2017 |
| SurfaceRaised | #1E2730 | #26231F | #F8F7F3 | #202731 | #252B1F |
| Line | #2C3944 | #3A342C | #D9D5CC | #33404F | #38402E |
| Text | #E6EDF2 | #EDE6DA | #15171A | #E3E9F0 | #E6E8DC |
| TextMuted | #8A9BA8 | #9A8F7E | #6B6F76 | #8795A6 | #969C85 |
| Accent | #5FB8C9 | #E0A030 | #15171A | #4F8FE0 | #C9B27C |
| OnAccent | #0E1317 | #121110 | #FFFFFF | #0D1014 | #12150F |
| Positive | #5DBB8A | #8DB04A | #2E7D4F | #57B894 | #9DB65A |
| Danger | #E0564F | #E0452F | #B3261E | #E0564F | #D9603F |
| Warning | #E0A84F | #E0A030 | #9A6700 | #E0A84F | #D9A441 |
| Radius | 4 | 0 | 10 | 2 | 2 |
| Stroke | 1 | 2 | 1 | 1 | 1 |
| HeadingCase | Upper | Upper | Normal | Upper | Upper |

Fuentes en todos los temas: `HeadingFont` Barlow Condensed, `BodyFont` Barlow, `MonoFont` JetBrains Mono.

- [ ] **Step 1: Test que falla** (hilo STA):

```csharp
[Theory] [InlineData("Neutral")] [InlineData("Drake")] [InlineData("Origin")] [InlineData("Aegis")] [InlineData("Anvil")]
public void Theme_DefinesEveryToken(string theme)       // las 17 claves, con su tipo
[Fact] public void Icons_DefineEveryModuleIconKey()     // las 16 + 4 de navegacion
[Theory] /* mismos temas */
public void Theme_TextOnBg_MeetsContrast(string theme)  // Text/Bg >= 7:1, TextMuted/Bg >= 4.5:1, OnAccent/Accent >= 4.5:1
```

- [ ] **Step 2:** Falla.
- [ ] **Step 3:** Descargar las fuentes desde los repositorios oficiales (`github.com/google/fonts`, carpetas `ofl/barlow`, `ofl/barlowcondensed`, `ofl/jetbrainsmono`) junto con `OFL.txt`. Crear los diccionarios. Los iconos se redibujan como trazos a partir de `Assets/Skin/icons/svg/*.svg` (viewBox 256); si un tono no cumple el contraste del test, ajustar el tono y actualizar la tabla.
- [ ] **Step 4:** Tests pasan.
- [ ] **Step 5:** `git commit -m "feat: add manufacturer themes, bundled fonts and vector icons"`

---

### Task 9: Vistas, shell y limpieza de recursos

**Files:**
- Create en `src/VerseDeck.App/Views/`: `DeckView.xaml`, `ModuleEditorView.xaml`, `VoiceView.xaml`, `MobileView.xaml`, `SettingsView.xaml`, `ActivityPanel.xaml` (cada una con su `.xaml.cs` vacío salvo `InitializeComponent`)
- Create: `src/VerseDeck.App/Services/WpfDialogService.cs`, `src/VerseDeck.App/Converters.cs` (`IconKeyToGeometry`, `AccentKeyToBrush`, `LinkStateToBrush`, `BytesToImage`, `BoolToVisibility`)
- Rewrite: `src/VerseDeck.App/MainWindow.xaml`, `MainWindow.xaml.cs`, `App.xaml`, `App.xaml.cs`
- Modify: `src/VerseDeck.App/VerseDeck.App.csproj`
- Delete: `src/VerseDeck.App/Assets/Skin/`, `src/VerseDeck.App/Assets/VideoUi/`, `assets/` (raíz)

**Interfaces:**
- Consumes: todos los ViewModels (Tareas 6 y 7), `ThemeService` (Tarea 5), claves de recurso (Tarea 8).
- `App.xaml.cs` crea repositorio, servicios y `ShellViewModel`; `ThemeService` recibe una acción que sustituye el diccionario de tema en `Application.Resources.MergedDictionaries[0]`. Se conserva el registro de errores no controlados actual.
- `MainWindow.xaml.cs`: constructor con `ShellViewModel`, `Loaded` → `InitializeAsync`, `Closing` → `ShutdownAsync`. Nada más.

Distribución (spec §2):
- Ventana 1440×900, mínimo 1180×760, fondo `Bg`.
- Cabecera 56 px: marca "VERSEDECK", perfil / nave, selector de perfil con "Cargar", chips VOZ y LAN.
- Raíl 64 px con cuatro `NavButton` (icono + etiqueta de 10 px).
- Deck: barra con título y conmutador "Editar"; por cada grupo, `SectionHeader` y `WrapPanel` de tiles de 220×96 con separación de 8 px. Tile: icono 28 px, nombre en `HeadingFont` 16 px, `KeyCap` con la tecla, frase en `TextMuted` 11 px, borde izquierdo de 3 px del color de `AccentKey`. `IsSelected` → borde `Accent`; `JustSent` → fondo `Accent` al 20 % que se desvanece en 400 ms.
- Inspector 320 px a la derecha, visible solo con `IsEditMode`.
- `ActivityPanel` plegable bajo el deck, cerrado por defecto.
- Ajustes: perfil (nombre, nave, guardar), sonidos, tema, consola de debug, aviso legal y de TOS (textos actuales).
- Pie 28 px: `StatusText` (color `Danger` si `StatusIsError`) y, a la derecha, "Creada con mucho amor por Zowix".

- [ ] **Step 1:** Escribir vistas, conversores, `WpfDialogService` y la composición. Quitar del `.csproj` los `Content` de `Assets\Skin` y `Assets\VideoUi`; mantener `icon.png`, `ready.mp3`, `system.mp3`.
- [ ] **Step 2:** Borrar las carpetas de recursos indicadas.
- [ ] **Step 3:** `dotnet build VerseDeck.slnx` sin avisos nuevos; `dotnet test tests/VerseDeck.Tests` pasa.
- [ ] **Step 4: Comprobación manual** con `dotnet run --project src/VerseDeck.App`, anotando el resultado de cada punto:
  1. Arranca, suena la bienvenida y se ven 16 módulos en sus grupos.
  2. Con Notepad en primer plano tras el clic, "Landing Gear" escribe una `n` y el tile destella.
  3. Modo editar: el clic selecciona y no envía; crear, guardar y borrar módulo.
  4. Tecla `ñ` en el editor: error en el pie, sin guardar.
  5. Guardar perfil nuevo con nave "Drake Cutlass Black": tema Drake. Repetir con Origin, Aegis, Anvil y "RSI Polaris" (Neutral). Capturar pantalla de cada tema.
  6. Voz en ManualToggle y en PTT con detección de botón; añadir frase con la voz activa y comprobar que la reconoce sin reiniciar.
  7. Servidor móvil: QR, PIN en el teléfono, pulsación, contador de dispositivos, parar.
  8. La carpeta de salida no contiene `Skin`, `VideoUi` ni `space.mp4`.
- [ ] **Step 5:** `git commit -m "feat: rebuild the interface on views, themes and view models"`

---

### Task 10: Cierre

**Files:**
- Modify: `README.md`, `docs/superpowers/specs/2026-10-02-cimientos-frontend-design.md` (Estado: implementado)

- [ ] **Step 1:** Repasar los criterios de aceptación del spec (§6) uno a uno y anotar cómo se comprobó cada uno.
- [ ] **Step 2:** README: añadir `dotnet test tests/VerseDeck.Tests`, la línea de TOS resumida y la lista de temas.
- [ ] **Step 3:** `.\build-installer.ps1` si Inno Setup está instalado; si no, `dotnet publish src/VerseDeck.App -c Release -r win-x64 --self-contained` y comprobar que el ejecutable publicado arranca.
- [ ] **Step 4:** `git commit -m "docs: update readme for the new foundations"`
