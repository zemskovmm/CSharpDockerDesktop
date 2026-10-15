#!/usr/bin/env python3
"""Run fixed native macOS fixtures and sample RSS; no payload or generic browser control."""
import argparse
import json
import pathlib
import subprocess
import time

WORKLOADS = ("combined", "lifecycle", "cutoff", "normal", "sustained", "combining", "width-cache", "stock-links", "links", "stock-alternate", "alternate", "attributes", "osc", "dcs", "csi", "a11y")


def webkit_processes():
    result = subprocess.run(["ps", "-axo", "pid=,rss=,comm="], check=True, capture_output=True, text=True)
    processes = {}
    for line in result.stdout.splitlines():
        parts = line.strip().split(maxsplit=2)
        if len(parts) == 3 and "/com.apple.WebKit." in parts[2]:
            processes[int(parts[0])] = {"pid": int(parts[0]), "rssKiB": int(parts[1]), "executable": parts[2].rsplit("/", 1)[-1]}
    return processes


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("app", type=pathlib.Path, help="Published .app bundle")
    parser.add_argument("--output", type=pathlib.Path, default=pathlib.Path("artifacts/terminal-surface/retention"))
    parser.add_argument("--workload", choices=WORKLOADS, action="append")
    args = parser.parse_args()
    binary = args.app.resolve() / "Contents/MacOS/TerminalSurfacePrototype"
    if not binary.is_file():
        parser.error("Publish the native development bundle first.")
    args.output.mkdir(parents=True, exist_ok=True)
    failed = False
    for workload in args.workload or WORKLOADS:
        previous = set(webkit_processes())
        started = time.monotonic()
        log = args.output / f"{workload}.log"
        with log.open("w") as out, (args.output / f"{workload}-rss.jsonl").open("w") as samples:
            process = subprocess.Popen([str(binary), "--check-retention", workload], stdout=out, stderr=subprocess.STDOUT)
            try:
                while process.poll() is None:
                    # XPC processes have launchd as parent. New PIDs are candidates,
                    # NOT definitive attribution if another WebKit app starts concurrently.
                    candidates = [value for pid, value in webkit_processes().items() if pid not in previous]
                    samples.write(json.dumps({"seconds": round(time.monotonic() - started, 2), "appPid": process.pid, "newWebKitCandidates": candidates}) + "\n")
                    samples.flush()
                    if time.monotonic() - started > 660:
                        process.terminate()
                        raise TimeoutError("Fixed synthetic runner exceeded 11 minutes.")
                    time.sleep(1)
            finally:
                if process.poll() is None:
                    process.terminate()
                process.wait(timeout=15)
        passed = process.returncode == 0 and f"RETENTION_CHECK_PASS {workload}" in log.read_text()
        print(f"{workload}: {'PASS' if passed else 'FAIL'} ({time.monotonic() - started:.1f}s)", flush=True)
        failed |= not passed
        # Let the completed app's WebKit helpers leave before the next PID baseline.
        time.sleep(2)
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
