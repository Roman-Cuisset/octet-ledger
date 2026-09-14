# OctetLedger

OctetLedger is a lightweight, privacy-friendly Windows network traffic statistics tool inspired
by vnStat. It reads Windows interface counters instead of capturing packets, and stores only
counter differences in a local SQLite database.

## Install

Download the x64 or ARM64 ZIP from GitHub Releases, extract it, and run `install.cmd`. The installer
needs no administrator privileges, adds `octetledger` to the user PATH, and starts an invisible
collector every 60 seconds through the current user's Windows startup configuration.

Once the WinGet package is accepted, these commands will also work:

```powershell
winget install --id RomanCuisset.OctetLedger --exact
winget install octetledger
```

For a WinGet portable installation, start background collection once with:

```powershell
octetledger collector install
```

## Commands

```powershell
octetledger                              # Primary interface counters
octetledger summary --all                # All active interfaces
octetledger interfaces                   # List interfaces
octetledger interface set "Wi-Fi"        # Save the default interface
octetledger live                         # Real-time rates

octetledger total                        # All recorded traffic since the beginning
octetledger today                        # Today's stored traffic
octetledger hourly --hours 24
octetledger daily --days 30
octetledger weekly --weeks 12
octetledger monthly --months 12
octetledger top --days 10

octetledger daily --json
octetledger monthly --csv report.csv
octetledger daily --all                  # Explicitly include every interface

octetledger collector status
octetledger collector install
octetledger collector stop
octetledger collector start
octetledger collector uninstall

octetledger database check
octetledger database backup
octetledger database backup D:\Backups\octetledger.db
octetledger database restore D:\Backups\octetledger.db
octetledger status
```

Reports select one primary physical interface by default, which avoids silently adding the same
traffic once for Wi-Fi/Ethernet and again for a VPN. Use `--all` only when separate per-interface
rows are wanted; OctetLedger never merges those rows into a misleading grand total.

The first collection creates a baseline. Traffic is recorded from the next sample onward. Data and
settings live in `%LOCALAPPDATA%\OctetLedger`. Uninstalling the program preserves these files.
If a stored report detects that automatic collection is stopped, it prints a warning and the repair
command. Background collection retries transient errors and records them in `collector.log` instead
of silently exiting.

Collector health is based on the latest successful collection, not only on the presence of a
process. `Running, collection delayed` means that the process exists but no collection has
succeeded for more than three minutes. `Running, retrying after errors` exposes repeated failures
while the last success is still recent. The status includes consecutive errors and points to the
log. Process control records the PID, process start time, and executable path, so another
`octetledger` command cannot be stopped as if it were the collector.

Stored traffic is attributed to the time at which the counter difference is observed. OctetLedger
also stores the real elapsed time since the preceding observation. `Peak avg` is therefore the
highest average over an observed interval, not an instantaneous packet-level peak. Reports warn
when an interval exceeds 90 seconds. OctetLedger does not invent a minute-by-minute distribution
for traffic that occurred while the computer was asleep or collection was interrupted, so that
traffic may appear in the hour or day in which collection resumes.

Report interface selection uses interfaces that actually contain data in the requested period. An
explicit `--interface` filter is applied before `top` ranks days. Unknown options, missing option
values, and extraneous arguments return exit code 2.

## Machine-readable output

`--json` writes a JSON array to standard output. Each row contains stable camel-case fields:
`period`, `interfaceId`, `interfaceName`, `bytesReceived`, `bytesSent`, `totalBytes`,
`averageBytesPerSecond`, `peakBytesPerSecond`, and `longestIntervalSeconds`. Byte values are integer
counts; rates and intervals are numeric bytes-per-second and seconds. New fields may be added in
future minor releases, but existing field names and units will not change without a major release.

`--csv` writes the same data with invariant-culture numbers and a UTF-8 byte-order mark. Text cells
that could be interpreted as spreadsheet formulas are prefixed with an apostrophe.

## Backup and restore

Create and verify a consistent SQLite backup:

```powershell
octetledger database backup D:\Backups\octetledger.db
octetledger database check D:\Backups\octetledger.db
```

Restore it with:

```powershell
octetledger database restore D:\Backups\octetledger.db
```

Restore checks the source database, stops the collector when necessary, replaces the active
database atomically, preserves the previous database beside it as
`octetledger.before-restore-YYYYMMDD-HHMMSS-ID.db`, and restarts the collector. Do not copy the live
database manually. Schema changes are versioned with SQLite `user_version`; migrations are applied
automatically and covered by tests.

## Build from source

The project targets .NET 10:

```powershell
dotnet restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
dotnet publish src/OctetLedger.Cli --configuration Release --runtime win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None --output artifacts/win-x64
```

Releases contain self-contained single-file executables for Windows x64 and ARM64. CI executes the
x64 binary and exercises installation, update, rejection of a bad update, collector crash recovery,
and uninstallation. ARM64 is cross-built but is not claimed as runtime-validated until it is tested
on an ARM64 Windows runner or physical machine.

Release signing is enabled automatically when the repository secrets
`WINDOWS_SIGNING_CERTIFICATE_BASE64` and `WINDOWS_SIGNING_CERTIFICATE_PASSWORD` contain a real
code-signing certificate and password. The workflow signs and verifies each executable before
packaging. Without those secrets, releases remain unsigned.

## Privacy

OctetLedger stores interface identifiers, interface names, byte counters, and timestamps. It does
not record visited sites, IP addresses, packet contents, or per-application activity.

## License

OctetLedger is released under the [MIT License](LICENSE).
