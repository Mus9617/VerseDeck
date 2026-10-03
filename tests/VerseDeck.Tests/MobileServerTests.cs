using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VerseDeck.Core.Models;
using VerseDeck.Data;
using VerseDeck.MobileServer;

namespace VerseDeck.Tests;

public sealed class MobileServerTests : IAsyncLifetime
{
    private const string Pin = "7391";
    private readonly TempDatabase _db = new();
    private readonly FakeInputSender _sender = new();
    private readonly HttpClient _http = new();
    private SqliteVerseDeckRepository _repository = null!;
    private MobilePanelServer _server = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _repository = await _db.CreateAsync();
        _server = new MobilePanelServer(_repository, _sender);
        _port = FreePort();
        await _server.StartAsync(_port, Pin);
        _http.BaseAddress = new Uri($"http://127.0.0.1:{_port}");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.StopAsync();
        await _db.DisposeAsync();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task PairAsync()
    {
        var response = await _http.PostAsJsonAsync("/api/pair", new { pin = Pin });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
    }

    private async Task<DeckButton> ButtonAsync(string name)
    {
        var profile = (await _repository.GetProfilesAsync()).First(p => p.IsActive);
        return (await _repository.GetButtonsAsync(profile.Id)).First(b => b.Name == name);
    }

    [Fact]
    public async Task Buttons_WithoutToken_Returns401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("/api/buttons")).StatusCode);
    }

    [Fact]
    public async Task Press_WithoutToken_Returns401_AndSendsNothing()
    {
        var button = await ButtonAsync("Lights");

        var response = await _http.PostAsJsonAsync("/api/press", new { type = "press", buttonId = button.Id, confirmed = false });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task Pair_WrongPin_Returns401()
    {
        var response = await _http.PostAsJsonAsync("/api/pair", new { pin = "0000" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Pair_SixthWrongAttempt_Returns429()
    {
        for (var i = 0; i < PairingGuard.MaxFailures; i++)
        {
            await _http.PostAsJsonAsync("/api/pair", new { pin = "0000" });
        }

        var response = await _http.PostAsJsonAsync("/api/pair", new { pin = Pin });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task Buttons_WithToken_ListsActiveProfileButtons()
    {
        await PairAsync();

        var buttons = await _http.GetFromJsonAsync<JsonElement>("/api/buttons");

        Assert.Equal(18, buttons.GetArrayLength());
    }

    [Fact]
    public async Task Press_WithToken_SendsExactlyOneKeyPress()
    {
        await PairAsync();
        var button = await ButtonAsync("Lights");

        var response = await _http.PostAsJsonAsync("/api/press", new { type = "press", buttonId = button.Id, confirmed = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("L", Assert.Single(_sender.Sent).Key);
    }

    [Fact]
    public async Task Press_ConfirmationRequiredButMissing_Returns400_AndSendsNothing()
    {
        await PairAsync();
        var button = await _repository.SaveButtonAsync((await ButtonAsync("Eject")) with { RequiresConfirmation = true });

        var response = await _http.PostAsJsonAsync("/api/press", new { type = "press", buttonId = button.Id, confirmed = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task Press_UnknownButton_Returns400()
    {
        await PairAsync();

        var response = await _http.PostAsJsonAsync("/api/press", new { type = "press", buttonId = 999999, confirmed = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RootPage_DoesNotContainPin()
    {
        var html = await _http.GetStringAsync("/");

        Assert.DoesNotContain(Pin, html);
        Assert.Contains("VERSEDECK", html);
    }

    [Fact]
    public async Task Start_OnBusyPort_Throws_AndIsNotRunning()
    {
        var second = new MobilePanelServer(_repository, _sender);

        await Assert.ThrowsAnyAsync<Exception>(() => second.StartAsync(_port, Pin));

        Assert.False(second.IsRunning);
    }

    private async Task<ClientWebSocket> ConnectSocketAsync()
    {
        await PairAsync();
        var socket = new ClientWebSocket();
        var token = _http.DefaultRequestHeaders.Authorization!.Parameter;
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws?token={token}"), CancellationToken.None);
        return socket;
    }

    [Fact]
    public async Task WebSocket_WithoutToken_IsRejected()
    {
        using var socket = new ClientWebSocket();

        await Assert.ThrowsAnyAsync<WebSocketException>(() => socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), CancellationToken.None));
    }

    [Fact]
    public async Task WebSocket_PressMessage_SendsExactlyOneKeyPress()
    {
        using var socket = await ConnectSocketAsync();
        var button = await ButtonAsync("Lights");

        var message = JsonSerializer.Serialize(new { type = "press", buttonId = button.Id, confirmed = false });
        await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[256];
        var reply = await socket.ReceiveAsync(buffer, CancellationToken.None);

        Assert.Contains("\"ok\":true", Encoding.UTF8.GetString(buffer, 0, reply.Count));
        Assert.Equal("L", Assert.Single(_sender.Sent).Key);
    }

    [Fact]
    public async Task Stop_WithOpenWebSocket_ReturnsPromptly()
    {
        using var socket = await ConnectSocketAsync();
        var watch = Stopwatch.StartNew();

        await _server.StopAsync();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"StopAsync took {watch.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    public async Task Request_WithForeignHostHeader_Returns403()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = "evil.example";

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Press_LegacyChordModifiers_Returns400_AndSendsNothing()
    {
        await PairAsync();
        var button = await ButtonAsync("Lights");
        await _db.ExecuteAsync($"UPDATE Buttons SET ActionModifiers='[\"A\",\"B\"]' WHERE Id={button.Id}");

        var response = await _http.PostAsJsonAsync("/api/press", new { type = "press", buttonId = button.Id, confirmed = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_sender.Sent);
    }
}
