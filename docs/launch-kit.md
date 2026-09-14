# OctetLedger 0.6.0 launch kit

## One-sentence description

OctetLedger is a privacy-first, vnStat-style bandwidth monitor for Windows that keeps useful traffic history in a local SQLite database without capturing packets.

## GitHub repository metadata

Use this description in the repository **About** field:

> Privacy-first vnStat-style bandwidth monitor for Windows — local history, budgets, reports, dashboard, backups, and safe updates.

Use these GitHub topics:

`windows`, `network-monitoring`, `bandwidth-monitor`, `traffic-statistics`, `vnstat`, `dotnet`, `sqlite`, `privacy`, `cli`

Set the website field to the latest release:

`https://github.com/Roman-Cuisset/octet-ledger/releases/latest`

## English launch post

### Title

OctetLedger 0.6.0 — a privacy-first vnStat-style bandwidth monitor for Windows

### Body

I built OctetLedger because Windows did not have the small, local, long-term network usage tool I wanted from vnStat.

It samples Windows interface counters once per minute instead of capturing packets. It stores counter differences in a local SQLite database and does not record visited sites, IP addresses, or packet contents.

Version 0.6.0 includes:

- hourly, daily, weekly, monthly, and top-usage reports;
- monthly budgets, warnings, comparisons, and projections;
- a read-only dashboard bound only to `127.0.0.1`;
- JSON and CSV output;
- integrity checks, retention, backups, import, and restore;
- checksum-verified updates with rollback;
- native x64 and ARM64 Windows builds;
- optional, explicit ETW-based per-application estimates.

The normal collector runs per-user and does not require Administrator rights. Everything works offline except update checks.

Project and downloads: https://github.com/Roman-Cuisset/octet-ledger

I would especially value feedback about laptops resuming from sleep, VPN/interface selection, ARM64 machines, and long-running collection.

## French launch post

### Title

OctetLedger 0.6.0 — un équivalent de vnStat, local et respectueux de la vie privée, pour Windows

### Body

J’ai créé OctetLedger parce qu’il manquait à Windows un petit outil de suivi réseau historique comparable à vnStat.

Il lit les compteurs d’interfaces Windows une fois par minute, sans capturer les paquets. Les différences de compteurs sont enregistrées dans une base SQLite locale. Aucun site visité, adresse IP ou contenu réseau n’est conservé.

La version 0.6.0 propose des rapports horaires à mensuels, des budgets et projections, un tableau de bord local, des exports JSON/CSV, des sauvegardes, la restauration, la rétention des données et des mises à jour vérifiées avec rollback. Des exécutables Windows x64 et ARM64 sont disponibles.

Projet et téléchargements : https://github.com/Roman-Cuisset/octet-ledger

Les retours les plus utiles concernent la sortie de veille, les VPN, le choix d’interface, Windows ARM64 et la collecte pendant plusieurs jours.

## Where to announce it

Publish separately and participate in each discussion; do not paste the same message everywhere at once.

1. **GitHub Release** — canonical technical notes and downloads.
2. **WinGet** — the most important discovery channel for Windows users; update the manifest immediately after release.
3. **Reddit** — `r/windows`, `r/Windows11`, and `r/software`; lead with the privacy model and a real dashboard screenshot.
4. **Hacker News** — use `Show HN: OctetLedger – a privacy-first vnStat for Windows` and keep the post factual.
5. **DEV Community / Hashnode** — publish a short technical article about reliable counter deltas, sleep gaps, SQLite retention, and update rollback.
6. **Mastodon / Bluesky / X** — use the one-sentence description, one screenshot, and the release link.
7. **Relevant GitHub discussions** — answer existing Windows bandwidth-monitor questions when OctetLedger directly solves the request; never mass-post links.

## Launch assets that convert interest into installs

Use the same three assets everywhere:

- a 10–15 second recording: install, `octetledger today`, then dashboard;
- one dashboard screenshot using synthetic data, never personal traffic history;
- a compact terminal screenshot showing `doctor`, `today`, and `budget status`.

The message should always explain the difference in one line: **local counter history, not packet inspection**.

## Release-day sequence

1. Publish and verify the GitHub release assets and SHA-256 files.
2. Submit or update the WinGet manifest.
3. Set the GitHub description, website, and topics above.
4. Add the synthetic dashboard screenshot to the README.
5. Publish the English launch post on one technical channel and the French post on one French-speaking channel.
6. Answer installation reports and convert recurring problems into `doctor` checks or documentation fixes.
7. One week later, publish measured facts: downloads, confirmed architectures, fixed issues, and resource usage. Avoid vanity claims.
