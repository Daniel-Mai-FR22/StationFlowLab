"""Independent WC FIFO/capacity/duration audit of an unmodified closed capture."""
import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path


def distance(a, b):
    return math.hypot(a['x'] - b['x'], a['z'] - b['z'])


def audit_events(control, events):
    errors, waiting, leases, completed = [], [], {}, {}
    kinds, durations, elapsed = Counter(), [], []
    checks = peak_queue = peak_use = 0

    def check(ok, message):
        nonlocal checks
        checks += 1
        if not ok and len(errors) < 100:
            errors.append(message)

    capacity = control['capacity']
    slots, uses = control['queueSlots'], control['usePoints']
    check(capacity == len(uses) and capacity > 0, 'Invalid saved WC capacity/use markers')
    check(control['queueCapacity'] == len(slots) and len(slots) > 0, 'Invalid saved WC queue markers')
    for e in events:
        kind, ident, now = e.get('kind', ''), e.get('agentId'), e['time']
        if kind.startswith('toilet_'):
            kinds[kind] += 1
        elif kind == 'activity_completed' and e.get('detail', '').startswith('Toilet;'):
            check(ident in completed, f'{ident}: activity completed without finished WC duration')
            check(distance(e['position'], control['exitPoint']) <= .125, f'{ident}: completion before reaching WC exit')
            kinds['activity_completed_Toilet'] += 1
            continue
        else:
            continue
        label = f'{now:.3f} {kind} {ident}'
        if kind == 'toilet_queue_joined':
            index = e['queueSlotIndex']
            check(ident not in waiting and ident not in leases, label + ': duplicate physical admission')
            check(0 <= index < len(slots), label + ': invalid queue slot')
            if 0 <= index < len(slots):
                point = slots[index]
                check(distance(e['queueArrivalPosition'], point) <= .001, label + ': marker telemetry differs from saved geometry')
                check(distance(e['position'], point) <= control['arrivalTolerance'] + .0001, label + ': remote registration')
                check(abs(e['position']['y'] - point['y']) <= .1501, label + ': wrong floor')
            predecessor = waiting[-1] if waiting else ''
            check(e.get('queuePredecessorJourneyId', '') == predecessor, label + ': incorrect arrival predecessor')
            if predecessor:
                check(distance(e['position'], e['queuePredecessorPosition']) >= control['minimumQueueSeparation'] - .0001,
                      label + ': predecessor personal space violated')
            waiting.append(ident)
        elif kind == 'toilet_queue_left':
            check(ident in waiting, label + ': removed non-waiting journey')
            if ident in waiting:
                waiting.remove(ident)
        elif kind == 'toilet_use_reserved':
            check(bool(waiting) and waiting[0] == ident, label + ': FIFO bypass')
            check(distance(e['position'], slots[0]) <= .125, label + ': reservation away from physical queue head')
            if ident in waiting:
                waiting.remove(ident)
            index = e['toiletUseSlotIndex']
            check(0 <= index < capacity, label + ': invalid use place')
            check(index not in [v['index'] for v in leases.values()], label + ': double leased WC place')
            check(ident not in leases, label + ': duplicate use lease')
            leases[ident] = dict(index=index, start=None, duration=None, releasing=False)
        elif kind == 'toilet_use_started':
            check(ident in leases, label + ': use without lease')
            lease = leases.get(ident)
            if lease:
                index = lease['index']
                check(lease['start'] is None and not lease['releasing'], label + ': repeated/releasing use')
                check(e['toiletUseSlotIndex'] == index, label + ': use place changed')
                if 0 <= index < len(uses):
                    check(distance(e['position'], uses[index]) <= .125, label + ': duration began before physical arrival')
                duration = e['toiletUseDurationSeconds']
                check(control['minimumUseSeconds'] <= duration <= control['maximumUseSeconds'], label + ': WC duration outside saved bounds')
                check(abs(e['toiletUseStartedAtSeconds'] - now) <= .001, label + ': inaccurate start clock')
                check(e.get('outcome') == 'WCUsing', label + ': runtime state not using WC')
                lease.update(start=now, duration=duration)
                durations.append(duration)
        elif kind in ('toilet_use_completed', 'toilet_use_interrupted'):
            check(ident in leases, label + ': departure without use lease')
            lease = leases.get(ident)
            if lease:
                check(not lease['releasing'], label + ': duplicate release initiation')
                if kind == 'toilet_use_completed':
                    check(lease['start'] is not None, label + ': completion without actual use')
                    if lease['start'] is not None:
                        used = now - lease['start']
                        check(used + .001 >= lease['duration'], label + ': completed too early')
                        elapsed.append(used)
                        completed[ident] = now
                lease['releasing'] = True
        elif kind == 'toilet_use_released':
            check(ident in leases, label + ': freed unknown place')
            lease = leases.get(ident)
            if lease:
                check(lease['releasing'], label + ': capacity freed before departure')
                if 0 <= lease['index'] < len(uses):
                    check(distance(e['position'], uses[lease['index']]) >= control['minimumQueueSeparation'] - .0001,
                          label + ': capacity freed before body clearance')
                del leases[ident]
        check(len(waiting) <= len(slots), label + ': queue capacity exceeded')
        check(len(leases) <= capacity, label + ': use capacity exceeded')
        check(e['toiletCapacity'] == capacity, label + ': runtime capacity differs from saved capacity')
        check(e['toiletUseCount'] == len(leases), label + ': use ledger/telemetry mismatch')
        check(e['toiletQueueCount'] == len(waiting), label + ': queue ledger/telemetry mismatch')
        peak_queue, peak_use = max(peak_queue, len(waiting)), max(peak_use, len(leases))
    return dict(success=not errors, checks=checks, errors=errors, eventKinds=dict(kinds),
                queuePeak=peak_queue, usePeak=peak_use, waitingAtEnd=waiting, leasesAtEnd=leases,
                sampledDurationsSeconds=durations, completedUseElapsedSeconds=elapsed,
                scope='Raw saved geometry, physical arrival FIFO, exclusive capacity leases, actual use start, sampled duration and physical clearance. Open visits at bounded capture end are retained.')


def audit_capture(directory):
    paths = [directory / 'manifest.json', directory / 'events.jsonl']
    before = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in paths}
    manifest = json.loads(paths[0].read_text(encoding='utf-8-sig'))
    events = [json.loads(line) for line in paths[1].read_text(encoding='utf-8-sig').splitlines() if line.strip()]
    controls = manifest.get('toiletControls', [])
    if len(controls) != 1 or not any(e.get('kind') == 'run_ended' for e in events):
        return dict(success=False, errors=['Expected one saved WC control and a closed capture'])
    report = audit_events(controls[0], events)
    report.update(directory=str(directory.resolve()), checkedUtc=datetime.now(timezone.utc).isoformat(), inputHashes=before)
    report['inputsPreserved'] = all(hashlib.sha256(p.read_bytes()).hexdigest() == before[p.name] for p in paths)
    report['success'] &= report['inputsPreserved']
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    if args.output and args.output.resolve().parent == args.directory.resolve():
        parser.error('Place reports outside capture inputs')
    report = audit_capture(args.directory)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False) + '\n', encoding='utf-8')
    print(json.dumps({k: report[k] for k in ('success', 'checks', 'errors', 'eventKinds', 'queuePeak', 'usePeak') if k in report}, ensure_ascii=False))
    return 0 if report['success'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
