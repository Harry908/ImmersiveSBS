using System.Drawing;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ImmersiveSBS.Browser;

namespace ImmersiveSBS.Tests;

/// <summary>The app's end of the browser extension, over a real WebSocket on 127.0.0.1.</summary>
public sealed class BrowserLinkTests : IDisposable
{
    private const string ExtensionOrigin = "chrome-extension://" + BrowserLink.ExtensionId;
    private readonly BrowserLink link = new(port: 0);
    private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

    public BrowserLinkTests()
    {
        Assert.True(link.Start());
    }

    private async Task<ClientWebSocket> ConnectAsync(string origin, string path = BrowserLink.Path)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", origin);
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{link.Port}{path}"), timeout.Token);
        return socket;
    }

    private Task SendAsync(ClientWebSocket socket, string json) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, timeout.Token);

    private async Task<JsonElement> ReceiveAsync(ClientWebSocket socket)
    {
        var buffer = new byte[4096];
        WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, timeout.Token);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone();
    }

    private async Task<BrowserConnection> WaitForAsync(Func<BrowserConnection, bool> ready)
    {
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (link.Connections.FirstOrDefault(ready) is { } c) return c;
            await Task.Delay(10);
        }
    }

    private const string Hello = """{"type":"hello","protocol":3,"version":"1.4.0","browser":"Microsoft Edge 155"}""";

    private const string Media = """
        {"type":"media","media":[{"id":"7:0:abc#1","tab":7,"frame":0,"kind":"video","playing":true,"audible":true,"area":230400,
          "videoWidth":1920,"videoHeight":1080,"delayable":true,"reason":null,"delay":0,"latency":null,"theater":true},
          {"id":"7:0:abc#2","tab":7,"frame":0,"kind":"audio","playing":false,"audible":true,"area":0,
          "videoWidth":0,"videoHeight":0,"delayable":false,"reason":"cross-origin","delay":0,"latency":12}]}
        """;

    [Fact]
    public async Task TheExtensionConnectsAndReportsItsMedia()
    {
        using ClientWebSocket socket = await ConnectAsync(ExtensionOrigin);
        await SendAsync(socket, Hello);
        await SendAsync(socket, Media);

        BrowserConnection c = await WaitForAsync(c => c.Media.Count == 2);
        Assert.Equal("Microsoft Edge 155", c.Browser);
        BrowserMedia m = c.Media[0];
        Assert.Equal("7:0:abc#1", m.Id);
        Assert.True(m.IsVideo && m.Playing && m.Audible && m.Delayable && m.Theater);
        Assert.Equal(230400, m.Area);
        Assert.Null(m.Latency);
        Assert.False(c.Media[1].Delayable || c.Media[1].Theater);
        Assert.Equal("cross-origin", c.Media[1].Reason);
    }

    /// <summary>
    /// The extension ships with the app: one still loaded from before speaks another protocol. It
    /// is told nothing and counts as no browser, and the app hears its version to say "reload it".
    /// </summary>
    [Fact]
    public async Task AnOldExtensionIsRefused()
    {
        (string Browser, string Version)? refused = null;
        link.Refused += (browser, version) => refused = (browser, version);
        using ClientWebSocket socket = await ConnectAsync(ExtensionOrigin);
        await SendAsync(socket, """{"type":"hello","protocol":2,"version":"1.2.4","browser":"Microsoft Edge 155"}""");
        await SendAsync(socket, Media);
        while (refused == null)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10);
        }
        Assert.Equal(("Microsoft Edge 155", "1.2.4"), refused);
        await Task.Delay(100);
        Assert.Empty(link.Connections);
    }

    /// <summary>
    /// The extension's theater needs to know whether the glasses show 3D, whether the user wants
    /// its button, and the 3D panel's values; a browser that connects is told at once, the rest
    /// only on a change. The panel's buttons come back as commands.
    /// </summary>
    [Fact]
    public async Task TheAppTellsItsStatusAndTakesTheTheatersCommands()
    {
        link.SetStatus(glasses: true, offer: true, settings: new { depth = 0.6, timing = "auto" });
        using ClientWebSocket socket = await ConnectAsync(ExtensionOrigin);
        await SendAsync(socket, Hello);
        JsonElement first = await ReceiveAsync(socket);
        Assert.Equal("status", first.GetProperty("type").GetString());
        Assert.True(first.GetProperty("glasses").GetBoolean() && first.GetProperty("offer").GetBoolean());
        Assert.Equal(0.6, first.GetProperty("settings").GetProperty("depth").GetDouble());

        link.SetStatus(glasses: true, offer: true, settings: new { depth = 0.6, timing = "auto" });
        link.SetStatus(glasses: false, offer: true, settings: new { depth = 0.6, timing = "auto" });
        JsonElement second = await ReceiveAsync(socket);
        Assert.False(second.GetProperty("glasses").GetBoolean());

        string? command = null;
        link.Command += (_, name) => command = name;
        await SendAsync(socket, """{"type":"command","name":"depthMore"}""");
        while (command == null)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10);
        }
        Assert.Equal("depthMore", command);

        // The tray's theater: every browser is asked, and opens it on the tab in front.
        link.RequestTheater(true);
        JsonElement open = await ReceiveAsync(socket);
        Assert.Equal("theater", open.GetProperty("type").GetString());
        Assert.True(open.GetProperty("open").GetBoolean());
    }

    [Fact]
    public async Task AWebPageOrAnotherExtensionIsRefused()
    {
        await Assert.ThrowsAnyAsync<WebSocketException>(() => ConnectAsync("https://example.com"));
        await Assert.ThrowsAnyAsync<WebSocketException>(() => ConnectAsync("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        await Assert.ThrowsAnyAsync<WebSocketException>(() => ConnectAsync(ExtensionOrigin, "/other"));
        Assert.Empty(link.Connections);
    }

    [Fact]
    public async Task ADelayNamesTheTheatersTabAndIsSentOnlyWhenItChanges()
    {
        // Only that tab plays late; music in another tab stays live. Moving to another tab at the
        // same delay is a change too; going live drops the tab.
        using ClientWebSocket socket = await ConnectAsync(ExtensionOrigin);
        await SendAsync(socket, Hello);
        BrowserConnection c = await WaitForAsync(c => c.Browser != "");

        link.SetDelay(c, 0.5204, tab: 12);
        link.SetDelay(c, 0.5204, tab: 12);
        link.SetDelay(c, 0.5204, tab: 14);
        link.SetDelay(c, 0, tab: 14);

        JsonElement first = await ReceiveAsync(socket);
        Assert.Equal("delay", first.GetProperty("type").GetString());
        Assert.Equal(12, first.GetProperty("tab").GetInt32());
        Assert.Equal(0.52, first.GetProperty("seconds").GetDouble());
        JsonElement second = await ReceiveAsync(socket);
        Assert.Equal(14, second.GetProperty("tab").GetInt32());
        JsonElement third = await ReceiveAsync(socket);
        Assert.Equal(0, third.GetProperty("seconds").GetDouble());
        Assert.Equal(JsonValueKind.Null, third.GetProperty("tab").ValueKind);
        Assert.Null(c.RequestedTab);
        Assert.Equal(0, c.RequestedDelay);
    }

    [Fact]
    public async Task ABrowserThatLeavesIsForgotten()
    {
        ClientWebSocket socket = await ConnectAsync(ExtensionOrigin);
        await SendAsync(socket, Hello);
        await WaitForAsync(c => c.Browser != "");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
        socket.Dispose();
        while (link.Connections.Count > 0)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// The theater opens: its picture, px ratio and whether its viewport is the screen; then its
    /// frames, state and boxes are kept for that video only, and closing it forgets them.
    /// </summary>
    [Fact]
    public async Task TheTheaterReportsItsPictureFramesStateAndBoxes()
    {
        using ClientWebSocket socket = await ConnectAsync(ExtensionOrigin);
        await SendAsync(socket, Hello);
        BrowserConnection c = await WaitForAsync(c => c.Browser != "");

        await SendAsync(socket, """
            {"type":"theater","id":"7:0:abc#1","open":true,"tab":7,"picture":{"x":0,"y":60,"width":1920,"height":960},"dpr":1,"onScreen":true,
             "view":{"full":true,"outerWidth":1920,"outerHeight":1080,"innerWidth":1920,"innerHeight":1080,"screenWidth":1920,"screenHeight":1080}}
            """);
        TheaterReport t = (await WaitForAsync(c => c.Theater != null)).Theater!;
        Assert.Equal("7:0:abc#1", t.MediaId);
        Assert.Equal(7, t.Tab);
        Assert.True(t.OnScreen);
        Assert.Equal("player full screen yes, window 1920×1080, viewport 1920×1080, screen 1920×1080 CSS px", t.View);
        Assert.Equal(new Rectangle(0, 60, 1920, 960), t.ToScreen(t.Picture!));
        Depth.PageVideo page = c.Followed!;
        Assert.Equal("7:0:abc#1", page.MediaId);

        double now = BrowserLink.ClockMs(System.Diagnostics.Stopwatch.GetTimestamp());
        await SendAsync(socket, $$"""{"type":"frame","id":"7:0:abc#1","presented":41,"media":12.5,"display":{{now + 16}},"width":1920,"height":1080}""");
        await SendAsync(socket, """{"type":"frame","id":"7:0:other#2","presented":99,"media":1,"display":1}""");
        await SendAsync(socket, """{"type":"state","id":"7:0:abc#1","paused":true,"seeking":false,"seeks":3,"rate":1,"clockError":0.4}""");
        await SendAsync(socket, """
            {"type":"overlays","id":"7:0:abc#1","boxes":[{"x":10,"y":980,"width":620,"height":40,"kind":"control"},
              {"x":100,"y":850,"width":400,"height":30,"kind":"caption"},{"x":"bad"},{"x":1,"y":1,"width":-5,"height":3}]}
            """);
        while (c.Overlays.Count == 0)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10);
        }
        Assert.Equal(41, page.Latest!.Value.Presented);
        Assert.Equal(12.5, page.Latest.Value.MediaTime);
        Assert.True(page.Paused);
        Assert.Equal(3, page.Seeks);
        Assert.Equal(0.4, page.ClockErrorMs);
        Assert.Equal(2, c.Overlays.Count);
        Assert.True(c.Overlays[1].IsCaption);

        // Moved (full screen now at another px ratio): the same theater, still followed.
        long since = t.Since;
        await SendAsync(socket, """
            {"type":"theater","id":"7:0:abc#1","open":true,"tab":7,"picture":{"x":0,"y":48,"width":1536,"height":768},"dpr":1.25,"onScreen":true}
            """);
        while (c.Theater!.Dpr != 1.25)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10);
        }
        Assert.Equal(since, c.Theater.Since);
        Assert.Same(page, c.Followed);
        Assert.Equal(new Rectangle(0, 60, 1920, 960), c.Theater.ToScreen(c.Theater.Picture!));

        await SendAsync(socket, """{"type":"theater","id":"7:0:abc#1","open":false}""");
        while (c.Theater != null)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10);
        }
        Assert.Null(c.Followed);
        Assert.Empty(c.Overlays);
    }

    [Fact]
    public async Task TheClockSyncIsWithinHalfTheRoundTrip()
    {
        // The page's clock runs 123 456.7 ms behind the app's; the page works out the offset from
        // the app's answers, NTP-style, with one-way delays of 0.1–5 ms either way.
        using ClientWebSocket socket = await ConnectAsync(ExtensionOrigin);
        await SendAsync(socket, Hello);
        await WaitForAsync(c => c.Browser != "");
        const double offset = 123456.7;
        double PageNow() => BrowserLink.ClockMs(System.Diagnostics.Stopwatch.GetTimestamp()) - offset;
        var random = new Random(3);
        for (int i = 0; i < 12; i++)
        {
            double t0 = PageNow();
            await Task.Delay(TimeSpan.FromMilliseconds(0.1 + random.NextDouble() * 5));
            await SendAsync(socket, $$"""{"type":"clock","key":"5:0:x","t0":{{t0}}}""");
            JsonElement answer = await ReceiveAsync(socket);
            await Task.Delay(TimeSpan.FromMilliseconds(0.1 + random.NextDouble() * 5));
            double t3 = PageNow();
            Assert.Equal("clock", answer.GetProperty("type").GetString());
            Assert.Equal("5:0:x", answer.GetProperty("key").GetString());
            Assert.Equal(t0, answer.GetProperty("t0").GetDouble(), 6);
            double t1 = answer.GetProperty("t1").GetDouble(), t2 = answer.GetProperty("t2").GetDouble();
            Assert.True(t2 >= t1);
            double theta = ((t1 - t0) + (t2 - t3)) / 2, delta = (t3 - t0) - (t2 - t1);
            Assert.InRange(theta - offset, -delta / 2 - 0.01, delta / 2 + 0.01);
        }
    }

    [Fact]
    public async Task GarbageIsIgnored()
    {
        using ClientWebSocket socket = await ConnectAsync(ExtensionOrigin);
        await SendAsync(socket, "not json");
        await SendAsync(socket, Hello);
        await SendAsync(socket, """{"type":"media","media":[{"id":"x"},{"nonsense":true},42]}""");
        await SendAsync(socket, """{"type":"theater","id":"nothing open","open":false}""");
        BrowserConnection c = await WaitForAsync(c => c.Browser != "");
        await Task.Delay(100);
        Assert.Empty(c.Media);
        Assert.Null(c.Theater);
    }

    public void Dispose()
    {
        link.Dispose();
        timeout.Dispose();
    }
}
