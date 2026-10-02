namespace VerseDeck.MobileServer;

internal static class MobilePage
{
    public const string Html = """
    <!doctype html>
    <html lang="es">
    <head>
      <meta charset="utf-8">
      <meta name="viewport" content="width=device-width, initial-scale=1">
      <link rel="manifest" href="/manifest.json">
      <title>VerseDeck Companion</title>
      <style>
        :root { color-scheme: dark; --bg:#0E1317; --surface:#161D23; --line:#2C3944; --text:#E6EDF2; --muted:#8A9BA8; --accent:#5FB8C9; --danger:#E0564F; }
        * { box-sizing:border-box; }
        body { margin:0; min-height:100vh; font-family:Segoe UI,Arial,sans-serif; background:var(--bg); color:var(--text); }
        main { padding:16px; max-width:920px; margin:0 auto; }
        header { display:flex; justify-content:space-between; gap:12px; align-items:center; margin-bottom:16px; padding-bottom:12px; border-bottom:1px solid var(--line); }
        h1 { font-size:22px; margin:0; letter-spacing:.04em; }
        .sub { color:var(--muted); font-size:12px; margin-top:4px; }
        .status { color:var(--muted); font-size:12px; font-weight:600; border:1px solid var(--line); border-radius:4px; padding:8px 10px; background:var(--surface); }
        .grid { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:8px; }
        .grid button { min-height:104px; text-align:left; border:1px solid var(--line); border-left:3px solid var(--tile,var(--accent)); border-radius:4px; color:var(--text); background:var(--surface); padding:12px; font:inherit; font-weight:600; }
        .grid button:active { background:var(--line); }
        .cat { color:var(--muted); font-size:10px; text-transform:uppercase; letter-spacing:.08em; display:block; margin-bottom:16px; }
        .name { font-size:18px; display:block; line-height:1.1; }
        .key { display:inline-block; margin-top:12px; color:var(--text); font-family:Consolas,monospace; font-size:12px; border:1px solid var(--line); border-radius:3px; padding:2px 6px; }
        form { max-width:320px; margin:48px auto; display:grid; gap:12px; }
        form label { color:var(--muted); font-size:12px; text-transform:uppercase; letter-spacing:.08em; }
        form input { font:inherit; font-size:28px; letter-spacing:.3em; text-align:center; padding:12px; color:var(--text); background:var(--surface); border:1px solid var(--line); border-radius:4px; }
        form button { font:inherit; font-weight:600; padding:12px; color:var(--bg); background:var(--accent); border:0; border-radius:4px; }
        .error { color:var(--danger); font-size:13px; min-height:18px; }
        [hidden] { display:none !important; }
        @media (min-width:760px) { .grid { grid-template-columns:repeat(4,minmax(0,1fr)); } }
      </style>
    </head>
    <body>
      <main>
        <header><div><h1>VERSEDECK</h1><div class="sub">Panel manual - solo red local</div></div><span class="status" id="status">Sin emparejar</span></header>
        <form id="pair" hidden>
          <label for="pin">PIN de emparejamiento</label>
          <input id="pin" inputmode="numeric" autocomplete="off" maxlength="8">
          <button type="submit">Emparejar</button>
          <div class="error" id="pairError"></div>
        </form>
        <section class="grid" id="buttons"></section>
      </main>
      <script>
        const status = document.getElementById('status');
        const buttons = document.getElementById('buttons');
        const pairForm = document.getElementById('pair');
        const pairError = document.getElementById('pairError');
        const tokenKey = 'versedeck-token';
        let token = sessionStorage.getItem(tokenKey);
        let ws = null;

        function showPairing(message) {
          sessionStorage.removeItem(tokenKey);
          token = null;
          if (ws) { ws.close(); ws = null; }
          buttons.replaceChildren();
          pairError.textContent = message || '';
          pairForm.hidden = false;
          status.textContent = 'Sin emparejar';
        }

        pairForm.addEventListener('submit', async event => {
          event.preventDefault();
          pairError.textContent = '';
          try {
            const response = await fetch('/api/pair', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ pin: document.getElementById('pin').value.trim() })
            });
            if (response.status === 429) { pairError.textContent = 'Demasiados intentos. Espera un minuto.'; return; }
            if (!response.ok) { pairError.textContent = 'PIN incorrecto.'; return; }
            token = (await response.json()).token;
            sessionStorage.setItem(tokenKey, token);
            pairForm.hidden = true;
            start();
          } catch (error) {
            pairError.textContent = `Error: ${error.message}`;
          }
        });

        function report(data) {
          status.textContent = data.ok ? 'Comando enviado' : `Error: ${data.error || 'no enviado'}`;
        }

        function connect() {
          ws = new WebSocket(`ws://${location.host}/ws?token=${encodeURIComponent(token)}`);
          ws.onopen = () => status.textContent = 'LAN conectado';
          ws.onclose = () => { if (token) status.textContent = 'Desconectado'; };
          ws.onerror = () => status.textContent = 'Error de conexion';
          ws.onmessage = event => { try { report(JSON.parse(event.data)); } catch { } };
        }

        function span(className, text) {
          const el = document.createElement('span');
          el.className = className;
          el.textContent = text;
          return el;
        }

        async function press(item) {
          if (item.requiresConfirmation && !confirm(`Ejecutar ${item.name}?`)) return;
          if (navigator.vibrate) navigator.vibrate(28);
          const payload = { type: 'press', buttonId: item.id, confirmed: item.requiresConfirmation };
          if (ws && ws.readyState === WebSocket.OPEN) {
            ws.send(JSON.stringify(payload));
            status.textContent = 'Enviando';
            return;
          }

          try {
            const response = await fetch('/api/press', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${token}` },
              body: JSON.stringify(payload)
            });
            if (response.status === 401) { showPairing('La sesion ha caducado.'); return; }
            report(await response.json());
          } catch (error) {
            status.textContent = `Error: ${error.message}`;
          }
        }

        async function loadButtons() {
          const response = await fetch('/api/buttons', { headers: { 'Authorization': `Bearer ${token}` } });
          if (response.status === 401) { showPairing('La sesion ha caducado.'); return false; }
          const data = await response.json();
          buttons.replaceChildren();
          for (const item of data) {
            const el = document.createElement('button');
            if (/^#[0-9a-f]{6}$/i.test(item.accentColor)) el.style.setProperty('--tile', item.accentColor);
            el.append(span('cat', item.category), span('name', item.name), span('key', item.key + (item.requiresConfirmation ? ' / CONFIRMAR' : '')));
            el.onclick = () => press(item);
            buttons.appendChild(el);
          }
          return true;
        }

        async function start() {
          if (await loadButtons()) connect();
        }

        if (token) { start(); } else { showPairing(''); }
      </script>
    </body>
    </html>
    """;
}
