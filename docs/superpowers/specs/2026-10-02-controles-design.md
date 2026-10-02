# Sub-proyecto 2 — Controles

Fecha: 2026-10-02
Estado: implementado en la rama `controles`

## Propósito

Que cada módulo sepa a qué acción del juego corresponde y use la tecla que el
jugador tiene de verdad en Star Citizen. Hoy las teclas se escriben a mano y
varios módulos por defecto no coinciden con el juego 4.10: Flight Ready usa
Alt genérico en lugar de Alt derecho, Doors envía `K` (que es VTOL), Cargo
envía `J` (contramedida), Self Destruct y Quantum necesitan mantener la tecla
y reciben un toque de 60 ms.

## Límites

- Solo se lee `actionmaps.xml` del perfil del jugador. Nunca se escribe en la
  carpeta del juego y no se abre `Data.p4k`.
- Una acción humana sigue enviando una sola pulsación. Algunas acciones del
  juego exigen mantener la tecla; para ellas la pulsación dura más (hasta
  2 s), pero sigue siendo una tecla, una vez.
- No se envían dobles pulsaciones ni botones de ratón, mando o joystick.

## Qué se sabe del archivo (comprobado en la instalación local, 4.10)

- Ruta: `<carpeta del canal>\user\client\0\Profiles\default\actionmaps.xml`.
- Contiene solo lo que el jugador ha cambiado; los valores por defecto no
  están en ningún archivo en claro.
- Forma: `ActionMaps > ActionProfiles > actionmap[name] > action[name] >
  rebind[input]`, con `input` como `kb1_lctrl+n`, `kb1_mouse5` o
  `js1_slider1`.
- El lanzador escribe la carpeta en su log:
  `Launching Star Citizen LIVE from (D:\RSI\StarCitizen\LIVE)`.

Por conocimiento del formato, sin comprobar en esta instalación: un `input`
vacío tras el prefijo significa "sin asignar" y `multiTap="2"` indica doble
pulsación.

## Piezas

Proyecto nuevo `VerseDeck.Game` (net8.0, sin dependencias de WPF):

1. **`GameInstallLocator`** — devuelve la carpeta del canal: primero el
   ajuste `GameFolder`, después la última línea "Launching Star Citizen LIVE
   from (...)" del log más reciente en `%AppData%\rsilauncher\logs`, después
   `<unidad>\Program Files\Roberts Space Industries\StarCitizen\LIVE` en cada
   unidad. Solo canal LIVE en esta versión.
2. **`ActionMapsReader`** — lee el XML y devuelve la lista de rebinds
   (mapa, acción, entrada cruda, multiTap). Un archivo ausente, vacío o mal
   formado da un resultado con error legible, nunca una excepción.
3. **`ScInput`** — interpreta una entrada: dispositivo (teclado, ratón,
   joystick, mando), instancia, modificadores y tecla. Traduce los nombres
   del juego a los de `KeyMap`. Una entrada de teclado que en realidad es un
   botón de ratón (`kb1_mouse5`) se clasifica como ratón.
4. **`GameActionCatalog`** — tabla propia, en `game-actions.json` incrustado:
   id, etiqueta en español, mapa, nombres de acción (uno o varios alias),
   tecla por defecto de teclado o ninguna, y milisegundos de pulsación si la
   acción exige mantener. Versión de juego a la que corresponde: 4.10.
5. **`BindingResolver`** — para una acción del catálogo y los rebinds
   leídos, decide la tecla y un estado.
6. **`ControlSync`** (en la app) — aplica las teclas resueltas a los módulos
   vinculados y vigila el archivo.

### Estados de un módulo vinculado

| Estado | Cuándo | Qué hace VerseDeck |
|---|---|---|
| Tu tecla | hay rebind de teclado utilizable | usa esa tecla |
| Por defecto | no hay rebind de teclado | usa la del catálogo |
| Sin tecla | rebind vacío, o el catálogo no tiene tecla por defecto | conserva la tecla anterior y avisa |
| No enviable | el rebind es ratón, doble pulsación o una tecla que `KeyMap` no conoce | conserva la tecla anterior y avisa |
| Conflicto | la tecla resuelta coincide con otro rebind del mismo mapa | usa la tecla y avisa |

Un rebind de joystick o mando no cambia la tecla de teclado: esos
dispositivos se ignoran al resolver.

### El catálogo y sus límites

Las teclas por defecto salen de la referencia pública de teclas de la 4.10.
Los nombres internos de acción salen de documentación comunitaria antigua y
del propio archivo del jugador; **no se pueden comprobar sin abrir
`Data.p4k`**. Si un nombre del catálogo es incorrecto, el efecto es que un
rebind del jugador para esa acción no se reconoce y se usa la tecla por
defecto. Para que eso se vea:

- La sección Controles lista todos los rebinds del archivo y marca los que
  no corresponden a ninguna acción del catálogo.
- Un módulo se puede vincular también a cualquier acción presente en el
  archivo, aunque no esté en el catálogo.

Acciones del catálogo inicial: preparar vuelo, energía (todo, motores,
escudos, armas), tren de aterrizaje, aterrizaje automático, pedir permiso de
aterrizaje, luces, puertas, bloqueo de puertas, modo escáner, ping, modo
minería, modo maestro (NAV/SCM), desacoplado, crucero, VTOL, eyectar,
autodestrucción, salir del asiento, señuelo, ruido, mobiGlas, chat y limpiar
visor.

### Cambios en lo existente

- `DeckButton` gana `GameAction` (texto, vacío si la tecla es manual).
  Columna nueva en `Buttons`, añadida con `ALTER TABLE` si falta.
- `AppSettings` gana `GameFolder`.
- `KeyPressAction`: modificadores con lado (`LCtrl`, `RCtrl`, `LShift`,
  `RShift`, `LAlt`, `RAlt`) además de los genéricos; duración máxima de
  250 ms a 2000 ms. El editor sigue sin exponer la duración: los módulos
  manuales se quedan en 60 ms.
- `KeyMap` y `KeyInputBuilder`: teclas con lado, teclado numérico, AvPág,
  RePág, Bloq Mayús y signos de puntuación. Los signos se envían por código
  de posición física, porque el juego los nombra por la distribución
  estadounidense y el teclado del jugador es español. Alt y Ctrl derechos
  llevan la marca de tecla extendida.

### Vinculación de los módulos existentes

Nada cambia solo al actualizar. La sección Controles ofrece "Vincular
módulos por defecto": muestra qué módulo iría a qué acción y qué tecla
cambiaría, y se aplica al confirmar. Star Map, Cargo y Comms no tienen
acción equivalente en el catálogo y se quedan manuales.

### Sincronización

Se sincroniza al arrancar, al pulsar "Sincronizar", al vincular un módulo y
cuando `actionmaps.xml` cambia (vigilado, con un segundo de espera para
agrupar escrituras). Sincronizar solo toca módulos vinculados cuyo estado
sea "Tu tecla", "Por defecto" o "Conflicto" y cuya tecla difiera.

## Interfaz

- Sección nueva **CONTROLES** en el raíl:
  - Juego: carpeta detectada, campo para fijarla a mano, estado del archivo,
    hora de la última lectura y botón Sincronizar.
  - Módulos vinculados: módulo, acción, tecla y estado.
  - Vincular módulos por defecto, con la lista previa de cambios.
  - Tus rebinds: lo que hay en el archivo, tal cual.
- Editor de módulo: desplegable "Acción del juego". Con una acción elegida,
  tecla y modificadores quedan en solo lectura.
- Tile: aviso "SIN TECLA" o "REVISAR" cuando el estado no es utilizable.

## Errores

- Sin instalación o sin archivo: Controles lo dice y el resto de la app
  funciona con teclas manuales.
- Archivo ilegible o a medio escribir: se conserva la última lectura buena y
  se reintenta en el siguiente cambio.
- Carpeta manual inexistente: error en el campo, no se guarda.

## Tests

- `ScInput`: teclado con y sin modificadores, lados, ratón bajo prefijo de
  teclado, joystick, vacío, nombre desconocido.
- `ActionMapsReader` con una copia del archivo real y con casos rotos.
- `BindingResolver`: cada estado de la tabla, alias de acción, conflicto.
- `GameInstallLocator` con carpetas y logs temporales.
- Migración de la columna y del ajuste sobre una base de la versión anterior.
- `ControlSync`: aplica, no toca manuales, no toca estados no utilizables,
  reacciona a un cambio de archivo, conserva la última lectura buena.
- `KeyMap`/`KeyInputBuilder`: teclas con lado, extendidas y signos.
- ViewModels de Controles y del editor con el vínculo.
- Comprobación en vivo contra `D:\rsi\StarCitizen\LIVE`.

## Criterios de aceptación

- Con la instalación local, Controles muestra la carpeta, los cuatro rebinds
  del archivo actual y el estado de cada módulo vinculado.
- Tras "Vincular módulos por defecto", Flight Ready envía Alt derecho + R.
- Cambiar `actionmaps.xml` con la app abierta actualiza el módulo afectado
  sin reiniciar.
- La app no escribe nada bajo la carpeta del juego.
- `dotnet build` y `dotnet test` pasan.

## Fuera de alcance

Canales PTU/EPTU, exportaciones de `controls\mappings`, escribir rebinds,
enviar ratón o joystick, mantener la tecla mientras dure el clic.
