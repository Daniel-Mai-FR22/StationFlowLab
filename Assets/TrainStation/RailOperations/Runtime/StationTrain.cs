using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace StationFlow.TrainStation.RailOperations
{
    /// <summary>A saved trainset. Only the shared operations clock evaluates its position and doors.</summary>
    [DisallowMultipleComponent]
    public sealed class StationTrain : MonoBehaviour
    {
        public string serviceId;
        public int trackNumber;
        public Transform visualRoot;
        [Tooltip("Route markers must be outside this moving train hierarchy.")]
        public Transform approachPoint;
        public Transform berthPoint;
        public Transform exitPoint;
        [Tooltip("Stationary platform anchors at each coach's platform-side door, numbered from the station head. Dated services require one per physical coach.")]
        public Transform[] coachBoardingAnchors = Array.Empty<Transform>();
        public Transform[] doorPanels = Array.Empty<Transform>();
        public Vector3[] closedDoorLocalPositions = Array.Empty<Vector3>();
        public Vector3[] openDoorLocalOffsets = Array.Empty<Vector3>();
        public bool hideOffstage = true;
        [SerializeField] StationTrainState state;
        [SerializeField] float doorOpenFraction;
        [SerializeField] float motionProgress;
        [SerializeField] float currentSpeedMetresPerSecond;
        [SerializeField] double scheduledArrivalSeconds;
        [SerializeField] double scheduledDepartureSeconds;
        [SerializeField] long arrivalCount;
        [SerializeField] long departureCount;
        StationRailService service;

        public StationRailService Service => service;
        public StationTrainState State => state;
        public float DoorOpenFraction => doorOpenFraction;
        public float MotionProgress => motionProgress;
        public float CurrentSpeedMetresPerSecond => currentSpeedMetresPerSecond;
        public double ScheduledArrivalSeconds => scheduledArrivalSeconds;
        public double ScheduledDepartureSeconds => scheduledDepartureSeconds;
        public long ArrivalCount => arrivalCount;
        public long DepartureCount => departureCount;
        public int PhysicalCoachCount
        {
            get
            {
                if (!visualRoot) return 0;
                int count = 0;
                foreach (Transform child in visualRoot) if (child.name.StartsWith("Car ", StringComparison.Ordinal)) ++count;
                return count;
            }
        }

        public bool TryGetCoachBoardingAnchor(int coachNumber, out Transform anchor)
        {
            anchor = coachBoardingAnchors != null && coachNumber >= 1 && coachNumber <= coachBoardingAnchors.Length ?
                coachBoardingAnchors[coachNumber - 1] : null;
            return anchor && anchor != transform && !anchor.IsChildOf(transform);
        }

        public List<string> ValidateCoachSetup(int expectedCoachCount)
        {
            var errors = new List<string>();
            if (expectedCoachCount < 1 || PhysicalCoachCount != expectedCoachCount)
                errors.Add(name + ": the timetable coach count must match the saved physical car modules.");
            if (coachBoardingAnchors == null || coachBoardingAnchors.Length != expectedCoachCount)
                errors.Add(name + ": assign one stationary boarding anchor per coach.");
            else
            {
                var unique = new HashSet<Transform>();
                for (int i = 0; i < coachBoardingAnchors.Length; ++i)
                    if (!TryGetCoachBoardingAnchor(i + 1, out var anchor) || !Finite(anchor.position) || !unique.Add(anchor))
                        errors.Add(name + ": coach boarding anchors must be unique, finite and outside the moving train.");
            }
            return errors;
        }

        public List<string> ValidateSetup()
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(serviceId)) errors.Add(name + ": assign a service ID.");
            if (!visualRoot || visualRoot == transform || !visualRoot.IsChildOf(transform)) errors.Add(name + ": visualRoot must be a saved child of the moving train root.");
            foreach (var marker in new[] { approachPoint, berthPoint, exitPoint })
            {
                if (!marker || marker == transform || marker.IsChildOf(transform)) errors.Add(name + ": all route markers must exist outside the moving train hierarchy.");
                else if (!Finite(marker.position)) errors.Add(name + ": route positions must be finite.");
            }
            if (approachPoint && berthPoint && exitPoint)
            {
                if (Vector3.Distance(approachPoint.position, berthPoint.position) < 1f || Vector3.Distance(exitPoint.position, berthPoint.position) < 1f)
                    errors.Add(name + ": arrival and departure need distinct staging positions.");
                if (Mathf.Abs(approachPoint.position.x - berthPoint.position.x) > .05f || Mathf.Abs(exitPoint.position.x - berthPoint.position.x) > .05f ||
                    Mathf.Abs(approachPoint.position.y - berthPoint.position.y) > .05f || Mathf.Abs(exitPoint.position.y - berthPoint.position.y) > .05f ||
                    approachPoint.position.z <= berthPoint.position.z || exitPoint.position.z <= berthPoint.position.z)
                    errors.Add(name + ": this terminus uses one straight track along Z, arriving from positive Z and reversing on departure.");
            }
            if (doorPanels == null || closedDoorLocalPositions == null || openDoorLocalOffsets == null ||
                doorPanels.Length != closedDoorLocalPositions.Length || doorPanels.Length != openDoorLocalOffsets.Length)
                errors.Add(name + ": door panel, closed position and opening offset arrays must have matching lengths.");
            else
            {
                var doors = new HashSet<Transform>();
                for (int i = 0; i < doorPanels.Length; ++i)
                {
                    var door = doorPanels[i];
                    if (!door || !door.IsChildOf(transform)) errors.Add(name + ": every door panel must belong to this trainset.");
                    else if (!doors.Add(door)) errors.Add(name + ": a door panel is assigned more than once.");
                    if (!Finite(closedDoorLocalPositions[i]) || !Finite(openDoorLocalOffsets[i])) errors.Add(name + ": door positions and opening offsets must be finite.");
                }
            }
            return errors;
        }

        static bool Finite(Vector3 vector) => StationRailSchedule.Finite(vector.x) && StationRailSchedule.Finite(vector.y) && StationRailSchedule.Finite(vector.z);

        internal void Bind(StationRailService assigned, bool refreshIdentity = false)
        {
            if (service == assigned) return;
            service = assigned;
            if (!refreshIdentity || assigned == null) return;
            serviceId = assigned.serviceId;
            if (visualRoot)
            {
                foreach (var label in visualRoot.GetComponentsInChildren<TMP_Text>(true))
                    if (label.name == "Service display") label.text = "TER " + assigned.trainNumber + "\n" + assigned.destination.ToUpperInvariant();
                if (assigned.CoachCount > 0)
                {
                    int coach = 0;
                    foreach (Transform car in visualRoot)
                    {
                        if (!car.name.StartsWith("Car ", StringComparison.Ordinal)) continue;
                        ++coach;
                        foreach (var label in car.GetComponentsInChildren<TMP_Text>(true))
                            if (label.name == "Car number") label.text = "2   " + coach.ToString("00");
                    }
                }
            }
        }
        internal void ResetCounts() { arrivalCount = 0; departureCount = 0; }
        internal void AddArrivals(long count) => arrivalCount += count;
        internal void AddDepartures(long count) => departureCount += count;

        internal bool Evaluate(double elapsed, double period, double startClock, bool runtimeVisibility, bool finiteDay = false)
        {
            if (service == null || !approachPoint || !berthPoint || !exitPoint) return false;
            var pose = StationRailTiming.Evaluate(service, elapsed, period, startClock, finiteDay);
            bool changed = state != pose.state;
            bool visible = !runtimeVisibility || !hideOffstage || pose.state != StationTrainState.Offstage;
            // Hide before resetting offstage; reveal only after the new approach pose has been assigned.
            if (visualRoot && !visible && visualRoot.gameObject.activeSelf) visualRoot.gameObject.SetActive(false);
            Vector3 position = berthPoint.position;
            currentSpeedMetresPerSecond = 0f;
            if (pose.state == StationTrainState.Arriving)
            {
                position = Vector3.LerpUnclamped(approachPoint.position, berthPoint.position, StationRailTiming.Smooth(pose.progress));
                currentSpeedMetresPerSecond = Vector3.Distance(approachPoint.position, berthPoint.position) /
                    service.approachDurationSeconds * StationRailTiming.SmoothDerivative(pose.progress);
            }
            else if (pose.state == StationTrainState.Departing)
            {
                position = Vector3.LerpUnclamped(berthPoint.position, exitPoint.position, StationRailTiming.Smooth(pose.progress));
                currentSpeedMetresPerSecond = Vector3.Distance(berthPoint.position, exitPoint.position) /
                    service.departureDurationSeconds * StationRailTiming.SmoothDerivative(pose.progress);
            }
            else if (pose.state == StationTrainState.Offstage) position = approachPoint.position;
            transform.SetPositionAndRotation(position, berthPoint.rotation);
            state = pose.state;
            motionProgress = pose.progress;
            doorOpenFraction = pose.doors;
            scheduledArrivalSeconds = pose.arrivalClock;
            scheduledDepartureSeconds = pose.departureClock;
            for (int i = 0; i < doorPanels.Length; ++i)
                if (doorPanels[i]) doorPanels[i].localPosition = closedDoorLocalPositions[i] + openDoorLocalOffsets[i] * doorOpenFraction;
            if (visualRoot && visible && !visualRoot.gameObject.activeSelf) visualRoot.gameObject.SetActive(true);
            return changed;
        }

    }
}
