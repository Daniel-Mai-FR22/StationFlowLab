"""Bound the explicitly recorded native traversal of saved station gate links.

This is an additional, narrow audit. It never infers a link from an execution
state, position, or speed, and never changes ordinary trajectory bounds. The
caller must retain its independent ticket/grant event and lifecycle checks.
"""
from __future__ import annotations

import math

ENDPOINT_ENVELOPE_METRES = 1.0
AGENT_RADIUS_METRES = .35
ENDPOINT_DATA_Z_TOLERANCE = .35
FLOOR_HEIGHT_TOLERANCE = .15
INTERIOR_POSITION_ALLOWANCE = .25
MAX_NATIVE_SPEED_MULTIPLIER = 3.0
MAX_ADJACENT_APPROACH_METRES = .5


def _number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def _vector(value):
    if not isinstance(value, dict) or not all(_number(value.get(k)) for k in ("x", "y", "z")):
        raise ValueError("missing/nonfinite native link vector")
    return tuple(value[k] for k in ("x", "y", "z"))


def _distance(a, b):
    return math.sqrt(sum((x-y)**2 for x, y in zip(a, b)))


def _corridor(position, start, end, width, approach_extension=0, exit_extension=0):
    """A finite lane with a bounded 2D extension for its adjacent ordinary point.

    Extra lateral and longitudinal distances share one Euclidean movement
    allowance. The approach cannot expand the far exit and vice versa; floor
    tolerance remains fixed. Flagged points supply zero extension.
    """
    dx, dz = end[0]-start[0], end[2]-start[2]
    length = math.hypot(dx, dz)
    ux, uz = dx/length, dz/length
    px, pz = position[0]-start[0], position[2]-start[2]
    along = px*ux+pz*uz
    lateral = abs(px*uz-pz*ux)
    lateral_excess = max(0., lateral - width/2 - AGENT_RADIUS_METRES)
    approach_excess = max(0., -ENDPOINT_ENVELOPE_METRES - along)
    exit_excess = max(0., along - length - ENDPOINT_ENVELOPE_METRES)
    planar_inside = not lateral_excess and not approach_excess and not exit_excess
    if approach_extension > 0:
        # A laterally displaced ordinary approach is still near the entry, not
        # an unflagged escape beside the middle/far end of the native link.
        planar_inside |= along <= ENDPOINT_ENVELOPE_METRES and not exit_excess and \
            math.hypot(lateral_excess, approach_excess) <= approach_extension
    if exit_extension > 0:
        planar_inside |= along >= length - ENDPOINT_ENVELOPE_METRES and not approach_excess and \
            math.hypot(lateral_excess, exit_excess) <= exit_extension
    return (bool(planar_inside) and abs(position[1]-start[1]) <= FLOOR_HEIGHT_TOLERANCE), along, lateral_excess


def audit_native_link_step(manifest, previous, current, profile):
    """Return candidate/allowed/errors/details for one pair of sampled positions.

    An absent opt-in or absent on-link flags yields no exception. A flagged but
    malformed/unauthorized pair yields errors, even if its movement was small.
    Fields and geometry are recorded by Unity from the actual owned NavMeshLink.
    """
    flagged = [r for r in (previous, current) if r and r.get("isOnNativeLink") is True]
    result = {"candidate": bool(flagged), "allowed": False, "errors": [], "details": {}}
    errors = result["errors"]
    if not flagged:
        return result
    if manifest.get("nativeGateLinkTelemetry") is not True:
        errors.append("native link telemetry was not explicitly enabled in the manifest")
        return result
    if manifest.get("controlledPlatformAccess") is not True:
        errors.append("native gate traversal requires controlled platform access")
        return result
    if previous is None:
        errors.append("native link first sample has no previous physical sample")
        return result
    try:
        track = flagged[0].get("nativeLinkTrack")
        direction = flagged[0].get("nativeLinkDirection")
        if type(track) is not int or direction not in ("Ingress", "Egress"):
            raise ValueError("invalid native link track/direction")
        controls = [g for g in manifest.get("platformControls", []) if g.get("track") == track]
        if len(controls) != 1:
            raise ValueError("native link track does not match one saved platform control")
        control = controls[0]
        prefix = direction.lower()
        start, end = _vector(control.get(prefix+"Start")), _vector(control.get(prefix+"End"))
        width = control.get(prefix+"Width")
        length = _distance(start, end)
        if not _number(width) or not 0 < width <= 3 or not 0 < length <= 4:
            raise ValueError("invalid saved native gate link dimensions")
        if abs(start[1]-end[1]) > .03 or abs(start[0]-end[0]) > .03:
            raise ValueError("saved gate link must be level and aligned with the platform")
        if (direction == "Ingress" and end[2] <= start[2]) or (direction == "Egress" and end[2] >= start[2]):
            raise ValueError("saved native gate link direction is reversed")

        profile_limit = max(profile["walkSpeed"], profile["fastWalkSpeed"])
        if not _number(profile_limit) or profile_limit <= 0:
            raise ValueError("invalid passenger speed profile")
        speeds = []
        for row in flagged:
            if row.get("nativeLinkTrack") != track or row.get("nativeLinkDirection") != direction:
                errors.append("consecutive native link samples changed track/direction")
            actual_start, actual_end = _vector(row.get("nativeLinkStart")), _vector(row.get("nativeLinkEnd"))
            for actual, authored in ((actual_start, start), (actual_end, end)):
                if (abs(actual[0]-authored[0]) > width/2+AGENT_RADIUS_METRES or
                        abs(actual[1]-authored[1]) > FLOOR_HEIGHT_TOLERANCE or
                        abs(actual[2]-authored[2]) > ENDPOINT_DATA_Z_TOLERANCE):
                    errors.append("native link data endpoints differ from the saved owned link")
            requested = row.get("requestedNavigationSpeed")
            if not _number(requested) or requested <= 0 or requested > profile_limit+.01:
                errors.append("native requested speed is invalid or exceeds the passenger profile")
            else:
                speeds.append(requested)
            mask = row.get("navigationAreaMask")
            if type(mask) is not int:
                errors.append("native traversal lacks an integer navigation area mask")
            if direction == "Ingress":
                area = control.get("ingressArea")
                if (type(area) is not int or area not in (3, 5, 6, 7) or type(mask) is not int or not mask & (1 << area)):
                    errors.append("native ingress lacks its own saved permission area")
                if type(mask) is int and any(mask & (1 << other) for other in (3, 5, 6, 7) if other != area):
                    errors.append("native ingress mask also permits another platform gate")
                if profile.get("trackNumber") != track or profile.get("purpose") not in ("Boarding", 0):
                    errors.append("native ingress uses the wrong assigned track/purpose")
                if row.get("platformAccessGranted") is not True:
                    errors.append("native ingress lacks a granted ticket-control token")
            elif type(mask) is int and not mask & (1 << 4):
                errors.append("native egress lacks the always-free egress area")

        previous_time, current_time = previous.get("time"), current.get("time")
        if not _number(previous_time) or not _number(current_time) or current_time <= previous_time:
            raise ValueError("native link sample clock must strictly increase")
        dt = current_time-previous_time
        requested = max(speeds) if speeds else 0
        previous_on, current_on = previous.get("isOnNativeLink") is True, current.get("isOnNativeLink") is True
        transition = previous_on != current_on
        adjacent_ordinary_step = min(requested*dt, MAX_ADJACENT_APPROACH_METRES)
        # Only the adjacent *unflagged* position may be one ordinary sampling
        # interval farther in 2D around its proper approach/exit endpoint.
        # Recorded on-link points keep the fixed lane/envelope; the floor and
        # native speed/snap bounds remain fixed for every point.
        approach_extension = adjacent_ordinary_step if not previous_on and current_on else 0
        exit_extension = adjacent_ordinary_step if previous_on and not current_on else 0
        before, after = _vector(previous.get("position")), _vector(current.get("position"))
        before_inside, before_along, before_lateral_excess = _corridor(before, start, end, width, approach_extension=approach_extension)
        after_inside, after_along, after_lateral_excess = _corridor(after, start, end, width, exit_extension=exit_extension)
        if not before_inside or not after_inside:
            errors.append("native link step leaves the finite saved lane/endpoint envelope")
        if after_along < before_along-.35:
            errors.append("native link movement reverses its one-way direction")
        step = _distance(before, after)
        allowance = ENDPOINT_ENVELOPE_METRES if transition else INTERIOR_POSITION_ALLOWANCE
        continuity_limit = (MAX_NATIVE_SPEED_MULTIPLIER*requested+.25)*dt+allowance
        if step > continuity_limit:
            errors.append("native link movement exceeds its bounded speed/endpoint snap allowance")
        if step > length+2*ENDPOINT_ENVELOPE_METRES:
            errors.append("native link step exceeds the entire finite link/endpoint envelope")
        result["details"] = dict(track=track, direction=direction, stepMetres=step,
                                 intervalSeconds=dt, requestedSpeed=requested,
                                 sampledSpeed=step/dt, continuityLimitMetres=continuity_limit,
                                 endpointTransition=transition,
                                 ordinaryApproachExtensionMetres=approach_extension,
                                 ordinaryExitExtensionMetres=exit_extension,
                                 ordinaryApproachLateralExcessMetres=before_lateral_excess if approach_extension else 0,
                                 ordinaryExitLateralExcessMetres=after_lateral_excess if exit_extension else 0,
                                 usedSampleIntervalGeometryAllowance=(before_along < -ENDPOINT_ENVELOPE_METRES or
                                                                      after_along > length+ENDPOINT_ENVELOPE_METRES or
                                                                      approach_extension > 0 and before_lateral_excess > 0 or
                                                                      exit_extension > 0 and after_lateral_excess > 0))
        result["allowed"] = not errors
    except (ValueError, KeyError, TypeError, ZeroDivisionError, OverflowError) as error:
        errors.append(str(error))
    return result
