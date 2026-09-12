# Changelog

All notable changes to HookReplay are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

### Fixed

- Redact URI path segments before they become matching identity, custom-matcher input, or cassette data, including percent-encoded values.
- Dispose superseded request and response content after capture so replaced content is neither leaked nor reused, including on capture and persistence failures.
- Honor a supported declared text `charset` instead of decoding and replaying every text body as UTF-8; unsupported declared charsets fail closed.
- Do not synthesize a response `Content-Type` that was never captured, and do not replay body-length or integrity headers that describe the pre-sanitization body.
- Roll back the in-memory interaction list when a durable cassette append fails, so a failed interaction cannot later become a replayable ghost recording.

### Compatibility

- Cassette schema version 1 now allows `request.bodyContentType` and response `body.contentType` to be `null` when the captured content declared no content type. Cassettes that declare a content type are unaffected, and replay never invents one.

## [0.1.0] - Planned (not yet published)

Initial package contents for the first release candidate. This entry describes the planned `0.1.0` package; it does not indicate that the package has been published.

- Record and replay `HttpClient` exchanges with deterministic, versioned cassettes.
- Enforce replay-only operation without network fallback.
- Sanitize sensitive HTTP material before durable cassette writes.

### Changed

- Sanitize response reason phrases before cassette persistence.
- Keep release metadata aligned with the validated package version.
