#!/usr/bin/env python3
"""Condense fixed matched-control metadata; never copy terminal payloads or full native reports."""
import argparse
import hashlib
import json
import pathlib
import re

VARIANTS = ("aggregate-empty-cycles", "aggregate-idle-cycles", "aggregate-cycles")
STAGES = ("aggregate-empty-baseline", "cycle-01-populated", "cycle-01-post-close",
          "cycle-01-post-idle", "cycle-12-populated", "cycle-12-post-close", "cycle-12-post-idle")
COUNTERS = ("generation", "received", "terminalPresent", "starts", "closes", "constructed", "opened",
            "disposed", "renders", "acceptedWrites", "staleWrites", "inactiveWrites", "finalized",
            "retiredRootsAlive", "attachedTerminalNodes", "domNodes", "normalLines", "alternateLines",
            "widthCacheEntries", "widthCacheEvictions", "a11yAggregateUnits", "frameCount", "frameWidth",
            "frameHeight", "opaqueSandbox")
CATEGORIES = {"WebKit malloc", "Owned physical footprint (unmapped) (graphics)", "IOSurface",
              "IOAccelerator", "MALLOC_SMALL", "MALLOC_LARGE", "JS VM Gigacage", "TOTAL"}
CATEGORY = re.compile(r"^\s*(\S+ (?:B|KB|MB|GB))\s+(\S+ (?:B|KB|MB|GB))\s+"
                      r"(\S+ (?:B|KB|MB|GB))\s+(\d+)\s+(.+?)\s*$")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", type=pathlib.Path)
    parser.add_argument("output", type=pathlib.Path)
    args = parser.parse_args()
    root = pathlib.Path(__file__).resolve().parent
    summary = {
        "scope": "Fixed synthetic actual Uno macOS development runs. One persistent WKWebView and opaque frame per variant; no xterm during empty measurement. No object-level cause, lifetime memory bound, production/Windows/privacy/Engine conformance.",
        "clocks": "Checkpoint elapsedSeconds starts at browser readiness; RSS and captureSeconds start at process launch. Native captures occur sequentially during held states; nearest RSS is observational, not simultaneous.",
        "conditions": json.loads((args.run / "conditions.json").read_text()),
        "inputSha256": {name: digest(root / name) for name in
                       ("RetentionChecks.cs", "Web/surface.js", "Web/bridge.js", "run-retention-checks.py")},
        "rendererSha256": digest(root / "Web/xterm.js"),
        "nativeCategoryColumns": ["dirty", "clean", "reclaimable", "regions"],
        "nativeColumns": ["pid", "nearestRssSeconds", "nearestRssMiB", "vmmapCaptureSeconds",
                          "footprintCaptureSeconds", "physicalFootprint", "peakPhysicalFootprint"],
        "nativeCaptureExitCodes": "All vmmap/footprint captures required exit code 0; otherwise summarization fails.",
        "workloads": {},
        "regressions": {},
    }
    for variant in VARIANTS:
        log = (args.run / (variant + ".log")).read_text()
        assert "RETENTION_CHECK_PASS " + variant in log, variant + " did not pass"
        records = [json.loads(line[10:]) for line in log.splitlines() if line.startswith("RETENTION ")]
        samples = [json.loads(line) for line in (args.run / (variant + "-rss.jsonl")).read_text().splitlines()]
        roles = {samples[0]["appPid"]: "app"}
        for sample in samples:
            roles.update({p["pid"]: p["executable"] for p in sample["newWebKitCandidates"]})
        def rss(sample, pid):
            if pid == sample["appPid"]:
                return sample["appRssKiB"]
            return next((p["rssKiB"] for p in sample["newWebKitCandidates"] if p["pid"] == pid), None)
        ranges = []
        for pid, role in roles.items():
            values = [rss(s, pid) / 1024 for s in samples if s["seconds"] >= 5 and rss(s, pid) is not None]
            ranges.append({"pid": pid, "role": role, "minRssMiB": round(min(values), 3),
                           "maxRssMiB": round(max(values), 3)})
        intervals = [b["seconds"] - a["seconds"] for a, b in zip(samples, samples[1:])]
        checkpoints = []
        for stage in STAGES:
            record = next(r for r in records if r["stage"] == stage)
            capture = json.loads((args.run / (variant + "-" + stage + "-capture.json")).read_text())
            item = {key: record[key] for key in ("stage", "elapsedSeconds", "deliveredBytes", "pendingChunks",
                    "producerJoined", "pumpJoined", "nativeAckPending", "inputQueueEntries")}
            item["stats"] = {key: record["stats"][key] for key in COUNTERS if key in record["stats"]}
            item["native"] = []
            for pid, role in roles.items():
                observations = [o for o in capture["observations"] if o["pid"] == pid]
                assert len(observations) == 2, "Missing native capture"
                native = {}
                for observation in observations:
                    assert observation.get("exitCode") == 0, "Native capture failed"
                    report = (args.run / f"{variant}-{stage}-{pid}-{observation['tool']}.txt").read_text()
                    if observation["tool"] == "vmmap":
                        for label in ("Physical footprint", "Physical footprint (peak)"):
                            match = re.search(r"^" + re.escape(label) + r":\s+(\S+)$", report, re.M)
                            assert match, "Missing footprint metric"
                            native[label] = match[1]
                    else:
                        if role == "com.apple.WebKit.WebContent":
                            item["webContentCategories"] = {m[5]: list(m.groups()[:4]) for line in report.splitlines()
                                if (m := CATEGORY.match(line)) and m[5] in CATEGORIES}
                nearest = min(samples, key=lambda s: abs(s["seconds"] - observations[0]["captureSeconds"]))
                value = rss(nearest, pid)
                item["native"].append([pid, nearest["seconds"], round(value / 1024, 3) if value is not None else None,
                    next(o["captureSeconds"] for o in observations if o["tool"] == "vmmap"),
                    next(o["captureSeconds"] for o in observations if o["tool"] == "footprint"),
                    native["Physical footprint"], native["Physical footprint (peak)"]])
            checkpoints.append(item)
        assert len([r for r in records if re.fullmatch(r"cycle-\d{2}-post-idle", r["stage"])]) == 12
        summary["workloads"][variant] = {"pass": True, "sampleCount": len(samples),
            "rssIntervalSeconds": [round(min(intervals), 2), round(max(intervals), 2)],
            "processRssRangesAfterFiveSeconds": ranges, "checkpoints": checkpoints,
            "postMeasurementAssertions": [r["stage"] for r in records if r["stage"] in
                ("control-replaced-terminal", "lifecycle-complete", "aggregate-reopened-compatible")]}
    for log in sorted((args.run / "regressions").glob("*.log")):
        summary["regressions"][log.stem] = "RETENTION_CHECK_PASS " + log.stem in log.read_text()
    args.output.write_text(json.dumps(summary, indent=2) + "\n")


if __name__ == "__main__":
    main()
