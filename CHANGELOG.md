# Changelog


## 0.9.0

### Dashboard redesign

- Redesign the web dashboard with a modern dark theme, gradient accents, and improved visual hierarchy.
- Split chart bars into stacked download (blue) and upload (purple) with hover tooltips showing per-day breakdown.
- Add collapsible sections for collection health, observations, interruptions, and events to reduce page length.
- Add cards with colored accent borders and tinted metric values for total, daily average, and peak day.
- Add status bar with colored health indicator dot reflecting collector state and consecutive errors.
- Add timeline visualization with colored dots for interruptions and collector events.
- Improve table readability with right-aligned numeric columns and hover highlights.
- Improve responsive layout for narrow viewports.

## 0.8.0

### Visibility and collection health

- Add `octetledger history` subcommand displaying next expected collection time, tracking start, raw observation detail boundaries, and interruption records with factual reason codes.
- Distinguish measured zero traffic from unobserved intervals and post-interruption traffic volume across CLI reports and the web dashboard.
- Expose explicit `Precision` (`counter intervals`, `daily archives`, `mixed`) on report rows, JSON, and CSV exports.
- Redesign the web dashboard with collection health indicators, striped bars for unmeasured intervals, and a dedicated interruptions timeline.

### Bounded logging and local diagnostics

- Centralize bounded structured JSON logging (`collector.log` <= 8 KiB active with repeat deduplication, rotating across `.log.1`, `.log.2`, `.log.3`).
- Route launcher errors through native `collector log` command to keep all log writers strictly bounded.
- Add `octetledger diagnostics preview` and `octetledger diagnostics export <path>` for privacy-safe local support reports without network transmission or private identifiers.

### Release gates and transition validation

- Gate releases on real native installation lifecycle validation on GitHub Actions runners before packaging artifacts.
- Add safe Windows transition testing (`scripts/test-windows-transition.ps1`) with two-phase `--prepare` / `--verify` across Wi-Fi/Ethernet, VPN, sleep, and reboot.
- Add local IPv6 ETW monitoring probe (`scripts/test-windows-ipv6.ps1`) and automated deterministic network transition tests.

## 0.7.1

### Collector reliability

- Add an independent per-user scheduled watchdog for missing workers, missing launchers, and workers with no successful collection for more than three minutes.
- Replace the semaphore with a directory-scoped exclusive file lock that Windows releases after a forced process termination.
- Keep deliberate stops persistent across supervision checks; preserve the last successful collection timestamp and require fresh readiness when starting.
- Isolate startup registrations and watchdog tasks by data directory; preserve the installation's `LOCALAPPDATA` in both generated scripts.
- Schedule the watchdog's first timer in the future with an explicit UTC boundary so task replacement does not miss its initial trigger.

### Correctness and recovery

- Reject non-finite live/monitor intervals and invalid or repeated global data-directory options without crashing.
- Serialize settings mutations and preserve unrelated concurrent changes; reject malformed settings without overwriting the original file.
- Preserve committed WAL traffic during database restoration; reject invalid schemas and orphaned traffic without replacing a healthy destination.
- Archive retention by local calendar day, handle daylight-saving boundaries, and exclude daily archives from hourly reports.
- Respect explicit virtual-interface preferences, select active zero-byte adapters, and keep every period when falling back to a historical interface.
- Rebaseline both traffic-rate directions when either interface counter resets.
- Reject overflowing or rounded-to-zero budgets; report undefined comparison changes when previous usage is zero.
- Render dashboard interface names as text, use server-local calendar dates, distinguish missing history from zero traffic, and show API failures and actual interface scope.
- Keep low-traffic physical interfaces visible in diagnostics and report stopped or delayed collectors as unhealthy.
- Handle malformed update metadata and terminate version-probe children on cancellation or timeout.
- Include IPv6 ETW traffic events and surface capture failures without leaving the command waiting for its configured duration.
- Validate rollback candidates before interruption; retain executable recovery backups on restoration failure and clean failed fresh installations.
- Fix executable restoration under Windows PowerShell by using a real backup path instead of a null string argument to `File.Replace`.

## 0.7.0

### Clarity

- Label `summary` and `interfaces` output as live Windows counters that can reset independently of OctetLedger history.
- Describe `live` as measuring rate between Windows counter reads.
- Print the data source, scope, and included interfaces at the top of stored reports (`today`, `daily`, etc.).
- Show interface scope, last collection time, generation time, and partial-day markers in the dashboard.
- Add an explanatory footer to the dashboard distinguishing per-day chart totals from per-interface table rows.

### Diagnostics

- `status` now shows current Windows interfaces alongside OctetLedger stored interface history, making it clear which adapters have recorded data and how the two data sources differ.
- `doctor` lists physical interfaces with their Windows counters and OctetLedger history, reports the last collection time, and suggests diagnostic commands when problems are found.
- Show whether the default interface was selected automatically or saved by user.

### Help and documentation

- Reorganize top-level help around three tasks: see current rate, see past usage, check collection health.
- Add help topics for `status`, `interfaces`, `live`, and `summary` explaining their data source.
- Group README commands by data source (Windows counters vs OctetLedger history).

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
