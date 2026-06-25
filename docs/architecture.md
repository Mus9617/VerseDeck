# VerseDeck Companion Architecture

VerseDeck Companion is a Windows-first fan companion app for manual ship controls. It is designed around a strict safety boundary: every user-facing action maps to one direct key press or key combination.

## Projects

- `src/VerseDeck.App`: WPF desktop shell, dashboard, profile/button/voice controls, QR display.
- `src/VerseDeck.Core`: domain models, safe action contract, repository and service interfaces.
- `src/VerseDeck.Input`: Windows `SendInput` implementation.
- `src/VerseDeck.Voice`: local Windows Speech Recognition command service.
- `src/VerseDeck.MobileServer`: embedded ASP.NET Core LAN server and WebSocket mobile panel.
- `src/VerseDeck.Data`: SQLite repository and schema creation.
- `src/VerseDeck.Market`: manual market calculator placeholder and future API boundary.
- `src/VerseDeck.Installer`: Inno Setup script.
- `assets/icons`: original, non-official SVG icon assets.
- `assets/themes`: theme tokens.

## ToS Safety Model

Allowed:

- `KeyPressAction`: one key plus optional modifiers, with a limited press duration.
- Windows button click, mobile button click, or accepted local voice phrase as the human intent source.

Not implemented by design:

- Macro sequences.
- Loops or timed automation.
- Game memory readers.
- DLL injection or hooks.
- Network interception.
- Game file modification.

If future action types are added, they should remain disabled until explicitly reviewed against this boundary.

## Data Model

SQLite creates these tables at startup:

- `Profiles`
- `Ships`
- `Panels`
- `Buttons`
- `VoiceCommands`
- `KeyBindings`
- `MarketRuns`
- `Settings`
- `ConnectedDevices`
- `CommandLog`

The MVP seeds:

- Active profile: `Global`
- Button: `Landing Gear`
- Default key: `N`
- Voice command: `bajar tren`

Data lives in `%AppData%\VerseDeck Companion\versedeck.db`.

## Mobile Panel

The desktop app starts an embedded local server on port `4785` by default. The app shows the LAN URL and QR code. The mobile page is a browser/PWA-style panel that connects back by WebSocket with a pairing PIN. Mobile browsers do not connect to or inspect the game.

Only loopback, private LAN, and link-local addresses are accepted.

## Voice

The MVP uses `System.Speech` with installed Windows recognizers. Commands are loaded from SQLite, matched by grammar, and ignored when the recognition confidence is below the configured threshold.
