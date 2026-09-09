# Security Policy

## Reporting a vulnerability

Report suspected vulnerabilities privately by either:

1. emailing **keelmatrix@gmail.com**; or
2. opening a private GitHub Security Advisory for this repository.

Do not create a public issue or publicly post sensitive vulnerability details, credentials, personal data, proprietary payloads, or cassette material. Redact cassette contents before sharing them privately, and include only the smallest useful reproduction.

Include, when available:

- affected package version and target runtime/framework;
- minimal reproduction steps or proof of concept;
- security impact and affected behavior;
- suggested mitigation or patch information;
- whether the report affects recorded cassettes or persisted data.

## Supported versions

Security fixes are prioritized for the latest maintained HookReplay release line and its supported `net8.0` and `netstandard2.0` targets. Older versions may receive fixes on a case-by-case basis. Unsupported runtimes and end-of-life release lines are not guaranteed security updates.

The maintainers will acknowledge and investigate reports on a best-effort basis. Do not use the security contact for Code of Conduct concerns; conduct reporting instructions are in [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

## Dependency vulnerability audit

Run the same fail-closed direct and transitive audit used by CI:

```powershell
pwsh -NoProfile -File scripts/CheckVulnerabilities.ps1
```

The audit command must complete and return machine-readable JSON. Any reported vulnerability fails the gate unless it is matched exactly by a reviewed entry in [`docs/dependency-vulnerability-exceptions.json`](docs/dependency-vulnerability-exceptions.json). Each exception must document the package ID, advisory URL, scope, reason, and mitigation; incomplete or broad exceptions are rejected by the audit script. Exceptions are temporary risk records, not a substitute for remediation.
