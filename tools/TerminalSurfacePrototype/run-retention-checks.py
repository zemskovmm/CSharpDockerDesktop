#!/usr/bin/env python3
"""Run fixed native macOS fixtures and sample RSS; no payload or generic browser control."""
import argparse
import json
import pathlib
import platform
import hashlib
import subprocess
import time

WORKLOADS = ("aggregate-sustained", "aggregate-cycles", "combined", "lifecycle", "cutoff", "normal", "sustained", "combining", "width-cache", "width-cache-long", "stock-links", "links", "stock-alternate", "alternate", "attributes", "osc", "dcs", "csi", "a11y", "a11y-boundaries")


def webkit_processes():
    result = subprocess.run(["ps", "-axo", "pid=,rss=,comm="], check=True, capture_output=True, text=True)
    processes = {}
    for line in result.stdout.splitlines():
        parts = line.strip().split(maxsplit=2)
        if len(parts) == 3 and "/com.apple.WebKit." in parts[2]:
            processes[int(parts[0])] = {"pid": int(parts[0]), "rssKiB": int(parts[1]), "executable": parts[2].rsplit("/", 1)[-1]}
    return processes


def rss(pid):
    result = subprocess.run(["ps", "-o", "rss=", "-p", str(pid)], capture_output=True, text=True)
    return int(result.stdout.strip()) if result.stdout.strip() else None


def memory_tools(output, stage, pids, requested_seconds, started):
    observations = []
    # VM region/category summaries only; no heap dumps, memory contents or forced GC.
    for pid in pids:
        for tool, arguments in (("vmmap", ["-summary", str(pid)]), ("footprint", ["-p", str(pid)])):
            capture_seconds = round(time.monotonic() - started, 2)
            try:
                result = subprocess.run([tool, *arguments], capture_output=True, text=True, timeout=10)
                (output / f"{stage}-{pid}-{tool}.txt").write_text(
                    f"exitCode={result.returncode}\n" + result.stdout + result.stderr)
                observations.append({"pid": pid, "tool": tool, "captureSeconds": capture_seconds,
                                     "exitCode": result.returncode})
            except (OSError, subprocess.TimeoutExpired) as error:
                (output / f"{stage}-{pid}-{tool}.txt").write_text(type(error).__name__)
                observations.append({"pid": pid, "tool": tool, "captureSeconds": capture_seconds,
                                     "failure": type(error).__name__})
    (output / f"{stage}-capture.json").write_text(json.dumps({"requestedStageSeconds": requested_seconds,
        "observations": observations}, indent=2) + "\n")


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
    conditions = {"platform": platform.platform(), "machine": platform.machine(),
                  "samplingSeconds": 1, "wallClock": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
                  "binarySha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
                  "assemblySha256": hashlib.sha256((binary.parent / "TerminalSurfacePrototype.dll").read_bytes()).hexdigest(),
                  "attribution": "New WebKit PIDs since per-workload baseline; launchd parentage prevents definitive ownership. No unrelated browser launch intentionally performed.",
                  "forcedCollection": False}
    for name, command in (("os", ["sw_vers"]), ("hardware", ["sysctl", "hw.model", "hw.memsize", "machdep.cpu.brand_string"])):
        conditions[name] = subprocess.run(command, capture_output=True, text=True).stdout
    (args.output / "conditions.json").write_text(json.dumps(conditions, indent=2) + "\n")
    failed = False
    for workload in args.workload or WORKLOADS:
        previous = set(webkit_processes())
        started = time.monotonic()
        log = args.output / f"{workload}.log"
        with log.open("w") as out, (args.output / f"{workload}-rss.jsonl").open("w") as samples:
            process = subprocess.Popen([str(binary), "--check-retention", workload], stdout=out, stderr=subprocess.STDOUT)
            seen_stages = set()
            try:
                while process.poll() is None:
                    # XPC processes have launchd as parent. New PIDs are candidates,
                    # NOT definitive attribution if another WebKit app starts concurrently.
                    candidates = [value for pid, value in webkit_processes().items() if pid not in previous]
                    samples.write(json.dumps({"seconds": round(time.monotonic() - started, 2), "appPid": process.pid, "appRssKiB": rss(process.pid), "newWebKitCandidates": candidates}) + "\n")
                    samples.flush()
                    if workload.startswith("aggregate-"):
                        # Collect native region evidence at matched stages, not every RSS poll.
                        for line in log.read_text().splitlines():
                            if not line.startswith("RETENTION "):
                                continue
                            stage = json.loads(line[10:])["stage"]
                            if stage in ("aggregate-empty-baseline", "cycle-01-populated", "cycle-01-post-close", "cycle-01-post-idle", "cycle-12-populated", "cycle-12-post-idle") and stage not in seen_stages:
                                seen_stages.add(stage)
                                memory_tools(args.output, workload + "-" + stage, [process.pid] + [value["pid"] for value in candidates], json.loads(line[10:])["elapsedSeconds"], started)
                    deadline = 840 if workload == "aggregate-sustained" else 660
                    if time.monotonic() - started > deadline:
                        process.terminate()
                        raise TimeoutError("Fixed synthetic runner exceeded its named finite deadline.")
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
