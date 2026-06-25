using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using VerseDeck.Core.Models;

namespace VerseDeck.MobileServer;

public sealed class MobilePanelServer : IAsyncDisposable
{
    private readonly IVerseDeckRepository _repository;
    private readonly IInputSender _inputSender;
    private readonly ConcurrentDictionary<string, ConnectedDevice> _devices = new();
    private IHost? _host;

    public MobilePanelServer(IVerseDeckRepository repository, IInputSender inputSender)
    {
        _repository = repository;
        _inputSender = inputSender;
    }

    public bool IsRunning => _host is not null;
    public string LocalIpAddress => GetLocalIpAddress();
    public IReadOnlyCollection<ConnectedDevice> ConnectedDevices => _devices.Values.ToList();
    public event EventHandler<string>? Diagnostic;

    public async Task StartAsync(int port, string pairingPin, CancellationToken cancellationToken = default)
    {
        if (_host is not null)
        {
            return;
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.ListenAnyIP(port));
        var app = builder.Build();
        app.UseWebSockets();

        app.MapGet("/", () => Results.Content(MobileHtml(port, pairingPin), "text/html; charset=utf-8"));
        app.MapGet("/manifest.json", () => Results.Json(new
        {
            name = "VerseDeck Companion",
            short_name = "VerseDeck",
            start_url = "/",
            display = "standalone",
            background_color = "#061017",
            theme_color = "#29D9FF"
        }));

        app.MapGet("/api/buttons", async () =>
        {
            var profile = (await _repository.GetProfilesAsync()).FirstOrDefault(p => p.IsActive)
                ?? (await _repository.GetProfilesAsync()).First();
            var buttons = await _repository.GetButtonsAsync(profile.Id);
            return Results.Json(buttons.Select(b => new
            {
                b.Id,
                b.Name,
                b.Icon,
                b.Category,
                b.AccentColor,
                Key = b.Action.Modifiers.Count == 0 ? b.Action.Key : $"{string.Join("+", b.Action.Modifiers)}+{b.Action.Key}",
                b.RequiresConfirmation
            }));
        });

        app.MapPost("/api/press", async (MobileActionRequest request) =>
        {
            try
            {
                await PressButton(request.ButtonId, request.Confirmed, cancellationToken);
                return Results.Json(new { ok = true });
            }
            catch (Exception ex)
            {
                Diagnostic?.Invoke(this, $"Mobile HTTP press failed button={request.ButtonId}: {ex.Message}");
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest || !IsPrivateLan(context.Connection.RemoteIpAddress))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (context.Request.Query["pin"] != pairingPin)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var id = Guid.NewGuid().ToString("N");
            _devices[id] = new ConnectedDevice(id, "Mobile browser", context.Connection.RemoteIpAddress?.ToString() ?? "unknown", DateTimeOffset.Now);
            await ReceiveLoop(socket, cancellationToken);
            _devices.TryRemove(id, out _);
        });

        _host = app;
        await app.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_host is null)
        {
            return;
        }

        await _host.StopAsync(cancellationToken);
        _host.Dispose();
        _host = null;
        _devices.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private async Task ReceiveLoop(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var request = JsonSerializer.Deserialize<MobileActionRequest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (request?.Type == "press")
            {
                try
                {
                    await PressButton(request.ButtonId, request.Confirmed, cancellationToken);
                    await socket.SendAsync(Encoding.UTF8.GetBytes("""{"ok":true}"""), WebSocketMessageType.Text, true, cancellationToken);
                }
                catch (Exception ex)
                {
                    Diagnostic?.Invoke(this, $"Mobile WS press failed button={request.ButtonId}: {ex.Message}");
                    var errorJson = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
                    await socket.SendAsync(Encoding.UTF8.GetBytes(errorJson), WebSocketMessageType.Text, true, cancellationToken);
                }
            }
        }
    }

    private async Task PressButton(long buttonId, bool confirmed, CancellationToken cancellationToken)
    {
        var profile = (await _repository.GetProfilesAsync(cancellationToken)).First(p => p.IsActive);
        var button = (await _repository.GetButtonsAsync(profile.Id, cancellationToken)).First(b => b.Id == buttonId);
        if (button.RequiresConfirmation && !confirmed)
        {
            await _repository.AddCommandLogAsync("Mobile", button.Name, "Rejected: confirmation required", cancellationToken);
            return;
        }

        await _inputSender.SendAsync(button.Action, cancellationToken);
        await _repository.AddCommandLogAsync("Mobile", button.Name, $"Sent {button.Action.Key}", cancellationToken);
        Diagnostic?.Invoke(this, $"Mobile sent {button.Name} => {button.Action.Key}");
    }

    private static bool IsPrivateLan(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.MapToIPv4().GetAddressBytes();
        return bytes[0] == 10
            || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 192 && bytes[1] == 168
            || bytes[0] == 169 && bytes[1] == 254;
    }

    private static string GetLocalIpAddress()
    {
        var candidates = new List<(IPAddress Address, int Score)>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address.Address))
                {
                    continue;
                }

                if (!IsPrivateLan(address.Address))
                {
                    continue;
                }

                var alias = networkInterface.Name.ToLowerInvariant();
                var score = 0;
                if (address.Address.ToString().StartsWith("192.168.", StringComparison.Ordinal)) score += 40;
                if (address.Address.ToString().StartsWith("10.", StringComparison.Ordinal)) score += 30;
                if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Ethernet) score += 20;
                if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 20;
                if (alias.Contains("vpn") || alias.Contains("radmin") || alias.Contains("virtual")) score -= 100;
                candidates.Add((address.Address, score));
            }
        }

        return candidates.OrderByDescending(c => c.Score).FirstOrDefault().Address?.ToString() ?? "127.0.0.1";
    }

    private static string MobileHtml(int port, string pin) => $$"""
    <!doctype html>
    <html lang="es">
    <head>
      <meta charset="utf-8">
      <meta name="viewport" content="width=device-width, initial-scale=1">
      <link rel="manifest" href="/manifest.json">
      <title>VerseDeck Companion</title>
      <style>
        :root { color-scheme: dark; --accent:#38E8FF; --bg:#02040A; }
        * { box-sizing:border-box; } body { margin:0; min-height:100vh; font-family:Segoe UI,Arial,sans-serif; background:linear-gradient(135deg,#07121B,#02040A 52%,#100B07); color:#EAFBFF; }
        main { padding:16px; max-width:920px; margin:0 auto; }
        header { display:flex; justify-content:space-between; gap:12px; align-items:flex-start; margin-bottom:16px; padding-bottom:12px; border-bottom:1px solid rgba(56,232,255,.28); }
        h1 { font-size:24px; margin:0; letter-spacing:0; } .sub { color:#84AEB8; font-size:12px; margin-top:4px; }
        .status { color:var(--accent); font-size:12px; font-weight:700; border:1px solid rgba(56,232,255,.4); border-radius:5px; padding:8px 10px; background:rgba(5,16,21,.72); }
        .grid { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:10px; }
        button { min-height:112px; text-align:left; border:1px solid color-mix(in srgb,var(--accent),transparent 28%); border-radius:5px; color:#EAFBFF; background:linear-gradient(145deg,color-mix(in srgb,var(--accent),transparent 72%),rgba(3,13,18,.92)); box-shadow:0 0 20px color-mix(in srgb,var(--accent),transparent 86%), inset 0 0 18px rgba(255,255,255,.04); padding:12px; font-weight:700; }
        button:active { transform:translateY(1px); filter:brightness(1.2); }
        .cat { color:#84AEB8; font-size:10px; display:block; margin-bottom:18px; }
        .name { font-size:18px; display:block; line-height:1.08; }
        .key { display:block; margin-top:12px; color:var(--accent); font-size:12px; font-weight:700; }
        @media (min-width:760px) { .grid { grid-template-columns:repeat(4,minmax(0,1fr)); } }
      </style>
    </head>
    <body>
      <main>
        <header><div><h1>VERSEDECK</h1><div class="sub">Manual MFD link - LAN only</div></div><span class="status" id="status">Conectando</span></header>
        <section class="grid" id="buttons"></section>
      </main>
      <script>
        const status = document.getElementById('status');
        const buttons = document.getElementById('buttons');
        const ws = new WebSocket(`ws://${location.host}/ws?pin={{pin}}`);
        ws.onopen = () => status.textContent = 'LAN conectado';
        ws.onclose = () => status.textContent = 'Desconectado';
        ws.onerror = () => status.textContent = 'Error';
        ws.onmessage = event => {
          try {
            const data = JSON.parse(event.data);
            status.textContent = data.ok ? 'Comando enviado' : `Error: ${data.error || 'no enviado'}`;
          } catch { }
        };
        async function loadButtons() {
          const data = await (await fetch('/api/buttons')).json();
          buttons.innerHTML = '';
          for (const item of data) {
            const el = document.createElement('button');
            el.style.setProperty('--accent', item.accentColor);
            el.innerHTML = `<span class="cat">${item.category}</span><span class="name">${item.name}</span><span class="key">${item.key}${item.requiresConfirmation ? ' / CONFIRM' : ''}</span>`;
            el.onclick = async () => {
              if (item.requiresConfirmation && !confirm(`Ejecutar ${item.name}?`)) return;
              if (navigator.vibrate) navigator.vibrate(28);
              const payload = { type:'press', buttonId:item.id, confirmed:item.requiresConfirmation };
              try {
                if (ws.readyState === WebSocket.OPEN) {
                  ws.send(JSON.stringify(payload));
                  status.textContent = 'Enviando';
                  return;
                }
              } catch { }

              try {
                const response = await fetch('/api/press', {
                  method:'POST',
                  headers:{ 'Content-Type':'application/json' },
                  body: JSON.stringify(payload)
                });
                const data = await response.json();
                status.textContent = data.ok ? 'Comando enviado' : `Error: ${data.error || 'no enviado'}`;
              } catch (error) {
                status.textContent = `Error: ${error.message}`;
              }
            };
            buttons.appendChild(el);
          }
        }
        loadButtons();
      </script>
    </body>
    </html>
    """;

    private sealed record MobileActionRequest(string Type, long ButtonId, bool Confirmed);
}
