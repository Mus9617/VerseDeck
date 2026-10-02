# Controles — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Vincular módulos a acciones de Star Citizen y usar la tecla real del jugador, leída de `actionmaps.xml`, con un panel que explique el estado de cada vínculo.

**Architecture:** Un proyecto nuevo sin interfaz, `VerseDeck.Game`, localiza la instalación, lee los rebinds y resuelve teclas contra un catálogo propio. En la app, `ControlSync` aplica el resultado a los módulos vinculados a través de `DeckSession` y vigila el archivo; `ControlsViewModel` lo muestra.

**Tech Stack:** .NET 8, System.Xml.Linq, System.Text.Json, FileSystemWatcher, WPF, CommunityToolkit.Mvvm, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-02-controles-design.md` (y `2026-10-02-roadmap.md`).

## Global Constraints

- Solo lectura bajo la carpeta del juego; nunca se abre `Data.p4k`.
- Un disparo humano envía una sola pulsación: una tecla más hasta tres modificadores. Sin dobles pulsaciones.
- Duración máxima de pulsación: 2000 ms. Módulos manuales: 60 ms.
- Ningún cambio en módulos al actualizar la app: vincular es una acción explícita del usuario.
- ViewModels sin tipos de WPF. Textos de interfaz en español sin tildes.
- Catálogo válido para Star Citizen 4.10.
- `git`: `C:\Users\music\AppData\Local\GitHubDesktop\app-3.6.5\resources\app\git\cmd\git.exe`. Verificación: `dotnet build VerseDeck.slnx`, `dotnet test tests/VerseDeck.Tests`.
- Rama de trabajo: `controles`.

## Review Focus

1. **Archivo a medio escribir** mientras el juego guarda: se conserva la última lectura buena, sin excepción ni módulos alterados. Test en Tarea 6.
2. **Rebind que la app no puede enviar** (ratón, doble pulsación, tecla desconocida): el módulo conserva su tecla y queda marcado; nunca se guarda una tecla inventada. Test en Tareas 3 y 6.
3. **Base de datos de la versión anterior**: la columna `GameAction` se añade sin perder módulos ni frases. Test en Tarea 5.
4. **Carpeta manual incorrecta o instalación ausente**: Controles lo explica y el deck sigue funcionando. Test en Tareas 4 y 7.
5. **Módulo vinculado a una acción que desaparece del catálogo o del archivo**: estado "Sin tecla", conserva la tecla. Test en Tarea 3.

---

### Task 1: Teclas con lado, signos y pulsación larga

**Files:**
- Modify: `src/VerseDeck.Core/Models/DomainModels.cs`, `src/VerseDeck.Input/KeyMap.cs`, `src/VerseDeck.Input/KeyInputBuilder.cs`
- Test: `tests/VerseDeck.Tests/KeyInputBuilderTests.cs`, `tests/VerseDeck.Tests/RepositoryTests.cs`

**Interfaces:**
- Produces:
  - `KeyPressAction.MaxPressDurationMs = 2000`; `AllowedModifiers` añade `LCtrl`, `RCtrl`, `LShift`, `RShift`, `LAlt`, `RAlt`.
  - `KeyMap.ToVirtualKey` acepta además: `LSHIFT` 0xA0, `RSHIFT` 0xA1, `LCTRL` 0xA2, `RCTRL` 0xA3, `LALT` 0xA4, `RALT` 0xA5, `PGUP` 0x21, `PGDN` 0x22, `CAPSLOCK` 0x14, `NP_0`–`NP_9` 0x60–0x69, `NP_MULTIPLY` 0x6A, `NP_ADD` 0x6B, `NP_SUBTRACT` 0x6D, `NP_PERIOD` 0x6E, `NP_DIVIDE` 0x6F.
  - `KeyMap.TryGetFixedScanCode(string key, out ushort scanCode)` para signos por posición física: `COMMA` 0x33, `PERIOD` 0x34, `SLASH` 0x35, `SEMICOLON` 0x27, `APOSTROPHE` 0x28, `LBRACKET` 0x1A, `RBRACKET` 0x1B, `BACKSLASH` 0x2B, `MINUS` 0x0C, `EQUALS` 0x0D, `GRAVE` 0x29. `ToVirtualKey` devuelve 0 para ellas en vez de lanzar; `KeyMap.IsSupported(string key)` cubre ambos casos.
  - `KeyInputBuilder`: usa el código fijo cuando existe; marca extendidas también 0xA3, 0xA5 y 0x6F.

- [ ] **Step 1: Tests que fallan**

```csharp
[Theory] [InlineData("RAlt", 0xA5, true)] [InlineData("RCtrl", 0xA3, true)] [InlineData("LAlt", 0xA4, false)] [InlineData("LShift", 0xA0, false)]
public void SidedModifier_MapsToItsOwnKey(string modifier, int vk, bool extended)
[Fact] public void PunctuationKey_UsesFixedScanCode_NotTheLayout()   // "COMMA": VirtualKey 0, ScanCode 0x33, sin llamar a scanCodeOf
[Fact] public void NumpadDivide_IsExtended()
[Theory] [InlineData("RAlt")] [InlineData("lctrl")] public void Validate_SidedModifier_IsAccepted(string m)
[Fact] public void Validate_Duration2000_IsAccepted_2001_Throws()
[Theory] [InlineData("COMMA")] [InlineData("NP_5")] [InlineData("PGUP")] public void IsSupported_NewKeys(string key)
```

- [ ] **Step 2:** Fallan. **Step 3:** Implementar. **Step 4:** Pasan, y los tests anteriores también.
- [ ] **Step 5:** `git commit -m "feat: sided modifiers, numpad, punctuation by scan code and long presses"`

---

### Task 2: Lectura de `actionmaps.xml`

**Files:**
- Create: `src/VerseDeck.Game/VerseDeck.Game.csproj` (net8.0, referencia a Core e Input), `src/VerseDeck.Game/ScInput.cs`, `src/VerseDeck.Game/ActionMapsReader.cs`
- Create: `tests/VerseDeck.Tests/Fixtures/actionmaps-real.xml` (copia del archivo local), `tests/VerseDeck.Tests/ScInputTests.cs`, `tests/VerseDeck.Tests/ActionMapsReaderTests.cs`
- Modify: `VerseDeck.slnx`, `src/VerseDeck.App/VerseDeck.App.csproj` (referencia), `tests/VerseDeck.Tests/VerseDeck.Tests.csproj` (copiar `Fixtures`)

**Interfaces:**
- Produces:
```csharp
public enum ScDevice { Keyboard, Mouse, Joystick, Gamepad, Unknown }
public sealed record ScInput(ScDevice Device, int Instance, IReadOnlyList<string> Modifiers, string Key, bool IsUnbound, string Raw)
{
    public static ScInput Parse(string raw);
    // Solo teclado, no vacio, y todas las teclas conocidas por KeyMap.
    public bool TryToKeyPress(int pressDurationMs, out KeyPressAction action);
}
public sealed record GameRebind(string ActionMap, string Action, ScInput Input, int MultiTap);
public sealed record ActionMapsFile(bool Ok, string? Error, IReadOnlyList<GameRebind> Rebinds);
public static class ActionMapsReader { public static ActionMapsFile Read(string path); }
```
- Traducción de nombres del juego a `KeyMap`: `lalt`→`LAlt`, `ralt`→`RAlt`, `lctrl`→`LCtrl`, `rctrl`→`RCtrl`, `lshift`→`LShift`, `rshift`→`RShift`, `escape`→`ESC`, `pgup`, `pgdn`, `np_N`, `np_add`…, letras, dígitos, `f1`–`f12`, `space`, `tab`, `enter`, `backspace`, `insert`, `delete`, `home`, `end`, flechas, `capslock` y los signos de la Tarea 1 con el mismo nombre. En una entrada con `+`, el último elemento es la tecla y los anteriores son modificadores.
- `mouseN`, `mwheel_up`, `mwheel_down` con cualquier prefijo → `ScDevice.Mouse`.
- `Read` abre con `FileShare.ReadWrite`, perfil por defecto `ActionProfiles[@profileName='default']` o el primero.

- [ ] **Step 1: Tests que fallan**

```csharp
// ScInputTests
[Fact] public void Keyboard_WithModifier()        // "kb1_lctrl+n" => Keyboard, 1, ["LCtrl"], "N"; KeyPress Key "N"
[Fact] public void Keyboard_MouseButton_IsMouse() // "kb1_mouse5" => Mouse; TryToKeyPress false
[Fact] public void Joystick_Slider()              // "js1_slider1" => Joystick, 1; TryToKeyPress false
[Theory] [InlineData("kb1_")] [InlineData("kb1_ ")] [InlineData("")] public void Blank_IsUnbound(string raw)
[Fact] public void UnknownKeyName_IsNotSendable() // "kb1_hyperkey" => Keyboard; TryToKeyPress false
[Fact] public void Comma_IsSendable()             // "kb1_comma" => Key "COMMA"
[Fact] public void RightAltCombination()          // "kb1_ralt+r" => ["RAlt"], "R"
[Fact] public void FourModifiers_IsNotSendable()

// ActionMapsReaderTests
[Fact] public void RealFile_HasFiveRebinds()      // v_autoland kb1_lctrl+n, v_lock_rotation kb1_mouse5,
                                                  // v_strafe_forward js1_slider1, v_toggle_relative_mouse_mode kb1_comma (spaceship_movement)
                                                  // y foip_pushtotalk_proximity kb1_backslash => 5 en total
[Fact] public void MissingFile_ReturnsError_NotException()
[Theory] [InlineData("")] [InlineData("<ActionMaps><ActionProfiles")] [InlineData("<Other/>")] public void BrokenFile_ReturnsError(string xml)
[Fact] public void MultiTap_IsRead()
[Fact] public void FileOpenForWritingByAnotherProcess_IsStillRead()
```

- [ ] **Step 2–4:** Fallan, implementar, pasan.
- [ ] **Step 5:** `git commit -m "feat: read Star Citizen actionmaps rebinds"`

---

### Task 3: Catálogo y resolución

**Files:**
- Create: `src/VerseDeck.Game/game-actions.json` (recurso incrustado), `src/VerseDeck.Game/GameActionCatalog.cs`, `src/VerseDeck.Game/BindingResolver.cs`
- Test: `tests/VerseDeck.Tests/GameActionCatalogTests.cs`, `tests/VerseDeck.Tests/BindingResolverTests.cs`

**Interfaces:**
- Produces:
```csharp
public sealed record GameAction(string Id, string Label, string ActionMap, IReadOnlyList<string> ActionNames, string? DefaultInput, int PressMs);
public sealed class GameActionCatalog
{
    public const string GameVersion = "4.10";
    public static GameActionCatalog Load();                 // del recurso incrustado
    public IReadOnlyList<GameAction> Actions { get; }
    public GameAction? Find(string id);                     // id de catalogo
    public string? PresetLinkFor(string moduleName);        // "Flight Ready" => "flight_ready"
}
public enum BindStatus { YourKey, Default, NoKey, NotSendable, Conflict }
public sealed record Resolution(BindStatus Status, KeyPressAction? Action, string Detail);
public static class BindingResolver
{
    // gameAction: id de catalogo, o "mapa/accion" para una accion solo presente en el archivo.
    public static Resolution Resolve(string gameAction, GameActionCatalog catalog, IReadOnlyList<GameRebind> rebinds);
}
```
- Contenido del catálogo (id · mapa · nombres de acción · tecla por defecto · ms):

| id | mapa | acciones | defecto | ms |
|---|---|---|---|---|
| flight_ready | spaceship_general | v_flightready | ralt+r | 60 |
| power_all | spaceship_power | v_power_toggle | u | 60 |
| power_thrusters | spaceship_power | v_power_toggle_thrusters, v_power_toggle_group_1 | i | 60 |
| power_shields | spaceship_power | v_power_toggle_shields, v_power_toggle_group_2 | o | 60 |
| power_weapons | spaceship_power | v_power_toggle_weapons, v_power_toggle_group_3 | p | 60 |
| landing_gear | spaceship_movement | v_toggle_landing_system | n | 60 |
| autoland | spaceship_movement | v_autoland | n | 1000 |
| request_landing | spaceship_general | v_atc_request | lalt+n | 60 |
| headlights | lights_controller | v_lights | l | 60 |
| doors_toggle | spaceship_general | v_toggle_all_doors | — | 60 |
| doorlocks_toggle | spaceship_general | v_toggle_all_doorlocks | — | 60 |
| scan_mode | spaceship_scanning | v_toggle_scan_mode | v | 60 |
| ping | spaceship_radar | v_invoke_ping | tab | 60 |
| mining_mode | spaceship_mining | v_toggle_mining_mode | m | 60 |
| master_mode | spaceship_movement | v_master_mode_cycle | b | 600 |
| decoupled | spaceship_movement | v_ifcs_toggle_vector_decoupling | c | 60 |
| cruise | spaceship_movement | v_ifcs_toggle_cruise_control | lalt+c | 60 |
| vtol | spaceship_movement | v_toggle_vtol | k | 60 |
| eject | spaceship_general | v_eject | ralt+y | 60 |
| self_destruct | spaceship_general | v_self_destruct | backspace | 2000 |
| exit_seat | spaceship_general | v_exit | y | 1000 |
| decoy | spaceship_defensive | v_weapon_countermeasure_decoy_launch | h | 60 |
| noise | spaceship_defensive | v_weapon_countermeasure_noise_launch | j | 60 |
| mobiglas | player | mobiglas | f1 | 60 |
| chat | default | toggle_chat | f12 | 60 |
| wipe_visor | player | visor_wipe | lalt+x | 60 |

  Vínculos de presets: Flight Ready→flight_ready, Power Toggle→power_all, Engines Toggle→power_thrusters, Landing Gear→landing_gear, Lights→headlights, Radar Ping→ping, Scan Mode→scan_mode, Quantum Mode→master_mode, Shields→power_shields, Weapons→power_weapons, Doors→doors_toggle, Eject→eject, Self Destruct→self_destruct.
- Reglas de `Resolve`:
  1. Rebinds candidatos: los del archivo cuya acción esté entre los nombres de la acción (el mapa no se exige, porque puede haber cambiado entre versiones) y cuyo dispositivo sea teclado o ratón. Joystick y mando se ignoran.
  2. Sin candidatos: tecla por defecto → `Default`; sin tecla por defecto → `NoKey`.
  3. Candidato vacío → `NoKey`. Candidato de ratón, con `MultiTap > 1` o no convertible → `NotSendable`. En ambos casos `Action` es `null`.
  4. Candidato convertible → `YourKey`, con `PressMs` del catálogo (60 si la acción no está en el catálogo).
  5. Si la tecla resultante coincide con la entrada de otro rebind de teclado del mismo mapa → `Conflict` (con `Action`).
  6. Id desconocido y sin rebind en el archivo → `NoKey`.

- [ ] **Step 1: Tests que fallan**

```csharp
// GameActionCatalogTests
[Fact] public void Load_Has26Actions_WithUniqueIds()
[Fact] public void EveryDefaultInput_IsSendable()            // ScInput.Parse("kb1_"+default).TryToKeyPress
[Fact] public void EveryPressMs_IsBetween20And2000()
[Fact] public void PresetLinks_PointToExistingActions()      // los 13
[Fact] public void PresetLinkFor_UnknownModule_IsNull()      // "Cargo", "Star Map", "Comms"

// BindingResolverTests
[Fact] public void NoRebind_UsesDefault()                    // flight_ready => Default, RAlt+R
[Fact] public void KeyboardRebind_Wins()                     // autoland con archivo real => YourKey, LCtrl+N, 1000 ms
[Fact] public void AliasActionName_IsRecognised()            // v_power_toggle_group_1 => power_thrusters
[Fact] public void JoystickRebind_IsIgnored()                // sigue Default
[Fact] public void BlankRebind_IsNoKey_WithoutAction()
[Fact] public void MouseRebind_IsNotSendable_WithoutAction()
[Fact] public void MultiTapRebind_IsNotSendable()
[Fact] public void NoDefaultAndNoRebind_IsNoKey()            // doors_toggle
[Fact] public void SameKeyAsAnotherRebindInSameMap_IsConflict()
[Fact] public void FileOnlyAction_ResolvesByMapAndName()     // "spaceship_movement/v_toggle_relative_mouse_mode" => YourKey, COMMA
[Fact] public void UnknownId_IsNoKey()
```

- [ ] **Step 2–4:** Fallan, implementar, pasan.
- [ ] **Step 5:** `git commit -m "feat: game action catalog and binding resolver"`

---

### Task 4: Localizar la instalación

**Files:**
- Create: `src/VerseDeck.Game/GameInstallLocator.cs`
- Test: `tests/VerseDeck.Tests/GameInstallLocatorTests.cs`

**Interfaces:**
- Produces:
```csharp
public sealed record GameInstall(string ChannelFolder, string ActionMapsPath, string Source); // Source: "Ajuste" | "Lanzador" | "Ruta habitual"
public sealed class GameInstallLocator(string launcherLogFolder, IReadOnlyList<string> driveRoots)
{
    public static GameInstallLocator ForThisMachine();
    public GameInstall? Locate(string? configuredFolder);
    public static bool IsChannelFolder(string folder);   // existe user\client\0 o StarCitizen_Launcher.exe
}
```
- Una carpeta configurada que no es válida no se sustituye por la detección: `Locate` devuelve `null` para que el fallo se vea.
- El log del lanzador se lee con `FileShare.ReadWrite`; la ruta viene con barras dobles (`D:\\RSI\\...`).

- [ ] **Step 1: Tests que fallan** (carpetas temporales)

```csharp
[Fact] public void ConfiguredFolder_Wins()
[Fact] public void ConfiguredFolder_Invalid_ReturnsNull()
[Fact] public void LauncherLog_LastLiveLaunchLine_IsUsed()     // dos lineas, gana la ultima; barras dobles
[Fact] public void LauncherLog_PointingToMissingFolder_FallsBackToDrives()
[Fact] public void StandardPath_OnAnyDriveRoot()
[Fact] public void NothingFound_ReturnsNull()
[Fact] public void ActionMapsPath_IsUnderProfilesDefault()
```

- [ ] **Step 2–4:** Fallan, implementar, pasan.
- [ ] **Step 5:** `git commit -m "feat: locate the Star Citizen install"`

---

### Task 5: Persistencia del vínculo

**Files:**
- Modify: `src/VerseDeck.Core/Models/DomainModels.cs`, `src/VerseDeck.Data/SqliteVerseDeckRepository.cs`, y los `new DeckButton(...)` existentes
- Test: `tests/VerseDeck.Tests/RepositoryTests.cs`

**Interfaces:**
- Produces: `DeckButton(..., bool MobileHaptics, string GameAction = "")`; `AppSettings(..., bool WelcomeSoundEnabled = true, string GameFolder = "")`. Columna `Buttons.GameAction TEXT NOT NULL DEFAULT ''`, añadida en `InitializeAsync` si `PRAGMA table_info(Buttons)` no la tiene. Clonar un perfil conserva el vínculo.

- [ ] **Step 1: Tests que fallan**

```csharp
[Fact] public async Task GameAction_RoundTrips()
[Fact] public async Task GameFolder_RoundTrips()
[Fact] public async Task DatabaseWithoutGameActionColumn_IsUpgraded_KeepingButtonsAndPhrases()
// crear BD, ALTER TABLE Buttons DROP COLUMN GameAction, InitializeAsync => 16 botones, frases intactas, GameAction ""
[Fact] public async Task Initialize_Twice_DoesNotFailOnExistingColumn()
```

- [ ] **Step 2–4:** Fallan, implementar, pasan.
- [ ] **Step 5:** `git commit -m "feat: persist module game action link and game folder"`

---

### Task 6: `ControlSync`

**Files:**
- Create: `src/VerseDeck.App/Services/ControlSync.cs`, `src/VerseDeck.App/Services/IFileWatch.cs` (interfaz y `FileWatch` real sobre `FileSystemWatcher`)
- Test: `tests/VerseDeck.Tests/ControlSyncTests.cs`

**Interfaces:**
- Consumes: `DeckSession`, `GameInstallLocator`, `ActionMapsReader`, `BindingResolver`, `GameActionCatalog`, `IUiScheduler`.
- Produces:
```csharp
public interface IFileWatch { IDisposable Watch(string path, Action changed); }
public sealed record ModuleLink(long ButtonId, string ModuleName, string GameAction, string ActionLabel, BindStatus Status, string KeyText, string Detail);
public sealed class ControlSync
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);
    public GameInstall? Install { get; }
    public string FileStatus { get; }                    // "Leido a las HH:mm:ss" | error legible
    public IReadOnlyList<GameRebind> Rebinds { get; }    // ultima lectura buena
    public IReadOnlyList<ModuleLink> Links { get; }
    public event EventHandler? Changed;
    public Task SyncAsync();                             // localizar, leer, resolver, aplicar
    public BindStatus? StatusOf(long buttonId);
}
```
- `SyncAsync` guarda un módulo solo si está vinculado, la resolución trae `Action` y la tecla, los modificadores o la duración difieren. Todas las escrituras de una sincronización terminan con una sola recarga de sesión (`DeckSession.SaveButtonsAsync(IEnumerable<DeckButton>)`, nuevo).
- Lectura fallida: `FileStatus` con el error, `Rebinds` y módulos intactos.
- Cambio de archivo: espera `Debounce` con `IUiScheduler.After`, reiniciando la espera si llega otro aviso, y llama a `SyncAsync`. El vigilante se recrea si cambia la carpeta.
- `SyncAsync` no se ejecuta dos veces a la vez: una llamada durante otra marca "repetir al terminar".

- [ ] **Step 1: Tests que fallan** (instalación falsa en carpeta temporal, `FakeFileWatch`, `FakeScheduler`)

```csharp
[Fact] public async Task Sync_AppliesResolvedKey_ToLinkedModule()          // Flight Ready vinculado => RAlt+R
[Fact] public async Task Sync_LeavesManualModulesUntouched()
[Fact] public async Task Sync_NoKeyOrNotSendable_KeepsPreviousKey_AndReportsStatus()
[Fact] public async Task Sync_NothingChanged_DoesNotRaiseSessionChanged()
[Fact] public async Task Sync_SeveralModules_ReloadsSessionOnce()
[Fact] public async Task Sync_NoInstall_ReportsIt_AndTouchesNothing()
[Fact] public async Task Sync_HalfWrittenFile_KeepsLastGoodRebinds_AndModules()
[Fact] public async Task FileChange_SyncsAfterDebounce_Once_ForBurstOfEvents()
[Fact] public async Task Sync_AppliesLongPressDuration()                    // self_destruct => 2000 ms
[Fact] public async Task Sync_NeverWritesUnderTheGameFolder()               // fechas y lista de archivos iguales antes y despues
[Fact] public async Task Links_ListEveryLinkedModule_WithLabelAndKeyText()
```

- [ ] **Step 2–4:** Fallan, implementar, pasan.
- [ ] **Step 5:** `git commit -m "feat: sync linked modules with the player's bindings"`

---

### Task 7: ViewModels

**Files:**
- Create: `src/VerseDeck.App/ViewModels/ControlsViewModel.cs`
- Modify: `ShellViewModel.cs` (`ShellServices` gana `ControlSync` y `GameActionCatalog`; sección `Controles`; sincroniza al iniciar), `ModuleEditorViewModel.cs`, `DeckViewModel.cs` (`ModuleTileViewModel.Warning`), `tests/VerseDeck.Tests/Harness.cs`
- Test: `tests/VerseDeck.Tests/ControlsViewModelTests.cs`, ampliar `DeckViewModelTests.cs`

**Interfaces:**
- Produces:
```csharp
public sealed record LinkRow(string Module, string Action, string KeyText, string StatusText, string StatusKey); // StatusKey: "Positive"|"Warning"|"Danger"
public sealed record RebindRow(string ActionMap, string Action, string Input, bool InCatalog);
public sealed record PresetLinkRow(string Module, string Action, string CurrentKey, string NewKey);
public sealed partial class ControlsViewModel : ObservableObject
// GameFolderText, FolderInput, FileStatus, CatalogVersion, ObservableCollection<LinkRow> Links,
// ObservableCollection<RebindRow> Rebinds, ObservableCollection<PresetLinkRow> PresetPreview,
// SyncCommand, SaveFolderCommand, LinkPresetsCommand
```
- Textos de estado: `Tu tecla`, `Por defecto (4.10)`, `Sin tecla en el juego`, `No enviable`, `Conflicto`. Claves: `YourKey` y `Default` → `Positive`; `Conflict` → `Warning`; `NoKey` y `NotSendable` → `Danger`.
- `PresetPreview`: módulos del perfil activo sin vínculo cuyo nombre tenga vínculo de preset. `LinkPresetsCommand` guarda los vínculos y sincroniza; no toca módulos ya vinculados.
- `SaveFolderCommand`: vacío = detección automática; carpeta no válida → error en el pie y no se guarda.
- Editor: `GameActionChoices` (primera opción `(ninguna)`, luego catálogo por etiqueta, luego acciones solo presentes en el archivo como `mapa/accion`), `SelectedGameAction`, `IsLinked`. Guardar con vínculo conserva la tecla actual y lanza sincronización; `Key`/`Modifiers` no se validan ni se usan cuando `IsLinked`.
- `ModuleTileViewModel.Warning`: `"SIN TECLA"` para `NoKey`, `"REVISAR"` para `NotSendable` y `Conflict`, vacío en el resto.

- [ ] **Step 1: Tests que fallan**

```csharp
[Fact] public async Task Initialize_ShowsDetectedFolder_AndRebinds()
[Fact] public async Task Rebinds_MarkThoseOutsideTheCatalog()
[Fact] public async Task PresetPreview_ListsThirteenModules_WithKeyChanges()   // Flight Ready: Alt+R => RAlt+R
[Fact] public async Task LinkPresets_LinksAndSyncs_AndEmptiesPreview()
[Fact] public async Task LinkPresets_LeavesStarMapCargoCommsManual()
[Fact] public async Task SaveFolder_Invalid_ReportsError_AndKeepsSetting()
[Fact] public async Task SaveFolder_Empty_ReturnsToAutoDetection()
[Fact] public async Task NoInstall_ShowsMessage_AndDeckStillWorks()
[Fact] public async Task Editor_LinkingModule_AppliesGameKey()
[Fact] public async Task Editor_Unlinking_KeepsLastKey_AndAllowsManualEdit()
[Fact] public async Task Editor_Choices_IncludeFileOnlyActions()
[Fact] public async Task Tile_ShowsWarning_ForNoKeyLink()                      // Doors vinculado a doors_toggle
[Fact] public async Task CloneProfile_KeepsLinks()
```

- [ ] **Step 2–4:** Fallan, implementar, pasan.
- [ ] **Step 5:** `git commit -m "feat: controls view model, editor link and tile warnings"`

---

### Task 8: Vistas

**Files:**
- Create: `src/VerseDeck.App/Views/ControlsView.xaml(.cs)`
- Modify: `MainWindow.xaml` (raíl y sección), `Views/ModuleEditorView.xaml`, `Views/DeckView.xaml` (aviso en el tile), `Themes/Icons.xaml` (`Icon.nav.controls`), `App.xaml.cs` (composición: `GameInstallLocator.ForThisMachine()`, `FileWatch`, catálogo), `tests/VerseDeck.Tests/ThemeResourceTests.cs` (icono nuevo)

- [ ] **Step 1:** Test que falla: `Icons_DefineEveryModuleAndNavigationKey` incluye `nav.controls`.
- [ ] **Step 2:** Escribir la vista con cuatro tarjetas (Juego, Módulos vinculados, Vincular módulos por defecto, Tus rebinds), el desplegable del editor con tecla y modificadores deshabilitados cuando `IsLinked`, y el aviso del tile en color `Warning`.
- [ ] **Step 3:** `dotnet build` sin avisos y `dotnet test` en verde.
- [ ] **Step 4: Comprobación en vivo** con `VERSEDECK_DATA_DIR` desechable y la instalación de `D:\rsi\StarCitizen\LIVE`, anotando cada resultado:
  1. Controles muestra `D:\RSI\StarCitizen\LIVE`, origen "Lanzador", y los cinco rebinds.
  2. "Vincular módulos por defecto" enseña trece cambios; tras aplicarlo, Flight Ready muestra `RAlt+R` y Doors el aviso "SIN TECLA".
  3. Una pulsación real de un módulo vinculado con Alt derecho llega a una caja de texto de prueba como esa tecla.
  4. Lista de archivos y fechas bajo `D:\rsi\StarCitizen\LIVE\user` idéntica antes y después.
  5. Capturas de la sección en dos temas.
- [ ] **Step 5:** `git commit -m "feat: controls section"`

---

### Task 9: Cierre

- [ ] **Step 1:** Repasar los criterios de aceptación del spec y anotar cómo se comprobó cada uno.
- [ ] **Step 2:** README: sección "Controles" (qué lee, qué no hace, catálogo 4.10 sin verificar nombres internos). Spec: estado implementado.
- [ ] **Step 3:** `git commit -m "docs: document the controls section"`
