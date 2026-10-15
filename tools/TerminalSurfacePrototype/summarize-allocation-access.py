#!/usr/bin/env python3
"""Project fixed metadata/type/reference excerpts from the allocation probe."""
import argparse
import hashlib
import json
import pathlib
import re


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=pathlib.Path)
    parser.add_argument("destination", type=pathlib.Path)
    args = parser.parse_args()
    raw = json.loads(args.source.read_text())
    if not raw["noContent"] or raw["memoryGraphSaved"]:
        parser.error("Requires the explicit content-free probe result.")
    keys = ("schema", "workload", "checkpoints", "platform", "wallClock", "binarySha256",
            "assemblySha256", "attribution", "noContent", "memoryGraphSaved", "stackLoggingEnabled",
            "inheritedMallocNanoZone", "forcedCollection", "securityChanged", "appPid", "fixturePassed",
            "durationSeconds", "ownershipEstablished", "blocker")
    summary = {key: raw[key] for key in keys if key in raw}
    summary["scope"] = "Content-free live reference trees; no new RSS/category measurements, causal ownership or bounded-memory claim."
    summary["preflight"] = [{key: item[key] for key in ("command", "exitCode", "failure")}
                            for item in raw["preflight"]]
    for item, source in zip(summary["preflight"], raw["preflight"]):
        if source["command"][0] in ("/usr/bin/xctrace", "/usr/sbin/DevToolsSecurity"):
            item["diagnostic"] = source["output"][:2048]
    summary["referenceTreeCaveat"] = "Tool chooses one heuristic parent per allocation; references can be false/stale. <WebKit Owned> is a broad tool root, not a retaining-object path."
    summary["interpretation"] = "Metadata excerpt projection only; semantic review must establish coverage and any causal ownership. See the research document for the recorded run's coverage boundary."
    summary["observations"] = []
    pattern = re.compile(r"^\s*(?:\d+ \([^)]+\) << TOTAL >>|\d+ \([^)]+\) <WebKit Owned>|\+ \d+ \([^)]+\) (?:malloc-block|WebCore::(?:Text|HTMLSpanElement|InlineCSSStyleProperties|RenderBlockFlow|RenderText)|WebKit::(?:RemoteImageBufferSetProxy|RemoteImageBufferProxy|PlatformCALayerRemote))|\+ ! 1 VM: VM_ALLOCATE \(graphics\))$")
    for observation in raw["observations"]:
        item = {key: value for key, value in observation.items() if key != "output"}
        output = observation["output"]
        item["referenceOutputSha256"] = hashlib.sha256(output.encode()).hexdigest()
        item["captureStartSecondsDerived"] = round(item["captureEndSeconds"] - item["durationSeconds"], 3)
        lines = output.splitlines()
        root_end = next((i for i in range(2, len(lines)) if re.match(r"^      \d", lines[i])), len(lines))
        item["selectedReferenceTreeLines"] = [{"line": number, "text": line} for number, line in
                                             enumerate(lines[:root_end], 1) if pattern.fullmatch(line)]
        # Copy bounded tool errors only on failure. Never silently turn a failure into evidence.
        if item["failure"] or item["exitCode"] != 0:
            item["failureEvidence"] = output[:2048]
        summary["observations"].append(item)
    summary["checkpointMetadata"] = raw["checkpointMetadata"]
    args.destination.write_text(json.dumps(summary, indent=2) + "\n")


if __name__ == "__main__":
    main()
