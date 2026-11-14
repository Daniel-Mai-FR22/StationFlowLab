using System;
using System.Collections.Generic;

namespace StationFlow.TrainStation.DecisionLab
{
    public enum DecisionArrivalProfile { EarlyService, Ordinary, LastMinute, Alighting }
    public enum DecisionJourneyPurpose { Boarding, Alighting }
    public enum DecisionJourneyStatus { Scheduled, WaitingForEntry, Active, Boarded, Exited, Unserved, Failed }

    [Serializable]
    public sealed class DecisionArrivalService
    {
        public string serviceId, trainNumber;
        public int trackNumber, expectedBoarding, expectedAlighting, throughPassengers;
        public int[] coachSeatCapacities = Array.Empty<int>();
        public float arrivalSeconds, departureSeconds, doorSeconds;
    }

    [Serializable]
    public sealed class DecisionArrivalSettings
    {
        public string scenarioDate = "2026-10-06";
        public int seed = 20261006;
        public double visualActorRatio = .1d, earlyRatio = .25d, ordinaryRatio = .6d, lateRatio = .15d, groupPeopleFraction = .35d;
        public double earlyMinSeconds = 1200d, earlyMaxSeconds = 2400d;
        public double ordinaryMinSeconds = 600d, ordinaryMaxSeconds = 1200d;
        public double lateMinSeconds = 180d, lateMaxSeconds = 420d;
        public DecisionEntranceWeight[] entranceWeights = Array.Empty<DecisionEntranceWeight>();
        public DecisionAlightingStopoverSettings alightingStopovers = new DecisionAlightingStopoverSettings();
    }

    /// <summary>A generated manifest snapshot. Runtime status belongs to a separate journey record.</summary>
    [Serializable]
    public sealed class DecisionArrivalPlan
    {
        public string journeyId, serviceId, trainNumber, groupId, entranceId, scenarioDate, ticketId;
        public int representedPassengers, trackNumber, groupMemberIndex, groupSize = 1;
        public int coachNumber;
        public int[] seatNumbers = Array.Empty<int>();
        public DecisionArrivalProfile arrivalProfile;
        public DecisionJourneyPurpose purpose;
        public float plannedEntrySeconds, publishedArrivalSeconds, publishedDepartureSeconds, leadSeconds;
        public float walkSpeed, fastWalkSpeed, familiarity, safetyMarginSeconds;
        public DecisionMobilityProfile mobilityProfile;
        public bool clippedAtSimulationStart;
        public DecisionNeeds needs;
        public bool alightingServiceVisitor;
        public int alightingServiceVisitLimit;
        public float alightingStopoverSeconds;
    }

    public static class DecisionArrivalPlanner
    {
        public static List<DecisionArrivalPlan> Generate(IReadOnlyList<DecisionArrivalService> services,
            IReadOnlyList<string> entranceIds, DecisionArrivalSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (services == null || entranceIds == null || entranceIds.Count == 0) throw new ArgumentException("Services and entrance IDs are required.");
            if (!DateTime.TryParseExact(settings.scenarioDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _)) throw new ArgumentException("Scenario date must use yyyy-MM-dd.");
            if (!Finite(settings.visualActorRatio) || settings.visualActorRatio <= 0d || settings.visualActorRatio > 1d ||
                !Unit(settings.groupPeopleFraction) || !NonNegative(settings.earlyRatio) || !NonNegative(settings.ordinaryRatio) ||
                !NonNegative(settings.lateRatio) || settings.earlyRatio + settings.ordinaryRatio + settings.lateRatio <= 0d)
                throw new ArgumentException("Invalid sampling/profile proportions.");
            CheckRange(settings.earlyMinSeconds, settings.earlyMaxSeconds); CheckRange(settings.ordinaryMinSeconds, settings.ordinaryMaxSeconds);
            CheckRange(settings.lateMinSeconds, settings.lateMaxSeconds);
            settings.alightingStopovers?.Validate();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var entranceSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entrance in entranceIds)
                if (string.IsNullOrWhiteSpace(entrance) || !entranceSet.Add(entrance)) throw new ArgumentException("Entrance IDs must be nonempty and unique.");
            var entranceWeights = PrepareEntranceWeights(settings.entranceWeights, entranceSet);
            var result = new List<DecisionArrivalPlan>();
            foreach (var service in services)
            {
                if (service == null || string.IsNullOrWhiteSpace(service.serviceId) || !ids.Add(service.serviceId) || service.trackNumber < 1 ||
                    service.expectedBoarding < 0 || service.expectedAlighting < 0 || service.throughPassengers < 0 || !Finite(service.arrivalSeconds) ||
                    !Finite(service.departureSeconds) || !NonNegative(service.doorSeconds) || service.departureSeconds <= service.arrivalSeconds + 2f * service.doorSeconds)
                    throw new ArgumentException("Invalid or duplicate arrival service.");
                if (service.departureSeconds > 0f)
                { GeneratePurpose(result, service, entranceIds, settings, false, entranceWeights); GeneratePurpose(result, service, entranceIds, settings, true, entranceWeights); }
            }
            foreach (var plan in result) settings.alightingStopovers?.ApplyToPlan(plan, settings.seed);
            DecisionMobilityProfiles.ApplyToPlans(result, settings.seed);
            result.Sort((a, b) => { int time = a.plannedEntrySeconds.CompareTo(b.plannedEntrySeconds); return time != 0 ? time : string.CompareOrdinal(a.journeyId, b.journeyId); });
            return result;
        }

        static void GeneratePurpose(List<DecisionArrivalPlan> output, DecisionArrivalService service, IReadOnlyList<string> entrances,
            DecisionArrivalSettings settings, bool alighting, IReadOnlyList<DecisionEntranceWeight> entranceWeights)
        {
            int demand = alighting ? service.expectedAlighting : service.expectedBoarding;
            var tickets = service.coachSeatCapacities != null && service.coachSeatCapacities.Length > 0 ?
                DecisionSeatTicketAllocator.Allocate(service.coachSeatCapacities, demand, service.throughPassengers, settings.visualActorRatio) : null;
            if (demand == 0) return;
            int count = tickets != null ? tickets.Count : Math.Max(1, Math.Min(demand, (int)Math.Ceiling(Math.Round(demand * settings.visualActorRatio, 6))));
            int grouped = Math.Min(count, (int)Math.Round(count * settings.groupPeopleFraction));
            if (grouped == 1) grouped = 0;
            string prefix = settings.scenarioDate + "/" + service.serviceId + (alighting ? "/A" : "/B");
            var random = new Random(unchecked(settings.seed ^ Hash(prefix)));
            int index = 0, groupOrdinal = 0;
            while (index < count)
            {
                int coachRoom = count - index;
                if (tickets != null)
                { coachRoom = 1; while (index + coachRoom < count && tickets[index + coachRoom].coachNumber == tickets[index].coachNumber) ++coachRoom; }
                int size = grouped >= 2 && coachRoom >= 2 ? Math.Min(coachRoom, Math.Min(grouped, random.Next(2, 5))) : 1;
                if (size > 1 && grouped - size == 1 && size < 4 && size < coachRoom) ++size;
                if (size > 2 && grouped - size == 1) --size;
                if (size > 1) grouped -= size;
                string groupId = size > 1 ? prefix + "/G" + (++groupOrdinal).ToString("00") : "";
                // Preserve the original profile/group/timestamp random stream. A configured
                // catalog uses its own immutable party key rather than consuming that stream.
                string entrance = entrances[random.Next(entrances.Count)];
                if (!alighting && entranceWeights.Count > 0)
                    entrance = SelectWeightedEntrance(entranceWeights, settings.seed,
                        string.IsNullOrEmpty(groupId) ? prefix + "/" + (index + 1).ToString("000") : groupId);
                double draw = random.NextDouble() * (settings.earlyRatio + settings.ordinaryRatio + settings.lateRatio);
                var profile = alighting ? DecisionArrivalProfile.Alighting : draw < settings.earlyRatio ? DecisionArrivalProfile.EarlyService :
                    draw < settings.earlyRatio + settings.ordinaryRatio ? DecisionArrivalProfile.Ordinary : DecisionArrivalProfile.LastMinute;
                double minimum = profile == DecisionArrivalProfile.EarlyService ? settings.earlyMinSeconds :
                    profile == DecisionArrivalProfile.Ordinary ? settings.ordinaryMinSeconds : settings.lateMinSeconds;
                double maximum = profile == DecisionArrivalProfile.EarlyService ? settings.earlyMaxSeconds :
                    profile == DecisionArrivalProfile.Ordinary ? settings.ordinaryMaxSeconds : settings.lateMaxSeconds;
                float lead = (float)(minimum + random.NextDouble() * (maximum - minimum));
                float entry = alighting ? service.arrivalSeconds + service.doorSeconds + (float)random.NextDouble() * 18f : service.departureSeconds - lead;
                // Group members share a profile, entry, service and needs, but hold separate physical leases.
                var commonNeeds = new DecisionNeeds { thirst = profile == DecisionArrivalProfile.EarlyService ? .7f + (float)random.NextDouble() * .3f : .1f + (float)random.NextDouble() * .6f,
                    toilet = (float)random.NextDouble() * .7f, shopping = (float)random.NextDouble() * .8f,
                    fatigue = (float)random.NextDouble() * .8f, information = .1f + (float)random.NextDouble() * .5f };
                for (int member = 0; member < size && index < count; ++member, ++index)
                    output.Add(new DecisionArrivalPlan { journeyId = prefix + "/" + (index + 1).ToString("000"), serviceId = service.serviceId, trainNumber = service.trainNumber,
                        scenarioDate = settings.scenarioDate, groupId = groupId, groupMemberIndex = member, groupSize = size,
                        entranceId = alighting ? "platform-" + service.trackNumber : entrance, trackNumber = service.trackNumber,
                        purpose = alighting ? DecisionJourneyPurpose.Alighting : DecisionJourneyPurpose.Boarding, arrivalProfile = profile,
                        representedPassengers = tickets != null ? tickets[index].RepresentedPassengers : demand / count + (index < demand % count ? 1 : 0),
                        coachNumber = tickets != null ? tickets[index].coachNumber : 0,
                        seatNumbers = tickets != null ? (int[])tickets[index].seatNumbers.Clone() : Array.Empty<int>(),
                        ticketId = tickets != null ? prefix + "/" + (index + 1).ToString("000") + "/ticket" : "",
                        publishedArrivalSeconds = service.arrivalSeconds, publishedDepartureSeconds = service.departureSeconds,
                        plannedEntrySeconds = Math.Max(0f, entry) + member * 1.1f, clippedAtSimulationStart = entry < 0f, leadSeconds = alighting ? 0f : lead,
                        walkSpeed = 1.05f + (index % 3) * .1f, fastWalkSpeed = 1.55f + (index % 3) * .05f,
                        familiarity = .2f + (float)random.NextDouble() * .65f, safetyMarginSeconds = 12f + (index % 3) * 3f,
                        needs = new DecisionNeeds { thirst = commonNeeds.thirst, toilet = commonNeeds.toilet, shopping = commonNeeds.shopping,
                            fatigue = commonNeeds.fatigue, information = commonNeeds.information } });
            }
        }

        static IReadOnlyList<DecisionEntranceWeight> PrepareEntranceWeights(DecisionEntranceWeight[] catalog, HashSet<string> entranceIds)
        {
            if (catalog == null || catalog.Length == 0) return Array.Empty<DecisionEntranceWeight>();
            var result = new List<DecisionEntranceWeight>(catalog.Length);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            double total = 0d;
            foreach (var entrance in catalog)
            {
                if (entrance == null || string.IsNullOrWhiteSpace(entrance.entranceId) ||
                    !entranceIds.Contains(entrance.entranceId) || !seen.Add(entrance.entranceId) || !NonNegative(entrance.weight))
                    throw new ArgumentException("Entrance weights require unique saved entrance IDs and finite nonnegative weights.");
                total += entrance.weight;
                result.Add(entrance);
            }
            if (!Finite(total) || total <= 0d) throw new ArgumentException("Entrance weights require a finite positive total.");
            // Inspector ordering is not part of the replay seed.
            result.Sort((a, b) => string.CompareOrdinal(a.entranceId, b.entranceId));
            return result;
        }

        static string SelectWeightedEntrance(IReadOnlyList<DecisionEntranceWeight> entrances, int seed, string partyKey)
        {
            double total = 0d;
            foreach (var entrance in entrances) total += entrance.weight;
            var random = new Random(unchecked(seed ^ Hash("entrance|" + partyKey)));
            double draw = random.NextDouble() * total;
            string lastPositive = null;
            foreach (var entrance in entrances)
            {
                if (entrance.weight <= 0d) continue;
                lastPositive = entrance.entranceId;
                if (draw < entrance.weight) return entrance.entranceId;
                draw -= entrance.weight;
            }
            // The catalog is validated; this only absorbs floating-point accumulation error.
            return lastPositive;
        }

        static void CheckRange(double a, double b) { if (!NonNegative(a) || !NonNegative(b) || b < a) throw new ArgumentException("Invalid arrival lead range."); }
        static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
        static bool NonNegative(double x) => Finite(x) && x >= 0d;
        static bool Unit(double x) => NonNegative(x) && x <= 1d;
        static int Hash(string value) { uint hash = 2166136261; foreach (char c in value) hash = unchecked((hash ^ c) * 16777619); return unchecked((int)hash); }
    }
}
