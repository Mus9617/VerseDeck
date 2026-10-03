# Sub-proyecto 3b — Reconocimiento de voz más fiable

Fecha: 2026-10-03
Estado: pendiente de revisión

## Cambio respecto a la hoja de ruta

La hoja de ruta decía "reconocimiento de voz nuevo". La prueba de abajo dice
que cambiar de motor no compensa: el reconocedor de Windows que ya usa
VerseDeck reconoce los comandos mejor que la alternativa libre probada, y
no hay que descargar nada. Su problema real es otro: **acepta como comando
cosas que no lo son**. Esta fase ataca eso.

## La prueba (hecha en este PC, 2026-10-03)

Sin micrófono: se generaron con tres voces Piper distintas (dos de España y
una de México) las 83 frases de los módulos por defecto y 24 frases que no
son comandos (respuestas del copiloto y conversación normal). Cada audio
pasó por cada motor con la misma lista de frases.

| Motor | Comandos reconocidos | Comando equivocado | Frases ajenas aceptadas |
|---|---|---|---|
| Windows (es-ES), como hoy, confianza 0,40 | 95 % | 11 | 56 de 72 |
| Windows, confianza 0,60 | 92 % | 7 | 27 de 72 |
| Windows, confianza 0,70 | 79 % | 2 | 11 de 72 |
| **Windows + modelo de descarte, 0,40** | **91 %** | **7** | **12 de 72** |
| Vosk small-es (57 MB), gramática cerrada | 75 % | 9 | 5 de 72 |

Detalle de los 12 aceptados con el modelo de descarte:

- 11 son frases del propio copiloto ("Comunicaciones.", "Modo escáner."),
  que la guarda de la fase 3a ya descarta.
- 1 es conversación: "Vamos a por ellos" tomado como "weapons".

De los 7 "comandos equivocados", 4 van al mismo módulo ("gear up" oído como
"gear down"; "communications" como "comunicaciones"), así que no cambian
nada. Los 3 que sí cambian de módulo son frases en inglés dichas por una
voz española: "eject" → "shields", "self destruct" → "shields off",
"open comms" → "weapons".

Vosk falla sobre todo porque su vocabulario español no contiene muchas
palabras de las frases (todas las inglesas, "eyectar", "escáner"…).

Límites de la prueba: voz sintética y audio limpio. Una voz real con ruido
de juego dará cifras peores en todos los motores; lo que compara es el orden
entre ellos y el efecto del modelo de descarte.

## Qué se hace

1. **Modelo de descarte.** Junto a la lista de comandos se carga la
   gramática de dictado de Windows. Si lo que se oye se parece más a habla
   libre que a un comando, gana el dictado y no se ejecuta nada. Es la
   mejora más grande de la prueba: de 56 frases ajenas aceptadas a 12 (y a
   1 contando la guarda del copiloto). Coste: unos 15 ms más por frase.
2. **Comprobar frases.** En el editor de módulo, "Probar frase" genera la
   frase con la voz del copiloto y la pasa por el reconocedor real, sin
   micrófono, contra todas las frases del perfil. Resultado:
   - "Bien": la entiende como este módulo y con qué confianza.
   - "Se confunde con X": la entiende como otro módulo.
   - "No la entiende": ni este ni otro módulo.
3. **Revisar todas.** En la sección Voz, un botón pasa todas las frases del
   perfil activo con todas las voces instaladas y lista las problemáticas,
   empezando por las que se confunden con otro módulo.
4. **Por qué no se ejecutó.** La sección Voz muestra los últimos 20
   reconocimientos con su frase, confianza y resultado: ejecutado, o
   descartado por habla libre, confianza baja, copiloto hablando o
   repetición.

Las pruebas de frases necesitan una voz del copiloto instalada. Sin ella,
los botones lo explican.

## Lo que no se hace

- No se añade Vosk ni otro motor: peor resultado y una descarga más.
- No se cambia la confianza mínima por defecto (0,40): con el modelo de
  descarte, subirla cuesta más comandos reconocidos de lo que ahorra.
- Palabra de activación ("Ordenador, …"): no probada; queda para más
  adelante.

## Piezas

En `VerseDeck.Voice`:

- `CommandGrammar`: crea las dos gramáticas (comandos y descarte) para un
  motor. La usan el servicio en vivo y el comprobador.
- `WindowsSpeechCommandService`: carga ambas; un resultado de la gramática
  de descarte se anota como descartado y no se emite. Emite un evento
  `Heard` con cada resultado (frase, confianza, gramática, motivo) para el
  historial.
- `PhraseChecker`: recibe audios WAV y la lista de frases con su módulo, los
  reconoce con un motor propio en memoria (`SetInputToWaveFile`) y devuelve
  por audio: frase reconocida, módulo, confianza o "descartado".

En la app:

- `VoiceDoctor`: une el copiloto (para generar el audio, con la caché de
  frases) y el comprobador. Genera, comprueba y clasifica.
- `VoiceViewModel`: historial de reconocimientos (20), comando "Revisar
  todas" y su lista de resultados.
- `ModuleEditorViewModel`: comando "Probar frase" y su resultado en texto.

## Errores

- Sin reconocedor en español instalado: la comprobación lo dice; el resto
  de VerseDeck no cambia.
- Generar o reconocer falla: se anota en el log y la frase sale como "no se
  pudo comprobar".
- La comprobación nunca usa el micrófono ni toca el motor en vivo.

## Tests

- `CommandGrammar` y el servicio: un resultado de descarte no emite
  comando; uno de comandos con confianza suficiente sí.
- `PhraseChecker` con audios de prueba generados en el propio test con la
  voz instalada en el scratchpad (opcional, fuera de la batería normal) y
  con un reconocedor falso dentro de la batería.
- `VoiceDoctor`: clasificación bien / se confunde / no la entiende, orden de
  la lista, sin voz instalada, fallo de generación.
- Historial: guarda 20, el más reciente primero, con el motivo correcto en
  cada caso (incluida la guarda del copiloto y la repetición).

## Criterios de aceptación

- Con el modelo de descarte activo, la prueba de arriba repetida con el
  servicio real acepta como mucho 1 frase de conversación de 21.
- "Probar frase" con "self destruct" en el perfil por defecto avisa de
  confusión o de que no la entiende.
- El historial muestra por qué se ignoró cada frase.
- `dotnet build` y `dotnet test` pasan.
