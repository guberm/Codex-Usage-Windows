# Usage Refresh Logging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add privacy-bounded local diagnostics for usage refreshes so percentage changes and refresh failures can be explained after the fact.

**Architecture:** A small `UsageLog` appends structured JSONL events in the existing app data directory, rotates one bounded backup, and never makes refresh success depend on log I/O. `Storage.ClearSession()` removes the account-bound logs; `MainWindow` supplies the refresh trigger and snapshots.

**Tech Stack:** C#/.NET 10, `System.Text.Json`, `System.IO`, existing dependency-free checks.

**Spec:** `docs/superpowers/specs/2026-10-28-usage-refresh-logging-design.md`

## Global Constraints

- Never log access/refresh/ID tokens, authorization headers, device codes, account IDs/emails, reset request IDs, or raw HTTP bodies.
- Keep a current file and one rotated file, each capped at 1 MiB (about 2 MiB maximum total).
- Logging I/O failure must not prevent refreshing or displaying usage.
- Delete logs on Sign out; demo/smoke must use temporary storage only.
- Do not modify the installed app or add behavior fixes for the separately reported findings.

## Review Focus

- Directory/file write denial: refresh still completes and UI remains functional.
- Exception text containing token-shaped data: log stores only safe error type/status, not the message.
- Rotation boundary and oversized record: both retained files stay within the configured bound.
- Sign out with current and rotated logs present: both are deleted.
- Demo/smoke storage: persistent `%LOCALAPPDATA%\CodexUsage` is not touched.

---

### Task 1: Bounded log writer and storage cleanup

**Files:**
- Create: `src/UsageLog.cs`
- Modify: `src/Storage.cs`
- Modify: `tests/Checks.csproj`
- Modify: `tests/Program.cs`

**Interfaces:**
- `UsageLog(string directory)`
- `RecordFetch(string trigger, TimeSpan elapsed, Usage? previous, Usage? current, Exception? error)`
- `RecordSkipped(string trigger, string reason)` for a refresh trigger suppressed while busy
- `Clear()` deletes current and rotated logs
- `Storage.Log` exposes the logger; `Storage.ClearSession()` clears its files

- [ ] **Step 1: Add failing checks** for successful snapshot fields including reset time, failure status/type without exception message leakage, rotation bounds (1 MiB each), best-effort behavior on an unwritable path, and Sign out deleting both log files.
- [ ] **Step 2: Run checks and confirm expected failures**

Run on Windows: `dotnet run --project tests/Checks.csproj -c Release`
Expected: compilation/test failures because `UsageLog` and the new assertions are not implemented.

- [ ] **Step 3: Implement the minimal JSONL writer**

Write one UTC-stamped event with trigger, elapsed time, result, safe error type/HTTP status, and previous/current windows (`name`, `remaining`, `resetAt`, `seconds`). Do not serialize exception messages. Rotate `activity.jsonl` to `activity.1.jsonl` before an append would exceed 1 MiB; discard any single oversized record rather than breaking the size bound. Catch only expected logging I/O/authorization failures so they cannot affect the app.

- [ ] **Step 4: Re-run checks and verify they pass**

Run on Windows: `dotnet run --project tests/Checks.csproj -c Release`
Expected: `PASS: ... checks` including the new logging regressions.

### Task 2: Wire refresh causes and document privacy

**Files:**
- Modify: `src/MainWindow.xaml.cs`
- Modify: `README.md`
- Add to repository: `docs/superpowers/specs/2026-10-28-usage-refresh-logging-design.md`
- Add to repository: `docs/superpowers/plans/2026-10-28-usage-refresh-logging.md`

- [ ] **Step 1: Route every usage fetch through a named trigger** (`startup`, `manual`, `timer`, `unlock`, `resume`, `network-restored`, `sign-in`, `reset-precheck`, `reset-postcheck`) and record its success/failure with elapsed time and before/after snapshots.
- [ ] **Step 2: Record a `busy` skip** when an automatic/manual refresh signal arrives during a fetch; do not change the refresh scheduling behavior in this slice.
- [ ] **Step 3: Update README privacy/storage and Sign out wording** to state what the local log contains and that Sign out deletes it.
- [ ] **Step 4: Run checks and build**

Run on Windows:
- `dotnet run --project tests/Checks.csproj -c Release`
- `dotnet build src/CodexUsage.csproj -c Release -r win-x64`

Expected: both exit 0; no live account requests are made.

### Task 3: Final verification and delivery

**Files:** no additional product files beyond Tasks 1–2.

- [ ] Review the complete diff for secret/log leakage, accidental behavior changes, and scope drift.
- [ ] Run `git diff --check` and verify only the listed paths changed.
- [ ] Do not run/install the live app on the Windows PC; report the static bug findings separately and leave them unfixed.
- [ ] Commit the approved implementation on a feature branch; push and release only after explicit user authorization (granted in the follow-up request).
