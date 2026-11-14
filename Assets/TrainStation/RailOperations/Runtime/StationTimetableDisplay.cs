using System;
using System.Linq;
using TMPro;
using UnityEngine;

namespace StationFlow.TrainStation.RailOperations
{
    public enum StationTimetableMode { Departures, Arrivals, Platform, Traffic }

    [Serializable]
    public sealed class StationTimetableRow
    {
        public TMP_Text time, location, train, track, status;
    }

    public struct StationTimetableValues
    {
        public string serviceId, time, location, train, track, status;
        public StationTrainState state;
    }

    /// <summary>Updates authored text fields from the same clock and train state used by the simulation.</summary>
    [DisallowMultipleComponent]
    public sealed class StationTimetableDisplay : MonoBehaviour
    {
        public StationRailOperations operations;
        public StationTimetableMode mode;
        public int[] trackFilter = Array.Empty<int>();
        public StationTimetableRow[] rows = Array.Empty<StationTimetableRow>();
        public TMP_Text clockLabel, pauseLabel, rateLabel;
        public Transform hourHand, minuteHand;

        void LateUpdate() => RefreshDisplay();

        public StationTimetableValues GetRowValues(int rowIndex)
        {
            if (!operations || !operations.schedule || operations.schedule.services == null) return default;
            var indices = operations.schedule.finiteOperatingDay
                ? operations.GetUpcomingServiceIndices(mode == StationTimetableMode.Arrivals)
                : Enumerable.Range(0, operations.schedule.services.Length).ToArray();
            var candidates = indices
                .Select(operations.GetServiceSnapshot)
                .Where(s => s.service != null && (trackFilter == null || trackFilter.Length == 0 || trackFilter.Contains(s.service.trackNumber)));
            if (operations.schedule.finiteOperatingDay && mode == StationTimetableMode.Platform)
                candidates = candidates.GroupBy(s => s.service.trackNumber).Select(g => g.OrderBy(s => s.departureSeconds).First());
            var snapshots = candidates
                .OrderBy(s => mode == StationTimetableMode.Platform ? s.service.trackNumber :
                    mode == StationTimetableMode.Arrivals ? s.arrivalSeconds : s.departureSeconds)
                .ToArray();
            if (rowIndex < 0 || rowIndex >= snapshots.Length) return default;
            var snapshot = snapshots[rowIndex];
            var service = snapshot.service;
            return new StationTimetableValues
            {
                serviceId = service.serviceId,
                time = mode == StationTimetableMode.Traffic
                    ? FormatTime(snapshot.arrivalSeconds) + " / " + FormatTime(snapshot.departureSeconds)
                    : FormatTime(mode == StationTimetableMode.Arrivals ? snapshot.arrivalSeconds : snapshot.departureSeconds),
                location = mode == StationTimetableMode.Arrivals ? service.origin : service.destination,
                train = service.trainNumber,
                track = service.trackNumber.ToString(),
                status = StatusText(snapshot.state, mode),
                state = snapshot.state
            };
        }

        public void RefreshDisplay()
        {
            if (!operations || !operations.schedule) return;
            if (clockLabel) clockLabel.text = FormatTime(operations.ClockSeconds, true);
            if (pauseLabel) pauseLabel.text = operations.IsPaused ? "REPRENDRE" : "PAUSE";
            if (rateLabel) rateLabel.text = "1 s réelle = " + operations.simulationRate.ToString("0.#") + " s gare";
            for (int i = 0; i < rows.Length; ++i)
            {
                var row = rows[i]; if (row == null) continue;
                var value = GetRowValues(i);
                Set(row.time, value.time); Set(row.location, value.location); Set(row.train, value.train);
                Set(row.track, value.track); Set(row.status, value.status);
                if (row.status) row.status.color = StatusColor(value.state);
            }
            double seconds = operations.ClockSeconds;
            if (hourHand) hourHand.localRotation = Quaternion.Euler(0, 0, (float)(-seconds / 120.0 % 360.0));
            if (minuteHand) minuteHand.localRotation = Quaternion.Euler(0, 0, (float)(-seconds / 10.0 % 360.0));
        }

        public void TogglePause() { if (Application.isPlaying && operations) operations.TogglePause(); RefreshDisplay(); }
        public void ResetSimulation() { if (Application.isPlaying && operations) operations.ResetSimulation(); RefreshDisplay(); }

        public static string FormatTime(double seconds, bool includeSeconds = false)
        {
            long whole = (long)Math.Floor(Math.Max(0, seconds));
            int hours = (int)(whole / 3600 % 24), minutes = (int)(whole / 60 % 60), remainder = (int)(whole % 60);
            return hours.ToString("00") + ":" + minutes.ToString("00") + (includeSeconds ? ":" + remainder.ToString("00") : "");
        }

        public static string StatusText(StationTrainState state, StationTimetableMode mode)
        {
            switch (state)
            {
                case StationTrainState.Arriving: return "EN APPROCHE";
                case StationTrainState.DoorsOpening: return "ARRIVÉ / OUVERTURE";
                case StationTrainState.Boarding: return "À QUAI";
                case StationTrainState.DoorsClosing: return "DÉPART IMMINENT";
                case StationTrainState.Departing: return "DÉPART EN COURS";
                default: return mode == StationTimetableMode.Arrivals ? "ATTENDU" : "PRÉVU";
            }
        }

        static Color StatusColor(StationTrainState state)
        {
            if (state == StationTrainState.Boarding || state == StationTrainState.DoorsOpening) return new Color(.44f, .90f, .78f);
            if (state == StationTrainState.DoorsClosing || state == StationTrainState.Departing) return new Color(1f, .78f, .18f);
            return new Color(.72f, .82f, .91f);
        }
        static void Set(TMP_Text text, string value) { if (text) text.text = value ?? ""; }
    }
}
