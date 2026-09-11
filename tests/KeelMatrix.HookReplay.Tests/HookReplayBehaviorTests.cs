using System.Net;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using KeelMatrix.Redaction;
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
    public async Task Invalid_mode_mutation_fails_closed_before_invoking_inner_handler()
    {
        string cassette = NewCassettePath();
        var options = new HookReplayOptions(cassette)
        {
            Mode = HookReplayMode.Replay
        };
        var throwing = new ThrowingHandler();

        try
        {
            using var client = new HttpClient(new HookReplayHandler(options, throwing));
            options.Mode = (HookReplayMode)123;

            ArgumentOutOfRangeException exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => client.GetAsync("https://example.test/invalid-mode"));

            Assert.Contains("Record or Replay", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, throwing.Calls);
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Replay_mismatch_diagnostics_choose_the_closest_unconsumed_candidate()
    {
        string cassette = NewCassettePath();

        try
        {
            var recordOptions = new HookReplayOptions(cassette)
            {
                Mode = HookReplayMode.Record
            };
            using (var recorder = new HttpClient(new HookReplayHandler(
                recordOptions,
                new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("recorded")
                }))))
            {
                await recorder.PostAsync(
                    "https://example.test/earlier",
                    new StringContent("first"));
                await recorder.PostAsync(
                    "https://example.test/target",
                    new StringContent("second"));
            }

            using var replay = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(cassette),
                new ThrowingHandler()));
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://example.test/target")
            {
                Content = new StringContent("actual")
            };

            HookReplayMismatchException exception = await Assert.ThrowsAsync<HookReplayMismatchException>(
                () => replay.SendAsync(request));

            Assert.Contains("body fingerprint", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("normalized URI component", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Cassette_size_limit_is_checked_before_replacement_and_preserves_valid_content()
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
            Assert.InRange(before.LongLength, 1, CassetteFile.MaxCassetteBytes);

            var recordOptions = new HookReplayOptions(cassette)
            {
                Mode = HookReplayMode.Record
            };
            using var recorder = new HttpClient(new HookReplayHandler(
                recordOptions,
                new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(new string('n', 16 * 1024))
                })));

            HookReplaySizeLimitException exception = await Assert.ThrowsAsync<HookReplaySizeLimitException>(
                () => recorder.GetAsync("https://example.test/new"));

            Assert.Contains("32 MiB", exception.Message, StringComparison.Ordinal);
            Assert.Equal(before, await File.ReadAllBytesAsync(cassette));

            IReadOnlyList<CassetteInteraction> readable = await CassetteFile.ReadAsync(
                cassette,
                1,
                new HttpSanitizer(Array.Empty<ITextRedactor>()),
                CancellationToken.None);
            Assert.Single(readable);
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Reader_rejects_concurrent_growth_past_limit_after_metadata_check()
    {
        string cassette = NewCassettePath();
        byte[] valid = Encoding.UTF8.GetBytes(ValidCassette("1.1", "200"));
        byte[] grown = new byte[(int)CassetteFile.MaxCassetteBytes + 1];
        Buffer.BlockCopy(valid, 0, grown, 0, valid.Length);
        Array.Fill(grown, (byte)' ', valid.Length, grown.Length - valid.Length);
        await File.WriteAllBytesAsync(cassette, valid);

        try
        {
            using var source = new GrowingCassetteStream(grown, valid.LongLength);
            HookReplaySizeLimitException exception = await Assert.ThrowsAsync<HookReplaySizeLimitException>(
                () => CassetteFile.ReadAsync(
                    cassette,
                    1,
                    new HttpSanitizer(Array.Empty<ITextRedactor>()),
                    CancellationToken.None,
                    _ => source));

            Assert.Contains("32 MiB safety limit", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Reader_allows_exact_maximum_cassette_and_rejects_one_byte_over()
    {
        string cassette = NewCassettePath();
        CassetteInteraction emptyBody = BoundaryInteraction(string.Empty);

        try
        {
            await CassetteFile.WriteAsync(
                cassette,
                1,
                new[] { emptyBody },
                CancellationToken.None);
            long emptyBodyLength = new FileInfo(cassette).Length;
            int bodyLength = checked((int)(CassetteFile.MaxCassetteBytes - emptyBodyLength));

            DeleteCassette(cassette);
            await CassetteFile.WriteAsync(
                cassette,
                1,
                new[] { BoundaryInteraction(new string('x', bodyLength)) },
                CancellationToken.None);

            Assert.Equal(CassetteFile.MaxCassetteBytes, new FileInfo(cassette).Length);
            IReadOnlyList<CassetteInteraction> readable = await CassetteFile.ReadAsync(
                cassette,
                1,
                new HttpSanitizer(Array.Empty<ITextRedactor>()),
                CancellationToken.None);
            Assert.Single(readable);

            using (var append = new FileStream(cassette, FileMode.Append, FileAccess.Write, FileShare.Read))
                append.WriteByte((byte)' ');

            HookReplaySizeLimitException exception = await Assert.ThrowsAsync<HookReplaySizeLimitException>(
                () => CassetteFile.ReadAsync(
                    cassette,
                    1,
                    new HttpSanitizer(Array.Empty<ITextRedactor>()),
                    CancellationToken.None));
            Assert.Contains("32 MiB safety limit", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteCassette(cassette);
        }
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
    public async Task Response_reason_phrase_is_sanitized_before_persistence_and_replay()
    {
        string cassette = NewCassettePath();
        const string rawReasonPhrase = "Bearer reason-secret-token";
        var options = new HookReplayOptions(cassette)
        {
            Mode = HookReplayMode.Record
        };
        options.Redactors.Add(new SentinelRedactor(rawReasonPhrase, "[CUSTOM]"));

        try
        {
            using (var recorder = new HttpClient(new HookReplayHandler(
                options,
                new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    ReasonPhrase = rawReasonPhrase,
                    Content = new StringContent("safe")
                }))))
            {
                using HttpResponseMessage response = await recorder.GetAsync(
                    "https://example.test/reason-phrase");
                Assert.Equal(rawReasonPhrase, response.ReasonPhrase);
            }

            string cassetteText = await File.ReadAllTextAsync(cassette);
            Assert.DoesNotContain(rawReasonPhrase, cassetteText, StringComparison.Ordinal);
            Assert.Contains("[CUSTOM]", cassetteText, StringComparison.Ordinal);

            var replayOptions = new HookReplayOptions(cassette)
            {
                Mode = HookReplayMode.Replay
            };
            replayOptions.Redactors.Add(new SentinelRedactor(rawReasonPhrase, "[CUSTOM]"));
            using var replay = new HttpClient(new HookReplayHandler(
                replayOptions,
                new ThrowingHandler()));
            using HttpResponseMessage replayed = await replay.GetAsync(
                "https://example.test/reason-phrase");

            Assert.Equal("[CUSTOM]", replayed.ReasonPhrase);
            Assert.Equal("safe", await replayed.Content.ReadAsStringAsync());
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Shipped_schema_v1_fixture_replays_without_rewriting_the_fixture()
    {
        string fixture = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "schema-v1-basic.json");
        byte[] before = await File.ReadAllBytesAsync(fixture);
        var options = new HookReplayOptions(fixture);
        var throwing = new ThrowingHandler();

        using (var client = new HttpClient(new HookReplayHandler(options, throwing)))
        using (HttpResponseMessage response = await client.GetAsync(
            "https://EXAMPLE.test/v1/items?sort=asc"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(
                "{\"items\":[{\"id\":1,\"name\":\"synthetic\"}]}",
                await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(0, throwing.Calls);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture));
    }

    [Fact]
    public async Task Concurrent_distinguishable_replay_requests_consume_distinct_interactions()
    {
        string cassette = NewCassettePath();
        string[] variants = { "red", "blue", "green", "yellow", "purple", "orange" };
        var recordOptions = new HookReplayOptions(cassette)
        {
            Mode = HookReplayMode.Record
        };
        recordOptions.MatchHeaders.Add("x-variant");
        using (var recorder = new HttpClient(new HookReplayHandler(
            recordOptions,
            new ResponseHandler(request =>
            {
                string variant = request.Headers.GetValues("x-variant").Single();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(variant)
                };
            }))))
        {
            await Task.WhenAll(variants.Select(async variant =>
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://example.test/concurrent");
                request.Headers.Add("x-variant", variant);
                using HttpResponseMessage response = await recorder.SendAsync(request);
                Assert.Equal(variant, await response.Content.ReadAsStringAsync());
            }));
        }

        var replayOptions = new HookReplayOptions(cassette);
        replayOptions.MatchHeaders.Add("x-variant");
        var throwing = new ThrowingHandler();
        using (var replay = new HttpClient(new HookReplayHandler(replayOptions, throwing)))
        {
            string[] results = await Task.WhenAll(variants.Select(async variant =>
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://example.test/concurrent");
                request.Headers.Add("x-variant", variant);
                using HttpResponseMessage response = await replay.SendAsync(request);
                return await response.Content.ReadAsStringAsync();
            }));

            Assert.Equal(variants.OrderBy(value => value), results.OrderBy(value => value));
        }

        Assert.Equal(0, throwing.Calls);
        DeleteCassette(cassette);
    }

    [Fact]
    public async Task Concurrent_recording_preserves_every_distinguishable_interaction()
    {
        string cassette = NewCassettePath();
        string[] paths = Enumerable.Range(1, 12)
            .Select(value => "/items/" + value.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        using (var recorder = new HttpClient(new HookReplayHandler(
            new HookReplayOptions(cassette)
            {
                Mode = HookReplayMode.Record
            },
            new ResponseHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath)
            }))))
        {
            string[] results = await Task.WhenAll(paths.Select(async path =>
            {
                using HttpResponseMessage response = await recorder.GetAsync(
                    "https://example.test" + path);
                return await response.Content.ReadAsStringAsync();
            }));

            Assert.Equal(paths.OrderBy(value => value), results.OrderBy(value => value));
        }

        string cassetteText = await File.ReadAllTextAsync(cassette);
        Assert.Equal(
            paths.Length,
            cassetteText.Split("\"normalizedUri\"", StringSplitOptions.None).Length - 1);

        var throwing = new ThrowingHandler();
        using (var replay = new HttpClient(new HookReplayHandler(
            new HookReplayOptions(cassette),
            throwing)))
        {
            await Task.WhenAll(paths.Select(async path =>
            {
                using HttpResponseMessage response = await replay.GetAsync(
                    "https://example.test" + path);
                Assert.Equal(path, await response.Content.ReadAsStringAsync());
            }));
        }

        Assert.Equal(0, throwing.Calls);
        DeleteCassette(cassette);
    }

    [Fact]
    public async Task Concurrent_indistinguishable_requests_have_one_success_and_explicit_misses()
    {
        string cassette = NewCassettePath();
        var recordOptions = new HookReplayOptions(cassette)
        {
            Mode = HookReplayMode.Record
        };
        using (var recorder = new HttpClient(new HookReplayHandler(
            recordOptions,
            new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("one")
            }))))
        {
            using HttpResponseMessage response = await recorder.GetAsync("https://example.test/one");
            Assert.Equal("one", await response.Content.ReadAsStringAsync());
        }

        var outcomes = new ConcurrentBag<bool>();
        var throwing = new ThrowingHandler();
        using (var replay = new HttpClient(new HookReplayHandler(
            new HookReplayOptions(cassette),
            throwing)))
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            {
                try
                {
                    using HttpResponseMessage response = await replay.GetAsync("https://example.test/one");
                    outcomes.Add(await response.Content.ReadAsStringAsync() == "one");
                }
                catch (HookReplayMismatchException)
                {
                    outcomes.Add(false);
                }
            }));
        }

        Assert.Equal(1, outcomes.Count(value => value));
        Assert.Equal(7, outcomes.Count(value => !value));
        Assert.Equal(0, throwing.Calls);
        DeleteCassette(cassette);
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

    [Fact]
    public async Task Unsupported_persisted_response_content_fails_closed()
    {
        string cassette = NewCassettePath();
        await File.WriteAllTextAsync(
            cassette,
            "{\"schemaVersion\":1,\"interactions\":[{\"request\":{\"method\":\"GET\",\"normalizedUri\":\"https://example.test/\",\"bodyFingerprint\":null,\"bodyContentType\":null,\"body\":null,\"headers\":[],\"matchHeaders\":[]},\"response\":{\"statusCode\":200,\"reasonPhrase\":\"OK\",\"version\":\"1.1\",\"headers\":[],\"bodyHeaders\":[],\"body\":{\"contentType\":\"application/octet-stream\",\"text\":\"not-supported\"}}}]}");

        try
        {
            var options = new HookReplayOptions(cassette)
            {
                Mode = HookReplayMode.Replay
            };
            using var client = new HttpClient(new HookReplayHandler(options, new ThrowingHandler()));

            HookReplayUnsupportedContentException exception =
                await Assert.ThrowsAsync<HookReplayUnsupportedContentException>(
                    () => client.GetAsync("https://example.test/"));

            Assert.Contains("not supported", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("not-supported", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Persisted_json_response_body_must_remain_valid_json()
    {
        string cassette = NewCassettePath();
        await File.WriteAllTextAsync(
            cassette,
            "{\"schemaVersion\":1,\"interactions\":[{\"request\":{\"method\":\"GET\",\"normalizedUri\":\"https://example.test/\",\"bodyFingerprint\":null,\"bodyContentType\":null,\"body\":null,\"headers\":[],\"matchHeaders\":[]},\"response\":{\"statusCode\":200,\"reasonPhrase\":\"OK\",\"version\":\"1.1\",\"headers\":[],\"bodyHeaders\":[],\"body\":{\"contentType\":\"application/json\",\"text\":\"not-json\"}}}]}");

        try
        {
            var options = new HookReplayOptions(cassette)
            {
                Mode = HookReplayMode.Replay
            };
            using var client = new HttpClient(new HookReplayHandler(options, new ThrowingHandler()));

            HookReplayUnsupportedContentException exception =
                await Assert.ThrowsAsync<HookReplayUnsupportedContentException>(
                    () => client.GetAsync("https://example.test/"));

            Assert.Contains("valid JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("not-json", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Persisted_response_body_content_type_must_match_its_header()
    {
        string cassette = NewCassettePath();
        string contents = ValidCassette("1.1", "200")
            .Replace(
                "\"bodyHeaders\":[]",
                "\"bodyHeaders\":[{\"name\":\"Content-Type\",\"value\":\"application/json\"}]",
                StringComparison.Ordinal)
            .Replace(
                "\"body\":null}}]}",
                "\"body\":{\"contentType\":\"text/plain; charset=utf-8\",\"text\":\"ordinary text\"}}}]}",
                StringComparison.Ordinal);
        await File.WriteAllTextAsync(cassette, contents);

        try
        {
            var options = new HookReplayOptions(cassette)
            {
                Mode = HookReplayMode.Replay
            };
            using var client = new HttpClient(new HookReplayHandler(options, new ThrowingHandler()));

            HookReplayMalformedCassetteException exception =
                await Assert.ThrowsAsync<HookReplayMalformedCassetteException>(
                    () => client.GetAsync("https://example.test/"));

            Assert.Contains("content type", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ordinary text", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Unsupported_persisted_request_content_fails_closed_before_matching_or_replay()
    {
        string cassette = NewCassettePath();
        await File.WriteAllTextAsync(
            cassette,
            "{\"schemaVersion\":1,\"interactions\":[{\"request\":{\"method\":\"POST\",\"normalizedUri\":\"https://example.test/\",\"bodyFingerprint\":null,\"bodyContentType\":\"application/octet-stream\",\"body\":\"not-supported\",\"headers\":[],\"matchHeaders\":[]},\"response\":{\"statusCode\":200,\"reasonPhrase\":\"OK\",\"version\":\"1.1\",\"headers\":[],\"bodyHeaders\":[],\"body\":null}}]}");

        try
        {
            var options = new HookReplayOptions(cassette)
            {
                Mode = HookReplayMode.Replay
            };
            var throwing = new ThrowingHandler();
            using var client = new HttpClient(new HookReplayHandler(options, throwing));

            HookReplayException exception = await Assert.ThrowsAnyAsync<HookReplayException>(
                () => client.PostAsync("https://example.test/", content: null));

            Assert.IsType<HookReplayUnsupportedContentException>(exception);
            Assert.DoesNotContain("not-supported", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, throwing.Calls);
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Theory]
    [InlineData(null, "text/plain", "ordinary text")]
    [InlineData("cf7e0cb3799813c75eb1ec05482a20a640dd269ce5c6479dbda0a88a0a0e01", null, "ordinary text")]
    [InlineData(null, null, "ordinary text")]
    [InlineData("cf7e0cb3799813c75eb1ec05482a20a640dd269ce5c6479dbda0a88a0a0e01", "text/plain", null)]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000", "text/plain", "ordinary text")]
    public async Task Persisted_request_body_relationships_must_be_consistent(
        string? bodyFingerprint,
        string? bodyContentType,
        string? body)
    {
        string cassette = NewCassettePath();
        await File.WriteAllTextAsync(cassette, CassetteWithRequest(bodyFingerprint, bodyContentType, body));

        try
        {
            using var client = HookReplayClient.Create(new HookReplayOptions(cassette), new ThrowingHandler());
            HookReplayMalformedCassetteException exception =
                await Assert.ThrowsAsync<HookReplayMalformedCassetteException>(
                    () => client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://example.test/")));

            Assert.Contains("request body", exception.Message, StringComparison.OrdinalIgnoreCase);
            if (body is not null)
                Assert.DoesNotContain(body, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Persisted_json_request_body_must_remain_valid_json()
    {
        string cassette = NewCassettePath();
        const string rawBody = "not-json";
        await File.WriteAllTextAsync(
            cassette,
            CassetteWithRequest(
                "0c21a879c732a67910d80988df4919d794f6a070aab610ef865032a28046b021",
                "application/json",
                rawBody));

        try
        {
            using var client = HookReplayClient.Create(new HookReplayOptions(cassette), new ThrowingHandler());
            HookReplayUnsupportedContentException exception =
                await Assert.ThrowsAsync<HookReplayUnsupportedContentException>(
                    () => client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://example.test/")));

            Assert.Contains("valid JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(rawBody, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Persisted_request_body_content_type_must_match_its_header()
    {
        string cassette = NewCassettePath();
        await File.WriteAllTextAsync(
            cassette,
            CassetteWithRequest(
                "cf7e0cb3799813c75eb1ec05482a20a640dd269ce5c6479dbda0a88a0a0e01",
                "text/plain; charset=utf-8",
                "ordinary text",
                "[{\"name\":\"Content-Type\",\"value\":\"application/json\"}]"));

        try
        {
            using var client = HookReplayClient.Create(new HookReplayOptions(cassette), new ThrowingHandler());
            HookReplayMalformedCassetteException exception =
                await Assert.ThrowsAsync<HookReplayMalformedCassetteException>(
                    () => client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://example.test/")));

            Assert.Contains("content type", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ordinary text", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Theory]
    [InlineData("[{\"name\":\"x-unknown\",\"value\":\"value\"}]")]
    [InlineData("[{\"name\":\"x-duplicate\",\"value\":\"one\"},{\"name\":\"X-DUPLICATE\",\"value\":\"two\"}]")]
    public async Task Persisted_request_match_headers_must_refer_to_unique_request_headers(string matchHeaders)
    {
        string cassette = NewCassettePath();
        await File.WriteAllTextAsync(
            cassette,
            CassetteWithRequest(null, null, null, "[]", matchHeaders));

        try
        {
            using var client = HookReplayClient.Create(new HookReplayOptions(cassette), new ThrowingHandler());
            await Assert.ThrowsAsync<HookReplayMalformedCassetteException>(
                () => client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.test/")));
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public void Cassette_path_parent_traversal_is_rejected_before_file_io()
    {
        string directory = Path.Combine(Path.GetTempPath(), "hookreplay-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string outside = Path.Combine(
            Path.GetDirectoryName(directory)!,
            "hookreplay-outside-" + Guid.NewGuid().ToString("N") + ".json");
        string traversal = Path.Combine(directory, "..", Path.GetFileName(outside));

        try
        {
            var options = new HookReplayOptions(traversal)
            {
                Mode = HookReplayMode.Record
            };

            HookReplayIOException exception = Assert.Throws<HookReplayIOException>(
                () => new HookReplayHandler(options, new ScriptedHandler(_ => "not-used")));

            Assert.Contains("parent-directory traversal", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outside));
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            if (File.Exists(outside))
                File.Delete(outside);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Custom_redactor_protects_query_headers_and_body_before_durable_write()
    {
        string cassette = NewCassettePath();
        const string rawSecret = "project-secret-opaque-value";
        var options = new HookReplayOptions(cassette)
        {
            Mode = HookReplayMode.Record
        };
        options.Redactors.Add(new SentinelRedactor(rawSecret, "[CUSTOM]"));

        try
        {
            using var client = new HttpClient(new HookReplayHandler(
                options,
                new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("response-safe")
                })));
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://example.test/custom?opaque=" + Uri.EscapeDataString(rawSecret));
            request.Headers.TryAddWithoutValidation("x-project-value", rawSecret);
            request.Content = new StringContent(rawSecret, Encoding.UTF8, "text/plain");

            await client.SendAsync(request);

            string cassetteText = await File.ReadAllTextAsync(cassette);
            Assert.DoesNotContain(rawSecret, cassetteText, StringComparison.Ordinal);
            Assert.Contains("[CUSTOM]", cassetteText, StringComparison.Ordinal);
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Custom_redactor_protects_mismatch_diagnostics_for_opaque_query_values()
    {
        string cassette = NewCassettePath();
        const string rawSecret = "project-secret-opaque-value";
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
                await recorder.GetAsync(
                    "https://example.test/diagnostics?opaque=" + Uri.EscapeDataString(rawSecret));
            }

            var replayOptions = new HookReplayOptions(cassette)
            {
                Mode = HookReplayMode.Replay
            };
            replayOptions.Redactors.Add(new SentinelRedactor(rawSecret, "[CUSTOM]"));
            using var replay = new HttpClient(new HookReplayHandler(replayOptions, new ThrowingHandler()));
            HookReplayMismatchException exception = await Assert.ThrowsAsync<HookReplayMismatchException>(
                () => replay.GetAsync(
                    "https://example.test/other?opaque=" + Uri.EscapeDataString(rawSecret)));

            Assert.DoesNotContain(rawSecret, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Sanitizer_failure_happens_before_any_cassette_bytes_are_written()
    {
        string cassette = NewCassettePath();
        var options = new HookReplayOptions(cassette)
        {
            Mode = HookReplayMode.Record
        };
        options.Redactors.Add(new ThrowingRedactor());

        try
        {
            using var client = new HttpClient(new HookReplayHandler(
                options,
                new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/sanitize")
            {
                Content = new StringContent("secret-value")
            };

            await Assert.ThrowsAsync<HookReplayUnsupportedContentException>(
                () => client.SendAsync(request));
            Assert.False(File.Exists(cassette));
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Sanitizer_failure_does_not_expose_input_in_exception_diagnostics()
    {
        string cassette = NewCassettePath();
        const string rawSecret = "reason-phrase-secret-token";
        var options = new HookReplayOptions(cassette)
        {
            Mode = HookReplayMode.Record
        };
        options.Redactors.Add(new LeakingThrowingRedactor(rawSecret));

        try
        {
            using var client = new HttpClient(new HookReplayHandler(
                options,
                new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    ReasonPhrase = rawSecret
                })));

            HookReplayUnsupportedContentException exception =
                await Assert.ThrowsAsync<HookReplayUnsupportedContentException>(
                    () => client.GetAsync("https://example.test/sanitize-diagnostics"));

            Assert.Null(exception.InnerException);
            Assert.DoesNotContain(rawSecret, exception.ToString(), StringComparison.Ordinal);
            Assert.False(File.Exists(cassette));
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Redirect_responses_are_recorded_and_replayed_without_following()
    {
        string cassette = NewCassettePath();

        try
        {
            using (var recorder = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(cassette) { Mode = HookReplayMode.Record },
                new ResponseHandler(_ =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Redirect)
                    {
                        Headers = { Location = new Uri("https://example.test/final") }
                    };
                    return response;
                }))))
            {
                HttpResponseMessage response = await recorder.GetAsync("https://example.test/redirect");
                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                Assert.Equal("https://example.test/final", response.Headers.Location?.ToString());
            }

            using var replay = HookReplayClient.Create(new HookReplayOptions(cassette), new ThrowingHandler());
            HttpResponseMessage replayed = await replay.GetAsync("https://example.test/redirect");
            Assert.Equal(HttpStatusCode.Redirect, replayed.StatusCode);
            Assert.Equal("https://example.test/final", replayed.Headers.Location?.ToString());
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public void Existing_reparse_point_in_cassette_path_is_rejected()
    {
        string root = Path.Combine(Path.GetTempPath(), "hookreplay-link-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "target");
        string link = Path.Combine(root, "link");
        Directory.CreateDirectory(target);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception exception) when (
                exception is PlatformNotSupportedException ||
                exception is UnauthorizedAccessException ||
                exception is IOException)
            {
                return;
            }

            var options = new HookReplayOptions(Path.Combine(link, "cassette.json"));
            Assert.Throws<HookReplayIOException>(() => new HookReplayHandler(options));
        }
        finally
        {
            if (File.Exists(link))
                File.Delete(link);
            else if (Directory.Exists(link))
                Directory.Delete(link);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Telemetry_opt_out_does_not_change_record_or_replay_behavior()
    {
        string cassette = NewCassettePath();
        string? previous = Environment.GetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY");
        Environment.SetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY", "1");

        try
        {
            using (var recorder = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(cassette) { Mode = HookReplayMode.Record },
                new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("telemetry-off")
                }))))
            {
                await recorder.GetAsync("https://example.test/telemetry-off");
            }

            using var replay = HookReplayClient.Create(new HookReplayOptions(cassette), new ThrowingHandler());
            Assert.Equal(
                "telemetry-off",
                await (await replay.GetAsync("https://example.test/telemetry-off")).Content.ReadAsStringAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY", previous);
            DeleteCassette(cassette);
        }
    }

    [Theory]
    [InlineData("not-a-version", "200")]
    [InlineData("1.1", "99")]
    [InlineData("1.1", "600")]
    public async Task Semantically_invalid_cassette_fields_fail_as_malformed(
        string version,
        string statusCode)
    {
        string cassette = NewCassettePath();
        await File.WriteAllTextAsync(cassette, ValidCassette(version, statusCode));

        try
        {
            using var client = HookReplayClient.Create(new HookReplayOptions(cassette));
            HookReplayMalformedCassetteException exception = await Assert.ThrowsAsync<HookReplayMalformedCassetteException>(
                () => client.GetAsync("https://example.test/"));

            Assert.Contains("Cassette", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Invalid_persisted_uri_is_rejected_as_malformed()
    {
        string cassette = NewCassettePath();
        await File.WriteAllTextAsync(cassette, ValidCassette("1.1", "200").Replace(
            "https://example.test/",
            "not-an-absolute-uri",
            StringComparison.Ordinal));

        try
        {
            using var client = HookReplayClient.Create(new HookReplayOptions(cassette));
            await Assert.ThrowsAsync<HookReplayMalformedCassetteException>(
                () => client.GetAsync("https://example.test/"));
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Empty_and_text_bodies_round_trip_at_the_handler_boundary()
    {
        string emptyCassette = NewCassettePath();
        string textCassette = NewCassettePath();

        try
        {
            using (var recorder = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(emptyCassette) { Mode = HookReplayMode.Record },
                new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)))))
            {
                HttpResponseMessage response = await recorder.GetAsync("https://example.test/empty");
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
            }

            using (var replay = HookReplayClient.Create(new HookReplayOptions(emptyCassette), new ThrowingHandler()))
            {
                HttpResponseMessage response = await replay.GetAsync("https://example.test/empty");
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
            }

            using (var recorder = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(textCassette) { Mode = HookReplayMode.Record },
                new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("ordinary text")
                }))))
            {
                await recorder.GetAsync("https://example.test/text");
            }

            using (var replay = HookReplayClient.Create(new HookReplayOptions(textCassette), new ThrowingHandler()))
            {
                HttpResponseMessage response = await replay.GetAsync("https://example.test/text");
                Assert.Equal("ordinary text", await response.Content.ReadAsStringAsync());
            }
        }
        finally
        {
            DeleteCassette(emptyCassette);
            DeleteCassette(textCassette);
        }
    }

    [Fact]
    public async Task Disposed_request_and_response_content_fail_without_writing()
    {
        string requestCassette = NewCassettePath();
        string responseCassette = NewCassettePath();

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/disposed")
            {
                Content = new StringContent("disposed")
            };
            request.Content.Dispose();
            using (request)
            using (var client = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(requestCassette) { Mode = HookReplayMode.Record },
                new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))))
            {
                await Assert.ThrowsAsync<HookReplayUnsupportedContentException>(
                    () => client.SendAsync(request));
            }

            using (var client = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(responseCassette) { Mode = HookReplayMode.Record },
                new DisposedResponseContentHandler())))
            {
                await Assert.ThrowsAsync<HookReplayUnsupportedContentException>(
                    () => client.GetAsync("https://example.test/disposed-response"));
            }

            Assert.False(File.Exists(requestCassette));
            Assert.False(File.Exists(responseCassette));
        }
        finally
        {
            DeleteCassette(requestCassette);
            DeleteCassette(responseCassette);
        }
    }

    [Fact]
    public async Task Unsupported_content_and_file_errors_are_explicit()
    {
        string contentCassette = NewCassettePath();
        string emptyContentCassette = NewCassettePath();
        string directory = Path.Combine(Path.GetTempPath(), "hookreplay-file-error-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            using (var client = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(contentCassette) { Mode = HookReplayMode.Record },
                new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))))
            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/binary")
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
            })
            {
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                    "application/octet-stream");
                await Assert.ThrowsAsync<HookReplayUnsupportedContentException>(
                    () => client.SendAsync(request));
            }

            using (var client = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(emptyContentCassette) { Mode = HookReplayMode.Record },
                new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))))
            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/empty-binary")
            {
                Content = new ByteArrayContent(Array.Empty<byte>())
            })
            {
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                    "application/octet-stream");
                await Assert.ThrowsAsync<HookReplayUnsupportedContentException>(
                    () => client.SendAsync(request));
            }
            Assert.False(File.Exists(emptyContentCassette));

            using var fileErrorClient = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(directory) { Mode = HookReplayMode.Record },
                new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
            await Assert.ThrowsAsync<HookReplayIOException>(
                () => fileErrorClient.GetAsync("https://example.test/file-error"));
        }
        finally
        {
            DeleteCassette(contentCassette);
            DeleteCassette(emptyContentCassette);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cancellation_is_propagated_without_persisting_a_cassette()
    {
        string cassette = NewCassettePath();
        using var started = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();

        try
        {
            var handler = new CancellationAwareHandler(started);
            using var client = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(cassette) { Mode = HookReplayMode.Record },
                handler));
            Task<HttpResponseMessage> request = client.GetAsync(
                "https://example.test/cancel",
                cancellation.Token);
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.False(File.Exists(cassette));
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    [Fact]
    public async Task Telemetry_failure_cannot_break_record_or_replay_and_only_minimal_events_are_requested()
    {
        string cassette = NewCassettePath();
        var telemetry = new RecordingTelemetry { ThrowOnUse = true };

        try
        {
            using (var recorder = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(cassette) { Mode = HookReplayMode.Record },
                new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("safe")
                }),
                telemetry)))
            {
                HttpResponseMessage response = await recorder.GetAsync("https://example.test/telemetry");
                Assert.Equal("safe", await response.Content.ReadAsStringAsync());
            }

            telemetry.ThrowOnUse = false;
            using (var replay = new HttpClient(new HookReplayHandler(
                new HookReplayOptions(cassette),
                new ThrowingHandler(),
                telemetry)))
            {
                await replay.GetAsync("https://example.test/telemetry");
            }

            Assert.Equal(1, telemetry.Activations);
            Assert.Equal(1, telemetry.Heartbeats);
            Assert.Equal(new[] { "activation", "heartbeat", "activation", "heartbeat" }, telemetry.Events);
        }
        finally
        {
            DeleteCassette(cassette);
        }
    }

    private static string ValidCassette(string version, string statusCode)
    {
        return "{\"schemaVersion\":1,\"interactions\":[{\"request\":{\"method\":\"GET\",\"normalizedUri\":\"https://example.test/\",\"bodyFingerprint\":null,\"bodyContentType\":null,\"body\":null,\"headers\":[],\"matchHeaders\":[]},\"response\":{\"statusCode\":"
            + statusCode
            + ",\"reasonPhrase\":\"OK\",\"version\":\""
            + version
            + "\",\"headers\":[],\"bodyHeaders\":[],\"body\":null}}]}";
    }

    private static CassetteInteraction BoundaryInteraction(string responseBody)
    {
        return new CassetteInteraction
        {
            Request = new CassetteRequest
            {
                Method = "GET",
                NormalizedUri = "https://example.test/boundary",
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
                    Text = responseBody
                }
            }
        };
    }

    private static string CassetteWithRequest(
        string? bodyFingerprint,
        string? bodyContentType,
        string? body,
        string requestHeaders = "[]",
        string matchHeaders = "[]")
    {
        return "{\"schemaVersion\":1,\"interactions\":[{\"request\":{\"method\":\"POST\",\"normalizedUri\":\"https://example.test/\",\"bodyFingerprint\":"
            + JsonSerializer.Serialize(bodyFingerprint)
            + ",\"bodyContentType\":"
            + JsonSerializer.Serialize(bodyContentType)
            + ",\"body\":"
            + JsonSerializer.Serialize(body)
            + ",\"headers\":"
            + requestHeaders
            + ",\"matchHeaders\":"
            + matchHeaders
            + "},\"response\":{\"statusCode\":200,\"reasonPhrase\":\"OK\",\"version\":\"1.1\",\"headers\":[],\"bodyHeaders\":[],\"body\":null}}]}";
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

    private sealed class GrowingCassetteStream : MemoryStream
    {
        private readonly long initialLength;

        public GrowingCassetteStream(byte[] bytes, long initialLength)
            : base(bytes, writable: false)
        {
            this.initialLength = initialLength;
        }

        public override long Length => Position == 0 ? initialLength : base.Length;
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

    private sealed class ThrowingRedactor : ITextRedactor
    {
        public string Redact(string input)
        {
            throw new InvalidOperationException("redactor failure");
        }
    }

    private sealed class LeakingThrowingRedactor : ITextRedactor
    {
        private readonly string secret;

        public LeakingThrowingRedactor(string secret)
        {
            this.secret = secret;
        }

        public string Redact(string input)
        {
            throw new InvalidOperationException("redactor failed for " + secret);
        }
    }

    private sealed class DisposedResponseContentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("disposed")
            };
            response.Content.Dispose();
            return Task.FromResult(response);
        }
    }

    private sealed class CancellationAwareHandler : HttpMessageHandler
    {
        private readonly ManualResetEventSlim started;

        public CancellationAwareHandler(ManualResetEventSlim started)
        {
            this.started = started;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            started.Set();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class RecordingTelemetry : IHookReplayTelemetry
    {
        public int Activations { get; private set; }
        public int Heartbeats { get; private set; }
        public bool ThrowOnUse { get; set; }
        public List<string> Events { get; } = new();

        public void TrackActivation()
        {
            Events.Add("activation");
            if (ThrowOnUse)
                throw new InvalidOperationException("telemetry failure");
            Activations++;
        }

        public void TrackHeartbeat()
        {
            Events.Add("heartbeat");
            if (ThrowOnUse)
                throw new InvalidOperationException("telemetry failure");
            Heartbeats++;
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
