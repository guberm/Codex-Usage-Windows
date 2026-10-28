Local refresh diagnostics, without telemetry.

- Records refresh source, UTC timestamp, duration, outcome, safe HTTP status/error type, and previous/current limit percentages and reset times.
- The journal stays in `%LOCALAPPDATA%\CodexUsage` and is bounded to `activity.jsonl` plus one rotated file (`activity.1.jsonl`), at most 1 MiB each.
- It excludes tokens, authorization headers, device codes, account identifiers, reset request IDs, and raw API response bodies. Logging failures do not interrupt refreshes; Sign out deletes both journal files.

Download `Codex-Usage-Windows-v0.1.2-win-x64.exe` and verify it with `SHA256SUMS.txt`. The self-contained x64 executable requires no separate .NET installation and is unsigned.

Validation: Windows dependency-free checks and the published-executable WPF smoke test run in `build.ps1` and CI. Live account access and reset-credit consumption were not part of release validation.
