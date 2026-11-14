using System;
using System.Collections.Generic;
using System.Linq;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Pure arrival-catalog contracts; no scenes, agents or Unity state are modified.</summary>
    public static class StationDecisionEntrancePlannerTests
    {
        static readonly string[] Entrances = { "entry-main", "entry-west", "entry-mid-west", "entry-mid-east", "entry-east" };

        public static int Run()
        {
            int checks = 0;
            RunAll((name, test) => { test(); ++checks; });
            return checks;
        }

        public static void RunAll(Action<string, Action> run)
        {
            run("missing entrance catalogs retain the uniform seeded baseline", MissingCatalog);
            run("weighted entrance plans replay with one portal per party", ReplayAndParties);
            run("zero-weight entrances exclude incoming flow while all positive entrances receive it", PositiveFlow);
            run("catalog and portal reordering cannot alter weighted replay", OrderNeutrality);
            run("new entrances and weights preserve times needs demand groups coaches and tickets", NonEntranceBaseline);
            run("unrelated train services cannot perturb an existing entrance plan", ServiceIsolation);
            run("relative entrance weights influence stochastic party flow", RelativeWeights);
            run("invalid entrance catalogs cannot invent an inbound assignment", InvalidCatalogs);
        }

        static void MissingCatalog()
        {
            var settings = Settings(); settings.entranceWeights = null;
            var original = DecisionArrivalPlanner.Generate(Services(), new[] { "entry-main", "entry-west" }, settings);
            settings.entranceWeights = Array.Empty<DecisionEntranceWeight>();
            var empty = DecisionArrivalPlanner.Generate(Services(), new[] { "entry-main", "entry-west" }, settings);
            Same(original, empty, true);
            Check(original.Where(p => p.purpose == DecisionJourneyPurpose.Boarding).Select(p => p.entranceId).Distinct().Count() == 2,
                "The compatibility baseline no longer uses both saved entrances.");
        }

        static void ReplayAndParties()
        {
            var first = Weighted(); var replay = Weighted();
            Same(first, replay, true);
            var parties = first.Where(p => !string.IsNullOrEmpty(p.groupId)).GroupBy(p => p.groupId).ToArray();
            Check(parties.Length > 0, "Fixture needs real companion parties.");
            Check(parties.All(g => g.Select(p => p.entranceId).Distinct().Count() == 1), "Companions were assigned different portals.");
            Check(parties.All(g => g.All(p => p.groupSize == g.Count())), "Entrance selection changed party membership.");
        }

        static void PositiveFlow()
        {
            var plans = Weighted();
            var boarding = plans.Where(p => p.purpose == DecisionJourneyPurpose.Boarding).ToArray();
            Check(boarding.All(p => p.entranceId != "entry-main"), "A zero-weight exit entrance received incoming passengers.");
            Check(Entrances.Skip(1).All(id => boarding.Any(p => p.entranceId == id)), "A positive entrance received no seeded flow.");
            Check(plans.Where(p => p.purpose == DecisionJourneyPurpose.Alighting).All(p => p.entranceId == "platform-" + p.trackNumber),
                "Boarding entrance weights altered train-door alighting portals.");
        }

        static void OrderNeutrality()
        {
            var settings = Settings(); settings.entranceWeights = Catalog();
            var original = DecisionArrivalPlanner.Generate(Services(), Entrances, settings);
            settings.entranceWeights = Catalog().Reverse().ToArray();
            var reordered = DecisionArrivalPlanner.Generate(Services(), Entrances.Reverse().ToArray(), settings);
            Same(original, reordered, true);
        }

        static void NonEntranceBaseline()
        {
            var original = DecisionArrivalPlanner.Generate(Services(), new[] { "entry-main", "entry-west" }, Settings());
            Same(original, Weighted(), false);
        }

        static void ServiceIsolation()
        {
            var settings = Settings(); settings.entranceWeights = Catalog();
            var services = Services();
            var original = DecisionArrivalPlanner.Generate(services, Entrances, settings);
            var extra = Services(1)[0]; extra.serviceId = "unrelated-train"; extra.departureSeconds += 10000f;
            var extended = DecisionArrivalPlanner.Generate(services.Concat(new[] { extra }).Reverse().ToArray(), Entrances, settings);
            Same(original, extended.Where(p => p.serviceId != extra.serviceId).ToList(), true);
        }

        static void RelativeWeights()
        {
            var settings = Settings(); settings.entranceWeights = new[] {
                new DecisionEntranceWeight { entranceId = "entry-main", weight = 0d },
                new DecisionEntranceWeight { entranceId = "entry-west", weight = 3d },
                new DecisionEntranceWeight { entranceId = "entry-east", weight = 1d } };
            var plans = DecisionArrivalPlanner.Generate(Services(24), Entrances, settings);
            var parties = plans.Where(p => p.purpose == DecisionJourneyPurpose.Boarding)
                .GroupBy(p => string.IsNullOrEmpty(p.groupId) ? p.journeyId : p.groupId).Select(g => g.First()).ToArray();
            double share = parties.Count(p => p.entranceId == "entry-west") / (double)parties.Length;
            Check(share > .68d && share < .82d, "A 3:1 catalog did not produce the expected stochastic entrance bias.");
            Check(parties.All(p => p.entranceId == "entry-west" || p.entranceId == "entry-east"), "An unlisted entrance received configured flow.");
        }

        static void InvalidCatalogs()
        {
            Reject(new DecisionEntranceWeight[] { null });
            Reject(new[] { new DecisionEntranceWeight { entranceId = "unknown-door", weight = 1d } });
            Reject(new[] { new DecisionEntranceWeight { entranceId = "entry-west", weight = 0d } });
            foreach (double weight in new[] { -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                Reject(new[] { new DecisionEntranceWeight { entranceId = "entry-west", weight = weight } });
            Reject(new[] { new DecisionEntranceWeight { entranceId = "entry-west", weight = 1d },
                new DecisionEntranceWeight { entranceId = "entry-west", weight = 2d } });
            Reject(new[] { new DecisionEntranceWeight { entranceId = "entry-west", weight = double.MaxValue },
                new DecisionEntranceWeight { entranceId = "entry-east", weight = double.MaxValue } });
        }

        static void Reject(DecisionEntranceWeight[] catalog)
        {
            var settings = Settings(); settings.entranceWeights = catalog;
            try { DecisionArrivalPlanner.Generate(Services(1), Entrances, settings); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("An invalid entrance catalog was accepted.");
        }

        static List<DecisionArrivalPlan> Weighted()
        {
            var settings = Settings(); settings.entranceWeights = Catalog();
            return DecisionArrivalPlanner.Generate(Services(), Entrances, settings);
        }

        static DecisionArrivalSettings Settings() => new DecisionArrivalSettings { visualActorRatio = .5d, groupPeopleFraction = .4d };
        static DecisionEntranceWeight[] Catalog() => Entrances.Select(id => new DecisionEntranceWeight {
            entranceId = id, weight = id == "entry-main" ? 0d : 1d }).ToArray();
        static DecisionArrivalService[] Services(int count = 8) => Enumerable.Range(1, count).Select(i => new DecisionArrivalService {
            serviceId = "entrance-fixture-" + i, trainNumber = "T" + i, trackNumber = 1 + i % 3,
            expectedBoarding = 200, expectedAlighting = 60, throughPassengers = 0,
            coachSeatCapacities = new[] { 55, 55, 55, 55 }, arrivalSeconds = 1000f + i * 900f,
            departureSeconds = 2000f + i * 900f, doorSeconds = 8f }).ToArray();

        static void Same(IReadOnlyList<DecisionArrivalPlan> original, IReadOnlyList<DecisionArrivalPlan> replay, bool entrance)
        {
            Check(original.Count == replay.Count, "Entrance configuration changed visible cohort count.");
            var byId = replay.ToDictionary(p => p.journeyId);
            foreach (var a in original)
            {
                Check(byId.TryGetValue(a.journeyId, out var b), "Entrance configuration changed immutable journey identity.");
                Check(!entrance || a.entranceId == b.entranceId, "Weighted entrance assignment did not replay.");
                Check(a.serviceId == b.serviceId && a.trainNumber == b.trainNumber && a.scenarioDate == b.scenarioDate &&
                    a.purpose == b.purpose && a.arrivalProfile == b.arrivalProfile && a.groupId == b.groupId &&
                    a.groupMemberIndex == b.groupMemberIndex && a.groupSize == b.groupSize && a.trackNumber == b.trackNumber &&
                    a.representedPassengers == b.representedPassengers && a.coachNumber == b.coachNumber && a.ticketId == b.ticketId &&
                    a.seatNumbers.SequenceEqual(b.seatNumbers), "Entrance configuration altered group, demand or ticket allocation.");
                Check(a.plannedEntrySeconds == b.plannedEntrySeconds && a.publishedArrivalSeconds == b.publishedArrivalSeconds &&
                    a.publishedDepartureSeconds == b.publishedDepartureSeconds && a.leadSeconds == b.leadSeconds &&
                    a.clippedAtSimulationStart == b.clippedAtSimulationStart, "Entrance selection perturbed the train-relative arrival clock.");
                Check(a.walkSpeed == b.walkSpeed && a.fastWalkSpeed == b.fastWalkSpeed && a.familiarity == b.familiarity &&
                    a.safetyMarginSeconds == b.safetyMarginSeconds && a.needs.thirst == b.needs.thirst && a.needs.toilet == b.needs.toilet &&
                    a.needs.shopping == b.needs.shopping && a.needs.fatigue == b.needs.fatigue && a.needs.information == b.needs.information,
                    "Entrance selection consumed the passenger profile random stream.");
                Check(a.alightingServiceVisitor == b.alightingServiceVisitor && a.alightingServiceVisitLimit == b.alightingServiceVisitLimit &&
                    a.alightingStopoverSeconds == b.alightingStopoverSeconds, "Entrance configuration changed post-train service plans.");
            }
        }

        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
