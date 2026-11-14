using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Explicit capture setup; changing these controls never changes the passenger simulation.</summary>
    [DisallowMultipleComponent]
    public sealed class StationDecisionCollectionPanel : MonoBehaviour
    {
        const string PreferencesKey = "StationFlow.Station.CollectionOptions.v2";
        static readonly string[] ResolutionLabels = { "480p · 854 × 480", "720p · 1280 × 720", "HD · 1920 × 1080" };
        StationDecisionWorld world;
        StationDecisionCaptureOptions options = new StationDecisionCaptureOptions();
        readonly HashSet<string> cameras = new HashSet<string>(StringComparer.Ordinal);
        StationSurveillanceCamera[] catalog = Array.Empty<StationSurveillanceCamera>();
        GUIStyle panel, text, muted, title, button, active, toggle;
        Texture2D panelTexture, buttonTexture, activeTexture;
        Vector2 scroll;
        Rect resolutionRootAnchor, videoScrollArea;
        bool initialized, open = true, resolutionOpen;
        string error = "";
        double nextFrameRateRefresh, gameFps;
        StationDecisionCameraFrameRate[] videoRates = Array.Empty<StationDecisionCameraFrameRate>();

        string frameRateRunId;
        bool frameRatesRecording;
        public double ActualGameFps { get { RefreshFrameRates(); return gameFps; } }
        public IReadOnlyList<StationDecisionCameraFrameRate> ActualVideoRates { get { RefreshFrameRates(); return videoRates; } }
        public bool IsVideoRecording => world && world.IsCollecting && world.CaptureSession.Options != null && world.CaptureSession.Options.video;
        bool IsFinalizing => world && world.CaptureSession && world.CaptureSession.IsFinalizing;
        bool OptionsLocked => world && (world.IsCollecting || IsFinalizing);

        void Update() => RefreshFrameRates();
        void RefreshFrameRates()
        {
            // Start/stop/restart must be visible immediately, including when triggered after this frame's Update.
            if (!world || !world.CaptureSession)
            {
                gameFps = 0; videoRates = Array.Empty<StationDecisionCameraFrameRate>();
                frameRateRunId = null; frameRatesRecording = false; nextFrameRateRefresh = 0;
                return;
            }
            bool recording = world.IsCollecting;
            string runId = recording ? world.CaptureSession.RunId : null;
            bool changed = recording != frameRatesRecording || !string.Equals(runId, frameRateRunId, StringComparison.Ordinal);
            if (!changed && Time.unscaledTimeAsDouble < nextFrameRateRefresh) return;
            frameRatesRecording = recording; frameRateRunId = runId;
            nextFrameRateRefresh = Time.unscaledTimeAsDouble + .25;
            gameFps = world.CaptureSession.GameFpsRolling1s;
            videoRates = recording ? world.CaptureSession.GetActualVideoRates() : Array.Empty<StationDecisionCameraFrameRate>();
        }

        public bool IsOpen => isActiveAndEnabled && open;
        public StationDecisionCaptureOptions SelectedOptions => BuildOptions();
        float Scale => Mathf.Clamp(Mathf.Min(Screen.width / 1280f, Screen.height / 720f), .35f, 1.5f);
        Vector2 View => new Vector2(Screen.width / Scale, Screen.height / Scale);
        Rect Bounds => new Rect((View.x - Mathf.Min(920f, View.x - 28f)) * .5f,
            (View.y - Mathf.Min(620f, View.y - 28f)) * .5f, Mathf.Min(920f, View.x - 28f), Mathf.Min(620f, View.y - 28f));

        public void Initialize(StationDecisionWorld owner)
        {
            world = owner;
            if (initialized) return;
            initialized = true;
            bool hasSavedOptions = false;
            try
            {
                string saved = PlayerPrefs.GetString(PreferencesKey, "");
                hasSavedOptions = !string.IsNullOrEmpty(saved);
                if (!string.IsNullOrEmpty(saved)) options = JsonUtility.FromJson<StationDecisionCaptureOptions>(saved) ?? new StationDecisionCaptureOptions();
                options.Validate(); options.NormalizeVideoChoicesForUi();
            }
            catch { options = new StationDecisionCaptureOptions(); hasSavedOptions = false; }
            catalog = world && world.CaptureSession ? world.CaptureSession.AvailableCameras : Array.Empty<StationSurveillanceCamera>();
            foreach (var camera in catalog)
                if (hasSavedOptions ? options.selectedCameraIds == null || options.selectedCameraIds.Length == 0 || options.selectedCameraIds.Contains(camera.cameraId)
                    : camera.cameraId == "HALL-01" || camera.cameraId == "HALL-02" || camera.cameraId == "HALL-03" || camera.cameraId == "HALL-04") cameras.Add(camera.cameraId);
            if (!hasSavedOptions && cameras.Count == 0) foreach (var camera in catalog) cameras.Add(camera.cameraId);
            open = world && world.CollectionSetupRequired;
        }

        public void Show() { open = true; error = ""; resolutionOpen = false; }
        public void Hide() { open = false; resolutionOpen = false; }
        public void SetOptions(StationDecisionCaptureOptions value)
        {
            if (OptionsLocked) throw new InvalidOperationException("Wait for the collection to finish before changing its options.");
            options = (value ?? new StationDecisionCaptureOptions()).Copy(); options.Validate(); options.NormalizeVideoChoicesForUi();
            resolutionOpen = false;
            cameras.Clear();
            foreach (var camera in catalog)
                if (options.selectedCameraIds.Length == 0 || options.selectedCameraIds.Contains(camera.cameraId)) cameras.Add(camera.cameraId);
        }

        StationDecisionCaptureOptions BuildOptions()
        {
            var result = options.Copy();
            result.selectedCameraIds = cameras.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            return result;
        }

        void OnGUI()
        {
            if (!Application.isPlaying || !IsOpen || !world) return;
            EnsureStyles();
            var previous = GUI.matrix; int previousDepth = GUI.depth; bool previousEnabled = GUI.enabled;
            GUI.depth = -100;
            GUI.matrix = Matrix4x4.Scale(Vector3.one * Scale);
            try
            {
                if (OptionsLocked) resolutionOpen = false;
                if (resolutionOpen &&
                    (Event.current.type == EventType.MouseDown && !PopupRect().Contains(Event.current.mousePosition) ||
                     Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape))
                { resolutionOpen = false; Event.current.Use(); }
                // Keep backdrop opacity unchanged; disabled control tint belongs only to the form.
                GUI.enabled = previousEnabled;
                GUI.Box(new Rect(0f, 0f, View.x, View.y), GUIContent.none, new GUIStyle { normal = { background = panelTexture } });
                Rect bounds = Bounds;
                GUI.Box(bounds, GUIContent.none, panel);
                // Open choices own pointer input; the popup is drawn after all scroll-view clipping ends.
                GUI.enabled = previousEnabled && !resolutionOpen;
                GUI.Label(new Rect(bounds.x + 24f, bounds.y + 16f, bounds.width - 80f, 30f), "COLLECTE DE DONNÉES", title);
                GUI.Label(new Rect(bounds.x + 24f, bounds.y + 49f, bounds.width - 76f, 24f),
                    world.rail.schedule.operatingDateIso + "  ·  Graine " + world.seed + "  ·  " + world.rail.schedule.services.Length + " trains", muted);
                if (GUI.Button(new Rect(bounds.xMax - 49f, bounds.y + 16f, 28f, 28f), "×", button)) Hide();

                videoScrollArea = new Rect(bounds.x + 24f, bounds.y + 84f, bounds.width - 48f, bounds.height - 219f);
                GUILayout.BeginArea(videoScrollArea);
                scroll = GUILayout.BeginScrollView(scroll);
                float contentWidth = bounds.width - 72f;
                GUILayout.BeginVertical(GUILayout.Width(contentWidth));
                bool recording = world.IsCollecting, finalizing = IsFinalizing;
                GUILayout.Label(finalizing ? "FINALISATION EN COURS" : recording ? "● ENREGISTREMENT EN COURS" : "CHOISIR LES DONNÉES À CONSERVER", recording || finalizing ? title : text);
                GUILayout.Label(finalizing ? "Les images et données déjà acceptées sont en cours de fermeture. Vous pouvez reprendre l’observation." :
                    recording ? "Les choix restent fixes jusqu’à l’arrêt. La pause conserve la synchronisation vidéo / simulation." :
                    "Un manifeste et une horloge de synchronisation sont toujours conservés pour chaque collecte.", muted);
                GUILayout.Space(12f);
                bool enabled = GUI.enabled; GUI.enabled = enabled && !recording && !finalizing;
                GUILayout.BeginHorizontal();
                GUILayout.BeginVertical(GUILayout.Width((contentWidth - 18f) * .5f));
                options.video = Toggle(options.video, "Vidéos CCTV et calibration", "Images, position, orientation et paramètres optiques.");
                options.timetable = Toggle(options.timetable, "Horaires et trains", "Programme, états réels et modifications.");
                options.profiles = Toggle(options.profiles, "Profils des voyageurs", "Âge, mobilité, groupe, voyage et places.");
                options.trajectories = Toggle(options.trajectories, "Positions et objectifs", "Position, orientation, vitesse et destination.");
                GUILayout.EndVertical();
                GUILayout.Space(18f);
                GUILayout.BeginVertical(GUILayout.Width((contentWidth - 18f) * .5f));
                options.animations = Toggle(options.animations, "Animations", "Motion, vitesse, phase et transitions.");
                options.skeletons = Toggle(options.skeletons, "Squelettes 3D", "Positions des articulations dans le monde, synchronisées.");
                options.decisions = Toggle(options.decisions, "Décisions", "Options évaluées et action choisie.");
                options.events = Toggle(options.events, "Événements et files", "Entrées, services, files, sorties et embarquements.");
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
                GUILayout.Space(12f);
                if (options.video)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.BeginVertical(GUILayout.Width((contentWidth - 18f) * .5f));
                    GUILayout.Label("Résolution vidéo", muted);
                    int resolution = Dropdown(options.VideoResolutionIndex, ResolutionLabels, ref resolutionOpen, ref resolutionRootAnchor);
                    options.SetVideoResolution(resolution);
                    GUILayout.EndVertical();
                    GUILayout.Space(18f);
                    GUILayout.BeginVertical(GUILayout.Width((contentWidth - 18f) * .5f));
                    GUILayout.Label("FPS vidéo variables", text);
                    GUILayout.Label("Cadence mesurée pendant la collecte, sans sélection de FPS.", muted);
                    GUILayout.EndVertical();
                    GUILayout.EndHorizontal();
                    GUILayout.Label("Chaque image conserve son horodatage réel pour la synchronisation et l’analyse.", muted);
                    GUILayout.Space(10f);
                    GUILayout.Label("CAMÉRAS  ·  " + cameras.Count + " / " + catalog.Length, text);
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Toutes", button, GUILayout.Width(95f), GUILayout.Height(26f))) foreach (var camera in catalog) cameras.Add(camera.cameraId);
                    if (GUILayout.Button("Aucune", button, GUILayout.Width(95f), GUILayout.Height(26f))) cameras.Clear();
                    GUILayout.EndHorizontal();
                    for (int i = 0; i < catalog.Length; i += 3)
                    {
                        GUILayout.BeginHorizontal();
                        for (int j = i; j < Mathf.Min(i + 3, catalog.Length); ++j)
                        {
                            var camera = catalog[j]; bool chosen = cameras.Contains(camera.cameraId);
                            bool next = GUILayout.Toggle(chosen, (chosen ? "☑  " : "☐  ") + camera.cameraId + "  " + camera.displayName,
                                chosen ? active : button, GUILayout.Width((contentWidth - 28f) / 3f), GUILayout.Height(30f));
                            if (next) cameras.Add(camera.cameraId); else cameras.Remove(camera.cameraId);
                        }
                        GUILayout.EndHorizontal();
                    }
                }
                GUI.enabled = enabled;
                if ((recording || finalizing) && world.CaptureSession)
                {
                    if (recording) DrawActualFrameRates(contentWidth);
                    else GUILayout.Label("Finalisation : " + world.CaptureSession.PendingDataCommands + " commandes de données restantes", text);
                    GUILayout.Label(world.CaptureSession.VideoFramesWritten + " images écrites  ·  " + world.CaptureSession.VideoFramesDropped + " captures non enregistrées", muted);
                    if (world.CaptureSession.CaptureBatchesRejectedData > 0 || world.CaptureSession.CaptureBatchesRejectedVideo > 0)
                        GUILayout.Label("Lots vidéo non enregistrés : " + world.CaptureSession.CaptureBatchesRejectedData + " (capacité des données)  ·  " + world.CaptureSession.CaptureBatchesRejectedVideo + " (caméras)", muted);
                }
                GUILayout.EndVertical(); GUILayout.EndScrollView(); GUILayout.EndArea();

                float footer = bounds.yMax - 124f;
                GUI.Label(new Rect(bounds.x + 24f, footer, bounds.width - 48f, 38f),
                    recording || finalizing ? world.OutputDirectory : StationDecisionCaptureSession.StorageRoot, muted);
                string problem = !string.IsNullOrEmpty(error) ? error : world.CollectionError;
                if (string.IsNullOrEmpty(problem) && world.CaptureSession) problem = world.CaptureSession.LastError;
                if (!string.IsNullOrEmpty(problem)) GUI.Label(new Rect(bounds.x + 24f, footer + 36f, bounds.width - 48f, 30f), problem, text);
                if (finalizing)
                {
                    GUI.enabled = false;
                    GUI.Button(new Rect(bounds.x + 24f, bounds.yMax - 49f, 250f, 32f), "Finalisation des fichiers…", active);
                    GUI.enabled = enabled;
                    if (GUI.Button(new Rect(bounds.xMax - 269f, bounds.yMax - 49f, 245f, 32f), "Reprendre l’observation", button))
                    { world.BeginObservation(); Hide(); }
                }
                else if (recording)
                {
                    if (GUI.Button(new Rect(bounds.x + 24f, bounds.yMax - 49f, 250f, 32f), "Arrêter et finaliser les fichiers", active))
                        world.StopDataCollection();
                    if (GUI.Button(new Rect(bounds.xMax - 269f, bounds.yMax - 49f, 245f, 32f), "Retour à l’observation", button)) Hide();
                }
                else
                {
                    if (GUI.Button(new Rect(bounds.x + 24f, bounds.yMax - 49f, 250f, 32f), "Démarrer la collecte", active))
                    {
                        if (options.video && cameras.Count == 0) error = "Sélectionnez au moins une caméra ou décochez les vidéos.";
                        else
                        {
                            var chosen = BuildOptions();
                            if (world.BeginDataCollection(chosen))
                            { PlayerPrefs.SetString(PreferencesKey, JsonUtility.ToJson(chosen)); PlayerPrefs.Save(); Hide(); }
                            else error = world.CollectionError;
                        }
                    }
                    if (GUI.Button(new Rect(bounds.xMax - 269f, bounds.yMax - 49f, 245f, 32f), "Observer sans enregistrer", button))
                    { world.BeginObservation(); Hide(); }
                }
                DrawDropdownPopup(previousEnabled);
            }
            finally { GUI.matrix = previous; GUI.depth = previousDepth; GUI.enabled = previousEnabled; }
        }

        int Dropdown(int selected, string[] labels, ref bool expanded, ref Rect rootAnchor)
        {
            if (GUILayout.Button(labels[selected] + (expanded ? "   ▲" : "   ▼"), button, GUILayout.Height(32f)))
            { expanded = !expanded; }
            if (Event.current.type == EventType.Repaint)
            {
                Rect rect = GUILayoutUtility.GetLastRect();
                // GUILayout coordinates are already logical GUI units. Only the known area and scroll
                // translation are needed; the outer GUI.matrix applies scale exactly once when drawing.
                rootAnchor = new Rect(videoScrollArea.x + rect.x - scroll.x, videoScrollArea.y + rect.y - scroll.y, rect.width, rect.height);
            }
            return selected;
        }

        Rect PopupRect()
        {
            Rect anchor = resolutionRootAnchor;
            float width = Mathf.Max(180f, anchor.width), height = 114f;
            float x = Mathf.Clamp(anchor.x, 12f, Mathf.Max(12f, View.x - width - 12f));
            float y = anchor.yMax + 4f;
            if (y + height > View.y - 12f) y = anchor.y - height - 4f;
            return new Rect(x, Mathf.Clamp(y, 12f, Mathf.Max(12f, View.y - height - 12f)), width, height);
        }

        void DrawDropdownPopup(bool enabled)
        {
            if (!resolutionOpen) return;
            string[] labels = ResolutionLabels;
            int selected = options.VideoResolutionIndex;
            Rect popup = PopupRect();
            GUI.enabled = enabled && !OptionsLocked;
            GUI.Box(new Rect(popup.x + 3f, popup.y + 4f, popup.width, popup.height), GUIContent.none, panel);
            GUI.Box(popup, GUIContent.none, panel);
            for (int i = 0; i < labels.Length; ++i)
                if (GUI.Button(new Rect(popup.x + 6f, popup.y + 6f + i * 34f, popup.width - 12f, 30f),
                    (i == selected ? "✓  " : "    ") + labels[i], i == selected ? active : button))
                {
                    options.SetVideoResolution(i);
                    resolutionOpen = false;
                }
        }

        void DrawActualFrameRates(float contentWidth)
        {
            RefreshFrameRates();
            GUILayout.Space(12f);
            GUILayout.Label("FPS RÉELS · DERNIÈRE SECONDE", text);
            GUILayout.Label("Jeu : " + FormatFps(gameFps) + " FPS", text);
            if (videoRates.Length == 0) GUILayout.Label(IsVideoRecording ? "Initialisation des mesures vidéo…" : "Vidéos désactivées pour cette collecte.", muted);
            for (int i = 0; i < videoRates.Length; i += 3)
            {
                GUILayout.BeginHorizontal();
                for (int j = i; j < Mathf.Min(i + 3, videoRates.Length); ++j)
                {
                    var rate = videoRates[j];
                    GUILayout.Label(rate.cameraId + " : " + FormatFps(rate.videoFpsRolling1s) + " FPS vidéo", text, GUILayout.Width((contentWidth - 28f) / 3f));
                }
                GUILayout.EndHorizontal();
            }
        }
        public static string FormatFps(double value) => double.IsNaN(value) || double.IsInfinity(value) ? "—" : Math.Max(0, value).ToString("F1");

        bool Toggle(bool value, string label, string detail)
        {
            value = GUILayout.Toggle(value, (value ? "☑  " : "☐  ") + label, value ? active : button, GUILayout.Height(29f));
            GUILayout.Label(detail, muted); GUILayout.Space(6f); return value;
        }

        void EnsureStyles()
        {
            if (panel != null) return;
            panelTexture = Texture(new Color(.035f, .065f, .080f, .97f));
            buttonTexture = Texture(new Color(.10f, .17f, .20f)); activeTexture = Texture(new Color(.10f, .33f, .29f));
            panel = new GUIStyle(GUI.skin.box) { normal = { background = panelTexture }, border = new RectOffset() };
            text = new GUIStyle(GUI.skin.label) { fontSize = 14, normal = { textColor = new Color(.9f, .96f, .96f) }, wordWrap = true };
            muted = new GUIStyle(text) { fontSize = 12, normal = { textColor = new Color(.63f, .75f, .77f) } };
            title = new GUIStyle(text) { fontSize = 20, fontStyle = FontStyle.Bold };
            button = new GUIStyle(GUI.skin.button) { fontSize = 13, alignment = TextAnchor.MiddleLeft,
                normal = { background = buttonTexture, textColor = Color.white }, padding = new RectOffset(10, 10, 3, 3), margin = new RectOffset(0, 7, 0, 4) };
            active = new GUIStyle(button) { normal = { background = activeTexture, textColor = new Color(.66f, 1f, .84f) } };
            button.onNormal = button.normal; button.onHover = button.normal; button.onActive = button.normal; button.onFocused = button.normal;
            active.onNormal = active.normal; active.onHover = active.normal; active.onActive = active.normal; active.onFocused = active.normal;
        }
        static Texture2D Texture(Color color) { var texture = new Texture2D(1, 1); texture.SetPixel(0, 0, color); texture.Apply(); return texture; }
        void OnDestroy() { if (panelTexture) Destroy(panelTexture); if (buttonTexture) Destroy(buttonTexture); if (activeTexture) Destroy(activeTexture); }
    }
}
