## v0.1.3

- **Auto-refresh interval is now user-selectable** in the panel: 5, 10, 15, 30 or 60 minutes. Default is 5 minutes (previously a fixed 15).
- **Removed the floating-window on/off toggle.** The pill is always shown; collapsing the details panel returns to tray-only mode. The old `Floating` preference value is ignored and no longer saved.

## v0.1.2

Local refresh diagnostics, without telemetry.

- Records refresh source, UTC timestamp, duration, outcome, safe HTTP status/error type, and previous/current limit percentages and reset times.
- The journal stays in `%LOCALAPPDATA%\CodexUsage` and is bounded to `activity.jsonl` plus one rotated file (`activity.1.jsonl`), at most 1 MiB each.
- It excludes tokens, authorization headers, device codes, account identifiers, reset request IDs, and raw API response bodies. Logging failures do not interrupt refreshes; Sign out deletes both journal files.
