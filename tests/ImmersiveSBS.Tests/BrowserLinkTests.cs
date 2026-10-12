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
    public async Task ADelayNamesTheFollowedVideosTab()
    {
        // Only that tab plays late; music in another tab stays live. Moving to another tab at the
        // same delay is a change too; going live drops the tab.
        using ClientWebSocket socket = await ConnectAsync(ExtensionOrigin);
        await SendAsync(socket, Hello2);
        BrowserConnection c = await WaitForAsync(c => c.Browser != "");

        link.SetDelay(c, 0.5, tab: 12);
        link.SetDelay(c, 0.5, tab: 12);
        link.SetDelay(c, 0.5, tab: 14);
        link.SetDelay(c, 0, tab: 14);

        JsonElement first = await ReceiveAsync(socket);
        Assert.Equal(12, first.GetProperty("tab").GetInt32());
        Assert.Equal(0.5, first.GetProperty("seconds").GetDouble());
        JsonElement second = await ReceiveAsync(socket);
        Assert.Equal(14, second.GetProperty("tab").GetInt32());
        JsonElement third = await ReceiveAsync(socket);
        Assert.Equal(0, third.GetProperty("seconds").GetDouble());
        Assert.False(third.TryGetProperty("tab", out _));
        Assert.Null(c.RequestedTab);
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

    private const string Hello2 = """{"type":"hello","protocol":2,"version":"1.1.0","browser":"Google Chrome 140"}""";

    [Fact]
    public async Task AFollowedVideoReportsItsFramesStateAndOverlays()
    {
        using ClientWebSocket socket = await ConnectAsync(ExtensionOrigin);
        await SendAsync(socket, Hello2);
        BrowserConnection c = await WaitForAsync(c => c.Protocol == 2);

        Depth.PageVideo? page = link.Follow(c, "7:0:abc#1");
        Assert.NotNull(page);
        JsonElement follow = await ReceiveAsync(socket);
        Assert.Equal("follow", follow.GetProperty("type").GetString());
        Assert.Equal("7:0:abc#1", follow.GetProperty("id").GetString());
        Assert.Same(page, link.Follow(c, "7:0:abc#1"));   // unchanged: nothing sent again

        double now = BrowserLink.ClockMs(System.Diagnostics.Stopwatch.GetTimestamp());
        await SendAsync(socket, $$"""{"type":"frame","id":"7:0:abc#1","presented":41,"media":12.5,"display":{{now + 16}},"width":1920,"height":1080}""");
        await SendAsync(socket, """{"type":"frame","id":"7:0:other#2","presented":99,"media":1,"display":1}""");
        await SendAsync(socket, """{"type":"state","id":"7:0:abc#1","paused":true,"seeking":false,"seeks":3,"rate":1,"clockError":0.4}""");
        await SendAsync(socket, """
            {"type":"overlays","id":"7:0:abc#1","boxes":[{"x":10,"y":300,"width":620,"height":40,"kind":"control"},
              {"x":100,"y":250,"width":400,"height":30,"kind":"caption"},{"x":"bad"},{"x":1,"y":1,"width":-5,"height":3}]}
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
        Assert.Equal(new CssRect(10, 300, 620, 40), c.Overlays[0].Box);

        // Following none: nothing is kept.
        Assert.Null(link.Follow(c, null));
        Assert.Empty(c.Overlays);
    }

    [Fact]
    public async Task AProtocolOneBrowserIsNeverAskedToFollow()
    {
        using ClientWebSocket socket = await ConnectAsync(ExtensionOrigin);
        await SendAsync(socket, Hello);
        BrowserConnection c = await WaitForAsync(c => c.Browser != "");
        Assert.Null(link.Follow(c, "1:0:a#1"));
    }

    [Fact]
    public async Task TheClockSyncIsWithinHalfTheRoundTrip()
    {
        // The page's clock runs 123 456.7 ms behind the app's; the page works out the offset from
        // the app's answers, NTP-style, with one-way delays of 0.1–5 ms either way.
        using ClientWebSocket socket = await ConnectAsync(ExtensionOrigin);
        await SendAsync(socket, Hello2);
        await WaitForAsync(c => c.Protocol == 2);
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
