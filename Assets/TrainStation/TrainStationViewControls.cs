using TMPro;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace StationFlow.TrainStation
{
    /// <summary>
    /// Presentation controls for objects already saved in TrainStation.unity.
    /// This component never constructs the environment or interface at runtime.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TrainStationViewControls : MonoBehaviour
    {
        [Header("Saved scene references")]
        public Camera[] viewpoints;
        public GameObject[] roofGroups;
        public GameObject interfaceRoot;
        public TextMeshProUGUI viewLabel;
        public TextMeshProUGUI controlHintLabel;
        public TextMeshProUGUI roofButtonLabel;
        public Image[] viewButtons;
        public string[] viewNames;
        public GameObject cameraCatalog;
        public TMP_Dropdown viewDropdown;

        [Header("Presentation")]
        [Min(0)] public int selectedView;
        public Color inactiveButtonColor = new Color(.105f, .155f, .185f, 1f);
        public Color activeButtonColor = new Color(.13f, .36f, .32f, 1f);

        static readonly string[] LegacyViewNames =
        {
            "01 / HALL - CAMERA LIBRE", "02 / PARVIS", "03 / QUAIS - VUE GENERALE",
            "04 / ACCUEIL", "05 / CAFE", "06 / CORRESPONDANCES"
        };

        public int ViewCount => viewpoints == null ? 0 : viewpoints.Length;
        public Camera SelectedCamera => viewpoints != null && selectedView >= 0 && selectedView < viewpoints.Length ? viewpoints[selectedView] : null;
        static TrainStationViewControls textInputOwner;
        bool suppressKeyboardShortcuts;
        public bool SuppressKeyboardShortcuts
        {
            get => suppressKeyboardShortcuts;
            set
            {
                suppressKeyboardShortcuts = value;
                if (value) textInputOwner = this;
                else if (textInputOwner == this) textInputOwner = null;
            }
        }

        public void SetHall() => SelectView(0);
        public void SetExterior() => SelectView(1);
        public void SetPlatforms() => SelectView(2);
        public void SetOverall() => SelectView(3);
        public void SetRetail() => SelectView(4);
        public void SetConcourse() => SelectView(5);

        public string GetViewName(int index)
        {
            if (index < 0 || index >= ViewCount) return string.Empty;
            if (viewNames != null && index < viewNames.Length && !string.IsNullOrWhiteSpace(viewNames[index]))
                return viewNames[index];
            if (index < LegacyViewNames.Length) return LegacyViewNames[index];
            Camera camera = viewpoints[index];
            var surveillance = camera ? camera.GetComponentInParent<StationSurveillanceCamera>() : null;
            return surveillance && !string.IsNullOrWhiteSpace(surveillance.displayName) ? surveillance.displayName : camera ? camera.name : "Camera non affectee";
        }

        public void PreviousView() => StepView(-1);
        public void NextView() => StepView(1);

        void StepView(int direction)
        {
            if (ViewCount == 0) return;
            for (int offset = 1; offset <= ViewCount; offset++)
            {
                int index = (Mathf.Clamp(selectedView, 0, ViewCount - 1) + offset * direction + ViewCount) % ViewCount;
                if (viewpoints[index]) { SelectView(index); return; }
            }
        }

        public void ResetGameView()
        {
            if (ViewCount == 0 || !viewpoints[0]) return;
            SelectView(0);
            var orbit = viewpoints[0].GetComponent<TrainStationOrbitCamera>();
            if (orbit) orbit.ResetView();
        }

        public void ToggleCameraCatalog()
        {
            if (cameraCatalog) cameraCatalog.SetActive(!cameraCatalog.activeSelf);
        }

        void OnEnable()
        {
            if (Application.isPlaying) SelectView(Mathf.Clamp(selectedView, 0, Mathf.Max(0, ViewCount - 1)));
        }

        void OnDisable() => SuppressKeyboardShortcuts = false;

        public void SelectView(int index)
        {
            if (viewpoints == null || index < 0 || index >= viewpoints.Length || viewpoints[index] == null)
                return;

            selectedView = index;
            for (int i = 0; i < viewpoints.Length; i++)
                if (viewpoints[i] != null)
                {
                    // The authored lens, mounting bracket and camera housing stay visible in the scene.
                    viewpoints[i].gameObject.SetActive(true);
                    viewpoints[i].enabled = i == index;
                    viewpoints[i].gameObject.tag = i == index ? "MainCamera" : "Untagged";
                    AudioListener listener = viewpoints[i].GetComponent<AudioListener>();
                    if (listener != null) listener.enabled = i == index;
                    TrainStationOrbitCamera orbit = viewpoints[i].GetComponent<TrainStationOrbitCamera>();
                    if (orbit != null) orbit.enabled = i == index;
                }

            if (viewLabel != null)
                viewLabel.text = GetViewName(index);
            if (controlHintLabel != null)
                controlHintLabel.text = viewpoints[index].GetComponent<TrainStationOrbitCamera>()
                    ? "Clic droit : tourner  |  Molette : zoom  |  Clic milieu : deplacer  |  Home : recentrer"
                    : "Camera fixe  |  Page haut / bas : changer de camera  |  1 : vue libre du hall";
            if (viewDropdown != null) viewDropdown.SetValueWithoutNotify(index);
            if (cameraCatalog != null) cameraCatalog.SetActive(false);

            if (viewButtons != null)
                for (int i = 0; i < viewButtons.Length; i++)
                    if (viewButtons[i] != null)
                        viewButtons[i].color = i == index ? activeButtonColor : inactiveButtonColor;
        }

        public void ToggleRoofs()
        {
            if (roofGroups == null || roofGroups.Length == 0)
                return;

            bool anyVisible = false;
            foreach (GameObject group in roofGroups)
                if (group != null && group.activeSelf)
                    anyVisible = true;

            foreach (GameObject group in roofGroups)
                if (group != null)
                    group.SetActive(!anyVisible);

            if (roofButtonLabel != null)
                roofButtonLabel.text = anyVisible ? "AFFICHER LES TOITURES" : "MASQUER LES TOITURES";
        }

        public void ToggleInterface()
        {
            if (interfaceRoot != null)
                interfaceRoot.SetActive(!interfaceRoot.activeSelf);
        }

        void Update()
        {
            // Only optional presentation shortcuts; the scene is fully serialized in edit mode.
            if (SuppressKeyboardShortcuts || IsEditingText()) return;
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;
            if (keyboard.f1Key.wasPressedThisFrame) ToggleInterface();
            if (keyboard.digit1Key.wasPressedThisFrame) SetHall();
            if (keyboard.digit2Key.wasPressedThisFrame) SetExterior();
            if (keyboard.digit3Key.wasPressedThisFrame) SetPlatforms();
            if (keyboard.digit4Key.wasPressedThisFrame) SetOverall();
            if (keyboard.digit5Key.wasPressedThisFrame) SetRetail();
            if (keyboard.digit6Key.wasPressedThisFrame) SetConcourse();
            if (keyboard.pageUpKey.wasPressedThisFrame) PreviousView();
            if (keyboard.pageDownKey.wasPressedThisFrame) NextView();
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.F1)) ToggleInterface();
            if (Input.GetKeyDown(KeyCode.Alpha1)) SetHall();
            if (Input.GetKeyDown(KeyCode.Alpha2)) SetExterior();
            if (Input.GetKeyDown(KeyCode.Alpha3)) SetPlatforms();
            if (Input.GetKeyDown(KeyCode.Alpha4)) SetOverall();
            if (Input.GetKeyDown(KeyCode.Alpha5)) SetRetail();
            if (Input.GetKeyDown(KeyCode.Alpha6)) SetConcourse();
            if (Input.GetKeyDown(KeyCode.PageUp)) PreviousView();
            if (Input.GetKeyDown(KeyCode.PageDown)) NextView();
#endif
        }

        public static bool IsEditingText()
        {
            if (textInputOwner && textInputOwner.suppressKeyboardShortcuts) return true;
            var eventSystem = UnityEngine.EventSystems.EventSystem.current;
            if (!eventSystem || !eventSystem.currentSelectedGameObject) return false;
            GameObject selected = eventSystem.currentSelectedGameObject;
            var tmpField = selected.GetComponent<TMP_InputField>();
            if (tmpField && tmpField.isFocused) return true;
            var field = selected.GetComponent<InputField>();
            return field && field.isFocused;
        }
    }
}
