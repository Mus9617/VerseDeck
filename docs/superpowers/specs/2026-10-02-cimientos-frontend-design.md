# Sub-proyecto 1 — Cimientos y frontend

Fecha: 2026-10-02
Estado: pendiente de revisión

## Propósito

Dejar la aplicación preparada para crecer (controles, copiloto, sesión) y
darle una interfaz propia, que no parezca generada por IA. Al terminar, la
app hace lo mismo que hoy, se ve y se organiza mejor, y los fallos conocidos
están corregidos.

## Fuera de alcance

Lector de `actionmaps.xml`, voz de salida, nuevo reconocimiento, checklists,
temporizadores, `Game.log`, comercio. Ver `2026-10-02-roadmap.md`.

## 1. Arquitectura

Hoy toda la lógica vive en `MainWindow.xaml.cs` (~1100 líneas). Se reparte así:

```
src/VerseDeck.App/
  App.xaml(.cs)            composición: crea servicios y ShellViewModel
  Services/
    DeckSession.cs         estado compartido: perfil activo, botones, frases
    ButtonExecutor.cs      confirmar -> enviar tecla -> log -> sonido
    ThemeService.cs        elige y aplica el tema
    AudioFeedback.cs       sonidos de bienvenida y de comando
    DebugLog.cs            escritura de debug.log
    IDialogService.cs      confirmaciones y errores (falso en tests)
  ViewModels/
    ShellViewModel.cs      sección activa, barra de estado, indicadores
    DeckViewModel.cs       tiles agrupados por categoría, modo editar
    ModuleEditorViewModel.cs  crear, guardar, borrar módulo
    ProfileViewModel.cs    cargar, guardar, clonar perfil
    VoiceViewModel.cs      frases, modo, PTT, detección
    MobileLinkViewModel.cs servidor, QR, dispositivos
    ActivityViewModel.cs   registro de comandos
  Views/                   un UserControl por ViewModel
  Themes/                  Tokens.xaml, Controls.xaml, Icons.xaml,
                           Neutral.xaml, Drake.xaml, Origin.xaml,
                           Aegis.xaml, Anvil.xaml
tests/VerseDeck.Tests/     xUnit, net8.0-windows
```

Reglas:

- Los ViewModels no usan tipos de WPF (ni `Brush`, ni `MessageBox`, ni
  `Dispatcher`). Exponen datos y comandos; el color sale de una clave de tema
  que resuelve la vista.
- `CommunityToolkit.Mvvm` (MIT) para `ObservableObject` y `RelayCommand`.
- `DeckSession` es la única fuente de verdad del perfil activo. Cuando
  cambia, lanza `Changed`; el deck, la voz y el servidor móvil reaccionan a
  ese evento. Esto elimina las recargas manuales repartidas por el código.
- Composición manual en `App.xaml.cs`, sin contenedor de dependencias.
- `IVerseDeckRepository` gana `DeleteButtonAsync` (hoy solo está en la clase
  concreta).

## 2. Distribución de la interfaz

Ventana con tres zonas:

- **Raíl izquierdo** (64 px): secciones Deck, Voz, Móvil, Ajustes. Deja
  sitio para Controles, Copiloto y Bitácora en sub-proyectos posteriores.
- **Área principal**: la sección activa. En Deck, los módulos agrupados bajo
  cabeceras de categoría (Flight, Navigation, Scan, Combat, Utility,
  Emergency, Systems, Custom) en lugar de una rejilla continua.
- **Inspector derecho** (320 px): solo visible en modo editar, con el módulo
  seleccionado y sus frases.

Cabecera: nombre de perfil y nave, selector de perfil, indicadores de voz y
LAN. Pie: última acción y mensajes de estado. Los errores no fatales van al
pie; `MessageBox` queda solo para confirmar acciones destructivas o módulos
con confirmación.

El registro de comandos pasa a un panel plegable bajo el deck. Perfil,
sonidos, aviso legal y consola de debug pasan a Ajustes.

## 3. Lenguaje visual

Qué se quita: vídeo de fondo, todos los PNG de `Assets/Skin` y
`Assets/VideoUi`, brillos, degradados, scanlines y ruido.

Qué se pone:

- Superficies planas, líneas de 1 px, rejilla de 8 px.
- Un solo color de acento por tema, más rojo reservado a Emergency.
- Tipografías OFL incluidas en la app: Barlow Condensed para títulos y
  etiquetas, Barlow para texto, JetBrains Mono para teclas.
- Iconos como `Geometry` en XAML, redibujados a partir de los 16 SVG de
  línea que ya existen. Heredan el color del tema.
- Tile de módulo: icono, nombre, tecla dibujada como tecla física, primera
  frase de voz. Estados: reposo, hover, pulsado y un destello breve de
  "enviado" que confirma la pulsación.

### Temas por fabricante

Cada tema es un `ResourceDictionary` que define los mismos tokens: fondo,
superficie, línea, texto, texto secundario, acento, radio de esquina, grosor
de línea y tipografía de títulos.

| Tema | Carácter | Acento | Esquinas |
|---|---|---|---|
| Neutral | Base sobria, gris azulado | cian apagado | 4 px |
| Drake | Industrial, contraste duro, etiquetas tipo estarcido | ámbar | 0 px |
| Origin | Claro, mucho aire, líneas finas | negro sobre blanco roto | 10 px |
| Aegis | Militar, gris acero, denso | azul frío | 2 px |
| Anvil | Funcional, verde oliva oscuro | arena | 2 px |

`ThemeService` deduce el fabricante del prefijo del nombre de nave
("Drake Cutlass Black" → Drake). Cualquier otro fabricante usa Neutral.
Ajustes ofrece "Automático según nave" (por defecto) o un tema fijo; la
elección se guarda en el ajuste `Theme` que ya existe. Los estilos usan
`DynamicResource`, así que el cambio es inmediato al cargar un perfil.

## 4. Correcciones incluidas

1. **Frases de voz que no se recargan.** `VoiceViewModel` reinicia el motor
   cuando `DeckSession.Changed` se dispara con la voz activa, y vuelve a
   pausarlo si el modo es PTT.
2. **Módulos por defecto que reaparecen.** Los presets se siembran una sola
   vez, marcada con el ajuste `DefaultDeckSeededV1`. Las bases de datos que
   ya tienen botones se marcan como sembradas sin añadir nada.
3. **PIN del panel móvil.** La página pide el PIN (ya no va incrustado en el
   HTML), lo cambia por un token de sesión en `POST /api/pair` y todas las
   rutas `/api/*` y `/ws` exigen ese token y origen de red privada. Cinco
   intentos fallidos bloquean esa IP un minuto. Los nombres se insertan con
   `textContent`, no con `innerHTML`.
4. **Pulsación que puede no llegar al juego.** `WindowsInputSender` envía
   scan codes (`MapVirtualKey` + `KEYEVENTF_SCANCODE`, con flag extendido
   donde toca), baja la tecla, espera `PressDurationMs` y la suelta. Sigue
   siendo una sola pulsación.
5. **Decimales según cultura.** Los ajustes se leen y escriben con cultura
   invariante. Al leer, un valor de confianza fuera de 0,1–0,98 se sustituye
   por 0,40.
6. **Peso muerto.** Se borra `Assets/Skin/raw_generated` y el resto de PNG
   sin uso. Las tablas SQLite sin uso se dejan: las usarán sub-proyectos
   posteriores.
7. **Confirmación móvil silenciosa.** Si un módulo requiere confirmación y
   llega sin ella, la respuesta es un error, no `ok: true`.

## 5. Tests

Proyecto `tests/VerseDeck.Tests` con xUnit:

- Repositorio contra SQLite temporal: siembra única, borrado de preset que
  no reaparece, ajustes con cultura es-ES.
- `KeyMap` y construcción de la entrada con scan codes (función pura
  separada de la llamada a `SendInput`).
- ViewModels con repositorio y emisor de teclas falsos: ejecutar módulo,
  modo editar no ejecuta, clonar perfil, recarga de voz al cambiar frases.
- `ThemeService`: fabricante por nombre de nave y tema fijo.
- Servidor móvil en puerto libre: sin token 401, PIN incorrecto 401, bloqueo
  tras cinco fallos, pulsación con token correcto.

La interfaz se comprueba arrancando la app con cada tema.

## 6. Criterios de aceptación

- `dotnet build` y `dotnet test` pasan.
- `MainWindow.xaml.cs` solo contiene arranque de ventana.
- Todas las funciones actuales siguen disponibles: ejecutar módulo, editar,
  crear, borrar, perfiles, frases, PTT con detección, servidor móvil con QR,
  sonidos, consola de debug.
- Cargar un perfil con nave Drake, Origin, Aegis o Anvil cambia el tema.
- La carpeta de salida no contiene `raw_generated` ni el vídeo.
- Los siete puntos de la sección 4 tienen test o comprobación manual
  documentada.
