# Reconocimiento más fiable — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el reconocimiento de voz descarte la conversación, que el jugador pueda comprobar sus frases antes de jugar y que vea por qué no se ejecutó algo, gastando lo mínimo mientras Star Citizen corre.

**Architecture:** `VerseDeck.Voice` añade un modelo de descarte (gramática de dictado) al motor en vivo y un `PhraseChecker` que reconoce WAV sin micrófono. En la app, `VoiceDoctor` genera audio con el copiloto y lo pasa al comprobador; `VoiceViewModel` guarda un historial de 20 reconocimientos. Todo lo nuevo que gasta CPU se ejecuta solo a petición.

**Tech Stack:** .NET 8, System.Speech, sherpa-onnx (vía `CopilotService`), WPF, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-03-reconocimiento-design.md`

## Global Constraints

- Eficiencia: nada nuevo se ejecuta en bucle ni por temporizador; la comprobación de frases solo a petición, cancelable, y el modelo de voz se libera al terminar.
- Ninguna comprobación usa el micrófono ni el motor en vivo.
- Una acción humana, una pulsación (sin cambios).
- Rama `reconocimiento`; `dotnet build VerseDeck.slnx`; `dotnet test tests/VerseDeck.Tests`; en PowerShell no aparece la palabra suelta `del`.

## Review Focus

1. Conversación durante la escucha continua: no ejecuta nada. Tasks 1 y 6.
2. Comprobar frases sin voz instalada o sin reconocedor español: mensaje claro. Task 3.
3. Comprobación pedida dos veces o cancelada a mitad: sin trabajo duplicado ni modelo cargado. Task 3.
4. Historial con origen de cada descarte (habla libre, confianza, PTT cerrado, copiloto, repetición, módulo borrado). Task 4.
5. Consumo en reposo: sin consultas periódicas a la base de datos si el panel móvil está apagado; el log del lanzador no se relee si no cambió. Task 5.

---

### Task 1: Modelo de descarte y evento `Heard`

- Core: `enum RecognitionOutcome { Executed, Discarded, LowConfidence, GateClosed, CopilotSpeaking, Repeated, ModuleGone, Offline, Failed }`; `record RecognitionHeard(string Text, double Confidence, RecognitionOutcome Outcome, DateTimeOffset At)`; `IVoiceCommandService.Heard`.
- Voice: `CommandGrammar.Load(SpeechRecognitionEngine, IEnumerable<string> phrases)` carga `Choices` con nombre `"commands"` y `DictationGrammar` con nombre `"discard"`. `RecognitionRules.Classify(grammarName, text, confidence, minimum, knownPhrase, gateOpen) → RecognitionOutcome?` (null = es un comando aceptable). El servicio emite `Heard` para todo lo que no acepta y `CommandRecognized` para el resto.
- Tests de `RecognitionRules` (todas las ramas) y un test real opcional (`VERSEDECK_VOICES_ROOT`) que genera "Vamos a por ellos" y "bajar tren" con Piper y comprueba descartado / aceptado con el motor real.

### Task 2: `PhraseChecker`

- Voice: `interface IPhraseChecker { Task<IReadOnlyList<PhraseHit>> CheckAsync(IReadOnlyList<string> grammar, IReadOnlyList<string> wavPaths, CancellationToken ct); }`; `record PhraseHit(string? Text, double Confidence, bool Discarded)`. `WindowsPhraseChecker`: un motor por llamada en un hilo propio, convierte cada WAV a 16 kHz con 300 ms de silencio, `SetInputToWaveFile`, `Recognize`, libera todo al acabar. Sin reconocedor español → `InvalidOperationException` con mensaje en español.
- Test real opcional con WAV generados.

### Task 3: `VoiceDoctor`

- App: `CopilotService.RenderAsync(VoiceInfo, string, CancellationToken) → CachedPhrase` (caché o genera, nunca reproduce) e `InstalledVoices`; `ReleaseEngine()`.
- `VoiceDoctor(DeckSession, CopilotService, IPhraseChecker, IDebugLog)`: `CheckPhraseAsync(long buttonId, string phrase, ct) → PhraseVerdict`; `CheckAllAsync(IProgress<string>?, ct) → IReadOnlyList<PhraseVerdict>`. `record PhraseVerdict(string Phrase, string Module, VerdictKind Kind, string? ConfusedWith, double Confidence, string Text)`; `enum VerdictKind { Ok, Confused, NotUnderstood, Unchecked }`. Gramática = frases activas del perfil más la probada. Con varias voces gana el peor resultado. Orden: Confused, NotUnderstood, Ok. Una segunda petición cancela la anterior. Al terminar libera el modelo de voz.
- Tests con `FakePhraseChecker`: bien, se confunde (otro módulo), mismo módulo con otra frase = bien, no la entiende, sin voces, fallo del comprobador, cancelación, orden, peor de varias voces.

### Task 4: Historial y comandos de interfaz

- `VoiceViewModel`: `History` (20, más reciente primero) con texto, confianza, resultado legible; recibe `Heard` del servicio y anota sus propios descartes (copiloto hablando, repetición, módulo borrado, apagado) y los ejecutados. `CheckAllCommand`, `CancelCheckCommand`, `CheckResults`, `IsChecking`, `CheckStatus`.
- `ModuleEditorViewModel`: `TestPhraseCommand` (prueba `NewPhrase` del módulo seleccionado), `PhraseTestResult`.
- Tests de cada motivo del historial, límite 20, orden; comandos con el falso.

### Task 5: Consumo en reposo

- `ShellViewModel`: el refresco periódico de actividad solo cuando el panel móvil está activo (los clics y la voz ya refrescan por evento).
- `GameInstallLocator`: recuerda la carpeta leída del log del lanzador mientras el archivo no cambie (ruta, tamaño, fecha).
- Tests: sin panel móvil, un registro nuevo en la base no se lee hasta otro evento; con panel, sí. El log del lanzador sin cambios se lee una sola vez.

### Task 6: Vistas, comprobación real y cierre

- Editor: botón PROBAR FRASE y resultado. Sección Voz: tarjeta "Historial" y tarjeta "Revisar frases" con lista y botón cancelar.
- Repetir la prueba de la spec con el servicio real (gramática de comandos + descarte) y anotar resultado.
- README, spec a implementado, revisión final, correcciones.
