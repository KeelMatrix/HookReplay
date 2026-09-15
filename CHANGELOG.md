# Changelog

All notable changes to HookReplay are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

## [0.1.0] - 2026-09-15

### Added

* Record and replay `HttpClient` exchanges through an explicit `Record` or `Replay` mode, with Replay guaranteed never to fall back to the network.
* Deterministic, versioned JSON cassettes designed for stable use across Windows, Linux, and macOS.
* Request matching based on HTTP method, normalized URI, and supported-body fingerprint, with opt-in header matching and a custom matcher extension point.
* Secret-safe cassette persistence and sanitized mismatch diagnostics, including built-in protection for common credentials, tokens, cookies, sensitive HTTP data, and supported request and response bodies.
* Bounded handling of empty, text, JSON, and form content with clear failures for unsupported or oversized bodies.
* Sequential replay of repeated identical requests, with explicit support for distinguishing otherwise ambiguous requests through selected headers or custom matching.
* Support for `net8.0` and `netstandard2.0`.
* Minimal best-effort activation and weekly usage telemetry through `KeelMatrix.Telemetry`, with documented opt-out controls and no request, response, or cassette content included in telemetry.
