# KeelMatrix.HookReplay

Record an intentional `HttpClient` exchange once, then replay it deterministically in tests and CI without any network fallback. HookReplay stores sanitized, versioned cassettes that are safe to review and commit.

## Install

```bash
dotnet add package KeelMatrix.HookReplay --version 0.1.0
```

## Quick Start

Record one interaction while the test server is available:

```csharp
using KeelMatrix.HookReplay;

var recordOptions = new HookReplayOptions("cassettes/greeting.json")
{
    Mode = HookReplayMode.Record
};

using (var recorder = HookReplayClient.Create(recordOptions))
using (HttpResponseMessage recorded = await recorder.GetAsync("https://api.example.test/greeting"))
{
    Console.WriteLine(await recorded.Content.ReadAsStringAsync());
}
```

Stop the server or otherwise remove network access, then replay the same request offline:

```csharp
var replayOptions = new HookReplayOptions("cassettes/greeting.json")
{
    Mode = HookReplayMode.Replay
};

using var replay = HookReplayClient.Create(replayOptions);
using HttpResponseMessage response =
    await replay.GetAsync("https://api.example.test/greeting");
```

Replay never falls back to the network. A cassette miss throws `HookReplayMismatchException`, even when an inner handler is configured.

## Important Limitations

- Supported target frameworks are `net8.0` and `netstandard2.0`.
- Empty, text, JSON, and `application/x-www-form-urlencoded` bodies are supported. Streaming, multipart, and arbitrary binary content are not.
- Request and response bodies are bounded to 1 MiB by default. Set `HookReplayOptions.MaxBodyBytes` to a lower or higher bounded value when appropriate. A complete cassette is limited to 32 MiB.
- Repeated identical requests consume matching recorded interactions sequentially. Concurrent indistinguishable requests are not deterministically orderable; use a selected matching header or custom matcher when they must be distinguished.

## Documentation

The [repository README](https://github.com/KeelMatrix/HookReplay/blob/main/README.md) covers the broader product workflow. See the [cassette compatibility policy](https://github.com/KeelMatrix/HookReplay/blob/main/docs/CASSETTE_COMPATIBILITY.md), [security policy](https://github.com/KeelMatrix/HookReplay/blob/main/SECURITY.md), [privacy policy](https://github.com/KeelMatrix/HookReplay/blob/main/PRIVACY.md), and [contributing guide](https://github.com/KeelMatrix/HookReplay/blob/main/CONTRIBUTING.md) for canonical details.

## License

MIT © KeelMatrix
