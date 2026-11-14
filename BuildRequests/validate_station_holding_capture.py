"""Read-only exclusive holding-marker audit, separate from physical FIFO arrival."""
import argparse
import collections
from datetime import datetime, timezone
import hashlib
import json
import math
import re
from pathlib import Path

CLAIM, RELEASE = "platform_holding_claimed", "platform_holding_released"

def sha256(path):
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()

def events(path):
    with path.open(encoding="utf-8-sig") as stream:
        for line_number, line in enumerate(stream, 1):
            if line.strip():
                try:
                    yield json.loads(line)
                except ValueError as error:
                    raise ValueError(f"events.jsonl:{line_number}: {error}") from error

def number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)

def vector(value):
    return isinstance(value, dict) and all(number(value.get(axis)) for axis in ("x", "y", "z"))

def distance(first, second):
    return math.sqrt(sum((first[axis] - second[axis]) ** 2 for axis in ("x", "y", "z")))

class HoldingAudit:
    def __init__(self, manifest):
        self.errors, self.checks = [], 0
        self.anchors, self.leases, self.by_journey, self.actor_owners = {}, {}, {}, {}
        self.claims, self.releases, self.peak = (collections.Counter() for _ in range(3))
        self.claim_journeys, self.last_time = set(), -1.0
        for gate in manifest.get("platformControls", []):
            track, anchors = gate.get("track"), gate.get("holdingAnchors")
            if not self.check(isinstance(track, int) and not isinstance(track, bool), "Gate track is not an integer."):
                continue
            if not self.check(isinstance(anchors, list) and bool(anchors), f"Track {track}: missing immutable holding anchors."):
                continue
            self.check(track not in self.anchors, f"Duplicate holding control track {track}.")
            self.anchors[track] = anchors
            for index, anchor in enumerate(anchors):
                if not self.check(vector(anchor), f"Track {track}/slot {index}: malformed holding floor anchor."):
                    continue
                if vector(gate.get("hall")):
                    self.check(abs(anchor["y"] - gate["hall"]["y"]) <= .15, f"Track {track}/slot {index}: holding anchor is on another floor.")
                for earlier, other in enumerate(anchors[:index]):
                    if vector(other):
                        self.check(distance(anchor, other) > 1e-4, f"Track {track}: slots {earlier}/{index} duplicate an authored anchor.")
        self.check(bool(self.anchors), "No holding controls were declared.")

    def check(self, ok, error):
        self.checks += 1
        if not ok:
            self.errors.append(error)
        return ok

    def consume(self, event):
        kind = event.get("kind")
        if kind not in (CLAIM, RELEASE):
            return
        now, jid = event.get("time"), event.get("agentId")
        track, slot = event.get("holdingTrack"), event.get("holdingSlotIndex")
        reported, goal = event.get("holdingLeaseCount"), event.get("holdingPosition")
        detail = event.get("detail", "")
        owner_match = re.search(r"(?:^|;)\s*owner=(-?\d+)(?:;|$)", detail) if isinstance(detail, str) else None
        actor_owner = int(owner_match.group(1)) if owner_match else None
        raw_slot_match = re.search(r"(?:^|;)\s*slot=(-?\d+)(?:;|$)", detail) if isinstance(detail, str) else None
        raw_track_match = re.search(r"(?:^|;)\s*track=(-?\d+)(?:;|$)", detail) if isinstance(detail, str) else None
        raw_slot = int(raw_slot_match.group(1)) if raw_slot_match else None
        raw_track = int(raw_track_match.group(1)) if raw_track_match else None
        prefix = f"{kind} at {now}, {jid}, track {track}/slot {slot}"
        valid = self.check(number(now) and now >= 0, prefix + ": invalid clock.")
        if number(now):
            self.check(now >= self.last_time - 1e-5, prefix + ": holding events moved backward in time.")
            self.last_time = max(self.last_time, now)
        valid &= self.check(isinstance(jid, str) and bool(jid), prefix + ": empty journey ownership.")
        known_track = isinstance(track, int) and not isinstance(track, bool) and track in self.anchors
        valid &= self.check(known_track, prefix + ": unknown track.")
        capacity = len(self.anchors[track]) if known_track else 0
        valid &= self.check(isinstance(reported, int) and not isinstance(reported, bool) and 0 <= reported <= capacity, prefix + ": reported lease count exceeds authored capacity.")
        valid &= self.check(vector(goal), prefix + ": missing exact holding floor position.")
        valid &= self.check(actor_owner is not None, prefix + ": missing recorded stable actor-owner identity.")
        valid &= self.check(raw_track == track and isinstance(raw_slot, int) and 0 <= raw_slot < capacity,
                            prefix + ": missing or mismatched explicit lease track/slot identity.")
        if kind == CLAIM:
            valid &= self.check(isinstance(slot, int) and not isinstance(slot, bool) and 0 <= slot < capacity and slot == raw_slot,
                                prefix + ": typed claimed slot differs from explicit lease identity.")
            if valid:
                valid &= self.check(distance(goal, self.anchors[track][slot]) <= 1e-4, prefix + ": claimed goal differs from its immutable authored floor anchor.")
        else:
            valid &= self.check(slot == -1, prefix + ": released journey still reports an assigned holding slot.")
            if vector(goal):
                valid &= self.check(distance(goal, {"x": 0, "y": 0, "z": 0}) <= 1e-6,
                                    prefix + ": released journey still reports an assigned holding position.")
        if not valid:
            return
        lease_slot = slot if kind == CLAIM else raw_slot
        key, owner = (track, lease_slot), (track, jid)
        if kind == CLAIM:
            if not self.check(key not in self.leases, prefix + ": a holding slot was claimed before prior explicit release."):
                return
            if not self.check(owner not in self.by_journey, prefix + ": a journey changed its holding slot without release."):
                return
            self.leases[key] = jid
            self.actor_owners[key] = actor_owner
            self.by_journey[owner] = slot
            self.claims[track] += 1
            self.claim_journeys.add(jid)
        else:
            if not self.check(self.leases.get(key) == jid and self.by_journey.get(owner) == lease_slot and self.actor_owners.get(key) == actor_owner,
                              prefix + ": release did not belong to its recorded journey and actor owner."):
                return
            del self.leases[key]
            del self.actor_owners[key]
            del self.by_journey[owner]
            self.releases[track] += 1
        count = sum(leased_track == track for leased_track, _ in self.leases)
        self.peak[track] = max(self.peak[track], count)
        self.check(count <= capacity, prefix + ": reconstructed ownership exceeded capacity.")
        self.check(count == reported, prefix + ": reported count differs from reconstructed explicit leases.")

    def result(self):
        return {"success": not self.errors, "checks": self.checks, "errorCount": len(self.errors), "errors": self.errors,
            "claimEvents": sum(self.claims.values()), "releaseEvents": sum(self.releases.values()),
            "distinctClaimedJourneys": len(self.claim_journeys), "capacityByTrack": {str(track): len(anchors) for track, anchors in self.anchors.items()},
            "claimEventsByTrack": dict(self.claims), "releaseEventsByTrack": dict(self.releases),
            "peakExclusiveHoldingLeasesByTrack": dict(self.peak),
            "leasesAtCaptureEnd": [{"track": track, "slot": slot, "journeyId": jid} for (track, slot), jid in self.leases.items()],
            "notes": ["A holding reservation is permitted before physical arrival at its goal; it is not FIFO queue admission.",
                "Stable exclusive ownership lasts until explicit release; a subsequent claim must follow that release.",
                "Current runtime owner integers identify the stable 1-based actor-pool slot, rather than a Unity instance ID.",
                "Release typed fields describe poststate: slot=-1 and position=zero. Explicit detail track/slot/owner identifies the released lease and must exactly match the active ownership record.",
                "Goals match immutable authored floor anchors. No geometry or movement limits are changed by this audit.",
                "Leases still held at a bounded capture end are censored, not forcibly cleared."]}

def validate_capture(directory):
    directory = directory.resolve()
    inputs = ("manifest.json", "events.jsonl")
    before = {name: sha256(directory / name) for name in inputs}
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8-sig"))
    audit = HoldingAudit(manifest)
    run_ended, last_time = False, 0.0
    for event in events(directory / "events.jsonl"):
        audit.consume(event)
        if number(event.get("time")):
            last_time = max(last_time, event["time"])
        run_ended |= event.get("kind") == "run_ended"
    audit.check(run_ended, "Capture is not closed; final ownership audit requires run_ended.")
    audit.check(sum(audit.claims.values()) > 0, "No holding claims were observed; this run cannot certify active holding ownership.")
    audit.check(sum(audit.releases.values()) > 0, "No holding releases were observed; this run cannot certify release and reuse.")
    after = {name: sha256(directory / name) for name in inputs}
    audit.check(before == after, "Capture inputs changed during audit.")
    report = audit.result()
    report.update({"capture": str(directory), "generatedUtc": datetime.now(timezone.utc).isoformat(), "lastEventSeconds": last_time,
        "inputSha256": before, "inputsUnchangedDuringAudit": before == after,
        "scope": "Independent holding ownership audit; complements frozen complete capture and physical FIFO validators."})
    return report

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    if args.capture.resolve() in output.parents:
        parser.error("Output must remain outside capture inputs.")
    result = validate_capture(args.capture)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps({key: result[key] for key in ("success", "checks", "errorCount", "claimEvents", "releaseEvents", "peakExclusiveHoldingLeasesByTrack")}, ensure_ascii=False))
    return 0 if result["success"] else 1

if __name__ == "__main__":
    raise SystemExit(main())
