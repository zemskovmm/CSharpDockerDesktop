#!/usr/bin/env python3
"""Content-free allocation access probe for the fixed native aggregate fixture.

No PID/name override, graph file, stack logging, RSS/category sampling or security
change. Stop inspecting on the first capability failure; allow the fixture to
finish its ordinary joined cleanup and security assertions.
"""
import argparse
import hashlib
import importlib.util
import json
import os
import pathlib
import platform
import selectors
import subprocess
import time

spec = importlib.util.spec_from_file_location("retention", pathlib.Path(__file__).with_name("run-retention-checks.py"))
retention = importlib.util.module_from_spec(spec)
spec.loader.exec_module(retention)
STAGES = ("cycle-01-post-close", "cycle-12-post-close")
MAX_BYTES = 4 * 1024 * 1024


def bounded(command, seconds=6):
    """Bound tool runtime and output without ever creating a memory graph file."""
    started = time.monotonic()
    try:
        process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                   stdin=subprocess.DEVNULL)
    except OSError as error:
        return {"command": command, "exitCode": None, "failure": type(error).__name__,
                "durationSeconds": round(time.monotonic() - started, 3),
                "outputBytes": 0, "output": ""}
    data = bytearray()
    failure = None
    with selectors.DefaultSelector() as selector:
        selector.register(process.stdout, selectors.EVENT_READ)
        while selector.get_map():
            if time.monotonic() - started >= seconds:
                failure = "timeout"
                break
            for key, _ in selector.select(0.05):
                chunk = os.read(key.fileobj.fileno(), 65536)
                if not chunk:
                    selector.unregister(key.fileobj)
                else:
                    data.extend(chunk[:MAX_BYTES - len(data)])
                    if len(data) >= MAX_BYTES:
                        failure = "output-limit"
                        break
            if failure:
                break
    if process.poll() is None and failure:
        process.kill()
    process.wait(timeout=2)
    process.stdout.close()
    return {"command": command, "exitCode": process.returncode, "failure": failure,
            "durationSeconds": round(time.monotonic() - started, 3),
            "outputBytes": len(data), "output": data.decode("utf-8", errors="replace")}


def records(log):
    return [json.loads(line[10:]) for line in log.read_text().splitlines(keepends=True)
            if line.startswith("RETENTION ") and line.endswith("\n")]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("app", type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path, required=True,
                        help="Fresh metadata-only output directory")
    args = parser.parse_args()
    binary = args.app.resolve() / "Contents/MacOS/TerminalSurfacePrototype"
    if platform.system() != "Darwin" or not binary.is_file():
        parser.error("Requires the published native macOS synthetic application.")
    # Inherit the unchanged measurement configuration; refuse hidden allocator
    # instrumentation rather than altering it or silently making unlike runs.
    if any(key.startswith(("Malloc", "DYLD_")) and key != "MallocNanoZone" for key in os.environ):
        parser.error("Remove inherited malloc/DYLD instrumentation before this probe.")
    if os.environ.get("MallocNanoZone") not in (None, "0", "1"):
        parser.error("Unrecognized inherited allocator configuration.")
    args.output.mkdir(parents=True, exist_ok=False)
    summary = {"schema": 1, "workload": "aggregate-cycles", "checkpoints": list(STAGES),
               "platform": platform.platform(), "wallClock": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
               "binarySha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
               "assemblySha256": hashlib.sha256((binary.parent / "TerminalSurfacePrototype.dll").read_bytes()).hexdigest(),
               "attribution": "New WebContent candidate since launch; launchd parentage is provisional ownership only.",
               "noContent": True, "memoryGraphSaved": False, "stackLoggingEnabled": False,
               "inheritedMallocNanoZone": os.environ.get("MallocNanoZone"),
               "forcedCollection": False, "securityChanged": False, "observations": []}
    # Supported capabilities/access prerequisites are read before launching any fixture.
    summary["preflight"] = [bounded(command) for command in (
        ["/usr/bin/heap", "--help"], ["/usr/bin/leaks", "--help"],
        ["/usr/bin/malloc_history", "-h"], ["/usr/bin/xctrace", "version"],
        ["/usr/sbin/DevToolsSecurity", "-status"])]
    previous = set(retention.webkit_processes())
    started = time.monotonic()
    log = args.output / "aggregate-cycles.log"
    inspected = set()
    blocked = False
    with log.open("w") as stream:
        app = subprocess.Popen([str(binary), "--check-retention", "aggregate-cycles"],
                               stdout=stream, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL)
        summary["appPid"] = app.pid
        try:
            while app.poll() is None:
                rows = records(log)
                current = rows[-1] if rows else None
                if current and current["stage"] in STAGES and current["stage"] not in inspected and not blocked:
                    stage = current["stage"]
                    inspected.add(stage)
                    candidates = [value for pid, value in retention.webkit_processes().items()
                                  if pid not in previous and value["executable"] == "com.apple.WebKit.WebContent"]
                    # No ambiguity fallback to a name match, existing process or arbitrary PID.
                    if len(candidates) != 1:
                        summary["blocker"] = "Expected exactly one newly launched WebContent candidate."
                        blocked = True
                        continue
                    pid = candidates[0]["pid"]
                    capture = bounded(["/usr/bin/leaks", "--noContent", "--nostacks", "--nosources",
                                       "--quiet", "--referenceTree", "--groupByType", str(pid)])
                    if capture["exitCode"] == 0 and "<< TOTAL >>" not in capture["output"]:
                        capture["failure"] = "missing-reference-tree"
                    capture.update({"stage": stage, "pid": pid, "role": candidates[0]["executable"],
                                    "requestedElapsedSeconds": current["elapsedSeconds"],
                                    "captureEndSeconds": round(time.monotonic() - started, 3),
                                    "stageStillCurrent": records(log)[-1]["stage"] == stage})
                    (args.output / f"{stage}-reference-tree.txt").write_text(capture["output"])
                    summary["observations"].append(capture)
                    if capture["failure"] or capture["exitCode"] != 0 or not capture["stageStillCurrent"]:
                        blocked = True
                        summary["blocker"] = "Reference ownership inspection failed or exceeded its matched checkpoint. No later inspection attempted."
                    print(f"{stage}: {'BLOCKED' if blocked else 'captured'}", flush=True)
                if time.monotonic() - started > 300:
                    summary["fixtureFailure"] = "finite-deadline"
                    app.terminate()
                    break
                time.sleep(0.1)
        finally:
            if app.poll() is None:
                app.terminate()
            app.wait(timeout=15)
    rows = records(log)
    summary["fixturePassed"] = app.returncode == 0 and "RETENTION_CHECK_PASS aggregate-cycles" in log.read_text()
    summary["durationSeconds"] = round(time.monotonic() - started, 2)
    summary["checkpointMetadata"] = [row for row in rows if row["stage"] in STAGES or
                                      row["stage"] in ("cycle-01-post-idle", "cycle-12-post-idle", "aggregate-reopened-compatible")]
    summary["ownershipEstablished"] = False  # Successful graphs still require semantic analysis.
    (args.output / "allocation-access.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(f"fixture: {'PASS' if summary['fixturePassed'] else 'FAIL'}; ownership requires evidence review", flush=True)
    return 0 if summary["fixturePassed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
