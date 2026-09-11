# OctetLedger

OctetLedger is a lightweight, privacy-friendly network traffic statistics tool for Windows.
It reads operating-system counters instead of capturing packets, then stores counter differences
in a local SQLite database.

## Current commands

```powershell
octetledger                       # Current counters for active interfaces
octetledger interfaces            # Useful Windows interfaces
octetledger interfaces --all      # Include filter-driver bindings
octetledger live                  # Live download and upload rates
octetledger collect               # Store one sample
octetledger monitor               # Collect continuously every 60 seconds
octetledger daily                 # Daily totals for the last 30 days
octetledger monthly               # Monthly totals for the last 12 months
octetledger status                # Database and collection status
octetledger version
octetledger help
```

The first `collect` or `monitor` sample creates a baseline. Traffic is recorded from the next
sample onward. Data is stored in `%LOCALAPPDATA%\OctetLedger\octetledger.db`.

## Install the command

For a packaged release on another PC:

1. Download and extract `OctetLedger-<version>-win-x64.zip`.
2. Double-click `install.cmd`.
3. Open a new PowerShell or Command Prompt window.
4. Run `octetledger`.

When working from the source repository, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1
```

Open a new terminal and run `octetledger`. The installer copies the executable to
`%LOCALAPPDATA%\Programs\OctetLedger` and adds that directory to the user `PATH`.
No administrator privileges are required. To remove the command while preserving collected data:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1
```

## Install the command

After publishing, install the command for the current Windows user:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

Open a new terminal and run `octetledger`. No administrator rights are required. To remove the
installed command without deleting the traffic database, run `scripts\uninstall.ps1`.

## Build from source

The project targets .NET 10:

```powershell
dotnet build --configuration Release
dotnet test --configuration Release
dotnet publish src/OctetLedger.Cli --configuration Release --runtime win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:DebugType=None `
  --output artifacts/win-x64
```

## Privacy

OctetLedger stores only interface identifiers, names, byte counters, and collection timestamps.
It does not record visited sites, IP addresses, packet contents, or application activity.

## Project status

The repository remains private while the initial storage format, background service behavior,
and license are being designed.
