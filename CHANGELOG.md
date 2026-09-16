# Changelog


## 0.6.2

- Show the interface name on every dashboard row so Wi-Fi and Ethernet traffic from the same day are distinguishable.
- Aggregate dashboard charts, daily averages, and peak-day values by calendar day across physical interfaces.

## 0.6.1

- Keep separate rows for every physical Wi-Fi/Ethernet interface in default stored reports, so switching interfaces cannot hide earlier traffic.
- Continue excluding VPN and virtual adapters by default to avoid duplicate transport traffic; `--all` still exposes every adapter.
- Report the exact longest collection interval, interface, and period instead of only emitting a generic gap notice.

## 0.6.0

### Reliability

- Rebaseline an interface without recording traffic when either Windows counter resets or an observation timestamp moves backward.
- Preserve exact totals and real elapsed intervals across multi-day collection and resume-from-sleep gaps.
- Reject healthy but unrelated SQLite files during import instead of accepting any valid SQLite database.
- Tag databases with an OctetLedger `application_id`, reject newer unsupported schemas without modifying them, and dispose failed database connections cleanly.
- Centralize interface and application table creation in one transactional, versioned schema manager.

### Windows and release engineering

- Build and execute the ARM64 binary on a native GitHub-hosted Windows ARM64 runner.
- Execute every release binary on its target architecture before packaging.
- Reject incomplete Authenticode secret configuration, timestamp over HTTPS, check signing command exit codes, and remove temporary certificates even after failure.

### User experience

- Add focused `help <command>` topics and a task-oriented top-level help screen.
- Expand `status` with data mode, database integrity, retention, and automatic-backup state.
- Make portable data-directory collection explicit and prevent it from accidentally controlling the registered per-user collector.
- Label elevated ETW per-application totals as explicit-window estimates, track process lifecycle events, group unresolved identities safely, and support Ctrl+C capture completion.
- Add a clearer project introduction, release/download badges, quick start, synthetic dashboard preview, and a reusable public launch kit.

## 0.5.0

- Added installation diagnostics, safe self-update with rollback, budgets, comparisons, projections, retention, automatic backups, portable data directories, the local dashboard, and opt-in per-application ETW monitoring.
