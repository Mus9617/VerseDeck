# Checklists, temporizadores y bitácora — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Checklists guiadas por voz (una frase, una pulsación), temporizadores con aviso hablado y bitácora dictada, sin trabajo en reposo.

**Architecture:** `VerseDeck.Voice` añade una gramática de compañero y su intérprete; el servicio en vivo emite `CompanionRecognized`. En la app, `ChecklistRunner`, `TimerService` y `LogbookService` reciben esos comandos (o clics) y usan `ButtonExecutor` y `CopilotService` ya existentes.

**Tech Stack:** .NET 8, System.Speech, SQLite, WPF, CommunityToolkit.Mvvm, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-03-checklists-temporizadores-bitacora-design.md`

## Global Constraints

- Una frase del jugador produce como mucho una pulsación; nunca se encadenan pasos.
- Cero trabajo en reposo: un único temporizador programado al próximo vencimiento; cuenta atrás visible solo con la sección en pantalla.
- Textos de interfaz sin tildes; frases habladas con tildes.
- Rama `abordo`. `dotnet build VerseDeck.slnx`; `dotnet test tests/VerseDeck.Tests`. Sin la palabra suelta `del` en PowerShell.

## Review Focus

1. "Hecho" sin checklist activa no hace nada. Task 3.
2. "Hecho" en un paso cuyo módulo está bloqueado o falla: no avanza. Task 3.
3. Muchos temporizadores: un solo temporizador del sistema. Task 4.
4. Base anterior sin tablas nuevas: migración y siembra única. Task 2.
5. Nota dictada vacía o solo "anota": no se guarda. Task 5.

### Task 1: Números, gramática e intérprete (`VerseDeck.Voice`)
`SpanishNumbers.ToWords(int)`, `SpanishNumbers.TryParse(string, out int)` (1–120); `CompanionGrammar.Build(culture, checklistNames)` → gramáticas `companion` y `companion-note`; `CompanionParser.Parse(string) → CompanionCommand?`. Modelo `CompanionCommand(CompanionKind Kind, string? Name, TimeSpan? Duration, string? Label, string? Text)` en Core. Tests de números e intérprete; test real opcional con la voz.

### Task 2: Persistencia
Tablas `Checklists`, `ChecklistSteps`, `LogbookNotes`; métodos en el repositorio; siembra única ("ChecklistsSeededV1") de Prevuelo y Aterrizaje; `DeckSession.Checklists` y clonado con el perfil. Tests de ida y vuelta, migración, siembra única, clonado con ids remapeados.

### Task 3: `ChecklistRunner`
Start, Done(source), Skip, Repeat, Cancel; anuncios con `CopilotService` si puede hablar; estado observable. Tests de la sección Review Focus 1 y 2, una pulsación por "hecho", fin.

### Task 4: `TimerService`
Add, Cancel, CancelAll, Dismiss; un solo `IUiScheduler.After` vivo; aviso hablado o sonido. Tests con el reloj y el planificador falsos.

### Task 5: Bitácora y voz en vivo
`LogbookService` (añadir, listar, borrar, exportar); servicio en vivo carga la gramática de compañero y emite `CompanionRecognized`; `VoiceViewModel` lo enruta y lo anota en el historial; firma del motor incluye nombres de checklists. Tests.

### Task 6: ViewModels y vista A BORDO
`AboardViewModel` con checklist activa, editor de checklists, temporizadores (cuenta atrás solo con la sección visible) y bitácora. Vista, icono, raíl. Comprobación en vivo, README, revisión final.
