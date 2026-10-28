# Codex Usage refresh logging design

Date: 2026-10-28

## Intent

Diagnose sudden usage-percentage changes and refresh failures in Codex Usage for Windows, without telemetry or exposing authentication material.

## Chosen approach

Use a small local JSON Lines log under the app's existing data directory. The user chose to include refresh cause, result/error, previous/current percentages, and reset times, with limited rotation and no secrets.

Alternatives considered:
- Windows Event Log: queryable centrally, but adds permissions and makes a portable tray app harder to inspect.
- Console/debug output: disappears when the tray app is launched normally and is not useful to the user.

## Scope

- Record one structured completion/failure event per usage fetch, including UTC timestamp, trigger (`startup`, `manual`, `timer`, `unlock`, `resume`, `network-restored`, `sign-in`, or reset pre/post-check), elapsed time, outcome, HTTP status/error type when known, and previous/current limit snapshots (`name`, remaining percent, reset timestamp, window duration).
- Keep logs local at `%LOCALAPPDATA%\CodexUsage`; retain a current file and one rotated file, each capped at 1 MiB (about 2 MiB maximum total).
- Keep logging best-effort: a logging I/O failure must not prevent refreshing or displaying usage.
- Delete log files on Sign out so values from one account are not retained when credentials/cache are cleared. Demo/smoke already use temporary storage and must not create persistent user logs.
- Add dependency-free checks for structured fields, rotation, and sign-out cleanup; update README privacy/storage notes.

## Privacy and non-goals

Never log access/refresh/ID tokens, authorization headers, device codes, account IDs/emails, reset request IDs, or raw HTTP bodies. Do not add telemetry, a log viewer/export workflow, or install/update the running app on the Windows PC as part of this change.

## Bug-review findings (not included as behavior changes here)

- `MainWindow.Refresh` returns immediately when `busy`; unlock/resume/network refresh triggers queued during an active request are dropped. Consider coalescing one pending refresh after the current request.
- Limit selection and change notification compare by list position (`WindowIndex` and `Windows[0]`). If API ordering changes, the compact pill can silently switch limits and report a misleading delta. A stable limit identity is the durable fix; confirm the API has an adequate identifier before changing the model.
- Periodic refresh cadence is measured from the last attempt, not the last successful fetch. A failed request can leave stale data until the next 15-minute interval unless an unlock/network event occurs.

These are source-level findings, not live-account reproductions. Keep them separate from logging unless explicitly approved.

## Acceptance

- Windows unit checks and application build pass.
- Tests prove a fetch success and failure are recorded without secrets, rotation remains bounded, and Sign out removes both log files.
- Existing UI smoke test may be run only in demo/temp mode; do not launch the live account app or install the new binary without separate approval.
