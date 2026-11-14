using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
#endif

namespace StationFlow.TrainStation.Editor
{
    public static partial class TrainStationBuilder
    {
        static readonly Color HudPanel = new Color(.025f, .047f, .063f, .985f);
        static readonly Color HudTeal = new Color(.44f, .90f, .78f, 1f);
        static readonly Color HudMuted = new Color(.64f, .73f, .77f, 1f);
        static readonly Color HudWhite = new Color(.94f, .96f, .96f, 1f);
        static readonly Color HudLine = new Color(.18f, .27f, .31f, 1f);
        static TMP_FontAsset hudFont;

        /// <summary>Creates authored uGUI objects once, while building the saved scene in the Editor.</summary>
        static void BuildUI(Transform root, Camera[] cameras)
        {
            hudFont = TMP_Settings.defaultFontAsset;
            if (hudFont == null)
                throw new System.InvalidOperationException("Import TMP Essential Resources before building TrainStation.");

            GameObject controlObject = new GameObject("Commandes de présentation — références sérialisées");
            controlObject.transform.SetParent(root, false);
            TrainStationViewControls controls = controlObject.AddComponent<TrainStationViewControls>();
            controls.viewpoints = cameras;

            GameObject canvasObject = new GameObject("Interface — Canvas éditable", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(root, false);
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600, 900);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = .5f;
            controls.interfaceRoot = canvasObject;
            RectTransform canvasRect = (RectTransform)canvasObject.transform;

            RectTransform header = HudPanelObject("01 · En-tête", canvasRect, 0, 0, 0, 82, HudPanel);
            HudStretchHorizontal(header, true, 0, 82);
            RectTransform logoBackground = HudPanelObject("Logo · fond", header, 18, 9, 142, 64, Color.white);
            RectTransform logoRect = HudRect("Efrei Research Lab · logo", logoBackground, 7, 0, 128, 64);
            RawImage logo = logoRect.gameObject.AddComponent<RawImage>();
            logo.texture = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/StationFlowDemo/Resources/Branding/EfreiResearchLab.png");
            logo.uvRect = new Rect(70f / 660f, 240f / 660f, 480f / 660f, 240f / 660f);
            logo.raycastTarget = false;

            HudText("Domaine", header, 184, 14, 290, 18, "ENVIRONNEMENTS DE RECHERCHE", 12, HudTeal, true);
            HudText("Titre", header, 182, 34, 290, 34, "TRAIN STATION", 27, HudWhite, true);
            HudPanelObject("Séparateur titre", header, 488, 20, 1, 42, HudLine);
            HudText("Nom de gare", header, 511, 19, 410, 27, "GARE DE VALMONT", 20, HudWhite, true);
            HudText("Lieu", header, 512, 47, 430, 19, "FRANCE  /  ARCHITECTURE FERROVIAIRE", 12, HudMuted, false);

            RectTransform status = HudRect("État du projet", header, -328, 18, 306, 49);
            status.anchorMin = status.anchorMax = new Vector2(1, 1);
            HudPanelObject("Repère vert", status, 0, 8, 5, 31, HudTeal);
            HudText("État statique", status, 17, 2, 286, 22, "ENVIRONNEMENT STATIQUE", 13, HudTeal, true);
            HudText("État édition", status, 17, 26, 286, 18, "SCÈNE & UI ÉDITABLES DANS UNITY", 11, HudMuted, false);
            HudFullWidth(HudPanelObject("Liseré", header, 0, 80, 0, 2, HudTeal));

            RectTransform catalog = HudPanelObject("02 · Catalogue des espaces", canvasRect, 18, 101, 220, 531, HudPanel);
            HudText("Titre du catalogue", catalog, 17, 16, 186, 21, "EXPLORER LA GARE", 14, HudWhite, true);
            HudText("Sous-titre", catalog, 17, 43, 186, 20, "UN LIEU · PLUSIEURS SCÉNARIOS", 10, HudMuted, false);
            HudPanelObject("Filet supérieur", catalog, 17, 76, 186, 1, HudLine);

            HudMetric(catalog, 17, "08", "VOIES");
            HudMetric(catalog, 116, "04", "QUAIS");
            HudText("Titre des espaces", catalog, 17, 184, 186, 18, "ESPACES & SERVICES", 11, HudTeal, true);
            string[] spaces =
            {
                "Grand hall & accueil", "Billetterie & information", "Cafés & commerces", "Quais & voies ferrées",
                "Galerie de correspondance", "Services voyageurs", "Parvis & accès urbains"
            };
            for (int i = 0; i < spaces.Length; i++)
            {
                HudText("Repère " + (i + 1).ToString("00"), catalog, 17, 218 + i * 27, 25, 21,
                    (i + 1).ToString("00"), 11, HudTeal, true);
                HudText("Espace · " + spaces[i], catalog, 46, 215 + i * 27, 158, 25,
                    spaces[i], 12, HudWhite, false);
            }
            HudPanelObject("Filet inférieur", catalog, 17, 415, 186, 1, HudLine);
            HudText("Note de contexte", catalog, 17, 429, 186, 38,
                "Décor permanent, prêt à accueillir\nde futurs scénarios de recherche.", 11, HudMuted, false);
            Button roofButton = HudButton("Toitures · afficher ou masquer", catalog, 17, 484, 186, 31,
                "MASQUER LES TOITURES", controls.ToggleRoofs, 11);
            controls.roofButtonLabel = roofButton.GetComponentInChildren<TextMeshProUGUI>();

            RectTransform viewBadge = HudPanelObject("03 · Point de vue actuel", canvasRect, 257, 102, 319, 33,
                new Color(.025f, .047f, .063f, .86f));
            HudPanelObject("Repère de vue", viewBadge, 0, 0, 3, 33, HudTeal);
            controls.viewLabel = HudText("Nom du point de vue", viewBadge, 14, 4, 293, 25,
                "01  /  GRAND HALL", 12, HudTeal, true);

            RectTransform footer = HudPanelObject("04 · Navigation entre points de vue", canvasRect, 0, 0, 0, 85, HudPanel);
            HudStretchHorizontal(footer, false, 0, 85);
            HudFullWidth(HudPanelObject("Filet navigation", footer, 0, 0, 0, 1, HudLine));
            HudText("Titre navigation", footer, 20, 11, 145, 22, "POINTS DE VUE", 11, HudTeal, true);
            HudText("Raccourcis navigation", footer, 20, 35, 151, 21, "Touches 1 — 6", 11, HudMuted, false);

            string[] labels = { "1  HALL", "2  PARVIS", "3  QUAIS", "4  ENSEMBLE", "5  COMMERCES", "6  CORRESPONDANCES" };
            UnityAction[] callbacks =
            {
                controls.SetHall, controls.SetExterior, controls.SetPlatforms,
                controls.SetOverall, controls.SetRetail, controls.SetConcourse
            };
            float[] widths = { 123, 128, 125, 157, 161, 209 };
            float buttonX = 176;
            controls.viewButtons = new Image[labels.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                Button button = HudButton("Vue · " + labels[i], footer, buttonX, 16, widths[i], 34,
                    labels[i], callbacks[i], 12);
                controls.viewButtons[i] = button.GetComponent<Image>();
                buttonX += widths[i] + 9;
            }
            Button cleanView = HudButton("Interface · afficher ou masquer", footer, -215, 16, 195, 34,
                "F1  /  MASQUER L’INTERFACE", controls.ToggleInterface, 11);
            RectTransform cleanRect = (RectTransform)cleanView.transform;
            cleanRect.anchorMin = cleanRect.anchorMax = new Vector2(1, 1);
            HudText("Conseil d’édition", footer, 176, 57, 1025, 17,
                "Unity Editor : développez la Hierarchy pour sélectionner et modifier chaque espace, mobilier et élément d’interface.",
                11, HudMuted, false);

            GameObject eventObject = new GameObject("EventSystem · interface", typeof(EventSystem));
            eventObject.transform.SetParent(root, false);
#if ENABLE_INPUT_SYSTEM
            InputSystemUIInputModule inputModule = eventObject.AddComponent<InputSystemUIInputModule>();
            HudAssignSavedInputActions(inputModule);
#else
            eventObject.AddComponent<StandaloneInputModule>();
#endif
            controls.SelectView(0);
            EditorUtility.SetDirty(controls);
        }

        static void HudMetric(RectTransform parent, float x, string value, string caption)
        {
            RectTransform metric = HudPanelObject("Capacité · " + caption, parent, x, 93, 87, 72,
                new Color(.073f, .12f, .15f, 1));
            HudText("Nombre", metric, 11, 3, 65, 42, value, 31, HudWhite, true);
            HudText("Libellé", metric, 12, 49, 67, 16, caption, 11, HudTeal, true);
        }

        static RectTransform HudRect(string name, Transform parent, float x, float y, float width, float height)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        static RectTransform HudPanelObject(string name, Transform parent, float x, float y, float width, float height, Color color)
        {
            RectTransform rect = HudRect(name, parent, x, y, width, height);
            Image image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return rect;
        }

        static void HudStretchHorizontal(RectTransform rect, bool top, float inset, float height)
        {
            rect.anchorMin = new Vector2(0, top ? 1 : 0);
            rect.anchorMax = new Vector2(1, top ? 1 : 0);
            rect.pivot = new Vector2(.5f, top ? 1 : 0);
            rect.anchoredPosition = new Vector2(0, top ? -inset : inset);
            rect.sizeDelta = new Vector2(0, height);
        }

        static void HudFullWidth(RectTransform rect)
        {
            rect.anchorMax = new Vector2(1, 1);
            rect.sizeDelta = new Vector2(0, rect.sizeDelta.y);
        }

#if ENABLE_INPUT_SYSTEM
        static void HudAssignSavedInputActions(InputSystemUIInputModule module)
        {
            // Use the project's imported asset and its persistent subasset references.
            // AssignDefaultActions creates transient ScriptableObjects that are not a saved authoring setup.
            const string path = "Assets/InputSystem_Actions.inputactions";
            InputActionAsset asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(path);
            if (asset == null)
                throw new System.InvalidOperationException("TrainStation needs the saved UI action map in " + path);
            module.actionsAsset = asset;
            foreach (Object subasset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                InputActionReference reference = subasset as InputActionReference;
                if (reference == null || reference.action == null || reference.action.actionMap.name != "UI") continue;
                switch (reference.action.name)
                {
                    case "Point": module.point = reference; break;
                    case "Click": module.leftClick = reference; break;
                    case "RightClick": module.rightClick = reference; break;
                    case "MiddleClick": module.middleClick = reference; break;
                    case "ScrollWheel": module.scrollWheel = reference; break;
                    case "Navigate": module.move = reference; break;
                    case "Submit": module.submit = reference; break;
                    case "Cancel": module.cancel = reference; break;
                    case "TrackedDevicePosition": module.trackedDevicePosition = reference; break;
                    case "TrackedDeviceOrientation": module.trackedDeviceOrientation = reference; break;
                }
            }
            if (module.point == null || module.leftClick == null)
                throw new System.InvalidOperationException("Saved UI actions must provide Point and Click bindings.");
        }
#endif

        static TextMeshProUGUI HudText(string name, Transform parent, float x, float y, float width, float height,
            string value, float size, Color color, bool bold)
        {
            RectTransform rect = HudRect(name, parent, x, y, width, height);
            TextMeshProUGUI text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = hudFont;
            text.text = value;
            text.fontSize = size;
            text.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            text.color = color;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = false;
            text.margin = Vector4.zero;
            return text;
        }

        static Button HudButton(string name, Transform parent, float x, float y, float width, float height,
            string label, UnityAction action, float fontSize)
        {
            RectTransform rect = HudPanelObject(name, parent, x, y, width, height,
                new Color(.105f, .155f, .185f, 1));
            Image image = rect.GetComponent<Image>();
            image.raycastTarget = true;
            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f);
            colors.pressedColor = new Color(.75f, .9f, .85f);
            colors.selectedColor = Color.white;
            button.colors = colors;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            TextMeshProUGUI text = HudText("Libellé", rect, 5, 0, width - 10, height, label, fontSize, HudWhite, true);
            text.alignment = TextAlignmentOptions.Center;
            UnityEventTools.AddPersistentListener(button.onClick, action);
            return button;
        }
    }
}
