# Sub-proyecto 3a — Voz del copiloto

Fecha: 2026-10-02
Estado: pendiente de revisión

El sub-proyecto 3 de la hoja de ruta se parte en tres, cada uno con su spec:

- **3a (este)**: el copiloto habla. Voz neuronal local y respuestas que se
  pueden activar y desactivar.
- **3b**: reconocimiento de voz nuevo, mejor en español.
- **3c**: checklists guiadas, temporizadores y bitácora por voz.

## Propósito

Que VerseDeck conteste con una voz natural cuando ejecuta un comando, y que
el jugador decida cuánto habla: todo, por categoría, por módulo o nada.

## Límites

- Gratis y local. La voz se genera en el PC; solo se usa la red para
  descargar un modelo de voz, y solo cuando el jugador pulsa "Descargar".
- Voz natural: sin filtro de radio ni efectos.
- La voz no envía teclas ni lee nada del juego.
- El copiloto no afirma lo que no sabe. VerseDeck sabe que envió una tecla,
  no el estado de la nave: casi todos los módulos son conmutadores. Las
  frases por defecto confirman la orden ("Tren de aterrizaje.", "Motores,
  hecho.") y no un estado ("Tren desplegado").

## Resultado de la prueba previa (hecha en este PC)

Motor: sherpa-onnx sobre .NET 8, con seis voces en español.

| Voz | Modelo | Tamaño | Tiempo para generar 4 s de audio | Licencia |
|---|---|---|---|---|
| Piper davefx (hombre, España) | vits-piper-es_ES-davefx-medium | 60 MB | 0,6 s | CC0 |
| Piper sharvard (dos voces, España) | vits-piper-es_ES-sharvard-medium | 73 MB | 0,6 s | CC BY 3.0 |
| Piper claude (México) | vits-piper-es_MX-claude-high | 60 MB | 0,7 s | Apache 2.0 |
| Kokoro dora (mujer) y alex (hombre) | kokoro-multi-lang-v1_0 | 337 MB | 4,3 s | Apache 2.0 |

Cargar un modelo tarda entre 2 y 4 s. Kokoro tarda en generar lo mismo que
dura la frase, así que no sirve para contestar al momento si no se guarda el
audio ya generado. De ahí sale la decisión central del diseño.

## Decisión central: caché de frases

Las respuestas son frases fijas. Cada frase se genera una vez por voz y se
guarda como WAV; después se reproduce al instante. Así cualquiera de las
voces sirve, incluida la más lenta.

- Clave de caché: voz, velocidad y texto.
- Al elegir voz o personalidad se generan en segundo plano las frases del
  deck activo. Una frase que aún no esté lista se genera en el momento.
- La caché vive en `%AppData%\VerseDeck Companion\voice-cache` y se puede
  vaciar desde la interfaz.

## Piezas

Proyecto nuevo `VerseDeck.Speech` (net8.0, sin WPF):

1. **`VoiceCatalog`** — lista propia de voces (`voices.json` incrustado): id,
   nombre visible, motor (piper o kokoro), carpeta, archivos, id de hablante,
   URL de descarga, tamaño, SHA-256 del archivo y texto de licencia.
2. **`VoiceInstaller`** — descarga el `.tar.bz2` de la publicación de
   sherpa-onnx en GitHub, comprueba el SHA-256, lo extrae en
   `%AppData%\VerseDeck Companion\voices\<carpeta>` e informa del progreso.
   Se puede cancelar; una descarga a medias no deja una voz "instalada".
3. **`ITtsEngine` / `SherpaTtsEngine`** — carga el modelo la primera vez que
   hace falta, en segundo plano, y lo mantiene cargado. Genera muestras de
   audio a partir de texto.
4. **`PhraseCache`** — guarda y recupera los WAV.
5. **`ResponsePack`** — personalidades en JSON incrustado. Cada una trae
   frases genéricas, frases por acción del juego y por nombre de módulo por
   defecto, un saludo y frases de fallo. Varias variantes por caso.
6. **`IAudioPlayer` / `NAudioPlayer`** — reproduce por el dispositivo de
   salida predeterminado, con volumen propio.

En la app:

7. **`CopilotService`** — escucha lo que pasa (módulo enviado, envío
   fallido, módulo sin tecla, perfil cargado, arranque), decide si toca
   hablar, elige la frase y la reproduce.
8. **`CopilotViewModel`** y la sección **COPILOTO**.

Dependencias nuevas, todas gratuitas: `org.k2fsa.sherpa.onnx` (Apache 2.0),
`NAudio` (MIT) y `SharpCompress` (MIT) para extraer `.tar.bz2`.

## Qué dice y cuándo

| Suceso | Ejemplo (personalidad sobria) |
|---|---|
| Módulo enviado | "Tren de aterrizaje." / "Tren, hecho." |
| Módulo que pide mantener | "Autodestrucción, manteniendo." |
| Envío fallido | "No he podido enviarlo." |
| Módulo vinculado sin tecla | "Esa acción no tiene tecla en el juego." |
| Perfil cargado | "Perfil Combate." |
| Arranque (opcional) | "Sistemas listos." |

Elección de la frase para un módulo, por este orden:

1. Texto propio del módulo, si el jugador lo escribió (variantes separadas
   por `|`).
2. Frases de la personalidad para la acción del juego a la que está
   vinculado.
3. Frases de la personalidad para el nombre del módulo por defecto.
4. Frase genérica con el nombre del módulo: "{nombre}, hecho."

No se repite la misma variante dos veces seguidas.

Personalidades iniciales: **Sobria**, **Militar** y **Con carácter**
(comentarios secos, sin faltar). Solo cambian los textos; la voz es la misma.

## Interruptores

- **General**: copiloto activado o no. Desactivado al instalar.
- **Silencio rápido**: botón en la cabecera que calla todo sin tocar los
  demás ajustes; se nota a simple vista.
- **Origen**: contestar a todo, o solo a los comandos de voz.
- **Por categoría**: lista de categorías con casilla.
- **Por módulo**: en el editor, "Respuesta": de la personalidad, texto
  propio o ninguna.
- **Saludo al arrancar**: casilla aparte.

## Convivencia con el reconocimiento de voz

El micrófono oye los altavoces. Mientras el copiloto habla, y 300 ms
después, el reconocimiento no acepta comandos. En push-to-talk no cambia
nada, porque ya está cerrado salvo al mantener el botón.

Si llega una respuesta nueva mientras suena otra, la nueva corta a la
anterior.

## Cambios en lo existente

- `DeckButton` gana `Response` (texto): vacío = de la personalidad, `-` =
  ninguna, otro = texto propio. Columna nueva en `Buttons`.
- `AppSettings` gana: `CopilotEnabled`, `CopilotVoice`, `CopilotPack`,
  `CopilotVolume`, `CopilotVoiceOnly`, `CopilotMutedCategories`,
  `CopilotGreeting`.
- `ButtonExecutor` expone también el envío fallido como evento.
- El sonido `system.mp3` al enviar un comando no suena cuando el copiloto va
  a contestar a ese comando.

## Interfaz: sección COPILOTO

- **Voz**: lista de voces del catálogo con tamaño y licencia; por cada una,
  "Descargar" con progreso o "Usar"; botón "Probar" con una frase de
  ejemplo; volumen.
- **Personalidad**: desplegable y ejemplo de frases.
- **Cuándo habla**: los interruptores de arriba.
- **Caché**: cuántas frases hay generadas, "Generar ahora" y "Vaciar".

## Errores

- Sin voz instalada: el copiloto no habla y la sección lo explica.
- Descarga fallida, sin espacio o SHA-256 distinto: mensaje claro y nada a
  medio instalar.
- Modelo que no carga o audio que no se puede reproducir: se anota en el
  log, el comando se ejecuta igual y el copiloto se calla hasta el siguiente
  intento. La voz nunca bloquea ni retrasa una pulsación.

## Tests

- Catálogo: campos completos y SHA-256 con formato válido.
- Instalador con un servidor HTTP local y un `.tar.bz2` de prueba: correcto,
  hash incorrecto, cancelación, reintento.
- Caché: misma clave devuelve el mismo archivo; voz o texto distinto, otro.
- Elección de frase: orden de las cuatro fuentes, sin repetición inmediata,
  texto propio con variantes, `-` calla.
- `CopilotService` con motor y reproductor falsos: cada interruptor, solo
  voz, silencio rápido, corte de la frase anterior, fallo del motor que no
  afecta al envío, cierre del reconocimiento mientras habla.
- Migración de la columna y de los ajustes.
- Una prueba real del motor, opcional y fuera de la batería normal: genera
  un WAV con una voz instalada.

## Criterios de aceptación

- Con una voz instalada y el copiloto activado, pulsar un módulo se oye
  contestado en menos de 300 ms cuando la frase está en caché.
- Cada interruptor calla lo que dice que calla.
- Sin conexión a internet todo funciona, salvo descargar voces nuevas.
- Un fallo de voz no retrasa ni impide ninguna pulsación.
- `dotnet build` y `dotnet test` pasan.

## Fuera de alcance

Frases con datos en vivo (temporizadores, avisos de sesión), elección de
dispositivo de salida, voces en otros idiomas, clonación de voz, respuestas
distintas según la frase reconocida ("bajar tren" frente a "subir tren").
