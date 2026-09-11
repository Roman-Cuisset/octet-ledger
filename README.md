# OctetLedger

OctetLedger is a lightweight network traffic statistics tool for Windows. It reads the counters maintained by Windows itself, so it does not capture packets or inspect their contents.

The project is currently an early prototype. Historical hourly, daily, and monthly statistics will be added in the next milestone.

## Current commands

```powershell
octetledger
octetledger interfaces
octetledger interfaces --all
octetledger live
octetledger live --interface "Wi-Fi" --interval 1
octetledger version
octetledger help
```

- `summary` shows cumulative counters for active interfaces.
- `interfaces` lists the useful Windows network interfaces and removes duplicate filter-driver bindings.
- `interfaces --all` includes every interface exposed by Windows for diagnostics.
- `live` shows the current download and upload rates until Ctrl+C is pressed.

## Build from source

OctetLedger currently requires the .NET 10 SDK.

```powershell
dotnet build --configuration Release
dotnet test --configuration Release
dotnet run --project src/OctetLedger.Cli -- live
```

## Privacy

OctetLedger only records byte counters for network interfaces. It does not record visited sites, IP addresses, packet contents, or application activity.

## Project status

The repository remains private while the initial storage format, service behavior, and license are being designed.
