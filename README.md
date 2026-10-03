<p align="center">
  <img src="./icon.png" alt="VerseDeck Companion" width="140">
</p>

# VerseDeck Companion

Botonera companion para Windows inspirada en Star Citizen, con controles manuales, voz local Push-to-Talk y panel movil LAN.

Autor: **Zowix**

Tutorial: https://www.youtube.com/watch?v=dQw4w9WgXcQ

## Source code

```powershell
dotnet build VerseDeck.slnx
dotnet test tests/VerseDeck.Tests
dotnet run --project src/VerseDeck.App/VerseDeck.App.csproj
```

Para probar sin tocar tu deck real, apunta la app a una carpeta de datos desechable:

```powershell
$env:VERSEDECK_DATA_DIR = "C:\temp\versedeck-pruebas"
dotnet run --project src/VerseDeck.App/VerseDeck.App.csproj
```

## Temas

El tema cambia solo segun el fabricante de la nave del perfil activo: **Drake**, **Origin**, **Aegis** y **Anvil**. Cualquier otra nave usa **Neutral**. En Ajustes se puede fijar un tema concreto.

## Controles

VerseDeck lee `actionmaps.xml` de tu perfil de Star Citizen y usa tu tecla en los modulos vinculados a una accion del juego. Si cambias un bind en el juego, el modulo se actualiza solo.

- Solo lectura: nunca escribe en la carpeta del juego ni abre `Data.p4k`.
- El juego solo guarda lo que has cambiado. Para lo demas se usa un catalogo propio de teclas por defecto, valido para la version **4.10**; tras un parche puede dejar de coincidir.
- Los nombres internos de las acciones del catalogo vienen de documentacion comunitaria y no estan verificados contra el juego. Si un modulo vinculado ignora tu rebind, vinculalo a la accion tal como aparece en "Tus rebinds".
- Las acciones que piden mantener la tecla (autodestruccion, modo maestro, aterrizaje automatico) se envian como una sola pulsacion larga, de 2 segundos como maximo.
- No se envian botones de raton, mando o joystick ni dobles pulsaciones.

## Copiloto

Una voz neuronal que confirma tus ordenes. Se genera en tu PC con [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx); no usa servicios en linea ni de pago.

- Las voces no vienen con la aplicacion. En la seccion Copiloto, DESCARGAR baja el modelo elegido desde la publicacion de sherpa-onnx en GitHub, comprueba su SHA-256 y lo guarda en `%AppData%\VerseDeck Companion\voices`. Es la unica conexion a internet de esta funcion.
- Voces en español: Piper davefx (datos CC0), Piper sharvard (datos CC BY 3.0, Universidad de Edimburgo), Piper claude (Apache 2.0) y Kokoro (Apache 2.0).
- Cada frase se genera una vez y se guarda; despues suena al instante.
- Interruptores: general, SILENCIO en la cabecera, solo comandos de voz, por categoria y por modulo (frase de la personalidad, texto propio o ninguna).
- Las frases confirman la orden, no el estado de la nave: VerseDeck sabe que envio la tecla, nada mas.
- Un modulo vinculado a una accion sin tecla en el juego ya no envia la tecla que tenia antes: avisa y no envia nada, tambien desde el telefono.
- Mientras el copiloto habla, lo que oiga el microfono se descarta: su propia voz no puede disparar un comando.
- El copiloto contesta a los clics del deck y a los comandos de voz. Los toques desde el panel movil no se contestan.

Licencias de lo que usa esta funcion: sherpa-onnx (Apache 2.0), ONNX Runtime (MIT) y SharpCompress (MIT). Los modelos de voz y los binarios de sherpa-onnx incluyen datos y codigo de eSpeak NG, que se publica bajo GPL 3.0; tenlo en cuenta si redistribuyes la aplicacion.

## Panel movil

El servidor solo responde dentro de la red local. El telefono se empareja escribiendo el PIN que muestra la app; sin emparejar no puede listar ni pulsar modulos.

## Limites que el proyecto se impone

- Un clic, un toque o una frase envia como maximo una pulsacion de tecla.
- Sin secuencias, bucles ni auto-repeticion.
- Sin lectura de memoria, inyeccion, hooks ni interceptacion de red.

Estas reglas buscan mantenerse lejos de lo que el TOS de RSI prohibe (programas "auto" y "macro"). No son una aprobacion de Cloud Imperium Games: CIG no publica una lista de herramientas permitidas.

## Tipografias

Barlow, Barlow Condensed y JetBrains Mono, bajo SIL Open Font License. Las licencias estan en `src/VerseDeck.App/Fonts`.

## Legal

VerseDeck Companion es una aplicacion fan no oficial. No esta afiliada a Cloud Imperium Games, Roberts Space Industries ni Star Citizen.
