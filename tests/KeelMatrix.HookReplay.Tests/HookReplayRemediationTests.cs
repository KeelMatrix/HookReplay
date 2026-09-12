using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KeelMatrix.Redaction;
using KeelMatrix.HookReplay;
using Xunit;

namespace KeelMatrix.HookReplay.Tests;

public sealed class HookReplayRemediationTests
{
    [Fact]
    public async Task Uri_path_values_are_redacted_before_identity_cassette_and_matcher_input()
    {
        string cassette = NewCassettePath();
        string googleKey = "AIza" + new string('Q', 35);
        string hexToken = new string('a', 40);
        const string rawSecret = "project+secret=opaque";
        string escapedSecret = Uri.EscapeDataString(rawSecret);
        string builtInUri = "https://example.test/v1/keys/" + googleKey + "/" + hexToken;
        string customUri = "https://example.test/v1/" + rawSecret + "/items";

        var recordOptions = new HookReplayOptions(cassette)
        {
            Mode = HookReplayMode.Record
        };
        recordOptions.Redactors.Add(new SentinelRedactor(rawSecret, "[CUSTOM]"));

        try
        {
            using (var recorder = new HttpClient(new HookReplayHandler(
                recordOptions,
                new ResponseHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(request.RequestUri!.AbsolutePath)
                }))))
            {
                using HttpResponseMessage builtIn = await recorder.GetAsync(builtInUri);
                Assert.Equal(HttpStatusCode.OK, builtIn.StatusCode);
                using HttpResponseMessage custom = await recorder.GetAsync(customUri);
                Assert.Equal(HttpStatusCode.OK, custom.StatusCode);
            }

            string cassetteText = await File.ReadAllTextAsync(cassette);
            Assert.DoesNotContain(googleKey, cassetteText, StringComparison.Ordinal);
            Assert.DoesNotContain(hexToken, cassetteText, StringComparison.Ordinal);
            Assert.DoesNotContain(rawSecret, cassetteText, StringComparison.Ordinal);
            Assert.DoesNotContain(escapedSecret, cassetteText, StringComparison.Ordinal);
            Assert.Contains("%2A%2A%2A", cassetteText, StringComparison.Ordinal);
            Assert.Contains("%5BCUSTOM%5D", cassetteText, StringComparison.Ordinal);

            IReadOnlyList<CassetteInteraction> persisted = await CassetteFile.ReadAsync(
                cassette,
                1,
                new HttpSanitizer(recordOptions.Redactors),
                CancellationToken.None);
            Assert.Equal(2, persisted.Count);
            Assert.Equal("https://example.test/v1/keys/%2A%2A%2A/%2A%2A%2A", persisted[0].Request.NormalizedUri);
            Assert.Equal("https://example.test/v1/%5BCUSTOM%5D/items", persisted[1].Request.NormalizedUri);

            var matcher = new NormalizedUriMatcher();
            var replayOptions = new HookReplayOptions(cassette)
            {
                Mode = HookReplayMode.Replay,
                RequestMatcher = matcher
            };
            replayOptions.Redactors.Add(new SentinelRedactor(rawSecret, "[CUSTOM]"));
            var throwing = new ThrowingHandler();
            using (var replay = new HttpClient(new HookReplayHandler(replayOptions, throwing)))
            {
                using (HttpResponseMessage builtIn = await replay.GetAsync(builtInUri))
                    Assert.Equal("/v1/keys/***/***", await builtIn.Content.ReadAsStringAsync());

                using (HttpResponseMessage escaped = await replay.GetAsync(
                           "https://example.test/v1/" + escapedSecret + "/items"))
                    Assert.Equal("/v1/[CUSTOM]/items", await escaped.Content.ReadAsStringAsync());
            }

            // Two matched replays, each exposing the sanitized incoming and recorded URI.
            Assert.Equal(4, matcher.Observed.Count);
            foreach (string value in matcher.Observed)
            {
                Assert.DoesNotContain(googleKey, value, StringComparison.Ordinal);
                Assert.DoesNotContain(hexToken, value, StringComparison.Ordinal);
                Assert.DoesNotContain(rawSecret, value, StringComparison.Ordinal);
                Assert.DoesNotContain(escapedSecret, value, StringComparison.Ordinal);
            }

            Assert.Equal(0, throwing.Calls);
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Uri_path_secrets_never_appear_in_mismatch_diagnostics()
    {
        string cassette = NewCassettePath();
        const string rawSecret = "project+secret=opaque";
        string escapedSecret = Uri.EscapeDataString(rawSecret);
        var recordOptions = new HookReplayOptions(cassette)
        {
            Mode = HookReplayMode.Record
        };
        recordOptions.Redactors.Add(new SentinelRedactor(rawSecret, "[CUSTOM]"));

        try
        {
            using (var recorder = new HttpClient(new HookReplayHandler(
                recordOptions,
                new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("safe")
                }))))
            {
                await recorder.GetAsync("https://example.test/v1/" + rawSecret + "/items");
            }

            var replayOptions = new HookReplayOptions(cassette)
            {
                Mode = HookReplayMode.Replay
            };
            replayOptions.Redactors.Add(new SentinelRedactor(rawSecret, "[CUSTOM]"));
            using var replay = new HttpClient(new HookReplayHandler(replayOptions, new ThrowingHandler()));

            HookReplayMismatchException exception = await Assert.ThrowsAsync<HookReplayMismatchException>(
                () => replay.GetAsync("https://example.test/v2/" + escapedSecret + "/items"));

            Assert.Contains("normalized URI component", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(rawSecret, exception.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(escapedSecret, exception.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Successful_recording_disposes_superseded_request_and_response_content()
    {
        string cassette = NewCassettePath();
        var requestContent = TrackedContent.WithContentType("request body", "text/plain");
        var responseContent = TrackedContent.WithContentType("response body", "text/plain");
        string? observedRequestContent = null;

        try
        {
            using var recorder = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(cassette) { Mode = HookReplayMode.Record },
                new ResponseHandler(request =>
                {
                    observedRequestContent = request.Content!
                        .ReadAsStringAsync()
                        .GetAwaiter()
                        .GetResult();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = responseContent
                    };
                })));
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/lifecycle")
            {
                Content = requestContent
            };
            using HttpResponseMessage response = await recorder.SendAsync(request);

            Assert.Equal("request body", observedRequestContent);
            Assert.Equal("response body", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            DeleteCassette(cassette);
        }

        Assert.Equal(1, requestContent.Disposals);
        Assert.Equal(1, responseContent.Disposals);
        Assert.Throws<ObjectDisposedException>(() => { _ = requestContent.ReadAsByteArrayAsync(); });
        Assert.Throws<ObjectDisposedException>(() => { _ = responseContent.ReadAsByteArrayAsync(); });
    }

    [Fact]
    public async Task Response_capture_failure_disposes_captured_content_without_writing()
    {
        string cassette = NewCassettePath();
        var requestContent = TrackedContent.WithContentType("request body", "text/plain");
        var responseContent = TrackedContent.WithContentType(new byte[] { 1, 2, 3 }, "application/octet-stream");
        HttpResponseMessage? created = null;
        string? observedRequestContent = null;

        try
        {
            using var recorder = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(cassette) { Mode = HookReplayMode.Record },
                new ResponseHandler(request =>
                {
                    observedRequestContent = request.Content!
                        .ReadAsStringAsync()
                        .GetAwaiter()
                        .GetResult();
                    created = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = responseContent
                    };
                    return created;
                })));
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/unsupported")
            {
                Content = requestContent
            };

            await Assert.ThrowsAsync<HookReplayUnsupportedContentException>(
                () => recorder.SendAsync(request));

            Assert.Equal("request body", observedRequestContent);
            Assert.False(File.Exists(cassette));
        }
        finally
        {
            DeleteCassette(cassette);
        }

        Assert.NotNull(created);
        Assert.Equal(1, requestContent.Disposals);
        Assert.Equal(1, responseContent.Disposals);
        Assert.Throws<ObjectDisposedException>(() => { _ = created!.Content.ReadAsByteArrayAsync(); });
    }

    [Fact]
    public async Task Persistence_failure_disposes_captured_content_and_leaves_no_cassette()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "hookreplay-write-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var requestContent = TrackedContent.WithContentType("request body", "text/plain");
        var responseContent = TrackedContent.WithContentType("response body", "text/plain");
        HttpResponseMessage? created = null;

        try
        {
            using var recorder = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(directory) { Mode = HookReplayMode.Record },
                new ResponseHandler(_ =>
                {
                    created = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = responseContent
                    };
                    return created;
                })));
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/write-failure")
            {
                Content = requestContent
            };

            await Assert.ThrowsAsync<HookReplayIOException>(() => recorder.SendAsync(request));
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }

        Assert.NotNull(created);
        Assert.Equal(1, requestContent.Disposals);
        Assert.Equal(1, responseContent.Disposals);
        Assert.Throws<ObjectDisposedException>(() => { _ = created!.Content.ReadAsByteArrayAsync(); });
    }

    [Fact]
    public async Task Declared_non_utf8_charset_round_trips_through_cassette_and_replay()
    {
        string cassette = NewCassettePath();
        const string text = "caf\u00E9 cr\u00E8me";
        Encoding latin1 = Encoding.GetEncoding(28591);
        byte[] rawBytes = latin1.GetBytes(text);
        var recordedContent = new ByteArrayContent(rawBytes);
        recordedContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain")
        {
            CharSet = "iso-8859-1"
        };

        try
        {
            using (var recorder = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(cassette) { Mode = HookReplayMode.Record },
                new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = recordedContent
                }))))
            {
                using HttpResponseMessage response = await recorder.GetAsync("https://example.test/charset");
                Assert.Equal(text, await response.Content.ReadAsStringAsync());
                Assert.Equal(rawBytes.LongLength, response.Content.Headers.ContentLength);
            }

            IReadOnlyList<CassetteInteraction> persisted = await CassetteFile.ReadAsync(
                cassette,
                1,
                new HttpSanitizer(Array.Empty<ITextRedactor>()),
                CancellationToken.None);
            Assert.Equal(text, persisted[0].Response.Body!.Text);
            Assert.Equal("text/plain; charset=iso-8859-1", persisted[0].Response.Body!.ContentType);

            using var replay = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(cassette),
                new ThrowingHandler()));
            using HttpResponseMessage replayed = await replay.GetAsync("https://example.test/charset");

            Assert.Equal(text, await replayed.Content.ReadAsStringAsync());
            Assert.Equal(rawBytes, await replayed.Content.ReadAsByteArrayAsync());
            Assert.Equal(rawBytes.LongLength, replayed.Content.Headers.ContentLength);
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Replay_drops_stale_body_dependent_headers_after_sanitization()
    {
        string cassette = NewCassettePath();
        const string rawJson = """{"access_token":"raw-secret-value","ok":true}""";
        const string sanitizedJson = """{"access_token":"[REDACTED]","ok":true}""";
        byte[] rawBytes = Encoding.UTF8.GetBytes(rawJson);
        var recordedContent = new ByteArrayContent(rawBytes);
        recordedContent.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        recordedContent.Headers.ContentLength = rawBytes.LongLength;
        recordedContent.Headers.TryAddWithoutValidation(
            "Content-MD5",
            Convert.ToBase64String(MD5.HashData(rawBytes)));

        try
        {
            using (var recorder = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(cassette) { Mode = HookReplayMode.Record },
                new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = recordedContent
                }))))
            {
                using HttpResponseMessage response = await recorder.GetAsync("https://example.test/integrity");
                Assert.Equal(rawJson, await response.Content.ReadAsStringAsync());
                Assert.Equal(rawBytes.LongLength, response.Content.Headers.ContentLength);
                Assert.True(response.Content.Headers.Contains("Content-MD5"));
            }

            IReadOnlyList<CassetteInteraction> persisted = await CassetteFile.ReadAsync(
                cassette,
                1,
                new HttpSanitizer(Array.Empty<ITextRedactor>()),
                CancellationToken.None);
            Assert.Equal(sanitizedJson, persisted[0].Response.Body!.Text);
            Assert.DoesNotContain(
                persisted[0].Response.BodyHeaders,
                header => header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                persisted[0].Response.BodyHeaders,
                header => header.Name.Equals("Content-MD5", StringComparison.OrdinalIgnoreCase));

            using var replay = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(cassette),
                new ThrowingHandler()));
            using HttpResponseMessage replayed = await replay.GetAsync("https://example.test/integrity");

            Assert.Equal(sanitizedJson, await replayed.Content.ReadAsStringAsync());
            Assert.Equal(
                Encoding.UTF8.GetBytes(sanitizedJson).LongLength,
                replayed.Content.Headers.ContentLength);
            Assert.NotEqual(rawBytes.LongLength, replayed.Content.Headers.ContentLength);
            Assert.False(replayed.Content.Headers.Contains("Content-MD5"));
            Assert.False(replayed.Content.Headers.Contains("Digest"));
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Message_level_body_dependent_headers_are_never_persisted_or_replayed()
    {
        string cassette = NewCassettePath();
        const string rawJson = """{"access_token":"raw-secret-value","ok":true}""";
        const string sanitizedJson = """{"access_token":"[REDACTED]","ok":true}""";
        const string uri = "https://example.test/message-level-headers";
        byte[] rawBytes = Encoding.UTF8.GetBytes(rawJson);
        string rawDigest = Convert.ToBase64String(SHA256.HashData(rawBytes));
        string requestDigest = "sha-256=" + rawDigest;
        string responseDigest = "sha-256=:" + rawDigest + ":";

        try
        {
            using (var recorder = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(cassette) { Mode = HookReplayMode.Record },
                new ResponseHandler(_ =>
                {
                    var content = new ByteArrayContent(rawBytes);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
                    {
                        CharSet = "utf-8"
                    };
                    content.Headers.ContentLength = rawBytes.LongLength;
                    content.Headers.TryAddWithoutValidation(
                        "Content-MD5",
                        Convert.ToBase64String(MD5.HashData(rawBytes)));
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = content
                    };
                    response.Headers.TryAddWithoutValidation("Digest", responseDigest);
                    response.Headers.TryAddWithoutValidation("Content-Digest", responseDigest);
                    response.Headers.TryAddWithoutValidation("Transfer-Encoding", "chunked");
                    return response;
                }))))
            {
                using var request = CreateJsonRequest(uri, rawBytes, requestDigest);
                using HttpResponseMessage response = await recorder.SendAsync(request);
                Assert.Equal(rawJson, await response.Content.ReadAsStringAsync());
            }

            string cassetteText = await File.ReadAllTextAsync(cassette);
            Assert.DoesNotContain(rawDigest, cassetteText, StringComparison.Ordinal);
            Assert.DoesNotContain(requestDigest, cassetteText, StringComparison.Ordinal);
            Assert.DoesNotContain(responseDigest, cassetteText, StringComparison.Ordinal);
            Assert.DoesNotContain("Transfer-Encoding", cassetteText, StringComparison.Ordinal);

            IReadOnlyList<CassetteInteraction> persisted = await CassetteFile.ReadAsync(
                cassette,
                1,
                new HttpSanitizer(Array.Empty<ITextRedactor>()),
                CancellationToken.None);
            Assert.Equal(sanitizedJson, persisted[0].Response.Body!.Text);
            Assert.DoesNotContain(persisted[0].Request.Headers, IsBodyDependent);
            Assert.DoesNotContain(persisted[0].Request.MatchHeaders, IsBodyDependent);
            Assert.DoesNotContain(persisted[0].Response.Headers, IsBodyDependent);
            Assert.DoesNotContain(persisted[0].Response.BodyHeaders, IsBodyDependent);

            var throwing = new ThrowingHandler();
            using var replay = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(cassette),
                throwing));
            using var replayRequest = CreateJsonRequest(uri, rawBytes, requestDigest);
            using HttpResponseMessage replayed = await replay.SendAsync(replayRequest);

            Assert.Equal(sanitizedJson, await replayed.Content.ReadAsStringAsync());
            Assert.Equal(
                Encoding.UTF8.GetBytes(sanitizedJson).LongLength,
                replayed.Content.Headers.ContentLength);
            Assert.False(replayed.Headers.Contains("Digest"));
            Assert.False(replayed.Headers.Contains("Content-Digest"));
            Assert.False(replayed.Headers.Contains("Transfer-Encoding"));
            Assert.False(replayed.Content.Headers.Contains("Content-MD5"));
            Assert.Equal(0, throwing.Calls);
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Record_disposes_response_content_when_cassette_response_construction_fails()
    {
        string cassette = NewCassettePath();
        const string sentinel = "reason-phrase-redaction-sentinel";
        var responseContent = TrackedContent.WithContentType("response body", "text/plain");
        HttpResponseMessage? created = null;
        var options = new HookReplayOptions(cassette) { Mode = HookReplayMode.Record };
        options.Redactors.Add(new ThrowingSentinelRedactor(sentinel));

        try
        {
            using var recorder = new HttpClient(new HookReplayHandler(
                options,
                new ResponseHandler(_ =>
                {
                    created = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        ReasonPhrase = sentinel,
                        Content = responseContent
                    };
                    return created;
                })));
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://example.test/reason-phrase");

            await Assert.ThrowsAsync<HookReplayUnsupportedContentException>(
                () => recorder.SendAsync(request));

            Assert.False(File.Exists(cassette));
        }
        finally
        {
            DeleteCassette(cassette);
        }

        Assert.NotNull(created);
        Assert.Equal(1, responseContent.Disposals);
        Assert.Throws<ObjectDisposedException>(() => { _ = created!.Content.ReadAsByteArrayAsync(); });
    }

    [Fact]
    public async Task Replay_does_not_synthesize_a_missing_content_type()
    {
        string cassette = NewCassettePath();
        byte[] rawBytes = Encoding.UTF8.GetBytes("plain body without a declared type");
        var recordedContent = new ByteArrayContent(rawBytes);

        try
        {
            using (var recorder = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(cassette) { Mode = HookReplayMode.Record },
                new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = recordedContent
                }))))
            {
                using HttpResponseMessage response = await recorder.GetAsync("https://example.test/no-content-type");
                Assert.Null(response.Content.Headers.ContentType);
                Assert.Equal("plain body without a declared type", await response.Content.ReadAsStringAsync());
            }

            string cassetteText = await File.ReadAllTextAsync(cassette);
            Assert.Contains("\"contentType\": null", cassetteText, StringComparison.Ordinal);

            IReadOnlyList<CassetteInteraction> persisted = await CassetteFile.ReadAsync(
                cassette,
                1,
                new HttpSanitizer(Array.Empty<ITextRedactor>()),
                CancellationToken.None);
            Assert.Null(persisted[0].Response.Body!.ContentType);
            Assert.DoesNotContain(
                persisted[0].Response.BodyHeaders,
                header => header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));

            using var replay = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(cassette),
                new ThrowingHandler()));
            using HttpResponseMessage replayed = await replay.GetAsync("https://example.test/no-content-type");

            Assert.Null(replayed.Content.Headers.ContentType);
            Assert.False(replayed.Content.Headers.Contains("Content-Type"));
            Assert.Equal("plain body without a declared type", await replayed.Content.ReadAsStringAsync());
            Assert.Equal(rawBytes.LongLength, replayed.Content.Headers.ContentLength);
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Unsupported_declared_charset_fails_closed_before_writing()
    {
        string recordCassette = NewCassettePath();
        string replayCassette = NewCassettePath();
        byte[] rawBytes = Encoding.UTF8.GetBytes("unsupported charset");
        var recordedContent = new ByteArrayContent(rawBytes);
        recordedContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain")
        {
            CharSet = "windows-1252"
        };

        try
        {
            using (var recorder = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(recordCassette) { Mode = HookReplayMode.Record },
                new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = recordedContent
                }))))
            {
                HookReplayUnsupportedContentException exception =
                    await Assert.ThrowsAsync<HookReplayUnsupportedContentException>(
                        () => recorder.GetAsync("https://example.test/unsupported-charset"));

                Assert.Contains("charset", exception.Message, StringComparison.OrdinalIgnoreCase);
                Assert.False(File.Exists(recordCassette));
            }

            await File.WriteAllTextAsync(
                replayCassette,
                "{\"schemaVersion\":1,\"interactions\":[{\"request\":{\"method\":\"GET\",\"normalizedUri\":\"https://example.test/\",\"bodyFingerprint\":null,\"bodyContentType\":null,\"body\":null,\"headers\":[],\"matchHeaders\":[]},\"response\":{\"statusCode\":200,\"reasonPhrase\":\"OK\",\"version\":\"1.1\",\"headers\":[],\"bodyHeaders\":[],\"body\":{\"contentType\":\"text/plain; charset=windows-1252\",\"text\":\"stale\"}}}]}");

            using var replay = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(replayCassette),
                new ThrowingHandler()));
            HookReplayUnsupportedContentException replayException =
                await Assert.ThrowsAsync<HookReplayUnsupportedContentException>(
                    () => replay.GetAsync("https://example.test/"));
            Assert.Contains("charset", replayException.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("stale", replayException.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteCassette(recordCassette);
            DeleteCassette(replayCassette);
        }
    }

    [Fact]
    public async Task Size_limit_failure_leaves_no_replayable_ghost_interaction()
    {
        string cassette = NewCassettePath();
        int existingBodyLength = (int)CassetteFile.MaxCassetteBytes - 8192;
        var existing = new CassetteInteraction
        {
            Request = new CassetteRequest
            {
                Method = "GET",
                NormalizedUri = "https://example.test/existing",
                Headers = new List<CassetteHeader>(),
                MatchHeaders = new List<CassetteHeader>()
            },
            Response = new CassetteResponse
            {
                StatusCode = 200,
                ReasonPhrase = "OK",
                Version = HttpVersion.Version11,
                Headers = new List<CassetteHeader>(),
                BodyHeaders = new List<CassetteHeader>
                {
                    new("Content-Type", "text/plain; charset=utf-8")
                },
                Body = new CassetteBody
                {
                    ContentType = "text/plain; charset=utf-8",
                    Text = new string('x', existingBodyLength)
                }
            }
        };

        try
        {
            await CassetteFile.WriteAsync(
                cassette,
                1,
                new[] { existing },
                CancellationToken.None);
            byte[] before = await File.ReadAllBytesAsync(cassette);

            var recordOptions = new HookReplayOptions(cassette)
            {
                Mode = HookReplayMode.Record
            };
            using var handler = new HookReplayHandler(
                recordOptions,
                new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(new string('n', 16 * 1024))
                }));
            using var client = new HttpClient(handler);

            await Assert.ThrowsAsync<HookReplaySizeLimitException>(
                () => client.GetAsync("https://example.test/ghost"));

            byte[] after = await File.ReadAllBytesAsync(cassette);
            Assert.Equal(before, after);
            Assert.DoesNotContain(
                "https://example.test/ghost",
                Encoding.UTF8.GetString(after),
                StringComparison.Ordinal);

            recordOptions.Mode = HookReplayMode.Replay;
            await Assert.ThrowsAsync<HookReplayMismatchException>(
                () => client.GetAsync("https://example.test/ghost"));
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Write_failure_rolls_back_before_the_next_successful_append()
    {
        string blockingDirectory = NewCassettePath();
        Directory.CreateDirectory(blockingDirectory);
        var recordOptions = new HookReplayOptions(blockingDirectory)
        {
            Mode = HookReplayMode.Record
        };

        try
        {
            using var handler = new HookReplayHandler(
                recordOptions,
                new ResponseHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(request.RequestUri!.AbsolutePath)
                }));
            using var client = new HttpClient(handler);

            await Assert.ThrowsAsync<HookReplayIOException>(
                () => client.GetAsync("https://example.test/failed"));

            Directory.Delete(blockingDirectory);
            using HttpResponseMessage recorded = await client.GetAsync("https://example.test/succeeded");
            Assert.Equal("/succeeded", await recorded.Content.ReadAsStringAsync());

            string cassetteText = await File.ReadAllTextAsync(blockingDirectory);
            Assert.Contains("https://example.test/succeeded", cassetteText, StringComparison.Ordinal);
            Assert.DoesNotContain("https://example.test/failed", cassetteText, StringComparison.Ordinal);

            recordOptions.Mode = HookReplayMode.Replay;
            await Assert.ThrowsAsync<HookReplayMismatchException>(
                () => client.GetAsync("https://example.test/failed"));

            using var fresh = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(blockingDirectory),
                new ThrowingHandler()));
            await Assert.ThrowsAsync<HookReplayMismatchException>(
                () => fresh.GetAsync("https://example.test/failed"));
            using HttpResponseMessage replayed = await fresh.GetAsync("https://example.test/succeeded");
            Assert.Equal("/succeeded", await replayed.Content.ReadAsStringAsync());
        }
        finally
        {
            if (Directory.Exists(blockingDirectory))
                Directory.Delete(blockingDirectory, recursive: true);
            DeleteCassette(blockingDirectory);
        }
    }

    [Fact]
    public async Task Persisted_request_body_without_a_declared_content_type_replays()
    {
        string cassette = NewCassettePath();
        const string persistedBody = "ordinary text";
        await File.WriteAllTextAsync(
            cassette,
            "{\"schemaVersion\":1,\"interactions\":[{\"request\":{\"method\":\"POST\","
            + "\"normalizedUri\":\"https://example.test/\",\"bodyFingerprint\":"
            + JsonSerializer.Serialize(Fingerprint(persistedBody))
            + ",\"bodyContentType\":null,\"body\":"
            + JsonSerializer.Serialize(persistedBody)
            + ",\"headers\":[],\"matchHeaders\":[]},\"response\":{\"statusCode\":200,"
            + "\"reasonPhrase\":\"OK\",\"version\":\"1.1\",\"headers\":[],\"bodyHeaders\":[],"
            + "\"body\":null}}]}");

        try
        {
            using var replay = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(cassette),
                new ThrowingHandler()));
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/")
            {
                Content = new StringContent(persistedBody)
            };
            using HttpResponseMessage response = await replay.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Shipped_absent_content_type_fixture_replays_without_rewriting_the_fixture()
    {
        string fixture = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "schema-v1-absent-content-type.json");
        byte[] before = await File.ReadAllBytesAsync(fixture);
        var throwing = new ThrowingHandler();

        using var replay = new HttpClient(new HookReplayHandler(
            new HookReplayOptions(fixture),
            throwing));
        using HttpResponseMessage replayed = await replay.GetAsync("https://example.test/v1/plain");

        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        Assert.Null(replayed.Content.Headers.ContentType);
        Assert.Equal(
            "synthetic body without a declared content type",
            await replayed.Content.ReadAsStringAsync());
        Assert.Equal(0, throwing.Calls);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture));
    }

    private static string Fingerprint(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var builder = new StringBuilder(hash.Length * 2);
        foreach (byte valueByte in hash)
            builder.Append(valueByte.ToString("x2", CultureInfo.InvariantCulture));

        return builder.ToString();
    }

    private static bool IsBodyDependent(CassetteHeader header)
    {
        return HttpSanitizer.IsBodyDependentHeaderName(header.Name);
    }

    private static HttpRequestMessage CreateJsonRequest(string uri, byte[] body, string digest)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = content
        };
        request.Headers.TryAddWithoutValidation("Digest", digest);
        request.Headers.TryAddWithoutValidation("Content-Digest", digest);
        return request;
    }

    private static string NewCassettePath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "hookreplay-remediation-" + Guid.NewGuid().ToString("N") + ".json");
    }

    private static void DeleteCassette(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private sealed class SentinelRedactor : ITextRedactor
    {
        private readonly string sentinel;
        private readonly string replacement;

        public SentinelRedactor(string sentinel, string replacement)
        {
            this.sentinel = sentinel;
            this.replacement = replacement;
        }

        public string Redact(string input)
        {
            return input.Replace(sentinel, replacement, StringComparison.Ordinal);
        }
    }

    private sealed class ThrowingSentinelRedactor : ITextRedactor
    {
        private readonly string sentinel;

        public ThrowingSentinelRedactor(string sentinel)
        {
            this.sentinel = sentinel;
        }

        public string Redact(string input)
        {
            if (input.Contains(sentinel, StringComparison.Ordinal))
                throw new InvalidOperationException("The configured redactor refused this value.");

            return input;
        }
    }

    private sealed class NormalizedUriMatcher : IHookReplayRequestMatcher
    {
        public List<string> Observed { get; } = new();

        public bool Matches(HookReplayRequest request, HookReplayRequest recordedRequest)
        {
            Observed.Add(request.NormalizedUri);
            Observed.Add(recordedRequest.NormalizedUri);
            return true;
        }
    }

    private sealed class TrackedContent : HttpContent
    {
        private readonly byte[] payload;
        private bool disposed;

        private TrackedContent(byte[] payload)
        {
            this.payload = payload;
        }

        public int Disposals { get; private set; }

        public static TrackedContent WithContentType(string text, string mediaType)
        {
            return WithContentType(Encoding.UTF8.GetBytes(text), mediaType);
        }

        public static TrackedContent WithContentType(byte[] payload, string mediaType)
        {
            var content = new TrackedContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return content;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(TrackedContent));

            return stream.WriteAsync(payload, 0, payload.Length);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = payload.Length;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                Disposals++;
            }

            base.Dispose(disposing);
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
}
