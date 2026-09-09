# KeelMatrix.HookReplay

Real HTTP calls make integration tests slow and flaky. HookReplay records an intentional HttpClient exchange once, then replays it deterministically with no network fallback and cassettes designed to be safe to commit.

## Install

~~~bash
dotnet add package KeelMatrix.HookReplay
~~~

## Five-minute record and replay

Record an exchange through the normal HttpClient API:

~~~csharp
using KeelMatrix.HookReplay;

var options = new HookReplayOptions("cassettes/github-user.json")
{
    Mode = HookReplayMode.Record
};

using HttpClient client = HookReplayClient.Create(options);
HttpResponseMessage response =
    await client.GetAsync("https://api.github.com/users/octocat");
~~~

After the cassette exists, switch only the mode:

~~~csharp
var options = new HookReplayOptions("cassettes/github-user.json")
{
    Mode = HookReplayMode.Replay
};

using HttpClient client = HookReplayClient.Create(options);
HttpResponseMessage response =
    await client.GetAsync("https://api.github.com/users/octocat");
~~~

Replay never falls back to the network. A cassette miss throws HookReplayMismatchException, even when an inner handler is configured. This makes replay suitable for offline tests and CI where an accidental live request must fail closed.

## Record versus Replay

Record sends requests only through the configured inner transport, captures successful responses, sanitizes the exchange, and persists the versioned cassette. The default transport used by HookReplayClient.Create is HttpClientHandler.

Replay reads the cassette and returns a reconstructed response without invoking its inner handler. Repeated identical requests consume matching interactions sequentially. Concurrent indistinguishable requests are not deterministically orderable; supply a distinguishing selected header or custom matcher when ordering matters.

For explicit handler composition:

~~~csharp
using var transport = new HttpClientHandler();
using var handler = new HookReplayHandler(options, transport);
using var client = new HttpClient(handler);
~~~

## Matching

The default identity is HTTP method, normalized URI with query parameters sorted canonically, and a SHA-256 body fingerprint when a supported body is present. Headers are ignored unless selected:

~~~csharp
options.MatchHeaders.Add("x-tenant-id");
~~~

Selected headers are sanitized before they are persisted or used in diagnostics. Sensitive selected headers use one-way fingerprints. For bounded custom matching, implement IHookReplayRequestMatcher; it receives sanitized request descriptions only:

~~~csharp
options.RequestMatcher = new TenantMatcher();

sealed class TenantMatcher : IHookReplayRequestMatcher
{
    public bool Matches(HookReplayRequest request, HookReplayRequest recorded)
        => request.Headers.TryGetValue("x-tenant-id", out var tenant)
           && recorded.Headers.TryGetValue("x-tenant-id", out var recordedTenant)
           && tenant == recordedTenant;
}
~~~

Mismatch diagnostics describe the nearest difference (method, normalized URI, selected header, or body fingerprint) without echoing raw secrets.

## Cassette format and safety

Cassettes are HookReplay schema version 1 JSON. They are UTF-8 with a final LF, stable property order, sorted headers/query parameters, and no machine-specific paths. Unsupported future schema versions fail with HookReplayUnsupportedCassetteVersionException; malformed files fail with HookReplayMalformedCassetteException. Replay revalidates persisted request and response content types, body representations, body fingerprints, and content-type headers before matching or returning a response, so unsupported or internally inconsistent content fails with a HookReplay-specific exception.

Authorization, proxy authorization, cookies, set-cookie, API-key-like headers, sensitive query/form fields, common secret properties in JSON bodies, and response reason phrases are passed through the safe text-redaction boundary before persistence. The package also applies the built-in KeelMatrix.Redaction protections. Add project-specific protection without touching cassette-writing stages; configured redactors are applied to opaque URI query values, non-structural header values, response reason phrases, and text/JSON/form body values before either matching data or cassette bytes are created:

~~~csharp
using KeelMatrix.Redaction;

options.Redactors.Add(new RegexReplaceRedactor("customer-[0-9]+", "[CUSTOMER]"));
~~~

No unsanitized cassette bytes are written. A custom redactor must be deterministic and must not reintroduce protected values.

Cassette paths may be relative or absolute, but parent-directory traversal and existing symbolic-link or reparse-point paths are rejected before cassette I/O. Replay never follows a network fallback.

## Supported content and limits

Empty content, text, JSON, and application/x-www-form-urlencoded content are supported for bounded request and response bodies. The default limit is 1 MiB per body and can be changed:

~~~csharp
options.MaxBodyBytes = 256 * 1024;
~~~

Oversized or unsupported content fails clearly with HookReplaySizeLimitException or HookReplayUnsupportedContentException. Streaming, multipart/binary canonicalization, WebSockets, gRPC, and server-sent events are outside this package.

## Telemetry and privacy

After a successful persisted record or replay, HookReplay requests the minimal shared telemetry activation and weekly heartbeat events. Payloads do not contain URLs, hosts, methods, bodies, cassette names or paths, headers, cookies, tokens, query parameters, counts, fingerprints, mismatch details, repository names, or source paths. Telemetry is best effort and cannot break record/replay. Set KEELMATRIX_NO_TELEMETRY=1, DO_NOT_TRACK=1, or use the shared repository opt-out file to disable it.

## Compatibility and policies

The package targets `net8.0` and `netstandard2.0`. The CI matrix validates Windows, Linux, and macOS for these shipping assets and the test suite includes cassette determinism and offline replay checks.

See the [cassette compatibility policy](https://github.com/KeelMatrix/HookReplay/blob/main/docs/CASSETTE_COMPATIBILITY.md), [privacy policy](https://github.com/KeelMatrix/HookReplay/blob/main/PRIVACY.md), and [security policy](https://github.com/KeelMatrix/HookReplay/blob/main/SECURITY.md) for the durable contracts and reporting channels.

## Troubleshooting

- **Cassette miss:** confirm the method, canonical URI/query, body, and any selected matching headers are the same. Replay never calls the network to fill a miss.
- **Malformed or unsupported cassette:** check `schemaVersion`, required fields, valid JSON/content types, and the [compatibility policy](docs/CASSETTE_COMPATIBILITY.md). Future schema versions are rejected instead of partially read.
- **Unsupported or oversized content:** use empty, text, JSON, or URL-form content within `MaxBodyBytes`; streaming, multipart, and binary content are intentionally rejected.
- **Unsafe cassette path:** choose a file path without parent-directory traversal and without symbolic-link or reparse-point components.
- **Telemetry opt-out:** set `KEELMATRIX_NO_TELEMETRY=1` or `DO_NOT_TRACK=1`, or use the shared telemetry repository opt-out file.

## License

MIT © KeelMatrix
