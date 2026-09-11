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

Releases contain self-contained single-file executables for Windows x64 and ARM64. Code signing is
not currently applied: doing it correctly requires the maintainer's private code-signing certificate.

## Privacy

OctetLedger stores interface identifiers, interface names, byte counters, and timestamps. It does
not record visited sites, IP addresses, packet contents, or per-application activity.

## License

OctetLedger is released under the [MIT License](LICENSE).
