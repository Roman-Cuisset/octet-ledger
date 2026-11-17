"""Measure the CLI and local dashboard against an isolated, synthetic SQLite fixture."""

import argparse
import http.client
import json
import socket
import sqlite3
import statistics
import subprocess
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path


ADAPTERS = (
    ("wifi", "Wi-Fi", "Intel Wi-Fi 6 AX201", "Wireless80211"),
    ("ethernet", "Ethernet", "Realtek PCIe GbE Family Controller", "Ethernet"),
    ("vpn", "VPN", "WireGuard Tunnel", "Ethernet"),
)


def timestamp(value):
    return value.strftime("%Y-%m-%dT%H:%M:%S.0000000+00:00")


def seed(cli, directory):
    directory.mkdir(parents=True, exist_ok=False)
    subprocess.run([*cli, "daily", "--json"], check=True, capture_output=True)
    now = datetime.now(timezone.utc).replace(second=0, microsecond=0)
    start = now - timedelta(days=90)
    minutes = [timestamp(start + timedelta(minutes=index)) for index in range(90 * 1440)]
    with sqlite3.connect(directory / "octetledger.db") as database:
        database.executemany(
            "INSERT INTO adapter_state VALUES (?, ?, ?, ?, 0, 0, ?, 1)",
            [(*adapter, timestamp(now)) for adapter in ADAPTERS],
        )
        database.executemany(
            "INSERT INTO traffic_minute VALUES (?, ?, ?, ?, 60, ?, 60)",
            ((adapter[0], minute, 2000 + index % 90, 700 + index % 8, 80)
             for adapter in ADAPTERS for index, minute in enumerate(minutes)),
        )
        archived = [timestamp(start.replace(hour=0, minute=0) - timedelta(days=index))
                    for index in range(1, 1001)]
        database.executemany(
            "INSERT INTO traffic_archive_day VALUES (?, ?, 200000, 90000, 86400, 80, 60)",
            ((adapter[0], day) for adapter in ADAPTERS for day in archived),
        )
        database.executemany(
            "INSERT INTO collection_sample (captured_utc, measured_intervals, zero_intervals, no_active_interfaces) "
            "VALUES (?, 3, 0, 0)", ((minute,) for minute in minutes),
        )
        database.execute("INSERT INTO collection_state VALUES (1, ?, ?, ?)",
                         (archived[-1], timestamp(now), minutes[0]))


def measure(action, count, warmups=2):
    for _ in range(warmups):
        action()
    readings = []
    for _ in range(count):
        start = time.perf_counter_ns()
        action()
        readings.append(round((time.perf_counter_ns() - start) / 1e6, 2))
    return {"median_ms": round(statistics.median(readings), 2), "samples_ms": readings}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable", type=Path)
    parser.add_argument("fixture", type=Path)
    parser.add_argument("--seed", action="store_true", help="Create the fixture in a new directory")
    parser.add_argument("--samples", type=int, default=9)
    args = parser.parse_args()
    if args.samples < 1:
        parser.error("--samples must be positive.")
    cli = [str(args.executable.resolve(strict=True)), "--data-dir", str(args.fixture.resolve())]
    if args.seed:
        seed(cli, args.fixture)
    if not (args.fixture / "octetledger.db").is_file():
        parser.error("The fixture is missing. Use --seed with a new directory.")

    results = {}
    for report, *options in (("hourly", "--hours", "24"), ("daily", "--days", "30"),
                             ("weekly", "--weeks", "12"), ("monthly", "--months", "12"), ("total",)):
        results[report] = measure(lambda: subprocess.run(
            [*cli, report, *options, "--json"], capture_output=True, check=True, timeout=30), args.samples)

    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    with subprocess.Popen([*cli, "dashboard", "--no-open", "--port", str(port)],
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE) as dashboard:
        connection = http.client.HTTPConnection("127.0.0.1", port, timeout=10)

        def read(path):
            connection.request("GET", path)
            response = connection.getresponse()
            if response.status != 200:
                raise RuntimeError(f"{path}: HTTP {response.status} {response.read()!r}")
            return response.read()

        try:
            for _ in range(100):
                try:
                    read("/")
                    break
                except ConnectionRefusedError:
                    connection.close()
                    if dashboard.poll() is not None:
                        raise RuntimeError(dashboard.stderr.read().decode(errors="replace"))
                    time.sleep(0.05)
            else:
                raise RuntimeError("The dashboard did not listen on loopback.")
            results["dashboard_html"] = measure(lambda: read("/"), args.samples)
            results["dashboard_data"] = measure(lambda: read("/api/data"), args.samples)
            results["dashboard_payload_bytes"] = len(read("/api/data"))
        finally:
            connection.close()
            dashboard.terminate()
    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
