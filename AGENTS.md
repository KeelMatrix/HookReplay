# HookReplay development guide

## Navigation

- Shipping code is in src/KeelMatrix.HookReplay/.
- Behavioral tests are in tests/KeelMatrix.HookReplay.Tests/.
- The public API baseline lives beside the shipping project.
- Package and smoke-test output belongs under artifacts/ and is ignored by git.

## Commands

- Restore: dotnet restore KeelMatrix.HookReplay.sln
- Format check: dotnet format KeelMatrix.HookReplay.sln --verify-no-changes
- Release build: dotnet build KeelMatrix.HookReplay.sln -c Release --no-restore
- Tests: dotnet test KeelMatrix.HookReplay.sln -c Release --no-build
- Package: dotnet pack src/KeelMatrix.HookReplay/KeelMatrix.HookReplay.csproj -c Release --no-build --include-symbols --output artifacts/packages
- Vulnerability check: dotnet list KeelMatrix.HookReplay.sln package --vulnerable

## Invariants

- Replay never invokes the inner handler, including on a cassette miss.
- Cassettes are UTF-8, LF-terminated, versioned JSON with deterministic property and collection ordering.
- Sanitization happens before any cassette bytes are written.
- Request identity never persists raw authorization, cookie, token, or API-key values.
- Request and response bodies are bounded and only supported text, JSON, form, or empty content is accepted.
- The shipping API is equivalent on net8.0 and netstandard2.0.
- No network, release, or publication behavior belongs in this repository beyond the configured record transport.

## Validation strategy

Start with the smallest affected test, then run the test project, Release builds for both TFMs, package inspection, and a clean consumer smoke test using only the produced package feed. Keep telemetry disabled for local and repository validation.

Changes should remain within the requested product surface. Update the API baseline when public members change and keep package contents free of local configuration, credentials, and temporary output.
