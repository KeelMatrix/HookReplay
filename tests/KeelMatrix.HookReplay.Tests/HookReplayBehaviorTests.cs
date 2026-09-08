using System.Net;
using System.Net.Sockets;
using System.Text;
using KeelMatrix.HookReplay;
using Xunit;

namespace KeelMatrix.HookReplay.Tests;

public sealed class HookReplayBehaviorTests
{
    [Fact]
    public async Task Record_persists_then_replay_succeeds_without_invoking_inner_handler()
    {
        string cassette = NewCassettePath();
        var server = new LoopbackServer("""{"name":"loopback"}""");
        server.Start();

        var recordOptions = new HookReplayOptions(cassette)
        {
            Mode = HookReplayMode.Record
        };
        using (HttpClient recorder = HookReplayClient.Create(recordOptions))
        {
            HttpResponseMessage response = await recorder.GetAsync(server.Uri);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("""{"name":"loopback"}""", await response.Content.ReadAsStringAsync());
        }

        server.Dispose();
        var replayOptions = new HookReplayOptions(cassette)
        {
            Mode = HookReplayMode.Replay
        };
        var throwing = new ThrowingHandler();
        using (var replay = new HttpClient(new HookReplayHandler(replayOptions, throwing)))
        {
            HttpResponseMessage response = await replay.GetAsync(server.Uri);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("""{"name":"loopback"}""", await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(0, throwing.Calls);
        Assert.True(File.Exists(cassette));
        DeleteCassette(cassette);
    }

    [Fact]
    public async Task Replay_miss_is_actionable_and_never_invokes_inner_handler()
    {
        string cassette = NewCassettePath();
        await File.WriteAllTextAsync(
            cassette,
            @"{""schemaVersion"":1,""interactions"":[]}");
        var throwing = new ThrowingHandler();
        var options = new HookReplayOptions(cassette)
        {
            Mode = HookReplayMode.Replay
        };

        using var client = new HttpClient(new HookReplayHandler(options, throwing));
        HookReplayMismatchException exception = await Assert.ThrowsAsync<HookReplayMismatchException>(
            () => client.GetAsync("https://example.test/missing?token=raw-secret"));

        Assert.Contains("never falls back", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw-secret", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, throwing.Calls);
        DeleteCassette(cassette);
    }

    [Fact]
    public async Task Cassette_is_deterministic_and_does_not_persist_secrets()
    {
        string first = NewCassettePath();
        string second = NewCassettePath();
        await RecordSensitiveExchangeAsync(first, "b=2&a=1");
        await RecordSensitiveExchangeAsync(second, "a=1&b=2");

        byte[] firstBytes = await File.ReadAllBytesAsync(first);
        byte[] secondBytes = await File.ReadAllBytesAsync(second);
        Assert.Equal(firstBytes, secondBytes);
        string cassetteText = Encoding.UTF8.GetString(firstBytes);
        Assert.Contains("a=1&b=2", cassetteText, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", cassetteText, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-token", cassetteText, StringComparison.Ordinal);
        Assert.DoesNotContain("session-secret", cassetteText, StringComparison.Ordinal);
        Assert.EndsWith("\n", cassetteText, StringComparison.Ordinal);

        DeleteCassette(first);
        DeleteCassette(second);
    }

    [Fact]
    public async Task Selected_headers_and_repeated_requests_are_matched_sequentially()
    {
        string cassette = NewCassettePath();
        var inner = new ScriptedHandler(
            request => request.Headers.TryGetValues("x-tenant", out var values)
                ? values.Single()
                : "none");
        var recordOptions = new HookReplayOptions(cassette)
        {
            Mode = HookReplayMode.Record
        };
        recordOptions.MatchHeaders.Add("x-tenant");
        using (var recorder = new HttpClient(new HookReplayHandler(recordOptions, inner)))
        {
            using var first = new HttpRequestMessage(HttpMethod.Get, "https://example.test/item");
            first.Headers.Add("x-tenant", "blue");
            using var second = new HttpRequestMessage(HttpMethod.Get, "https://example.test/item");
            second.Headers.Add("x-tenant", "green");
            Assert.Equal("blue", await (await recorder.SendAsync(first)).Content.ReadAsStringAsync());
            Assert.Equal("green", await (await recorder.SendAsync(second)).Content.ReadAsStringAsync());
        }

        var replayOptions = new HookReplayOptions(cassette)
        {
            Mode = HookReplayMode.Replay
        };
        replayOptions.MatchHeaders.Add("x-tenant");
        var throwing = new ThrowingHandler();
        using (var replay = new HttpClient(new HookReplayHandler(replayOptions, throwing)))
        {
            using var green = new HttpRequestMessage(HttpMethod.Get, "https://example.test/item");
            green.Headers.Add("x-tenant", "green");
            using var blue = new HttpRequestMessage(HttpMethod.Get, "https://example.test/item");
            blue.Headers.Add("x-tenant", "blue");
            Assert.Equal("green", await (await replay.SendAsync(green)).Content.ReadAsStringAsync());
            Assert.Equal("blue", await (await replay.SendAsync(blue)).Content.ReadAsStringAsync());
            await Assert.ThrowsAsync<HookReplayMismatchException>(
                () => replay.GetAsync("https://example.test/item"));
        }

        Assert.Equal(0, throwing.Calls);
        DeleteCassette(cassette);
    }

    [Fact]
    public async Task Body_sanitization_and_limits_are_explicit()
    {
        string cassette = NewCassettePath();
        var inner = new ScriptedHandler(_ => "ok");
        var options = new HookReplayOptions(cassette)
        {
            Mode = HookReplayMode.Record
        };

        using (var client = new HttpClient(new HookReplayHandler(options, inner)))
        using (var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/body"))
        {
            request.Content = new StringContent(
                """{"access_token":"raw-token","name":"safe"}""",
                Encoding.UTF8,
                "application/json");
            await client.SendAsync(request);
        }

        string cassetteText = await File.ReadAllTextAsync(cassette);
        Assert.DoesNotContain("raw-token", cassetteText, StringComparison.Ordinal);
        DeleteCassette(cassette);

        string limitedCassette = NewCassettePath();
        var limited = new HookReplayOptions(limitedCassette)
        {
            Mode = HookReplayMode.Record,
            MaxBodyBytes = 4
        };
        using var limitedClient = new HttpClient(new HookReplayHandler(limited, inner));
        using var oversized = new HttpRequestMessage(HttpMethod.Post, "https://example.test/body")
        {
            Content = new StringContent("12345")
        };
        await Assert.ThrowsAsync<HookReplaySizeLimitException>(
            () => limitedClient.SendAsync(oversized));
        Assert.False(File.Exists(limitedCassette));
    }

    [Fact]
    public async Task Custom_matcher_can_disambiguate_requests_without_persisting_raw_body()
    {
        string cassette = NewCassettePath();
        var inner = new ScriptedHandler(request =>
            request.Headers.TryGetValues("x-variant", out var values)
                ? values.Single()
                : "none");
        var recordOptions = new HookReplayOptions(cassette)
        {
            Mode = HookReplayMode.Record
        };
        using (var recorder = new HttpClient(new HookReplayHandler(recordOptions, inner)))
        {
            using var first = new HttpRequestMessage(HttpMethod.Get, "https://example.test/variant");
            first.Headers.Add("x-variant", "one");
            using var second = new HttpRequestMessage(HttpMethod.Get, "https://example.test/variant");
            second.Headers.Add("x-variant", "two");
            await recorder.SendAsync(first);
            await recorder.SendAsync(second);
        }

        var replayOptions = new HookReplayOptions(cassette)
        {
            Mode = HookReplayMode.Replay,
            RequestMatcher = new VariantMatcher()
        };
        var throwing = new ThrowingHandler();
        using (var replay = new HttpClient(new HookReplayHandler(replayOptions, throwing)))
        {
            using var second = new HttpRequestMessage(HttpMethod.Get, "https://example.test/variant");
            second.Headers.Add("x-variant", "two");
            using var first = new HttpRequestMessage(HttpMethod.Get, "https://example.test/variant");
            first.Headers.Add("x-variant", "one");
            Assert.Equal("two", await (await replay.SendAsync(second)).Content.ReadAsStringAsync());
            Assert.Equal("one", await (await replay.SendAsync(first)).Content.ReadAsStringAsync());
        }

        Assert.Equal(0, throwing.Calls);
        DeleteCassette(cassette);
    }

    [Fact]
    public async Task Form_content_is_canonicalized_and_duplicate_response_headers_replay()
    {
        string cassette = NewCassettePath();
        var inner = new ResponseHandler(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)599)
            {
                Content = new StringContent("accepted")
            };
            response.Headers.TryAddWithoutValidation("x-duplicate", "first");
            response.Headers.TryAddWithoutValidation("x-duplicate", "second");
            return response;
        });
        var recordOptions = new HookReplayOptions(cassette)
        {
            Mode = HookReplayMode.Record
        };
        using (var recorder = new HttpClient(new HookReplayHandler(recordOptions, inner)))
        using (var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/form"))
        {
            request.Content = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["b"] = "2",
                    ["a"] = "1"
                });
            HttpResponseMessage response = await recorder.SendAsync(request);
            Assert.Equal((HttpStatusCode)599, response.StatusCode);
        }

        string cassetteText = await File.ReadAllTextAsync(cassette);
        Assert.Contains("a=1\u0026b=2", cassetteText, StringComparison.Ordinal);
        var replayOptions = new HookReplayOptions(cassette);
        using (var replay = HookReplayClient.Create(replayOptions, new ThrowingHandler()))
        {
            HttpResponseMessage response = await replay.PostAsync(
                "https://example.test/form",
                new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        ["a"] = "1",
                        ["b"] = "2"
                    }));
            Assert.Equal((HttpStatusCode)599, response.StatusCode);
            Assert.Equal(new[] { "first", "second" }, response.Headers.GetValues("x-duplicate"));
        }

        DeleteCassette(cassette);
    }

    [Fact]
    public async Task Malformed_and_future_cassettes_have_distinct_failures()
    {
        string malformed = NewCassettePath();
        await File.WriteAllTextAsync(malformed, "{not-json");
        var malformedOptions = new HookReplayOptions(malformed);
        using (var client = HookReplayClient.Create(malformedOptions))
        {
            await Assert.ThrowsAsync<HookReplayMalformedCassetteException>(
                () => client.GetAsync("https://example.test"));
        }

        string future = NewCassettePath();
        await File.WriteAllTextAsync(
            future,
            @"{""schemaVersion"":99,""interactions"":[]}");
        var futureOptions = new HookReplayOptions(future);
        using (var client = HookReplayClient.Create(futureOptions))
        {
            await Assert.ThrowsAsync<HookReplayUnsupportedCassetteVersionException>(
                () => client.GetAsync("https://example.test"));
        }

        DeleteCassette(malformed);
        DeleteCassette(future);
    }

    private static async Task RecordSensitiveExchangeAsync(string cassette, string query)
    {
        var inner = new ResponseHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent(
                    """{"access_token":"session-secret","value":"stable"}""",
                    Encoding.UTF8,
                    "application/json")
            };
            response.Headers.Add("x-source", "test");
            return response;
        });
        var options = new HookReplayOptions(cassette)
        {
            Mode = HookReplayMode.Record
        };
        using var client = new HttpClient(new HookReplayHandler(options, inner));
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://EXAMPLE.test/resource?" + query);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer raw-token");
        request.Headers.TryAddWithoutValidation("Cookie", "session=session-secret");
        await client.SendAsync(request);
    }

    private static string NewCassettePath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "hookreplay-" + Guid.NewGuid().ToString("N") + ".json");
    }

    private static void DeleteCassette(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
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

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string> responseText;

        public ScriptedHandler(Func<HttpRequestMessage, string> responseText)
        {
            this.responseText = responseText;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseText(request))
            });
        }
    }

    private sealed class ResponseHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> responseFactory;

        public ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            this.responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class VariantMatcher : IHookReplayRequestMatcher
    {
        public bool Matches(HookReplayRequest request, HookReplayRequest recordedRequest)
        {
            return request.Headers.TryGetValue("x-variant", out string? actual)
                && recordedRequest.Headers.TryGetValue("x-variant", out string? recorded)
                && string.Equals(actual, recorded, StringComparison.Ordinal);
        }
    }

    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly string responseBody;
        private readonly CancellationTokenSource stop = new();
        private Task? acceptLoop;

        public LoopbackServer(string responseBody)
        {
            this.responseBody = responseBody;
        }

        public Uri Uri { get; private set; } = null!;

        public void Start()
        {
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Uri = new Uri("http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/");
            acceptLoop = AcceptLoopAsync();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    _ = HandleAsync(client);
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException) when (stop.IsCancellationRequested)
            {
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                var buffer = new byte[4096];
                int total = 0;
                while (total < buffer.Length)
                {
                    int read = await stream.ReadAsync(buffer, total, buffer.Length - total).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    total += read;
                    if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal))
                        break;
                }

                byte[] body = Encoding.UTF8.GetBytes(responseBody);
                string headers =
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/json; charset=utf-8\r\n" +
                    "X-Source: loopback\r\n" +
                    "Content-Length: " + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n" +
                    "Connection: close\r\n\r\n";
                byte[] prefix = Encoding.ASCII.GetBytes(headers);
                await stream.WriteAsync(prefix, 0, prefix.Length).ConfigureAwait(false);
                await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
            try
            {
                acceptLoop?.GetAwaiter().GetResult();
            }
            catch
            {
            }
            stop.Dispose();
        }
    }
}
