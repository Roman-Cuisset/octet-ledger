# Changelog

## 0.11.1

### Collector registration for portable package managers

- Clear the Windows Internet zone mark from the executable when `collector install` or `collector start` registers the collector. The hidden VBScript launcher starts it through ShellExecute, which honored the mark and blocked the worker without a log or PID; WinGet and browser downloads previously failed with `Collector could not be launched`.
- Restrict that removal to the currently running `octetledger.exe` with the official product name and an unchanged on-disk image; arbitrary paths, other products, and files replaced after process start are rejected. Windows security policies remain untouched.
- Resolve portable package alias symlinks when comparing executable paths, so the background worker is recognized, reported ready, and waited for during stop and uninstall instead of discarding its PID file.
- Add regressions for the unblock guard, alternate streams, and portable alias identity.

## 0.11.0

### Security

- Render local interface names, interruption reasons, and collector event kinds as text in dashboard timelines instead of interpreting them as HTML.
- Exercise hostile local data in a real Chromium browser regression test.
- Require the advertised loopback authority, restrict the dashboard to GET and HEAD, and reject cross-origin browser requests. Apply a content security policy and serve HTML, CSS, and JavaScript as separate embedded assets.

### Statistics correctness

- Ignore late or repeated pre-captured collection frames without replacing newer counter baselines, changing interface availability, or regressing collection history.
- Acquire SQLite's write lock before reading current Windows counters in both `collect` and `monitor`, serializing competing collectors while preserving clock rollback and counter-reset handling.
- Add regressions for out-of-order and repeated frames, disconnection history, reset baselines, migrated databases, concurrent writers, and failed acquisition. Preserve the existing database schema and stored history; no retroactive correction of previously inflated totals is attempted.
- Attribute application payload bytes to the UTC hour of each ETW event when a capture spans multiple hours.

### Dashboard measurement fidelity

- Split Trend curves and shaded areas at days without measurements instead of substituting zero or joining unmeasured periods; preserve isolated measured points.
- Bound Catmull-Rom Bézier control points to adjacent measured values, preventing negative traffic and overshoot between samples.
- Distinguish measured zero-byte days, including daily archives without raw observations, from missing measurements and baseline-only observations in all three chart modes.

### Dashboard interruption scope

- Distinguish collection-wide interruptions from selected-interface interruptions with filled and hollow markers, separate tooltip labels, and an explicit legend.
- Ignore interruptions of excluded interfaces when marking the chart, while retaining them in the complete interruption timeline; honour explicit virtual-interface preferences.
- Keep unknown coverage separate from interruptions and classify intervals on half-open day boundaries, including instantaneous clock events.

### Dashboard accessibility and report performance

- Expose chart mode selection and day summaries to assistive technology. Support arrow navigation, Home, End, and Escape, with readable chart labels and contained scrolling on narrow screens.
- Aggregate raw and archived all-time totals per interface before joining adapter metadata. Reuse adapter metadata and classifications and parse stored timestamps using their round-trip format with a fallback for older formats.

### Automatic SQLite retention

- Apply configured raw retention after successful `collect` calls and on the first successful `monitor` collection of each local calendar day.
- Skip repeated maintenance within a running monitor and use indexed SQLite checks on restart; reuse the existing transactional coverage boundary without changing the database schema or settings format.
- Preserve archived traffic and observation aggregates atomically; retry interrupted retention and serialize competing maintenance writers.
- Cover configured retention, same-day restarts, rollback after an interrupted archive, old imported rows, concurrent maintenance, long outages, and both daylight-saving transitions.

### Network interface classification

- Prefer the device/driver description over the user-editable interface name, so hardware renamed with `VPN`, `WAN`, `TAP`, `Virtual`, or an asterisk is not hidden solely by its name.
- Match virtual-driver markers on term boundaries, replace the overly broad `WAN`/`Miniport` exclusions with `WAN Miniport`, and retain detection of known virtual/tunnel drivers.
- Preserve explicit preferences and interface selectors, the single-interface fallback, and unfiltered `--all` reports without rewriting stored interface metadata.

### Installation, rollback, and database restore

- Set a configurable deadline for executable calls during installation and rollback, including recovery calls. Package the shared PowerShell process helper for both architectures.
- Remove the Internet-origin mark from the installed executable after validation while preserving the mark on downloaded files and other named streams.
- Refuse SQLite restores while a client holds the destination open or when journal files remain without the original database. Recover journals left by an interrupted process before preserving the previous database and replacing it.
- Exercise timeouts, rejected updates and rollbacks, and preservation of installed files, settings, and statistics in the Windows lifecycle test.
- Bound the update version check even if a child process keeps an output pipe open after the version process exits.
- Restore the retained version’s collector registration after a failed update and remove newer startup entries when rolling back to a legacy collector.
- Retry the atomic restoration of the working executable when a brief Windows file lock interrupts recovery from a failed rollback.
- Run the .NET test suite on native Windows x64 and ARM64 in the release workflow. Use the supported `apps top --count 100` in live IPv6 validation.
- Start the kernel ETW provider before opening the application traffic event source so `apps monitor` can capture TCP and UDP traffic.
- Prefer installed Chrome for dashboard browser tests and use Edge when Chrome is unavailable.

## 0.10.1

### Virtual adapter filtering & documentation

- Exclude Windows WAN miniports (`WAN Miniport (IP)`, `(IPv6)`, `(Network Monitor)`, etc.), Wi-Fi Direct virtual adapters, and synthesized adapters with asterisk in names from default physical selection.
- Prevent inactive zero-byte virtual adapter rows from cluttering dashboard and daily reports.
- Update README dashboard preview screenshot with the modern v0.10.x interactive SVG chart.

## 0.10.0

### Interactive SVG traffic chart

- Replace static HTML bars with a high-performance interactive SVG chart featuring 3 viewing modes:
  - **Stacked**: Combined download (cyan-blue) and upload (purple) with rounded pill tops.
  - **Side-by-side**: Grouped download and upload bars for direct 1-to-1 comparison per day.
  - **Trend**: Smooth Catmull-Rom spline curves with translucent shaded area fill and day vertices.
- Add dynamic Y-axis scale with 5 reference gridlines and formatted byte units.
- Add quick stats summary bar: Total Download (with percentage), Total Upload (with percentage), Download/Upload ratio, and Peak day.
- Add floating glassmorphism tooltip card with exact download/upload metrics, percentages, observation count, and interruption warnings.
- Replace intrusive full-column warning boxes with clean, discrete interruption indicator dots.
- Highlight current day with bold styling and partial measurement indication.

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
