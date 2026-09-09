# Privacy

HookReplay uses `KeelMatrix.Telemetry` transitively for minimal anonymous usage telemetry.

## HookReplay-specific behavior

Telemetry is requested only after a successful core operation:

- a recorded exchange has been persisted; or
- an exchange has been replayed successfully from a cassette.

Record attempts that fail, replay misses, package restore, assembly loading, and options construction do not trigger activation telemetry.

Telemetry delivery is best-effort. A telemetry failure cannot change record/replay behavior.

HookReplay does not send URLs, hosts, methods, request or response bodies, cassette names or paths, headers, cookies, tokens, query parameters, interaction counts, fingerprints, mismatch details, repository names, or source paths.

The product also enforces secret-safe cassette handling before durable writes. Authorization and proxy authorization values, cookies, set-cookie values, API-key-like credentials, token-like fields, and configured sensitive JSON/form fields are excluded or redacted rather than sent as telemetry or stored as raw cassette data.

## Opt out

Use the shared telemetry opt-out controls, including `KEELMATRIX_NO_TELEMETRY=1` or `DO_NOT_TRACK=1`, when supported by the installed telemetry package. HookReplay also respects the shared repository opt-out configuration.

The shared telemetry package is the detailed source of truth for event definitions, opt-out precedence, local storage, delivery, endpoint, and retention:

- [Telemetry README](https://github.com/KeelMatrix/Telemetry#readme)
- [Telemetry privacy policy](https://github.com/KeelMatrix/Telemetry/blob/main/PRIVACY.md)
