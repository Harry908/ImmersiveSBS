using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImmersiveSBS.Depth;

namespace ImmersiveSBS.Browser;

/// <summary>A box the theater's player draws over its picture (a control or a caption), viewport CSS px.</summary>
/// <param name="Kind">"caption", "control", "panel" (the 3D settings panel) or "other".</param>
public sealed record PageBox(CssRect Box, string Kind)
{
    public bool IsCaption => Kind == "caption";
    public bool IsPanel => Kind == "panel";
}

/// <summary>
/// One browser (one profile) with the ImmersiveSBS link extension, connected. Its media list is
/// replaced whole on each report; reading it is safe from any thread.
/// </summary>
public sealed class BrowserConnection
{
    private readonly WebSocket socket;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private volatile IReadOnlyList<BrowserMedia> media = [];
    private volatile PageVideo? followed;
    private volatile IReadOnlyList<PageBox> overlays = [];
    private double requestedDelay;

    internal BrowserConnection(int number, WebSocket socket)
    {
        Number = number;
        this.socket = socket;
    }

    public int Number { get; }

    /// <summary>"Microsoft Edge 131", from the extension's hello; empty until then.</summary>
    public string Browser { get; internal set; } = "";

    public string ExtensionVersion { get; internal set; } = "";

    /// <summary>The extension's protocol, from its hello; the connection counts only with <see cref="BrowserLink.Protocol"/>.</summary>
    public int Protocol { get; internal set; }

    /// <summary>Its hello named the app's protocol: it is in <see cref="BrowserLink.Connections"/>.</summary>
    public bool Accepted { get; internal set; }

    /// <summary>The theater's video, with what its page reports about it as it comes; null while no theater is open.</summary>
    public PageVideo? Followed => followed;

    /// <summary>The open theater, as its page last reported it; null when none.</summary>
    public TheaterReport? Theater
    {
        get => theater;
        internal set => theater = value;
    }

    private volatile TheaterReport? theater;

    /// <summary>The boxes over the theater's picture, as last reported.</summary>
    public IReadOnlyList<PageBox> Overlays
    {
        get => overlays;
        internal set => overlays = value;
    }

    /// <summary>Follows <paramref name="mediaId"/> (null: none); false when it already was.</summary>
    internal bool SetFollowed(string? mediaId)
    {
        if (followed?.MediaId == mediaId) return false;
        followed = mediaId == null ? null : new PageVideo(mediaId);
        overlays = [];
        return true;
    }

    public IReadOnlyList<BrowserMedia> Media
    {
        get => media;
        internal set => media = value;
    }

    /// <summary>The delay last asked of this browser, seconds.</summary>
    public double RequestedDelay => Volatile.Read(ref requestedDelay);

    /// <summary>The tab whose sound is delayed, or null for every tab.</summary>
    public int? RequestedTab { get; private set; }

    internal bool SetRequestedDelay(double seconds, int? tab)
    {
        bool tabChanged = RequestedTab != tab;
        RequestedTab = tab;
        return (Interlocked.Exchange(ref requestedDelay, seconds) != seconds) | tabChanged;
    }

    internal Task SendAsync(object message, CancellationToken cancel) =>
        SendBytesAsync(JsonSerializer.SerializeToUtf8Bytes(message), cancel);

    internal Task SendTextAsync(string json, CancellationToken cancel) =>
        SendBytesAsync(Encoding.UTF8.GetBytes(json), cancel);

    private async Task SendBytesAsync(byte[] bytes, CancellationToken cancel)
    {
        await sendGate.WaitAsync(cancel);
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancel);
        }
        finally
        {
            sendGate.Release();
        }
    }

    public override string ToString() => string.IsNullOrEmpty(Browser) ? $"browser {Number}" : Browser;
}

/// <summary>
/// The app's end of the browser extension (extension/): a WebSocket server on 127.0.0.1 that
/// accepts connections only from the extension's own origin (a fixed ID, from the key in its
/// manifest; a web page cannot claim another origin). The extension reports the pages' video and
/// audio elements and plays their sound late inside the page when asked, which keeps the sound at
/// full quality on its usual output: no capture, no turning down, no clock to follow. It is the
/// only way the app delays sound: without it there is no buffer.
/// </summary>
public sealed class BrowserLink : IDisposable
{
    public const int DefaultPort = 47391;
    public const string Path = "/immersivesbs";

    /// <summary>The extension's ID, from the public key in extension/manifest.json.</summary>
    public const string ExtensionId = "dopaoaefmhhmpbdgpnkgpkjhbndebmnl";

    /// <summary>
    /// The one protocol this app speaks: the extension ships in its folder (extension/), so any
    /// other is an old copy still loaded in the browser, refused with a message to reload it.
    /// </summary>
    public const int Protocol = 3;

    private const int MaxConnections = 8;
    private const int MaxMessageBytes = 1 << 20;
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);

    private readonly TcpListener listener;
    private readonly HashSet<string> origins;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly List<BrowserConnection> connections = [];
    private IReadOnlyList<BrowserConnection> snapshot = [];
    private int nextNumber = 1;
    private Timer? ping;

    /// <param name="port">0 picks a free port (tests).</param>
    /// <param name="origins">Origins allowed to connect; the extension's by default.</param>
    public BrowserLink(int port = DefaultPort, IEnumerable<string>? origins = null)
    {
        listener = new TcpListener(IPAddress.Loopback, port);
        this.origins = new HashSet<string>(origins ?? [$"chrome-extension://{ExtensionId}"], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The port listened on, once started.</summary>
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>The connected browsers; a snapshot, any thread.</summary>
    public IReadOnlyList<BrowserConnection> Connections => Volatile.Read(ref snapshot);

    /// <summary>A browser connected or left, or reported new media or its theater; raised on a pool thread.</summary>
    public event Action? Changed;

    /// <summary>
    /// A browser's extension speaks another protocol (an old copy loaded in the browser): its
    /// name and the extension's version; raised on a pool thread. It is not in <see cref="Connections"/>.
    /// </summary>
    public event Action<string, string>? Refused;

    /// <summary>Starts listening; false (and logged) when the port is taken, e.g. by another instance.</summary>
    public bool Start()
    {
        try
        {
            listener.Start();
        }
        catch (SocketException e)
        {
            Log.Write($"Browser: cannot listen for the extension on port {((IPEndPoint)listener.LocalEndpoint).Port}: {e.Message}");
            return false;
        }
        _ = AcceptLoopAsync();
        // Keeps the extension's service worker awake (it is stopped after 30 s without traffic).
        ping = new Timer(_ => Broadcast(new { type = "ping" }), null, PingInterval, PingInterval);
        Log.Write($"Browser: listening for the extension on 127.0.0.1:{Port}.");
        return true;
    }

    /// <summary>
    /// Asks <paramref name="connection"/> to play the sound of tab <paramref name="tab"/>'s pages
    /// <paramref name="seconds"/> late (0: live), and every other tab's live; sent only when it
    /// changes. Any thread.
    /// </summary>
    public void SetDelay(BrowserConnection connection, double seconds, int tab)
    {
        seconds = Math.Round(Math.Max(0, seconds), 3);
        int? delayed = seconds > 0 ? tab : null;
        if (!connection.SetRequestedDelay(seconds, delayed)) return;
        Send(connection, new { type = "delay", seconds, tab = delayed });
    }

    /// <summary>
    /// Opens the theater on the largest playing video of the tab in front of each browser, or
    /// closes it (the tray, the shortcut). Without a click in the page, the player fills the
    /// browser window and the window goes full screen.
    /// </summary>
    public void RequestTheater(bool open) => Broadcast(new { type = "theater", open });

    private string status = "";

    /// <summary>
    /// What every browser is told about the app: whether the glasses show 3D,
    /// whether to offer the theater button, and the 3D settings its panel shows. Sent when it
    /// changes and to each browser that connects. Any thread.
    /// </summary>
    public void SetStatus(bool glasses, bool offer, object? settings)
    {
        string text = JsonSerializer.Serialize(new { type = "status", glasses, offer, settings });
        if (Interlocked.Exchange(ref status, text) == text) return;
        foreach (BrowserConnection c in Connections) SendText(c, text);
    }

    /// <summary>A button of the theater's 3D panel was pressed ("depthMore", "modelNext"…); raised on a pool thread.</summary>
    public event Action<BrowserConnection, string>? Command;


    /// <summary>This process's clock in ms, as the extension's clock sync and frame times use it.</summary>
    public static double ClockMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private void SendText(BrowserConnection connection, string json) =>
        _ = connection.SendTextAsync(json, stop.Token).ContinueWith(
            t => Log.Write($"Browser: sending to {connection} failed: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);

    private void Send(BrowserConnection connection, object message) =>
        _ = connection.SendAsync(message, stop.Token).ContinueWith(
            t => Log.Write($"Browser: sending to {connection} failed: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);

    private void Broadcast(object message)
    {
        foreach (BrowserConnection c in Connections) Send(c, message);
    }

    private async Task AcceptLoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException e)
            {
                Log.Write("Browser: accepting a connection failed: " + e.Message);
                continue;
            }
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            WebSocket? socket = null;
            BrowserConnection? connection = null;
            try
            {
                using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                {
                    handshake.CancelAfter(TimeSpan.FromSeconds(5));
                    if (!await HandshakeAsync(stream, handshake.Token)) return;
                }
                socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
                {
                    IsServer = true,
                    KeepAliveInterval = TimeSpan.FromSeconds(15),
                });
                lock (gate)
                {
                    if (connections.Count >= MaxConnections) return;
                    connection = new BrowserConnection(nextNumber++, socket);
                    connections.Add(connection);
                }
                await ReceiveLoopAsync(socket, connection);
            }
            catch (Exception e) when (e is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The browser closed, or the app is stopping.
            }
            finally
            {
                if (connection != null)
                {
                    lock (gate)
                    {
                        connections.Remove(connection);
                        Volatile.Write(ref snapshot, connections.Where(c => c.Accepted).ToArray());
                    }
                    Log.Write($"Browser: {connection} disconnected.");
                    Changed?.Invoke();
                }
                socket?.Dispose();
            }
        }
    }

    /// <summary>
    /// Reads the HTTP upgrade request and answers it: 101 for the extension's origin on our path,
    /// 403 for anything else.
    /// </summary>
    private async Task<bool> HandshakeAsync(NetworkStream stream, CancellationToken cancel)
    {
        var buffer = new byte[8192];
        int length = 0;
        while (true)
        {
            if (length == buffer.Length) return false;
            int n = await stream.ReadAsync(buffer.AsMemory(length), cancel);
            if (n == 0) return false;
            length += n;
            if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0) break;
        }

        string[] lines = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n");
        string[] request = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        string? origin = headers.GetValueOrDefault("Origin");
        bool valid = request.Length >= 3 && request[0] == "GET" && request[1] == Path
                     && headers.GetValueOrDefault("Upgrade")?.Contains("websocket", StringComparison.OrdinalIgnoreCase) == true
                     && headers.GetValueOrDefault("Sec-WebSocket-Version") == "13"
                     && headers.TryGetValue("Sec-WebSocket-Key", out string? key) && key.Length > 0
                     && origin != null && origins.Contains(origin);
        if (!valid)
        {
            Log.Write($"Browser: refused a connection from {origin ?? "no origin"} ({lines[0]}).");
            await stream.WriteAsync("HTTP/1.1 403 Forbidden\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), cancel);
            return false;
        }

        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(headers["Sec-WebSocket-Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n"), cancel);
        return true;
    }

    private async Task ReceiveLoopAsync(WebSocket socket, BrowserConnection connection)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
        {
            ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(), stop.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                return;
            }
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes) throw new WebSocketException("Message too large.");
            if (!result.EndOfMessage) continue;
            // When it arrived, for the clock sync: before any parsing.
            long received = Stopwatch.GetTimestamp();
            if (result.MessageType == WebSocketMessageType.Text) Handle(connection, message.GetBuffer().AsSpan(0, (int)message.Length), received);
            message.SetLength(0);
        }
    }

    private void Handle(BrowserConnection connection, ReadOnlySpan<byte> utf8, long received)
    {
        JsonDocument document;
        try
        {
            var reader = new Utf8JsonReader(utf8);
            document = JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            return;
        }
        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out JsonElement type)) return;
            // Nothing but the hello counts from an extension that does not speak this protocol.
            if (!connection.Accepted && type.GetString() != "hello") return;
            switch (type.GetString())
            {
                case "hello":
                    connection.Browser = Text(root, "browser");
                    connection.ExtensionVersion = Text(root, "version");
                    connection.Protocol = (int)Number(root, "protocol", 0);
                    if (connection.Protocol != Protocol)
                    {
                        Log.Write($"Browser: {connection} has extension {connection.ExtensionVersion} (protocol {connection.Protocol}); " +
                                  $"this app needs protocol {Protocol}: reload the extension from the app's folder.");
                        Refused?.Invoke(connection.ToString(), connection.ExtensionVersion);
                        return;
                    }
                    lock (gate)
                    {
                        connection.Accepted = true;
                        Volatile.Write(ref snapshot, connections.Where(c => c.Accepted).ToArray());
                    }
                    Log.Write($"Browser: {connection} connected (extension {connection.ExtensionVersion}, protocol {connection.Protocol}).");
                    if (Volatile.Read(ref status) is { Length: > 0 } current) SendText(connection, current);
                    Changed?.Invoke();
                    break;
                case "command":
                    if (Text(root, "name") is { Length: > 0 and < 40 } name) Command?.Invoke(connection, name);
                    break;
                case "theater":
                    OnTheater(connection, root);
                    Changed?.Invoke();
                    break;
                case "media":
                    if (root.TryGetProperty("media", out JsonElement list)) connection.Media = BrowserMedia.ParseList(list);
                    Changed?.Invoke();
                    break;
                case "clock":
                    // The page's clock ping: answered at once with when it arrived and when the
                    // answer leaves (NTP-style; the page works out the offset).
                    Send(connection, new
                    {
                        type = "clock",
                        key = Text(root, "key"),
                        t0 = Number(root, "t0", 0),
                        t1 = ClockMs(received),
                        t2 = ClockMs(Stopwatch.GetTimestamp()),
                    });
                    break;
                case "frame":
                    if (FollowedBy(connection, root) is { } page)
                    {
                        double display = Number(root, "display", double.NaN), mediaTime = Number(root, "media", double.NaN);
                        long presented = (long)Number(root, "presented", -1);
                        if (double.IsFinite(display) && double.IsFinite(mediaTime) && presented >= 0)
                            page.AddFrame(new PageVideo.FrameEvent(presented, mediaTime, (long)(display * Stopwatch.Frequency / 1000)));
                    }
                    break;
                case "state":
                    if (FollowedBy(connection, root) is { } followed)
                    {
                        followed.SetState(Flag(root, "paused"), Flag(root, "seeking"), (int)Number(root, "seeks", 0));
                        double error = Number(root, "clockError", double.NaN);
                        if (double.IsFinite(error)) followed.ClockErrorMs = error;
                    }
                    break;
                case "overlays":
                    if (FollowedBy(connection, root) is { } boxed && root.TryGetProperty("boxes", out JsonElement boxes))
                    {
                        IReadOnlyList<PageBox> parsed = ParseBoxes(boxes);
                        connection.Overlays = parsed;
                        // For the depth thread straight away, with when they appeared on the screen
                        // (the page's next frame on the app's clock; before its clock is synced, when
                        // they arrived), so each frame gets the boxes it shows.
                        if (connection.Theater is { } shown)
                        {
                            double at = Number(root, "at", double.NaN);
                            long from = double.IsFinite(at) ? (long)(at * Stopwatch.Frequency / 1000) : received;
                            boxed.AddBoxes(Math.Min(from, Stopwatch.GetTimestamp()),
                                parsed.Where(b => !b.IsCaption).Select(b => shown.ToScreen(b.Box)).ToArray(),
                                parsed.Where(b => b.IsCaption).Select(b => shown.ToScreen(b.Box)).ToArray(),
                                parsed.Where(b => b.IsPanel).Select(b => shown.ToScreen(b.Box)).ToArray());
                        }
                    }
                    break;
            }
        }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";

    private static double Number(JsonElement root, string name, double fallback) =>
        root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double v) && double.IsFinite(v)
            ? v : fallback;

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.True;

    /// <summary>
    /// The theater opened, moved or closed: its report, and the video its frame, state and box
    /// messages are about from now on.
    /// </summary>
    private static void OnTheater(BrowserConnection connection, JsonElement root)
    {
        string id = Text(root, "id");
        if (!Flag(root, "open"))
        {
            if (connection.Theater?.MediaId != id) return;
            connection.Theater = null;
            connection.SetFollowed(null);
            Log.Write($"Browser: the theater in {connection} closed.");
            return;
        }
        CssRect? picture = root.TryGetProperty("picture", out JsonElement p) && p.ValueKind == JsonValueKind.Object
            ? new CssRect(Number(p, "x", 0), Number(p, "y", 0), Number(p, "width", 0), Number(p, "height", 0))
            : null;
        TheaterReport? was = connection.Theater;
        long since = was?.MediaId == id ? was.Since : Stopwatch.GetTimestamp();
        string view = "";
        if (root.TryGetProperty("view", out JsonElement v) && v.ValueKind == JsonValueKind.Object)
            view = $"player full screen {(Flag(v, "full") ? "yes" : "no")}, window {Number(v, "outerWidth", 0):0.#}×{Number(v, "outerHeight", 0):0.#}, " +
                   $"viewport {Number(v, "innerWidth", 0):0.#}×{Number(v, "innerHeight", 0):0.#}, " +
                   $"screen {Number(v, "screenWidth", 0):0.#}×{Number(v, "screenHeight", 0):0.#} CSS px";
        connection.Theater = new TheaterReport(id, (int)Number(root, "tab", -1), picture, Math.Max(0.1, Number(root, "dpr", 1)),
            Flag(root, "onScreen"), since, view);
        if (connection.SetFollowed(id)) Log.Write($"Browser: the theater in {connection} opened.");
    }

    /// <summary>The theater's video, if the message is about it (others are left over from before a change).</summary>
    private static PageVideo? FollowedBy(BrowserConnection connection, JsonElement root) =>
        connection.Followed is { } page && page.MediaId == Text(root, "id") ? page : null;

    /// <summary>The boxes of an "overlays" message; malformed ones are left out, and at most 64 kept.</summary>
    internal static IReadOnlyList<PageBox> ParseBoxes(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array) return [];
        var result = new List<PageBox>();
        foreach (JsonElement b in list.EnumerateArray())
        {
            if (b.ValueKind != JsonValueKind.Object || result.Count >= 64) continue;
            double x = Number(b, "x", double.NaN), y = Number(b, "y", double.NaN);
            double w = Number(b, "width", double.NaN), h = Number(b, "height", double.NaN);
            if (!double.IsFinite(x + y + w + h) || w <= 0 || h <= 0) continue;
            string kind = Text(b, "kind") is "caption" or "control" or "panel" ? Text(b, "kind") : "other";
            result.Add(new PageBox(new CssRect(x, y, w, h), kind));
        }
        return result;
    }

    public void Dispose()
    {
        if (stop.IsCancellationRequested) return;
        stop.Cancel();
        ping?.Dispose();
        try { listener.Stop(); } catch (SocketException) { }
    }
}
