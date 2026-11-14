using System.Linq;
using TMPro;
using UnityEngine;

namespace StationFlow.TrainStation.Social
{
    /// <summary>Controls and status for the saved station scenario. Never creates actors or UI.</summary>
    public sealed class StationSocialDashboard : MonoBehaviour
    {
        public StationSocialWorld world;
        public TMP_Text statusLabel, eventLabel, automaticLabel;
        float nextRefresh;
        int nextPassenger;

        void Update()
        {
            if (Time.unscaledTime < nextRefresh || !world) return;
            nextRefresh = Time.unscaledTime + .2f;
            int seated = world.seats.Count(s => s && s.Occupant);
            if (statusLabel) statusLabel.text = PopulationSummary(seated);
            RefreshAutomatic();
        }
        public string PopulationSummary(int seated)
        {
            if (!world) return "";
            var desk = world.informationDesk;
            int walking = world.passengers.Count(p => p && (Application.isPlaying ? p.IsWalking : (p.activity == StationPassengerActivity.WalkingGroup || p.activity == StationPassengerActivity.SoloWalking)));
            int shopping = world.shopDesks.Where(d => d).Sum(d => d.QueueCount);
            return $"VOYAGEURS {world.TotalPassengerCount:00}  /  ASSIS {seated:00}\n" +
                $"MARCHE {walking:00}  /  GROUPES {world.ConversationGroupCount + world.walkingGroups.Length:00}\n" +
                $"FILES : ACCUEIL {(desk ? desk.QueueCount : 0):00} / ACHATS {shopping:00}\n" +
                $"COMMERCES {world.shopDesks.Length:00} / ENTRETIEN {world.cleaners.Length:00}";
        }
        public void RequestOneVisit()
        { RequestVisit(false); }
        public void RequestOneRandomVisit()
        { RequestVisit(true); }
        void RequestVisit(bool random)
        {
            if (!Application.isPlaying || !world || world.passengers.Length == 0) return;
            for (int i = 0; i < world.passengers.Length; i++)
            {
                int index = (nextPassenger + i) % world.passengers.Length;
                var passenger = world.passengers[index];
                if (!passenger || !(random ? passenger.TryRequestRandomActivity() : passenger.TryRequestInformation())) continue;
                nextPassenger = (index + 1) % world.passengers.Length;
                if (eventLabel) eventLabel.text = passenger.name + "\nDestination : " + ServiceName(passenger) + ".";
                return;
            }
            if (eventLabel) eventLabel.text = "Voyageurs déjà occupés.\nAttendre la fin d'une activité.";
        }
        public void ToggleAutomatic()
        {
            if (!Application.isPlaying || !world) return;
            bool next = !world.passengers.Any(p => p && p.automaticRequests);
            foreach (var passenger in world.passengers) if (passenger) passenger.automaticRequests = next;
            RefreshAutomatic();
        }
        void RefreshAutomatic()
        {
            if (automaticLabel) automaticLabel.text = "EVENEMENTS AUTO : " + (world.passengers.Any(p => p && p.automaticRequests) ? "ON" : "OFF");
        }
        public void OnCustomerApproached(StationPassenger passenger)
        { if (eventLabel) eventLabel.text = passenger.name + "\nArrivée : " + ServiceName(passenger) + "."; }
        public void OnConversationStarted(StationPassenger passenger)
        { if (eventLabel) eventLabel.text = passenger.name + "\nEchange : " + ServiceName(passenger) + "."; }
        public void OnConversationEnded(StationPassenger passenger)
        { if (eventLabel) eventLabel.text = passenger.name + "\nEchange terminé, reprise de l'activité."; }
        static string ServiceName(StationPassenger passenger)
        { return passenger && passenger.CurrentService ? passenger.CurrentService.serviceName : "Accueil"; }
    }
}
