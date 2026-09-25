# Privacy Policy

**OctetLedger** is designed from the ground up as a private, offline-first network traffic monitor for Windows.

## Data Collection and Storage

- **Offline-only**: OctetLedger never transmits network statistics, telemetry, crash reports, or analytics to external servers. All data remains strictly on your local machine under your Windows user profile (`%LOCALAPPDATA%\OctetLedger`).
- **No Content Inspection**: Normal monitoring records only Windows network interface counter differences (bytes received and bytes sent), timestamps, and interface identifiers. It does not inspect, capture, or log visited websites, domain names, IP addresses, ports, or packet contents.
- **Per-Application Monitoring (Opt-in)**: Per-application byte estimates are captured solely when the user explicitly runs the elevated `apps monitor` command in an Administrator terminal. Only process names and aggregate byte counts are stored for that explicit window.
- **Local Dashboard**: The optional web dashboard (`octetledger dashboard`) binds strictly to `127.0.0.1` (loopback) and never accepts remote network connections.
- **Network Access**: The only outgoing network request OctetLedger ever makes is an optional check for new releases via the GitHub Releases API when update checking is enabled. You can disable this check at any time with `octetledger update disable`.

## Contact and Source Code

The complete source code is public and auditable under the MIT license:
<https://github.com/Roman-Cuisset/octet-ledger>
