## v0.1.6

- **Banked resets now show when they expire.** The credits row reads the reset details and displays the next expiration as a local date and time (for example, "2 banked resets · next expires 6/20/2026 8:30 AM"). Only available resets count, the earliest expiration is shown, and a failed details call degrades to the plain count.

## v0.1.5

- **A limit reset now raises a notification.** When a limit's reset time passes, Codex Usage tells you the window reset and how much allowance is back (for example, "Codex · Weekly reset · 100% available"). Uses the existing notification switch; stale readings (older than a day) do not fire it.

## v0.1.4

- **Added 1-minute and 3-minute auto-refresh intervals.** The panel selector now offers 1, 3, 5, 10, 15, 30 or 60 minutes; the default is still 5.

## v0.1.3

- **Auto-refresh interval is now user-selectable** in the panel: 5, 10, 15, 30 or 60 minutes. Default is 5 minutes (previously a fixed 15).
- **Removed the floating-window on/off toggle.** The pill is always shown; collapsing the details panel returns to tray-only mode. The old `Floating` preference value is ignored and no longer saved.

## v0.1.2

Local refresh diagnostics, without telemetry.

- Records refresh source, UTC timestamp, duration, outcome, safe HTTP status/error type, and previous/current limit percentages and reset times.
- The journal stays in `%LOCALAPPDATA%\CodexUsage` and is bounded to `activity.jsonl` plus one rotated file (`activity.1.jsonl`), at most 1 MiB each.
- It excludes tokens, authorization headers, device codes, account identifiers, reset request IDs, and raw API response bodies. Logging failures do not interrupt refreshes; Sign out deletes both journal files.
