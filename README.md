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
