# Contributing

Keep changes focused on documented HookReplay behavior and use synthetic, secret-free test data.

## Prerequisites and validation

Install the .NET SDK version in `global.json`. The canonical developer commands are in `AGENTS.md`:

```text
dotnet restore KeelMatrix.HookReplay.sln --configfile NuGet.config
dotnet build KeelMatrix.HookReplay.sln -c Release --no-restore
dotnet test KeelMatrix.HookReplay.sln -c Release --no-build
dotnet format KeelMatrix.HookReplay.sln --verify-no-changes
dotnet pack src/KeelMatrix.HookReplay/KeelMatrix.HookReplay.csproj -c Release --no-build --include-symbols --output artifacts/packages
```

Run the package-consumer smoke after packing. Update the README, compatibility policy, changelog, or XML documentation whenever behavior or user-facing contracts change. Add a regression test for behavior changes.

Security reports must use the private channels in [SECURITY.md](SECURITY.md), not a public issue. Public API changes require a reviewed update to the shipping/unshipped API baseline beside the shipping project.

Do not commit credentials, real customer data, generated build output, or local telemetry configuration.
