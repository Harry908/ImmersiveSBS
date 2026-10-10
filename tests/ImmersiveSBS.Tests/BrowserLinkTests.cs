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

    private const string Hello = """{"type":"hello","protocol":1,"version":"1.0.0","browser":"Microsoft Edge 131"}""";

    [Fact]
    public async Task TheExtensionConnectsAndReportsItsMedia()
    {
        using ClientWebSocket socket = await ConnectAsync(ExtensionOrigin);
        await SendAsync(socket, Hello);
        await SendAsync(socket, """
            {"type":"media","media":[{"id":"7:0:abc#1","tab":7,"frame":0,"kind":"video","playing":true,"audible":true,"seeks":2,
              "rect":{"x":10,"y":20,"width":640,"height":360},"viewport":{"width":1280,"height":720},"dpr":1.5,"visible":true,
              "fullscreen":false,"videoWidth":1920,"videoHeight":1080,"delayable":true,"reason":null,"delay":0,"latency":null}]}
            """);

        BrowserConnection c = await WaitForAsync(c => c.Media.Count == 1);
        Assert.Equal("Microsoft Edge 131", c.Browser);
        BrowserMedia m = c.Media[0];
        Assert.Equal("7:0:abc#1", m.Id);
        Assert.True(m.IsVideo && m.Playing && m.Audible && m.Delayable);
        Assert.Equal(new CssRect(10, 20, 640, 360), m.Rect);
        Assert.Equal(1.5, m.Dpr);
        Assert.Equal(2, m.Seeks);
        Assert.Null(m.Latency);
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
    public async Task ADelayIsSentOnlyWhenItChanges()
    {
        using ClientWebSocket socket = await ConnectAsync(ExtensionOrigin);
        await SendAsync(socket, Hello);
        BrowserConnection c = await WaitForAsync(c => c.Browser != "");

        link.SetDelay(c, 1.0204);
        link.SetDelay(c, 1.0204);
        link.SetDelay(c, 0);

        JsonElement first = await ReceiveAsync(socket);
        Assert.Equal("delay", first.GetProperty("type").GetString());
        Assert.Equal(1.02, first.GetProperty("seconds").GetDouble());
        JsonElement second = await ReceiveAsync(socket);
        Assert.Equal(0, second.GetProperty("seconds").GetDouble());
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

    [Fact]
    public async Task GarbageIsIgnored()
    {
        using ClientWebSocket socket = await ConnectAsync(ExtensionOrigin);
        await SendAsync(socket, "not json");
        await SendAsync(socket, """{"type":"media","media":[{"id":"x"},{"nonsense":true},42]}""");
        await SendAsync(socket, Hello);
        BrowserConnection c = await WaitForAsync(c => c.Browser != "");
        Assert.Empty(c.Media);
    }

    public void Dispose()
    {
        link.Dispose();
        timeout.Dispose();
    }
}

/// <summary>Placing a page's video on the screen and matching it with the found video.</summary>
public class BrowserMediaTests
{
    private static BrowserMedia Video(CssRect? rect, double dpr = 1.25, CssSize? viewport = null, bool visible = true, string kind = "video", string id = "1:0:a#1") =>
        new(id, 1, 0, kind, true, true, 0, rect, viewport ?? new CssSize(1280, 800), dpr, visible, false, 1920, 1080, true, null, 0, null);

    [Fact]
    public void APageFillingTheWindowIsPlacedByItsCssPxTimesTheScale()
    {
        // 1280×800 CSS px at 1.25 = 1600×1000 px: the whole client area (full screen).
        var client = new Rectangle(100, 50, 1600, 1000);
        Rectangle? screen = Video(new CssRect(100, 80, 640, 360)).ScreenRect(client);
        Assert.Equal(new Rectangle(225, 150, 800, 450), screen);
    }

    [Fact]
    public void ThePageSitsUnderTheToolbarAtTheBottomOfTheWindow()
    {
        // 100 px of tab strip and toolbar above the page.
        var client = new Rectangle(100, 50, 1600, 1100);
        Rectangle? screen = Video(new CssRect(0, 0, 1280, 720)).ScreenRect(client);
        Assert.Equal(new Rectangle(100, 150, 1600, 900), screen);
    }

    [Fact]
    public void APictureScrolledPartlyOutIsCutToThePage()
    {
        var client = new Rectangle(0, 0, 1600, 1100);
        Rectangle? screen = Video(new CssRect(0, -100, 640, 360)).ScreenRect(client);
        Assert.Equal(new Rectangle(0, 100, 800, 325), screen);
    }

    [Fact]
    public void APageBiggerThanTheWindowIsAnotherWindows()
    {
        Assert.Null(Video(new CssRect(0, 0, 640, 360)).ScreenRect(new Rectangle(0, 0, 1200, 700)));
        Assert.Null(Video(null).ScreenRect(new Rectangle(0, 0, 1600, 1000)));
    }

    [Fact]
    public void TheBestCoveringVisibleVideoIsMatched()
    {
        var client = new Rectangle(0, 0, 1600, 1000);
        var found = new Rectangle(120, 100, 805, 450);
        (string, BrowserMedia)[] media =
        [
            ("small", Video(new CssRect(900, 500, 160, 90), id: "small")),
            ("hidden", Video(new CssRect(100, 80, 640, 360), visible: false, id: "hidden")),
            ("audio", Video(new CssRect(100, 80, 640, 360), kind: "audio", id: "audio")),
            ("main", Video(new CssRect(100, 80, 640, 360), id: "main")),
        ];
        var match = BrowserMedia.Match(media, client, found);
        Assert.NotNull(match);
        Assert.Equal("main", match.Value.Owner);
        Assert.Equal(new Rectangle(125, 100, 800, 450), match.Value.Screen);

        // Nothing there: no match.
        Assert.Null(BrowserMedia.Match(media, client, new Rectangle(1200, 50, 300, 200)));
    }

    [Fact]
    public void OverlapIsIntersectionOverUnion()
    {
        Assert.Equal(1, BrowserMedia.Overlap(new Rectangle(0, 0, 10, 10), new Rectangle(0, 0, 10, 10)));
        Assert.Equal(0, BrowserMedia.Overlap(new Rectangle(0, 0, 10, 10), new Rectangle(20, 0, 10, 10)));
        Assert.Equal(50.0 / 150, BrowserMedia.Overlap(new Rectangle(0, 0, 10, 10), new Rectangle(5, 0, 10, 10)), 6);
    }
}
