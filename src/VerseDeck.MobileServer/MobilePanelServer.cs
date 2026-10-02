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
using Microsoft.Extensions.Logging;
using VerseDeck.Core.Models;

namespace VerseDeck.MobileServer;

public sealed class MobilePanelServer : IAsyncDisposable
{
    private readonly IVerseDeckRepository _repository;
    private readonly IInputSender _inputSender;
    private readonly ConcurrentDictionary<string, ConnectedDevice> _devices = new();
    private WebApplication? _host;

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

        var guard = new PairingGuard(pairingPin, () => DateTimeOffset.Now);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.ListenAnyIP(port));
        var app = builder.Build();
        app.UseWebSockets();

        // Every route is LAN-only; everything except the page and pairing also needs a paired token.
        app.Use(async (context, next) =>
        {
            // A page in the PC's browser could reach this server through a rebound DNS name; real clients use the IP.
            if (!IsPrivateLan(context.Connection.RemoteIpAddress) || !IsAddressOrLocalhost(context.Request.Host.Host))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var path = context.Request.Path;
            var needsToken = path.StartsWithSegments("/ws")
                || path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/pair");
            if (needsToken && !guard.IsValidToken(TokenOf(context.Request)))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        });

        app.MapGet("/", () => Results.Content(MobilePage.Html, "text/html; charset=utf-8"));
        app.MapGet("/manifest.json", () => Results.Json(new
        {
            name = "VerseDeck Companion",
            short_name = "VerseDeck",
            start_url = "/",
            display = "standalone",
            background_color = "#0E1317",
            theme_color = "#5FB8C9"
        }));

        app.MapPost("/api/pair", (PairRequest request, HttpContext context) =>
        {
            var remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var result = guard.TryPair(remote, request.Pin);
            if (result.Status != PairStatus.Ok)
            {
                Diagnostic?.Invoke(this, $"Mobile pairing rejected from {remote}: {result.Status}");
            }

            return result.Status switch
            {
                PairStatus.Ok => Results.Json(new { token = result.Token }),
                PairStatus.LockedOut => Results.StatusCode(StatusCodes.Status429TooManyRequests),
                _ => Results.StatusCode(StatusCodes.Status401Unauthorized)
            };
        });

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
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var id = Guid.NewGuid().ToString("N");
            _devices[id] = new ConnectedDevice(id, "Mobile browser", context.Connection.RemoteIpAddress?.ToString() ?? "unknown", DateTimeOffset.Now);
            // Stopping the server must end open sockets at once, not after the host's shutdown timeout.
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, app.Lifetime.ApplicationStopping);
            try
            {
                await ReceiveLoop(socket, stopping.Token);
            }
            catch (WebSocketException)
            {
                // The phone dropped off the network without closing the socket.
            }
            catch (OperationCanceledException)
            {
                // The server is stopping.
            }
            finally
            {
                _devices.TryRemove(id, out _);
            }
        });

        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        _host = app;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_host is null)
        {
            return;
        }

        await _host.StopAsync(cancellationToken);
        await _host.DisposeAsync();
        _host = null;
        _devices.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private static string? TokenOf(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header["Bearer ".Length..].Trim();
        }

        return request.Query["token"];
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

            MobileActionRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<MobileActionRequest>(buffer.AsSpan(0, result.Count), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException)
            {
                continue;
            }

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
        var button = (await _repository.GetButtonsAsync(profile.Id, cancellationToken)).FirstOrDefault(b => b.Id == buttonId)
            ?? throw new InvalidOperationException("Modulo no encontrado");
        if (button.RequiresConfirmation && !confirmed)
        {
            await _repository.AddCommandLogAsync("Mobile", button.Name, "Rejected: confirmation required", cancellationToken);
            throw new InvalidOperationException("Confirmacion requerida");
        }

        button.Action.Validate();
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

        if (address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv4MappedToIPv6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
        }

        var bytes = address.MapToIPv4().GetAddressBytes();
        return bytes[0] == 10
            || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 192 && bytes[1] == 168
            || bytes[0] == 169 && bytes[1] == 254;
    }

    private static bool IsAddressOrLocalhost(string host)
    {
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host.Trim('[', ']'), out _);
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

    private sealed record PairRequest(string? Pin);

    private sealed record MobileActionRequest(string Type, long ButtonId, bool Confirmed);
}
