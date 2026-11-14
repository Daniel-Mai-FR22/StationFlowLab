#if UNITY_EDITOR
using System.Linq;
using TMPro;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace StationFlow.TrainStation.Social.Editor
{
    public static class StationSocialPresentation
    {
        public static void Ensure(StationSocialWorld world)
        {
            var sceneRoots = world.gameObject.scene.GetRootGameObjects();
            var canvas = sceneRoots.SelectMany(r => r.GetComponentsInChildren<Canvas>(true)).First(c => c.name.Contains("Interface"));
            if (canvas.GetComponentInChildren<StationSocialDashboard>(true)) { RefreshPopulation(world); return; }
            foreach (var text in canvas.GetComponentsInChildren<TMP_Text>(true))
            {
                if (text.name == "État statique") text.text = "SCENARIO SOCIAL ACTIF";
                if (text.name == "Note de contexte") text.text = "Accueil, entretien et voyageurs.\nActeurs et parcours éditables.";
            }
            var panel = Rect("Scénario social — commandes éditables", canvas.transform, new Vector2(-20, -104), new Vector2(275, 310));
            panel.anchorMin = panel.anchorMax = new Vector2(1, 1); panel.pivot = new Vector2(1, 1);
            panel.gameObject.AddComponent<Image>().color = new Color(.025f, .047f, .063f, .97f);
            var dashboard = panel.gameObject.AddComponent<StationSocialDashboard>(); dashboard.world = world;
            Text("Titre", panel, "VIE DE LA GARE", 18, 15, 240, 28, 17, new Color(.44f, .90f, .78f));
            dashboard.statusLabel = Text("Etats", panel, dashboard.PopulationSummary(SeatedCount(world)), 18, 51, 240, 77, 12, Color.white);
            dashboard.eventLabel = Text("Dernier événement", panel, "PLAY : événements aléatoires\nou demande manuelle ci-dessous.", 18, 132, 240, 58, 12, new Color(.65f, .75f, .78f));
            Button(panel, "Déclencher une visite", 200, dashboard.RequestOneRandomVisit);
            dashboard.automaticLabel = Button(panel, "EVENEMENTS AUTO : ON", 250, dashboard.ToggleAutomatic);
            UnityEventTools.AddPersistentListener(world.informationDesk.customerApproached, dashboard.OnCustomerApproached);
            UnityEventTools.AddPersistentListener(world.informationDesk.conversationStarted, dashboard.OnConversationStarted);
            UnityEventTools.AddPersistentListener(world.informationDesk.conversationEnded, dashboard.OnConversationEnded);
            RefreshPopulation(world);
        }
        public static void RefreshPopulation(StationSocialWorld world)
        {
            var dashboard = world.gameObject.scene.GetRootGameObjects()
                .SelectMany(r => r.GetComponentsInChildren<StationSocialDashboard>(true)).FirstOrDefault();
            if (!dashboard || !dashboard.statusLabel) return;
            dashboard.world = world;
            dashboard.statusLabel.text = dashboard.PopulationSummary(SeatedCount(world));
            dashboard.statusLabel.fontSize = 12;
            foreach (var button in dashboard.GetComponentsInChildren<Button>(true))
            {
                if (button.onClick.GetPersistentEventCount() == 0) continue;
                if (button.onClick.GetPersistentMethodName(0) != nameof(StationSocialDashboard.RequestOneVisit)) continue;
                UnityEventTools.RemovePersistentListener(button.onClick, 0);
                UnityEventTools.AddPersistentListener(button.onClick, dashboard.RequestOneRandomVisit);
                var label = button.GetComponentInChildren<TMP_Text>(true);
                if (label) { label.text = "Déclencher une visite"; UnityEditor.EditorUtility.SetDirty(label); }
                UnityEditor.EditorUtility.SetDirty(button);
            }
            foreach (var desk in world.shopDesks.Where(d => d).Concat(new[] { world.informationDesk }).Where(d => d))
            {
                Bind(desk.customerApproached, dashboard, dashboard.OnCustomerApproached);
                Bind(desk.conversationStarted, dashboard, dashboard.OnConversationStarted);
                Bind(desk.conversationEnded, dashboard, dashboard.OnConversationEnded);
                UnityEditor.EditorUtility.SetDirty(desk);
            }
            UnityEditor.EditorUtility.SetDirty(dashboard);
            UnityEditor.EditorUtility.SetDirty(dashboard.statusLabel);
        }
        static int SeatedCount(StationSocialWorld world) => world.passengers.Count(p => p && p.activity == StationPassengerActivity.Seated);
        static void Bind(StationPassengerEvent signal, StationSocialDashboard dashboard, UnityAction<StationPassenger> action)
        {
            for (int i = 0; i < signal.GetPersistentEventCount(); i++)
                if (signal.GetPersistentTarget(i) == dashboard && signal.GetPersistentMethodName(i) == action.Method.Name) return;
            UnityEventTools.AddPersistentListener(signal, action);
        }
        static TMP_Text Button(Transform parent, string caption, float y, UnityAction action)
        {
            var rect = Rect(caption, parent, new Vector2(18, -y), new Vector2(239, 36));
            var image = rect.gameObject.AddComponent<Image>(); image.color = new Color(.09f, .28f, .30f);
            var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image;
            UnityEventTools.AddPersistentListener(button.onClick, action);
            var label = Text("Libellé", rect, caption, 5, 0, 229, 36, 12, Color.white);
            label.alignment = TextAlignmentOptions.Center;
            return label;
        }
        static TextMeshProUGUI Text(string name, Transform parent, string value, float x, float y, float w, float h, float size, Color color)
        {
            var rect = Rect(name, parent, new Vector2(x, -y), new Vector2(w, h));
            var label = rect.gameObject.AddComponent<TextMeshProUGUI>(); label.font = TMP_Settings.defaultFontAsset;
            label.text = value; label.fontSize = size; label.color = color; label.raycastTarget = false;
            return label;
        }
        static RectTransform Rect(string name, Transform parent, Vector2 position, Vector2 size)
        {
            var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rect.SetParent(parent, false); rect.anchorMin = rect.anchorMax = new Vector2(0, 1); rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = position; rect.sizeDelta = size; return rect;
        }
    }
}
#endif
