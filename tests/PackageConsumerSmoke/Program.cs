using System.Net;
using System.Net.Sockets;
using System.Text;
using KeelMatrix.HookReplay;

const string expectedBody = "{\"source\":\"loopback\"}";
string root = Path.Combine(
    Path.GetTempPath(),
    "hookreplay-package-consumer-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    string cassette = Path.Combine(root, "recorded.json");
    Uri serverUri;
    using (var server = new LoopbackServer(expectedBody))
    {
        server.Start();
        serverUri = server.Uri;
        var recordOptions = new HookReplayOptions(cassette)
        {
            Mode = HookReplayMode.Record
        };

        using var recorder = HookReplayClient.Create(recordOptions);
        using HttpResponseMessage recorded = await recorder.GetAsync(serverUri);
        Ensure(recorded.StatusCode == HttpStatusCode.OK, "Record did not return HTTP 200.");
        Ensure(
            await recorded.Content.ReadAsStringAsync() == expectedBody,
            "Record did not return the loopback response body.");
    }

    var throwing = new ThrowingHandler();
    var replayOptions = new HookReplayOptions(cassette)
    {
        Mode = HookReplayMode.Replay
    };
    using (var replay = new HttpClient(new HookReplayHandler(replayOptions, throwing)))
    using (HttpResponseMessage response = await replay.GetAsync(serverUri))
    {
        Ensure(response.StatusCode == HttpStatusCode.OK, "Replay did not return HTTP 200.");
        Ensure(
            await response.Content.ReadAsStringAsync() == expectedBody,
            "Replay did not return the recorded response body.");
    }
    Ensure(throwing.Calls == 0, "Replay invoked the inner network handler.");

    string unsupportedCassette = Path.Combine(root, "unsupported-response.json");
    await File.WriteAllTextAsync(
        unsupportedCassette,
        "{\"schemaVersion\":1,\"interactions\":[{\"request\":{\"method\":\"GET\",\"normalizedUri\":\"https://example.test/\",\"bodyFingerprint\":null,\"bodyContentType\":null,\"body\":null,\"headers\":[],\"matchHeaders\":[]},\"response\":{\"statusCode\":200,\"reasonPhrase\":\"OK\",\"version\":\"1.1\",\"headers\":[],\"bodyHeaders\":[],\"body\":{\"contentType\":\"application/octet-stream\",\"text\":\"not-supported\"}}}]}");

    var unsupportedOptions = new HookReplayOptions(unsupportedCassette)
    {
        Mode = HookReplayMode.Replay
    };
    using var unsupportedClient = new HttpClient(
        new HookReplayHandler(unsupportedOptions, new ThrowingHandler()));
    try
    {
        using HttpResponseMessage _ = await unsupportedClient.GetAsync("https://example.test/");
        throw new InvalidOperationException(
            "Unsupported persisted response content was accepted during replay.");
    }
    catch (HookReplayUnsupportedContentException exception)
    {
        Ensure(
            exception.Message.Contains("not supported", StringComparison.OrdinalIgnoreCase),
            "Unsupported persisted response content did not report its failure category.");
        Ensure(
            !exception.Message.Contains("not-supported", StringComparison.Ordinal),
            "Unsupported persisted response content echoed the stored body.");
    }

    Console.WriteLine("Package consumer smoke passed: record, offline replay, and fail-closed validation.");
}
finally
{
    if (Directory.Exists(root))
        Directory.Delete(root, recursive: true);
}

static void Ensure(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

sealed class ThrowingHandler : HttpMessageHandler
{
    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Calls++;
        throw new InvalidOperationException("Network handler invoked.");
    }
}

sealed class LoopbackServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly byte[] responseBytes;

    public LoopbackServer(string responseBody)
    {
        responseBytes = Encoding.UTF8.GetBytes(responseBody);
    }

    public Uri Uri { get; private set; } = null!;

    public void Start()
    {
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Uri = new Uri("http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/response");
        _ = ServeOneAsync();
    }

    private async Task ServeOneAsync()
    {
        using TcpClient client = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = client.GetStream();
        var buffer = new byte[1024];
        int bytesRead = await stream.ReadAsync(buffer);
        while (bytesRead > 0 && !ContainsHeaderTerminator(buffer, bytesRead))
            bytesRead = await stream.ReadAsync(buffer);

        string headers =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            "Content-Length: " + responseBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n" +
            "Connection: close\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes);
        await stream.WriteAsync(responseBytes);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        listener.Stop();
    }

    private static bool ContainsHeaderTerminator(byte[] buffer, int length)
    {
        for (int index = 3; index < length; index++)
        {
            if (buffer[index - 3] == '\r' &&
                buffer[index - 2] == '\n' &&
                buffer[index - 1] == '\r' &&
                buffer[index] == '\n')
            {
                return true;
            }
        }

        return false;
    }
}
