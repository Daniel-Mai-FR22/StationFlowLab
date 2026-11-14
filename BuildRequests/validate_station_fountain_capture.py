"""Stream a saved Daily capture; audit hall fountain and observed display/service use.

This is independent of the Editor frame/path observer. It uses recorded simulator
positions, not image detections. Near-annulus occupancy is not a completed arc.
"""
import argparse
from collections import Counter
from datetime import datetime, timezone
import json
import math
from pathlib import Path


def rows(path):
    with path.open(encoding='utf-8-sig') as stream:
        for number, line in enumerate(stream, 1):
            if line.strip():
                try:
                    yield json.loads(line)
                except (ValueError, TypeError) as exc:
                    raise ValueError(f'{path.name}:{number}: invalid JSON: {exc}') from exc


def flat(a, b):
    return math.hypot(a['x'] - b['x'], a['z'] - b['z'])


def stationary_kind(row):
    execution = row.get('execution')
    if execution == 'Using' and row.get('activity') == 'Display' and row.get('targetPoi') == 'display-hall':
        return 'DisplayUsing'
    if execution in {'WCUsing', 'WaitingForTrain', 'WaitingForGroup'}:
        return execution
    return None


class Summary:
    """Constant-memory numeric distribution; histogram resolution is explicit."""
    def __init__(self, bin_width=.05):
        self.count = 0
        self.total = 0.
        self.minimum = math.inf
        self.maximum = -math.inf
        self.bins = Counter()
        self.bin_width = bin_width

    def add(self, value):
        self.count += 1
        self.total += value
        self.minimum = min(self.minimum, value)
        self.maximum = max(self.maximum, value)
        self.bins[int(math.floor(value / self.bin_width))] += 1

    def result(self):
        return dict(count=self.count, minimum=self.minimum if self.count else None,
                    maximum=self.maximum if self.count else None,
                    mean=self.total / self.count if self.count else None,
                    histogramBinWidth=self.bin_width,
                    histogram={f'{k * self.bin_width:.3f}': v for k, v in sorted(self.bins.items())})


def audit(directory, layout_path=None):
    directory = Path(directory).resolve()
    manifest_path = directory / 'manifest.json'
    manifest = json.loads(manifest_path.read_text(encoding='utf-8-sig'))
    center = dict(x=0., y=0., z=4.)
    radius = 5.
    layout_source = 'documented saved Daily fountain: center (0,0,4), radius 5 m'
    if layout_path:
        layout_path = Path(layout_path).resolve()
        layout = json.loads(layout_path.read_text(encoding='utf-8-sig'))
        center = layout.get('fountainCenter', center)
        radius = float(layout.get('fountainRadius', radius))
        layout_source = str(layout_path)
    errors, checks, error_count = [], 0, 0

    def check(condition, message):
        nonlocal checks, error_count
        checks += 1
        if not condition:
            error_count += 1
            if len(errors) < 100:
                errors.append(message)

    check(radius > 0 and all(math.isfinite(float(center[k])) for k in ('x', 'z')), 'Invalid fountain layout')
    telemetry_enabled = manifest.get('stationaryHoldTelemetry') is True
    if 'stationaryHoldTelemetry' in manifest:
        check(type(manifest['stationaryHoldTelemetry']) is bool, 'Manifest stationaryHoldTelemetry must be boolean')
    pois = {p['poiId']: p for p in manifest.get('pois', [])}
    display = pois.get('display-hall', {})
    info = pois.get('info-accueil', {})
    stops = display.get('standingPoints', [])
    check(len(stops) == 24, f'Manifest must record 24 Display standing positions; found {len(stops)}')
    check(display.get('capacity') == len(stops) and len(stops) > 0, 'Display capacity/standing marker mismatch')
    check(bool(info.get('departurePosition')), 'Missing accueil departure point in manifest')
    for i, stop in enumerate(stops):
        check(flat(stop, center) >= radius + .27, f'Display stop {i} lies in basin/body clearance')
        for previous in stops[:i]:
            check(flat(stop, previous) >= .9, f'Display stop {i} is less than 0.9 m from another stop')

    kinds, ended, counter_pending = Counter(), set(), {}
    info_served = info_clearing = info_completed = 0
    served_distances, exit_distances = Summary(), Summary()
    counter_examples = []
    first_event = last_event = None
    for event in rows(directory / 'events.jsonl'):
        now, kind, ident = float(event.get('time', 0)), event.get('kind', ''), event.get('agentId', '')
        first_event = now if first_event is None else min(first_event, now)
        last_event = now if last_event is None else max(last_event, now)
        kinds[kind] += 1
        if kind in ('journey_finished', 'journey_unserved'):
            ended.add(ident)
        if kind == 'counter_service_completed' and event.get('detail') == 'Info':
            info_served += 1
            counter_pending[ident] = dict(servedAt=now, clearanceAt=None)
            if info.get('position'):
                served_distances.add(flat(event['position'], info['position']))
        elif kind == 'counter_clearance_started' and event.get('detail') == 'info-accueil':
            info_clearing += 1
            if ident in counter_pending:
                counter_pending[ident]['clearanceAt'] = now
        elif kind == 'activity_completed' and event.get('detail', '').startswith('Info;'):
            info_completed += 1
            pending = counter_pending.pop(ident, None)
            check(pending is not None, f'{now:.3f} {ident}: Info completed without observed successful counter service')
            if pending and info.get('departurePosition'):
                distance = flat(event['position'], info['departurePosition'])
                exit_distances.add(distance)
                if pending['clearanceAt'] is not None:
                    check(distance <= .65, f'{now:.3f} {ident}: Info completion is {distance:.3f} m from departure')
                if len(counter_examples) < 20:
                    counter_examples.append(dict(journeyId=ident, **pending, completedAt=now, departureDistance=distance))
        elif kind == 'activity_interrupted' and event.get('detail', '').startswith('Info:'):
            counter_pending.pop(ident, None)

    sample_events = (event for event in rows(directory / 'events.jsonl') if event.get('kind') == 'trajectory_sample')
    expected = next(sample_events, None)
    sample_id = None
    sample_display = {}
    sample_count = trajectory_count = using_count = 0
    first_sample = last_sample = None
    sample_intervals = Summary(.01)
    stop_distances = Summary()
    minimum_clearance = math.inf
    minimum_record = None
    east, west = set(), set()
    annulus_samples = Counter()
    returning_journeys = set()
    returning_samples = 0
    observed_reading_seconds = observed_returning_seconds = 0.
    display_transition_intervals = 0
    stationary_distances = {kind: Summary(.001) for kind in
                            ('DisplayUsing', 'WCUsing', 'WaitingForTrain', 'WaitingForGroup')}
    stationary_seconds = Counter()
    stationary_censored = stationary_errors = 0
    stationary_examples = []
    semantic_endpoint_exceedances = 0
    semantic_endpoint_examples = []
    anchor_distances = Summary(.001)
    held_executions = Counter()
    held_journeys = set()
    same_serial_intervals = 0
    states = {}
    passages = Counter()
    passage_journeys = set()
    passage_examples = []
    continuity_resets = 0
    south, north = center['z'] - radius - 2., center['z'] + radius + 2.
    hall_half_width = radius + 5.

    def close_sample():
        for stop, journeys in sample_display.items():
            check(len(journeys) == 1, f'Sample {sample_id}: multiple Display Using actors near stop {stop}: {journeys}')

    for row in rows(directory / 'trajectories.jsonl'):
        trajectory_count += 1
        sid, now = row.get('sampleId'), float(row['time'])
        ident, position = row.get('agentId', ''), row['position']
        check(all(math.isfinite(float(position[k])) for k in ('x', 'y', 'z')), f'{ident}: non-finite recorded position')
        if sid != sample_id:
            if sample_id is not None:
                close_sample()
            sample_display = {}
            while expected is not None and expected.get('sampleId', -1) < sid:
                expected = next(sample_events, None)
            check(expected is not None and expected.get('sampleId') == sid, f'Sample {sid}: missing trajectory_sample event')
            if expected is not None and expected.get('sampleId') == sid:
                check(abs(float(expected['time']) - now) <= .001, f'Sample {sid}: event/trajectory times differ')
            if last_sample is not None:
                check(now >= last_sample, f'Sample {sid}: decreasing simulator time')
                sample_intervals.add(now - last_sample)
            first_sample = now if first_sample is None else first_sample
            last_sample = now
            sample_id = sid
            sample_count += 1
        check(abs(now - last_sample) <= .001, f'Sample {sid}: row times differ within one population sample')
        clearance = flat(position, center)
        check(clearance >= radius - .005, f'{now:.3f} {ident}: recorded body center lies in fountain basin ({clearance:.4f} m)')
        if clearance < minimum_clearance:
            minimum_clearance = clearance
            minimum_record = dict(journeyId=ident, time=now, sampleId=sid, position=position)
        near = radius - .005 <= clearance <= radius + 3. and abs(position['z'] - center['z']) <= radius
        if near:
            side = 'west' if position['x'] < center['x'] else 'east'
            annulus_samples[side] += 1
            (west if side == 'west' else east).add(ident)
        if row.get('activity') == 'Display' and row.get('execution') == 'Using' and row.get('targetPoi') == 'display-hall':
            using_count += 1
            if stops:
                distance, nearest = min((flat(position, stop), i) for i, stop in enumerate(stops))
                stop_distances.add(distance)
                check(distance <= .65, f'{now:.3f} {ident}: Display Using is {distance:.3f} m from nearest saved stop')
                # Only stationary Using bodies inside disjoint 0.45 m stop discs
                # prove physical stop sharing; moving/avoiding samples remain in
                # the distance distribution without ambiguous nearest ownership.
                velocity = row.get('velocity', {})
                speed = math.hypot(float(velocity.get('x', 0)), float(velocity.get('z', 0)))
                if distance <= .45 and speed <= .2:
                    sample_display.setdefault(nearest, []).append(ident)
        if row.get('execution') == 'ReturningToDisplay':
            returning_samples += 1
            returning_journeys.add(ident)
            check(row.get('activity') == 'Display' and row.get('targetPoi') == 'display-hall' and bool(stops),
                  f'{now:.3f} {ident}: return movement lacks the saved Display standing area')

        state = states.setdefault(ident, dict(previous=None, origin=None, candidate=None))
        previous = state['previous']
        hold_kind = stationary_kind(row)
        held = row.get('isStationaryHeld') is True if telemetry_enabled else False
        hold_serial = row.get('stationaryHoldSerial') if telemetry_enabled else None
        if telemetry_enabled:
            check(type(row.get('isStationaryHeld')) is bool, f'{now:.3f} {ident}: stationary hold flag is not boolean')
            check(type(hold_serial) is int and hold_serial >= 0, f'{now:.3f} {ident}: invalid stationary hold serial')
            anchor = row.get('stationaryAnchor')
            valid_anchor = isinstance(anchor, dict) and all(isinstance(anchor.get(k), (float, int)) and
                not isinstance(anchor.get(k), bool) and math.isfinite(float(anchor[k])) for k in ('x', 'y', 'z'))
            check(valid_anchor, f'{now:.3f} {ident}: invalid explicit stationary anchor')
            if held:
                check(type(hold_serial) is int and hold_serial > 0, f'{now:.3f} {ident}: held body needs a positive serial')
                held_executions[row.get('execution', '')] += 1
                held_journeys.add(ident)
                if valid_anchor:
                    anchor_distance = math.sqrt(sum((float(position[k]) - float(anchor[k])) ** 2 for k in ('x', 'y', 'z')))
                    anchor_distances.add(anchor_distance)
                    anchored = anchor_distance <= .005
                    check(anchored, f'{now:.3f} {ident}: held body is {anchor_distance:.6f} m from its explicit anchor')
                    if not anchored:
                        stationary_errors += 1
                        if len(stationary_examples) < 20:
                            stationary_examples.append(dict(journeyId=ident, time=now, holdSerial=hold_serial,
                                                            anchorDisplacementMeters=anchor_distance,
                                                            anchor=anchor, position=position))
        continuous = True
        if previous:
            elapsed = now - previous['time']
            step = flat(position, previous['position'])
            continuous = 0 <= elapsed <= 2. and step <= 3. * elapsed + .4
            previous_hold = previous['stationaryKind']
            if telemetry_enabled:
                same_hold = held and previous['held'] and hold_serial == previous['holdSerial'] and \
                    row.get('execution') == previous['execution'] and \
                    row.get('targetPoi') == previous['targetPoi'] and row.get('groupId') == previous['groupId']
                interval_kind = hold_kind or 'Other/' + row.get('execution', '')
                has_hold_endpoint = held or previous['held']
            else:
                same_hold = hold_kind and hold_kind == previous_hold and \
                    row.get('targetPoi') == previous['targetPoi'] and row.get('groupId') == previous['groupId']
                interval_kind = hold_kind
                has_hold_endpoint = hold_kind or previous_hold
            if same_hold and 0 < elapsed <= 2.:
                displacement = math.sqrt(sum((float(position[k]) - float(previous['position'][k])) ** 2
                                             for k in ('x', 'y', 'z')))
                stationary_distances.setdefault(interval_kind, Summary(.001)).add(displacement)
                stationary_seconds[interval_kind] += elapsed
                if telemetry_enabled:
                    same_serial_intervals += 1
                within_tolerance = displacement <= .005
                if telemetry_enabled:
                    check(within_tolerance,
                          f'{now:.3f} {ident}: stationary {interval_kind} displaced {displacement:.6f} m '
                          f'between consecutive matching hold samples')
                if not within_tolerance:
                    example = dict(journeyId=ident, state=interval_kind, holdSerial=hold_serial,
                                   fromTime=previous['time'], toTime=now, displacementMeters=displacement,
                                   fromPosition=previous['position'], toPosition=position)
                    if telemetry_enabled:
                        stationary_errors += 1
                        if len(stationary_examples) < 20:
                            stationary_examples.append(example)
                    else:
                        # Native NavMeshAgent.isStopped permits avoidance
                        # displacement. A semantic label is not an explicit
                        # immobility contract or proof of a continuous hold.
                        # Preserve the measured .005 m exceedance as evidence
                        # without making exact immobility an acceptance gate.
                        semantic_endpoint_exceedances += 1
                        if len(semantic_endpoint_examples) < 20:
                            semantic_endpoint_examples.append(example)
            elif has_hold_endpoint:
                # Different execution/target/group or a recording gap is not a
                # continuously observed stationary interval. In particular,
                # ReturningToDisplay is walking, and FIFO queues can advance.
                stationary_censored += 1
            if continuous and previous['display'] and row.get('activity') == 'Display' and row.get('targetPoi') == 'display-hall':
                previous_execution = previous['execution']
                execution = row.get('execution')
                if previous_execution == execution == 'Using':
                    observed_reading_seconds += elapsed
                elif previous_execution == execution == 'ReturningToDisplay':
                    observed_returning_seconds += elapsed
                elif {previous_execution, execution} <= {'Using', 'ReturningToDisplay'}:
                    # The transition happened between samples; assigning its
                    # whole interval to reading would count return movement.
                    display_transition_intervals += 1
            if not continuous:
                if state['candidate']:
                    continuity_resets += 1
                state['origin'] = None
                state['candidate'] = None
        zone = ('south' if position['z'] <= south else 'north' if position['z'] >= north else None)
        in_hall = abs(position['x'] - center['x']) <= hall_half_width
        if not in_hall:
            if state['candidate']:
                continuity_resets += 1
            state['origin'] = None
            state['candidate'] = None
        elif zone is None and state['origin'] and state['candidate'] is None:
            state['candidate'] = dict(origin=state['origin'], startedAt=previous['time'] if previous else now,
                                      crossedSides=set(), samples=0)
        candidate = state['candidate']
        if candidate and previous and continuous:
            candidate['samples'] += 1
            z0, z1 = previous['position']['z'] - center['z'], position['z'] - center['z']
            if (z0 < 0 <= z1) or (z1 < 0 <= z0):
                fraction = -z0 / (z1 - z0) if z1 != z0 else 0.
                crossing_x = previous['position']['x'] + fraction * (position['x'] - previous['position']['x'])
                candidate['crossedSides'].add('west' if crossing_x < center['x'] else 'east')
        if in_hall and zone:
            if candidate and zone != candidate['origin']:
                sides = candidate['crossedSides']
                side = next(iter(sides)) if len(sides) == 1 else 'mixed' if sides else 'unclassified'
                direction = candidate['origin'] + '_to_' + zone
                passages[direction + '/' + side] += 1
                passage_journeys.add(ident)
                if len(passage_examples) < 50:
                    passage_examples.append(dict(journeyId=ident, direction=direction, side=side,
                                                 startedAt=candidate['startedAt'], completedAt=now,
                                                 observedSamples=candidate['samples']))
            state['origin'] = zone
            state['candidate'] = None
        state['previous'] = dict(time=now, position=position, execution=row.get('execution'),
                                 display=row.get('activity') == 'Display' and row.get('targetPoi') == 'display-hall',
                                 stationaryKind=hold_kind, targetPoi=row.get('targetPoi'), groupId=row.get('groupId'),
                                 held=held, holdSerial=hold_serial)
    if sample_id is not None:
        close_sample()
    check(trajectory_count > 0, 'Capture contains no recorded trajectories')
    check(using_count > 0, 'Capture has no actual Display Using samples')
    censored = [ident for ident, state in states.items() if state['candidate']]
    return dict(schema='stationflow.station_fountain_capture_audit.v1', success=error_count == 0,
                checks=checks, errorCount=error_count, errors=errors,
                createdUtc=datetime.now(timezone.utc).isoformat(), capture=str(directory),
                simulatorSchema=manifest.get('schema'), geometry=dict(center=center, radiusMeters=radius, source=layout_source),
                window=dict(firstEventSeconds=first_event, lastEventSeconds=last_event, firstSampleSeconds=first_sample,
                            lastSampleSeconds=last_sample, populationSamples=sample_count, trajectoryRows=trajectory_count,
                            actualSampleIntervalSeconds=sample_intervals.result()),
                basin=dict(minimumBodyCenterRadiusMeters=minimum_clearance if trajectory_count else None,
                           minimumRecord=minimum_record, allowedNumericalToleranceMeters=.005,
                           scope='Every saved trajectory position; body center clearance, not a collision-free or image-tracking certificate.'),
                display=dict(savedStops=len(stops), actualUsingSamples=using_count, nearestStopDistanceMeters=stop_distances.result(),
                             maximumAcceptedArrivalDistanceMeters=.65, physicalOwnershipDiscRadiusMeters=.45,
                             returningSamples=returning_samples, returningUniqueJourneys=len(returning_journeys),
                             sampleEndpointReadingSeconds=observed_reading_seconds,
                             sampleEndpointReturningSeconds=observed_returning_seconds,
                             unassignedTransitionIntervals=display_transition_intervals,
                             scope='Using/display-hall only is reading; ReturningToDisplay is movement and cannot consume reading time in this audit. Durations are sample-endpoint estimates for consecutive matching states, not exact frame dwell; mixed transition intervals and gaps >2 s remain unassigned. The 0.65 m Using limit is unchanged. Duplicate stop check uses stationary bodies <=0.2 m/s in disjoint 0.45 m discs. No lease ledger or exact remaining Display dwell exists in this capture schema.'),
                stationary=dict(telemetryEnabled=telemetry_enabled,
                                acceptanceMode='explicit_recorded_holds' if telemetry_enabled else 'semantic_diagnostics_only',
                                requiredForAcceptance=telemetry_enabled,
                                numericalToleranceMeters=.005, errorCount=stationary_errors,
                                explicitAnchorDistanceMeters=anchor_distances.result(), heldRowsByExecution=dict(held_executions),
                                uniqueExplicitHeldJourneys=len(held_journeys), sameSerialSampleIntervalChecks=same_serial_intervals,
                                diagnosticSemanticEndpointExceedanceCount=semantic_endpoint_exceedances,
                                diagnosticSemanticEndpointExamples=semantic_endpoint_examples,
                                states={kind: dict(distanceMeters=summary.result(), observedIntervalSeconds=stationary_seconds[kind])
                                        for kind, summary in stationary_distances.items()},
                                censoredTransitionOrGapIntervals=stationary_censored, examples=stationary_examples,
                                scope=('Explicit hold telemetry: every held row is compared to its recorded anchor with unchanged 0.005 m tolerance. Consecutive endpoint displacement is checked only when both rows are held with the same journey, hold serial, execution, target and group; new serials, resume/state transitions and gaps >2 s are censored. Labels alone do not prove a hold. Native per-frame evidence remains separate.' if telemetry_enabled else
                                       'Exact hold immobility acceptance is not applicable without explicit hold telemetry. Native NavMeshAgent.isStopped permits avoidance displacement. Consecutive matching Display Using/WCUsing/WaitingForTrain/WaitingForGroup labels and target/group produce endpoint displacement diagnostics; the unchanged 0.005 m threshold counts diagnostic exceedances without adding acceptance errors. Transitions/gaps are censored, and unobserved resume/rehold between samples can produce apparent displacement. This does not certify continuous holds or whole-label immobility. Return walking, Service and GateQueue are excluded. Display stop distance, physical ownership and accueil egress checks remain separate acceptance requirements. Native settings and per-frame evidence remain separate.')),
                annulus=dict(innerRadiusMeters=radius, outerRadiusMeters=radius+3., halfZExtentMeters=radius,
                             positionSamples=dict(annulus_samples), uniqueWestJourneys=len(west), uniqueEastJourneys=len(east),
                             uniqueBothSides=len(west & east), scope='Observed occupancy near the basin; not completed arc traversals.'),
                passages=dict(southPlaneZ=south, northPlaneZ=north, maximumAbsXFromCenter=hall_half_width,
                              completed=dict(passages), uniqueJourneys=len(passage_journeys), examples=passage_examples,
                              censoredCandidates=len(censored), censoredAtCaptureEnd=sum(i not in ended for i in censored),
                              endedBeforeOppositeBoundary=sum(i in ended for i in censored), continuityResets=continuity_resets,
                              scope='Observed continuous south-to-north/north-to-south hall passages. Side is interpolated at center-z crossing. Gaps >2 s, steps >3*dt+0.4 m, or excursions outside x corridor censor candidates; no 50/50 requirement. Passage completion is independent of terminal journey completion.'),
                accueil=dict(successfulCounterServices=info_served, clearanceStarts=info_clearing, activityCompletions=info_completed,
                             serviceFrontDistanceMeters=served_distances.result(), completedDepartureDistanceMeters=exit_distances.result(),
                             openSuccessfulVisits=len(counter_pending), examples=counter_examples,
                             scope='Actual Info service-completion, clearance-start and activity-completion events. Physical social FIFO joins are not in JSONL, so this audit makes no FIFO-order claim.'),
                eventKinds=dict(kinds), provenance='Independent offline saved-trajectory/event audit; distinct from native-fountain-observations.json. Uses actual sample/event times, keeps unfinished observations censored, streams JSONL with state bounded by observed journeys and current sample.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('captureDirectory')
    parser.add_argument('--output', required=True)
    parser.add_argument('--layout', help='Optional saved fountain-authoring.json geometry sidecar')
    args = parser.parse_args()
    try:
        result = audit(args.captureDirectory, args.layout)
    except (OSError, ValueError, KeyError, TypeError) as exc:
        result = dict(schema='stationflow.station_fountain_capture_audit.v1', success=False, checks=0, errorCount=1,
                      errors=[str(exc)], capture=str(Path(args.captureDirectory).resolve()))
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False) + '\n', encoding='utf-8')
    print(json.dumps(dict(success=result['success'], checks=result['checks'], errorCount=result['errorCount'],
                          errors=result['errors'], output=str(output.resolve()))))
    raise SystemExit(0 if result['success'] else 1)


if __name__ == '__main__':
    main()
