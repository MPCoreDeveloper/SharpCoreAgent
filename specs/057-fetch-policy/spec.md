# Feature Specification: Web policy

**Feature**: `057-fetch-policy`
**Status**: implemented 2026-11-04

## What ships

- `fetch_url` arguments: `allow_cookies`, `ignore_robots`, `robots_fail_open`, `use_browser`, `timeout_ms`.
- Pure fetch: HTTP GET only; no JavaScript.
- Cookies off by default; when `allow_cookies` is true, a per-call, host-keyed jar sends and receives `Cookie`/`Set-Cookie`; no cross-host leakage.
- robots.txt: fetched once per origin, cached for one hour, `User-agent` and `*` groups, longest matching Allow/Disallow.
- 404/410 robots = empty; 401/403/5xx and transport failures fail closed unless `robots_fail_open`.
- `use_browser: true` requires `--browser`; absent is an explicit refusal.
- Fixed UA: `sharpcoreagent/1.0 (+https://github.com/MPCoreDeveloper/SharpCoreAgent)`.

## Non-goals

- Persisting cookies across calls.
- Browser spoofing.
- A native browser driver; Playwright MCP remains the browser.

## Success criteria

- A Disallow refuses before the page request.
- 500 robots refuses; `robots_fail_open` allows.
- `ignore_robots` skips the robots request.
- `use_browser` without `--browser` refuses with `--browser`.
- Full suite, build and evaluation green.
