# MVP Checklist

- App Windows opens with a futuristic dashboard.
- Profile name, ship name, and role can be edited.
- `Landing Gear` button is created and persisted in SQLite.
- The button key and modifiers are configurable.
- Pressing the Windows button sends exactly one key press action through `SendInput`.
- A local mobile server can be started from the dashboard.
- The dashboard shows the local URL, PIN, and QR code.
- The mobile page loads buttons from the desktop app and sends button presses through WebSocket.
- Voice command `bajar tren` can be saved and enabled.
- Recognized voice commands send the same safe button action.
- Command activity is logged locally.
- Inno Setup script exists for a basic installer.
