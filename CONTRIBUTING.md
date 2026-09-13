# Contributing

Keep changes focused on documented HookReplay behavior and use synthetic, secret-free test data.

## Prerequisites and validation

Install the .NET SDK version in `global.json`. The canonical developer commands are in `AGENTS.md`:

```powershell
dotnet restore KeelMatrix.HookReplay.sln --configfile NuGet.config
dotnet build KeelMatrix.HookReplay.sln -c Release --no-restore
dotnet test KeelMatrix.HookReplay.sln -c Release --no-build
dotnet format KeelMatrix.HookReplay.sln --verify-no-changes
dotnet pack src/KeelMatrix.HookReplay/KeelMatrix.HookReplay.csproj -c Release --no-build --include-symbols --output artifacts/packages
pwsh -NoProfile -File scripts/CheckVulnerabilities.ps1
```

Run the package-consumer smoke after packing. Update the README, compatibility policy, changelog, or XML documentation whenever behavior or user-facing contracts change. Add a regression test for behavior changes.

To verify a simulated release tag without publishing, run `pwsh -NoProfile -File scripts/VerifyReleaseVersion.ps1 -Tag v1.2.3`.

Security reports must use the private channels in [SECURITY.md](SECURITY.md), not a public issue. Public API changes require a reviewed update to the shipping/unshipped API baseline beside the shipping project.

Do not commit credentials, real customer data, generated build output, or local telemetry configuration.

## Commit checks

Enable the repository-controlled checks once in each clone:

```bash
git config core.hooksPath .githooks
```

The local hook rejects identity trailers and internal metadata. The history-hygiene workflow is the authoritative backstop and scans every commit reachable from the checked-out refs.

## Release publication

The release workflow creates one validated `.nupkg` and its adjacent `.snupkg`, then pushes the exact `.nupkg` once to NuGet.org. NuGet publishes the adjacent symbol package as part of that paired push, so the workflow intentionally has no separate symbol push.
