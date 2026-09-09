# Cassette compatibility

HookReplay cassettes are a versioned product contract. The current format is schema version `1` and is serialized as UTF-8 JSON with a final LF, stable property ordering, deterministic collection ordering, and no machine-specific paths.

## Evolution rules

Additive, backwards-compatible changes may add optional fields with safe defaults. Existing fields must keep their meaning, matching semantics, sanitization guarantees, and deterministic serialization behavior. A reader may ignore an optional field only when doing so cannot change replay safety or request identity.

Increment `schemaVersion` when a field changes meaning, an existing field becomes required, matching or consumption semantics change, persisted values need a different interpretation, or a reader cannot safely preserve the current security invariants. A schema change must have explicit read behavior or migration guidance; it must not be silently inferred from file contents.

HookReplay rejects a cassette with a future schema version using `HookReplayUnsupportedCassetteVersionException`. It does not partially interpret or rewrite that cassette.

## Determinism and security

Equivalent logical recordings use UTF-8, LF line endings, stable JSON property order, canonical query/header collection order, and path-independent values. Sanitization happens before any cassette bytes are written. Authorization, cookie, token, API-key-like, and other protected values must never be persisted as raw durable data, and mismatch diagnostics must not reveal them.

## Compatibility fixtures and changelog

Every schema change must add a fully synthetic, secret-free compatibility fixture and tests that read and replay it. Tests must prove the fixture is not rewritten accidentally and that current serialization remains deterministic. Any compatibility-affecting change must be described in `CHANGELOG.md`, including migration or unsupported-version behavior when applicable.
