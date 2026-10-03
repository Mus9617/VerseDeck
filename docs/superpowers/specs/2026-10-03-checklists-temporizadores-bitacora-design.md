# Sub-proyecto 3c — Checklists, temporizadores y bitácora

Fecha: 2026-10-03
Estado: aprobado por delegación ("tú decides")

## Nota previa: palabra de activación "Verse"

Medido con el motor real y voces sintéticas antes de esta fase:

| Configuración | Comandos bien | Conversación aceptada | Conversación que empieza por "Verse" | CPU por frase de conversación |
|---|---|---|---|---|
| Solo palabra "Verse" | 85–97 % | 2–9 de 39 | 14–28 de 39 | ~6 ms |
| Descarte por dictado (actual) | 96 % | 0 de 39 | 0 de 39 | ~105 ms |

La palabra de activación sola deja pasar conversación y dispara con cualquier
frase que empiece por "Verse". No se adopta. El reconocedor de Windows
adapta su modelo entre pasadas, así que las cifras varían de una ejecución a
otra; por eso se dan como rangos.

## Propósito

Tres ayudas de copiloto que no tocan el juego más allá de lo ya permitido:

1. **Checklists guiadas**: la versión legal de una macro. El copiloto lee
   cada paso; el jugador dice "hecho" y, si el paso está vinculado a un
   módulo, se envía la tecla de ese módulo. Una frase del jugador, una
   pulsación.
2. **Temporizadores**: "avísame en diez minutos", "temporizador refinería
   treinta minutos". Al vencer, aviso con la voz del copiloto y en pantalla.
3. **Bitácora**: "anota dejé la Cutlass en Seraphim". Notas con fecha,
   perfil y nave, escritas o dictadas.

## Eficiencia (prioridad del usuario)

- Ningún temporizador del sistema por cada aviso: un solo temporizador
  programado para el próximo vencimiento. Sin avisos pendientes, cero
  trabajo.
- La cuenta atrás visible solo se refresca mientras la sección está en
  pantalla, una vez por segundo.
- Las frases de checklist y temporizador se generan con la voz del copiloto
  bajo demanda; las fijas pasan por la caché existente.
- La bitácora usa dictado solo tras la palabra "anota" o "nota"; en
  push-to-talk solo mientras se mantiene el botón, como el resto.
- Las palabras de control ("hecho", "siguiente", "saltar", "cancelar
  checklist") solo cuentan con una checklist en marcha; si no, se descartan.

## Línea del TOS

- Una frase del jugador produce como mucho una pulsación. Avanzar un paso
  vinculado envía la tecla de ese paso y nada más. Nunca se encadenan pasos.
- Los temporizadores y la bitácora no envían teclas.

## Voz: frases nuevas

Se añaden al vocabulario del motor en vivo (además de las frases de módulos):

| Frase | Acción |
|---|---|
| "checklist <nombre>" / "empezar <nombre>" | Inicia la checklist |
| "hecho" / "siguiente" / "listo" | Paso actual completado (envía su tecla si está vinculado) |
| "saltar" | Paso actual omitido, sin tecla |
| "repetir" | El copiloto repite el paso actual |
| "cancelar checklist" | Termina la checklist |
| "avísame en <n> minutos/segundos" | Temporizador sin nombre |
| "temporizador <etiqueta> <n> minutos" | Temporizador con nombre |
| "cancelar temporizadores" | Borra todos |
| "anota <texto libre>" / "nota <texto libre>" | Nota en la bitácora |

Números: de uno a ciento veinte, en palabras (el reconocedor devuelve
palabras). Etiquetas: refinería, reclamación, hangar, carga, combustible,
misión, descanso.

## Checklists

- Datos: tabla `Checklists(Id, ProfileId, Name)` y `ChecklistSteps(Id,
  ChecklistId, Position, Text, ButtonId NULL)`. Se clonan con el perfil.
- Dos de ejemplo sembradas una sola vez: "Prevuelo" (Energía general,
  Motores, Escudos, Preparar vuelo) y "Aterrizaje" (Pedir permiso, Tren,
  Motores), vinculadas a los módulos por defecto si existen.
- En marcha: el copiloto dice "Paso 1 de 4: Energía." Con el copiloto
  apagado o sin voz, el paso se muestra en pantalla y en el pie.
- "Hecho" en un paso vinculado ejecuta ese módulo con el mismo ejecutor que
  un clic (confirmación, bloqueo de módulos sin tecla, sonido, historial).
- También se puede avanzar con botones en la sección.
- Al terminar: "Checklist Prevuelo completa."

## Temporizadores

- En memoria (no sobreviven a cerrar la app; se dice en la interfaz).
- Al vencer: frase del copiloto ("Refinería: han pasado treinta minutos."),
  sonido del sistema si el copiloto está callado, y el aviso queda en la
  lista hasta descartarlo.
- Interfaz: crear a mano (etiqueta, minutos), lista con tiempo restante,
  cancelar uno o todos.

## Bitácora

- Tabla `LogbookNotes(Id, CreatedAt, ProfileName, ShipName, Text, Source)`.
- Sección BITACORA: lista de notas (más recientes arriba), campo para
  escribir, borrar nota, exportar a texto.
- El copiloto confirma: "Anotado."

## Piezas

- `VerseDeck.Voice`: `SpanishNumbers` (palabras ↔ número 1–120),
  `CompanionGrammar` (gramáticas de checklist, temporizador y nota) y su
  intérprete de texto reconocido → `CompanionCommand`.
- `VerseDeck.Core`: modelos `Checklist`, `ChecklistStep`, `LogbookNote`,
  `CompanionCommand`; repositorio ampliado.
- App: `ChecklistRunner`, `TimerService` (un solo temporizador al próximo
  vencimiento sobre `IUiScheduler`), `LogbookService`; ViewModels
  `ChecklistsViewModel`, `TimersViewModel`, `LogbookViewModel`; una sección
  nueva **A BORDO** con tres tarjetas: Checklists, Temporizadores y Bitácora.

## Tests

- `SpanishNumbers`: 1–120 ida y vuelta, formas con "y", "veintiún",
  "ciento".
- Intérprete: cada frase de la tabla, números, etiquetas, texto de nota,
  basura.
- `ChecklistRunner`: inicio, hecho con y sin módulo, saltar, repetir,
  cancelar, fin, una pulsación por "hecho", módulo bloqueado, palabras de
  control sin checklist activa.
- `TimerService`: un solo temporizador programado, orden de vencimiento,
  cancelar, varios a la vez, frase al vencer.
- Bitácora: guardar, listar, borrar, exportar, perfil y nave.
- Migración de tablas sobre una base anterior; siembra única de ejemplos.
- Prueba real opcional: frases de temporizador y nota generadas con la voz
  y reconocidas por el motor real.

## Criterios de aceptación

- "checklist prevuelo" + cuatro "hecho" envían exactamente cuatro teclas,
  una por paso vinculado, y el copiloto anuncia cada paso.
- "avísame en diez minutos" crea un temporizador de 10 minutos; sin
  temporizadores no hay ningún temporizador del sistema activo.
- "anota dejé la nave en Lorville" guarda esa nota con perfil y nave.
- `dotnet build` y `dotnet test` pasan.
