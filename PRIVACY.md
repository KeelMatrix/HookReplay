# Privacy

HookReplay uses `KeelMatrix.Telemetry` transitively for minimal anonymous usage telemetry.

## HookReplay-specific behavior

Telemetry is requested only after a successful core operation:

- a recorded exchange has been persisted; or
- an exchange has been replayed successfully from a cassette.

Record attempts that fail, replay misses, package restore, assembly loading, and options construction do not trigger activation telemetry.

HookReplay does not send URLs, hosts, methods, request or response bodies, cassette names or paths, headers, cookies, tokens, query parameters, interaction counts, fingerprints, mismatch details, repository names, or source paths.

The product also enforces secret-safe cassette handling before durable writes. Authorization and proxy authorization values, cookies, set-cookie values, API-key-like credentials, token-like fields, and configured sensitive JSON/form fields are excluded or redacted rather than sent as telemetry or stored as raw cassette data.

## Shared telemetry behavior

The shared telemetry client owns opt-out controls and precedence, delivery, identity and state, event cadence, endpoint, and retention. Its maintained documentation is the source of truth for those details:

- [Telemetry README](https://github.com/KeelMatrix/Telemetry#readme)
- [Telemetry privacy policy](https://github.com/KeelMatrix/Telemetry/blob/main/PRIVACY.md)
