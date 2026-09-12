# Cassette compatibility

HookReplay cassettes are a versioned product contract. The current format is schema version `1` and is serialized as UTF-8 JSON with a final LF, stable property ordering, deterministic collection ordering, and no machine-specific paths.

## Evolution rules

Additive, backwards-compatible changes may add optional fields with safe defaults. Existing fields must keep their meaning, matching semantics, sanitization guarantees, and deterministic serialization behavior. A reader may ignore an optional field only when doing so cannot change replay safety or request identity.

Increment `schemaVersion` when a field changes meaning, an existing field becomes required, matching or consumption semantics change, persisted values need a different interpretation, or a reader cannot safely preserve the current security invariants. A schema change must have explicit read behavior or migration guidance; it must not be silently inferred from file contents.

HookReplay rejects a cassette with a future schema version using `HookReplayUnsupportedCassetteVersionException`. It does not partially interpret or rewrite that cassette.

## Determinism and security

Equivalent logical recordings use UTF-8, LF line endings, stable JSON property order, canonical query/header collection order, and path-independent values. Sanitization happens before any cassette bytes are written. Authorization, cookie, token, API-key-like, and other protected values must never be persisted as raw durable data, and mismatch diagnostics must not reveal them.

URI path segments and query values are redacted before they become `normalizedUri`, matching identity, or custom-matcher input. Path segments are decoded before redaction and re-encoded canonically, so a protected value cannot hide behind percent-encoding and raw and escaped forms normalize identically.

Text bodies are stored as decoded text. The declared `charset` is preserved in the content type and is honored on replay for UTF-8, US-ASCII, ISO-8859-1, UTF-16, and UTF-32; text without a declared charset is UTF-8 and any other declared charset is rejected. A content type is optional: `request.bodyContentType` and response `body.contentType` may be `null` when the captured content declared no content type, and replay never synthesizes one.

Persisted and replayed bodies are the sanitized representation. Headers that describe the exact pre-sanitization body bytes or their transfer framing (`Content-Length`, `Content-MD5`, `Content-Digest`, `Repr-Digest`, `Digest`, and `Transfer-Encoding`) are therefore never persisted or replayed, in message-level and content header collections alike, and replay recalculates the body length.

Appending an interaction is transactional: an interaction becomes part of replay state only after the durable cassette write succeeds, so a failed write cannot leave a ghost recording behind.

## Compatibility fixtures and changelog

Every schema change must add a fully synthetic, secret-free compatibility fixture and tests that read and replay it. Tests must prove the fixture is not rewritten accidentally and that current serialization remains deterministic. Any compatibility-affecting change must be described in `CHANGELOG.md`, including migration or unsupported-version behavior when applicable.
