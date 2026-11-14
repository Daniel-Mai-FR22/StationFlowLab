using System;
using System.Linq;
using System.Text;
using StationFlow.TrainStation.RailOperations;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Observation workspace; selection never changes passenger behavior.</summary>
    [DisallowMultipleComponent]
    public sealed class StationDecisionDashboard : MonoBehaviour
    {
        [Header("Saved scene references")]
        public StationDecisionWorld world;
        public TrainStationViewControls viewControls;
        public StationDecisionFollowCamera followCamera;
        [Header("Presentation")]
        public bool visible = true;
        public bool rosterExpanded = true;
        public bool detailsExpanded = true;
        [Range(.8f, 1.1f)] public float interfaceScale = 1f;
        [Min(0)] public int selectedParticipant;

        public bool IsTextInputFocused => CollectionPanel && CollectionPanel.IsOpen;
        StationDecisionCollectionPanel CollectionPanel => GetComponent<StationDecisionCollectionPanel>();
        public DecisionActivity? ActivityFilter => activityFilter == 0 ? (DecisionActivity?)null : ActivityChoices[activityFilter - 1];
        public StationDecisionPassenger SelectedPassenger => ParticipantCount == 0 ? null :
            world.IsDaily ? world.participants.FirstOrDefault(p => p && p.HasEntered && p.passengerId == selectedJourneyId) :
            world.participants[Mathf.Clamp(selectedParticipant, 0, ParticipantCount - 1)];

        const float Margin = 12f, TopHeight = 64f, BottomHeight = 54f;
        static readonly Color Ink = new Color(.91f, .96f, .96f);
        static readonly Color Muted = new Color(.57f, .69f, .72f);
        static readonly Color Accent = new Color(.34f, .85f, .70f);
        static readonly Color Amber = new Color(1f, .77f, .39f);
        static readonly Color Red = new Color(1f, .48f, .42f);
        static readonly string[] Tabs = { "Observation", "Décision", "Journaux" };
        static readonly string[] DayTabs = { "Voyageur", "Décision", "Journaux", "Horaires" };
        static readonly string[] Filters = { "Tous", "En gare", "Terminés" };
        static readonly DecisionActivity[] ActivityChoices = (DecisionActivity[])Enum.GetValues(typeof(DecisionActivity));
        GUIStyle panelStyle, labelStyle, mutedStyle, titleStyle, headingStyle, valueStyle;
        GUIStyle buttonStyle, activeButtonStyle, smallButtonStyle, activityButtonStyle, rowStyle, selectedRowStyle;
        Texture2D panelTexture, buttonTexture, hoverTexture, activeTexture, rowTexture;
        Vector2 rosterScroll, detailScroll, activityMenuScroll;
        string selectedJourneyId;
        int filter, tab, observedSelection, activityFilter;
        float copiedUntil;
        bool clearGuiFocus, restartRequested, activityMenuOpen;
        Canvas pointerCanvas;
        readonly RectTransform[] pointerRects = new RectTransform[4];
        readonly Image[] pointerImages = new Image[4];

        int ParticipantCount => world && world.participants != null ? world.participants.Length : 0;
        bool CatalogOpen => viewControls && viewControls.cameraCatalog && viewControls.cameraCatalog.activeInHierarchy;
        // Keep at least 1280 logical pixels so the compact 960 x 540 layout never overlaps.
        float Scale => Mathf.Clamp(Mathf.Min(Screen.width / 1280f, Screen.height / 720f) * interfaceScale, .45f,
            Mathf.Min(Screen.width / 1280f, 1.5f));
        Vector2 ViewSize => new Vector2(Screen.width / Scale, Screen.height / Scale);
        Rect TopBounds => new Rect(Margin, Margin, ViewSize.x - Margin * 2f, TopHeight);
        Rect BottomBounds => new Rect(Margin, ViewSize.y - Margin - BottomHeight, ViewSize.x - Margin * 2f, BottomHeight);
        int VideoRateRows => CollectionPanel ? Mathf.CeilToInt(CollectionPanel.ActualVideoRates.Count / 6f) : 0;
        float FrameRatesHeight => world && world.IsCollecting ? 34f + VideoRateRows * 22f : 0f;
        float FrameRatesReserve => FrameRatesHeight > 0f ? FrameRatesHeight + 8f : 0f;
        Rect FrameRatesBounds => new Rect(Margin, BottomBounds.y - FrameRatesReserve, ViewSize.x - Margin * 2f, FrameRatesHeight);
        Rect BottomPointerBounds => FrameRatesHeight > 0f ? new Rect(BottomBounds.x, FrameRatesBounds.y, BottomBounds.width, BottomBounds.yMax - FrameRatesBounds.y) : BottomBounds;
        Rect HiddenBounds => new Rect(Margin, Margin, 194f, 34f);
        Rect RosterBounds => new Rect(Margin, Margin * 2f + TopHeight, 230f,
            rosterExpanded ? Mathf.Max(100f, ViewSize.y - TopHeight - BottomHeight - Margin * 5f - FrameRatesReserve) : 44f);
        Rect DetailBounds => new Rect(ViewSize.x - Margin - 338f, Margin * 2f + TopHeight, 338f,
            detailsExpanded ? Mathf.Max(100f, ViewSize.y - TopHeight - BottomHeight - Margin * 5f - FrameRatesReserve) : 44f);
        Rect ActivityFilterBounds => new Rect(RosterBounds.x + 12f, RosterBounds.y + 47f, RosterBounds.width - 24f, 30f);
        Rect ActivityMenuBounds => new Rect(ActivityFilterBounds.x, ActivityFilterBounds.yMax + 4f, ActivityFilterBounds.width,
            Mathf.Min((ActivityChoices.Length + 1) * 28f + 8f, Mathf.Max(0f, RosterBounds.yMax - ActivityFilterBounds.yMax - 16f)));

        void OnEnable()
        {
            if (!Application.isPlaying) return;
            ResolveReferences();
            observedSelection = selectedParticipant;
            CreatePointerSurfaces();
            UpdatePointerSurfaces();
        }

        void ResolveReferences()
        {
            if (!world) world = GetComponent<StationDecisionWorld>();
            if (!viewControls)
                foreach (var candidate in FindObjectsByType<TrainStationViewControls>(FindObjectsSortMode.None))
                    if (candidate.gameObject.scene == gameObject.scene) { viewControls = candidate; break; }
            if (!followCamera) followCamera = GetComponent<StationDecisionFollowCamera>();
        }

        void Update()
        {
            if (CollectionPanel && CollectionPanel.IsOpen)
            { if (viewControls) viewControls.SuppressKeyboardShortcuts = true; UpdatePointerSurfaces(); return; }
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.f7Key.wasPressedThisFrame) ToggleVisibility();
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.F7)) ToggleVisibility();
#endif
            // Opening Play keeps the overview; later Inspector changes are deliberate selections.
            if (selectedParticipant != observedSelection) SelectParticipant(selectedParticipant);
            if (!visible || CatalogOpen) activityMenuOpen = false;
            if (viewControls) viewControls.SuppressKeyboardShortcuts = activityMenuOpen;
            UpdatePointerSurfaces();
        }

        void ToggleVisibility()
        {
            visible = !visible;
            clearGuiFocus = true;
            activityMenuOpen = false;
        }

        public void SelectParticipant(int index)
        {
            selectedParticipant = ParticipantCount == 0 ? 0 : Mathf.Clamp(index, 0, ParticipantCount - 1);
            if (world && world.IsDaily && ParticipantCount > 0)
            { var chosen = world.participants[selectedParticipant]; selectedJourneyId = chosen && chosen.HasEntered ? chosen.passengerId : null; }
            observedSelection = selectedParticipant;
            detailScroll = Vector2.zero;
            clearGuiFocus = true;
            activityMenuOpen = false;
            if (!followCamera) ResolveReferences();
            // Repeat clicks re-enter follow after the user has opened any fixed camera.
            if (followCamera && SelectedPassenger) followCamera.Follow(SelectedPassenger);
        }

        public void SetActivityFilter(DecisionActivity? activity)
        {
            activityFilter = activity.HasValue ? Array.IndexOf(ActivityChoices, activity.Value) + 1 : 0;
            rosterScroll = Vector2.zero;
            activityMenuOpen = false;
            clearGuiFocus = true;
        }

        void SelectRelative(int direction)
        {
            if (world && world.IsDaily)
            {
                var indices = Enumerable.Range(0, ParticipantCount).Where(i => Matches(world.participants[i])).ToArray();
                if (indices.Length == 0) return;
                int current = Array.IndexOf(indices, selectedParticipant);
                SelectParticipant(indices[(Mathf.Max(0, current) + direction + indices.Length) % indices.Length]); return;
            }
            if (ParticipantCount > 0)
                SelectParticipant((Mathf.Clamp(selectedParticipant, 0, ParticipantCount - 1) + direction + ParticipantCount) % ParticipantCount);
        }

        /// <summary>Coordinates use Unity's bottom-left screen origin.</summary>
        public bool IsPointerOverDashboard(Vector2 screenPosition)
        {
            if (CollectionPanel && CollectionPanel.IsOpen) return true;
            if (!isActiveAndEnabled || CatalogOpen) return false;
            Vector2 point = new Vector2(screenPosition.x, Screen.height - screenPosition.y) / Scale;
            if (!visible) return HiddenBounds.Contains(point);
            return TopBounds.Contains(point) || RosterBounds.Contains(point) || DetailBounds.Contains(point) || BottomPointerBounds.Contains(point);
        }

        void OnGUI()
        {
            if (CollectionPanel && CollectionPanel.IsOpen) return;
            if (!Application.isPlaying || CatalogOpen) { activityMenuOpen = false; return; }
            EnsureStyles();
            Matrix4x4 matrix = GUI.matrix;
            int depth = GUI.depth;
            GUI.depth = -30;
            GUI.matrix = Matrix4x4.Scale(Vector3.one * Scale);
            try
            {
                if (clearGuiFocus) { GUI.FocusControl(null); clearGuiFocus = false; }
                if (activityMenuOpen && ((Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape) ||
                    (Event.current.type == EventType.MouseDown && !ActivityMenuBounds.Contains(Event.current.mousePosition) &&
                     !ActivityFilterBounds.Contains(Event.current.mousePosition))))
                { activityMenuOpen = false; GUI.FocusControl(null); Event.current.Use(); GUIUtility.ExitGUI(); }
                if (!visible)
                {
                    if (GUI.Button(HiddenBounds, "OBSERVATOIRE  ·  F7", buttonStyle)) ToggleVisibility();
                }
                else
                {
                    DrawOverview();
                    DrawRoster();
                    DrawDetails();
                    DrawCameraBar();
                    DrawActualFrameRates();
                }
            }
            finally
            {
                if (viewControls) viewControls.SuppressKeyboardShortcuts = activityMenuOpen;
                GUI.matrix = matrix; GUI.depth = depth;
            }
            if (restartRequested && world)
            {
                restartRequested = false;
                world.RestartScene();
                GUIUtility.ExitGUI();
            }
        }

        void DrawOverview()
        {
            Rect bounds = TopBounds;
            GUI.Box(bounds, GUIContent.none, panelStyle);
            GUI.BeginGroup(bounds);
            GUI.Label(new Rect(14f, 9f, 165f, 24f), "TRAIN STATION", headingStyle);
            GUI.Label(new Rect(14f, 34f, 170f, 19f), world && world.IsDaily ? world.rail.schedule.operatingDateIso : world ? "Graine " + world.seed : "Observatoire", mutedStyle);
            GUI.Label(new Rect(182f, 8f, 120f, 31f), world && world.IsDaily ? world.rail.ClockText.Substring(0, 5) : world ? Duration(world.ElapsedSeconds) : "00:00", valueStyle);
            GUI.Label(new Rect(183f, 39f, 110f, 17f), world && world.Paused ? "EN PAUSE" : "SIMULATION", mutedStyle);
            int boarded = world ? world.BoardingCount : 0;
            int ended = world ? world.FailedCount : 0;
            Counter(304f, "EN GARE", world && world.IsDaily ? world.ActivePassengerCount : Mathf.Max(0, ParticipantCount - boarded - ended), Ink);
            Counter(385f, world && world.IsDaily ? "EMBARQUÉS" : "À BORD", boarded, Accent);
            Counter(466f, "SORTIS / FIN", ended, Muted);
            if (world && world.IsDaily)
            {
                GUI.Label(new Rect(555f, 8f, 100f, 29f), "×" + TimeScaleText(), valueStyle);
                if (GUI.Button(new Rect(553f, 37f, 108f, 22f), "Horaires du jour", smallButtonStyle))
                { tab = 3; detailsExpanded = true; detailScroll = Vector2.zero; }
                if (GUI.Button(new Rect(668f, 37f, 84f, 22f), world.IsCollecting ? "● Collecte" : "Collecte", world.IsCollecting ? activeButtonStyle : smallButtonStyle))
                    CollectionPanel.Show();
            }
            else Counter(555f, "DÉCISIONS", world ? world.DecisionCount : 0, Ink);
            float x = bounds.width - 491f;
            bool previousEnabled = GUI.enabled;
            GUI.enabled = world;
            if (GUI.Button(new Rect(x, 15f, 85f, 34f), world && world.Paused ? "Reprendre" : "Pause",
                world && world.Paused ? activeButtonStyle : buttonStyle))
            { if (world.CollectionSetupRequired && CollectionPanel) CollectionPanel.Show(); else world.TogglePause(); }
            if (GUI.Button(new Rect(x + 91f, 15f, 116f, 34f), "Recommencer", buttonStyle)) restartRequested = true;
            if (GUI.Button(new Rect(x + 213f, 15f, 110f, 34f), "+ 75 s retard", buttonStyle)) world.InjectDelay();
            if (GUI.Button(new Rect(x + 329f, 15f, 112f, 34f), "Annuler train", buttonStyle)) world.InjectCancellation();
            GUI.enabled = previousEnabled;
            if (GUI.Button(new Rect(bounds.width - 42f, 15f, 30f, 34f), "F7", smallButtonStyle)) ToggleVisibility();
            GUI.EndGroup();
        }

        void Counter(float x, string caption, int value, Color color)
        {
            Color previous = GUI.contentColor;
            GUI.contentColor = color;
            GUI.Label(new Rect(x, 8f, 85f, 29f), value.ToString(), valueStyle);
            GUI.contentColor = previous;
            GUI.Label(new Rect(x, 39f, 92f, 18f), caption, mutedStyle);
        }

        void DrawRoster()
        {
            Rect bounds = RosterBounds;
            GUI.Box(bounds, GUIContent.none, panelStyle);
            GUI.Label(new Rect(bounds.x + 12f, bounds.y + 10f, 164f, 24f), "VOYAGEURS  " + (world && world.IsDaily ? world.ActivePassengerCount : ParticipantCount), headingStyle);
            if (GUI.Button(new Rect(bounds.xMax - 39f, bounds.y + 8f, 29f, 28f), rosterExpanded ? "−" : "+", smallButtonStyle))
            { rosterExpanded = !rosterExpanded; activityMenuOpen = false; clearGuiFocus = true; }
            if (!rosterExpanded) return;
            if (GUI.Button(ActivityFilterBounds, GUIContent.none, activityButtonStyle))
            { activityMenuOpen = !activityMenuOpen; activityMenuScroll = Vector2.zero; GUIUtility.ExitGUI(); }
            GUI.Label(new Rect(ActivityFilterBounds.x + 8f, ActivityFilterBounds.y + 5f, ActivityFilterBounds.width - 34f, 22f),
                ActivityFilterLabel(activityFilter), headingStyle);
            GUI.Label(new Rect(ActivityFilterBounds.xMax - 24f, ActivityFilterBounds.y + 5f, 18f, 22f), activityMenuOpen ? "▴" : "▾", headingStyle);
            if (activityMenuOpen) { DrawActivityMenu(); return; }
            int newFilter = GUI.Toolbar(new Rect(bounds.x + 12f, bounds.y + 85f, bounds.width - 24f, 29f), filter, Filters, smallButtonStyle);
            if (newFilter != filter) { filter = newFilter; rosterScroll = Vector2.zero; }
            GUILayout.BeginArea(new Rect(bounds.x + 9f, bounds.y + 123f, bounds.width - 18f, bounds.height - 151f));
            rosterScroll = GUILayout.BeginScrollView(rosterScroll, false, false);
            int matches = 0;
            for (int i = 0; i < ParticipantCount; ++i)
            {
                StationDecisionPassenger passenger = world.participants[i];
                if (!Matches(passenger)) continue;
                matches++;
                Rect row = GUILayoutUtility.GetRect(0f, 54f, GUILayout.ExpandWidth(true));
                bool selected = world && world.IsDaily ? passenger.passengerId == selectedJourneyId : i == selectedParticipant;
                if (GUI.Button(row, GUIContent.none, selected ? selectedRowStyle : rowStyle)) SelectParticipant(i);
                Fill(new Rect(row.x + 7f, row.y + 12f, 3f, 29f), PassengerColor(passenger));
                GUI.Label(new Rect(row.x + 17f, row.y + 5f, row.width - 22f, 22f), JourneyLabel(passenger), headingStyle);
                GUI.Label(new Rect(row.x + 17f, row.y + 28f, row.width - 22f, 21f),
                    passenger.Finished ? ExecutionName(passenger.ExecutionState) : ActivityName(passenger.CurrentActivity.ToString()), mutedStyle);
            }
            if (matches == 0) GUILayout.Label("Aucun voyageur dans cette liste.", mutedStyle);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
            GUI.Label(new Rect(bounds.x + 13f, bounds.yMax - 24f, bounds.width - 20f, 20f), "Sélectionner pour suivre", mutedStyle);
        }

        bool Matches(StationDecisionPassenger passenger)
        {
            if (!passenger || (filter == 1 && passenger.Finished) || (filter == 2 && !passenger.Finished)) return false;
            if (world && world.IsDaily && !passenger.HasEntered) return false;
            return !ActivityFilter.HasValue || passenger.CurrentActivity == ActivityFilter.Value;
        }

        static string ActivityFilterLabel(int index) => index == 0 ? "Toutes les actions" : ActivityName(ActivityChoices[index - 1].ToString());

        void DrawActivityMenu()
        {
            Rect bounds = ActivityMenuBounds;
            GUI.Box(bounds, GUIContent.none, panelStyle);
            Rect viewport = new Rect(bounds.x + 4f, bounds.y + 4f, bounds.width - 8f, bounds.height - 8f);
            float contentHeight = (ActivityChoices.Length + 1) * 28f;
            float contentWidth = viewport.width - (contentHeight > viewport.height ? 17f : 0f);
            activityMenuScroll = GUI.BeginScrollView(viewport, activityMenuScroll, new Rect(0f, 0f, contentWidth, contentHeight), false, false);
            int chosen = -1;
            for (int i = 0; i <= ActivityChoices.Length; ++i)
            {
                if (GUI.Button(new Rect(0f, i * 28f, contentWidth, 27f),
                    (i == activityFilter ? "✓  " : "    ") + ActivityFilterLabel(i), activityButtonStyle))
                    chosen = i;
            }
            GUI.EndScrollView();
            if (chosen >= 0)
            {
                SetActivityFilter(chosen == 0 ? (DecisionActivity?)null : ActivityChoices[chosen - 1]);
                GUIUtility.ExitGUI();
            }
        }

        void DrawDetails()
        {
            Rect bounds = DetailBounds;
            StationDecisionPassenger passenger = SelectedPassenger;
            GUI.Box(bounds, GUIContent.none, panelStyle);
            GUI.Label(new Rect(bounds.x + 14f, bounds.y + 10f, bounds.width - 123f, 24f),
                passenger ? JourneyLabel(passenger).ToUpperInvariant() : world && world.IsDaily ? "JOURNÉE EN GARE" : "VOYAGEUR", headingStyle);
            if (GUI.Button(new Rect(bounds.xMax - 108f, bounds.y + 8f, 28f, 28f), "‹", smallButtonStyle)) SelectRelative(-1);
            if (GUI.Button(new Rect(bounds.xMax - 74f, bounds.y + 8f, 28f, 28f), "›", smallButtonStyle)) SelectRelative(1);
            if (GUI.Button(new Rect(bounds.xMax - 40f, bounds.y + 8f, 28f, 28f), detailsExpanded ? "−" : "+", smallButtonStyle))
                detailsExpanded = !detailsExpanded;
            if (!detailsExpanded) return;
            int nextTab = GUI.Toolbar(new Rect(bounds.x + 12f, bounds.y + 47f, bounds.width - 24f, 31f), tab, world && world.IsDaily ? DayTabs : Tabs, smallButtonStyle);
            if (nextTab != tab) { tab = nextTab; detailScroll = Vector2.zero; }
            GUILayout.BeginArea(new Rect(bounds.x + 14f, bounds.y + 89f, bounds.width - 28f, bounds.height - 104f));
            detailScroll = GUILayout.BeginScrollView(detailScroll, false, false);
            if (world && world.IsDaily && (tab == 3 || !passenger)) DrawDay();
            else if (!passenger) GUILayout.Label("Aucun voyageur affecté au scénario.", mutedStyle);
            else if (tab == 0) DrawObservation(passenger);
            else if (tab == 1) DrawDecision(passenger);
            else DrawLogs(passenger);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        void DrawObservation(StationDecisionPassenger passenger)
        {
            GUILayout.Label(passenger.Finished ? ExecutionName(passenger.ExecutionState) :
                ActivityName(passenger.CurrentActivity.ToString()), titleStyle);
            GUILayout.Label(passenger.Finished ? (passenger.Boarded ? "Embarquement logique terminé" :
                "Trajet terminé sans embarquement") : ExecutionName(passenger.ExecutionState), mutedStyle);
            if (world && world.IsDaily && passenger.ArrivalPlan != null)
            {
                var plan = passenger.ArrivalPlan;
                Pair("Arrivée en gare", plan.arrivalProfile == DecisionArrivalProfile.EarlyService ? "En avance / services" :
                    plan.arrivalProfile == DecisionArrivalProfile.LastMinute ? "Proche du départ" : passenger.IsAlighter ? "Descente du train" : "Arrivée habituelle");
                Pair("Entrée", passenger.IsAlighter ? "Quai " + plan.trackNumber : plan.entranceId.Replace("entry-", ""));
                Pair("Accompagnement", string.IsNullOrEmpty(plan.groupId) ? "Seul" : "Groupe de " + plan.groupSize);
                if (passenger.MobilityProfile != null)
                {
                    var mobility = passenger.MobilityProfile;
                    Pair("Âge", mobility.ageYears + " ans");
                    Pair("Mobilité", mobility.condition == DecisionMobilityCondition.MobilityLimited ? "Mobilité réduite" :
                        mobility.condition == DecisionMobilityCondition.ReducedEndurance ? "Endurance réduite" : "Habituelle");
                    Pair("Vitesse de marche", passenger.walkSpeed.ToString("0.00") + " m/s" + (passenger.UsingGroupPace ? " · rythme du groupe" : " · individuelle"));
                }
                Pair("Représente", plan.representedPassengers + " personnes estimées");
                Pair("Voiture / places", plan.coachNumber + " / " + string.Join(", ", plan.seatNumbers ?? Array.Empty<int>()));
                if (passenger.CoachQueue && passenger.HasPlatformAccess && !passenger.IsAlighter)
                    Pair("File voiture", passenger.Boarded ? "Embarqué" : passenger.CoachQueue.CanBoard(passenger) ? "Monte à bord" :
                        passenger.CoachQueue.Contains(passenger.JourneyId) ? "En file · " + passenger.CoachQueue.Count + " acteurs" : "Vers la voiture " + plan.coachNumber);
                if (passenger.PlatformGate && !passenger.IsAlighter)
                {
                    var gate = passenger.PlatformGate;
                    Pair("Contrôle d’accès", passenger.HasPlatformAccess ? "Passé" : gate.CanProceed(passenger) ? "Franchissement" :
                        gate.QueueOrdinal(passenger.JourneyId) > 0 ? "File · rang " + gate.QueueOrdinal(passenger.JourneyId) : "Attente hors quai");
                    if (!passenger.HasPlatformAccess) Pair("Attente estimée", Duration(gate.EstimatedWaitSeconds(passenger)));
                }
            }
            if (!string.IsNullOrEmpty(passenger.CurrentSeatId))
            {
                GUILayout.Space(7f);
                Pair("Sur place", SeatedActivityName(passenger.SeatedActivity));
                Pair("Place", passenger.CurrentSeatId);
                if (passenger.IsSeated) Pair("Temps sur place", Duration(passenger.SeatedSecondsRemaining));
            }
            if (!passenger.Finished && passenger.CurrentActivity == DecisionActivity.Toilet && passenger.ToiletQueue)
            {
                var wc = passenger.ToiletQueue;
                GUILayout.Space(7f);
                Pair("Places WC", wc.ActiveUseCount + " / " + wc.Capacity);
                int rank = wc.QueueOrdinal(passenger.JourneyId);
                if (rank > 0) Pair("File WC", "Rang " + rank + " · " + wc.Count + " en attente");
                if (passenger.ExecutionState == "WCUsing") Pair("Temps restant WC", Duration(passenger.ToiletSecondsRemaining));
            }
            GUILayout.Space(9f);
            StationRailService service = FindService(passenger.serviceId);
            GUILayout.Label(passenger.IsAlighter ? "TRAIN D’ARRIVÉE" : "VOYAGE ATTRIBUÉ", headingStyle);
            GUILayout.Label(service == null ? passenger.serviceId : service.trainNumber + "  ·  " + service.destination, labelStyle);
            if (passenger.IsAlighter)
            {
                Pair("Quai d’arrivée", passenger.ArrivalPlan.trackNumber.ToString());
                Pair("Après la descente", passenger.IsAlightingServiceVisitor ? "Services puis sortie" : "Sortie directe");
                if (passenger.IsAlightingServiceVisitor)
                {
                    Pair("Services terminés", passenger.AlightingServiceVisitsCompleted + " / " + passenger.ArrivalPlan.alightingServiceVisitLimit);
                    Pair("Fin de séjour envisagée", DayClock(world.rail.schedule.StartClockSeconds + passenger.AlightingStopoverDeadlineSeconds));
                }
            }
            else
            {
                Pair("Quai connu", passenger.PlatformKnown ? passenger.KnownTrip.trackNumber.ToString() : "À confirmer");
                Pair("Information connue", TrainStatus(passenger.KnownTrip.status));
                Pair("Départ annoncé", world && world.IsDaily ? DayClock(world.rail.schedule.StartClockSeconds + passenger.KnownTrip.departureSeconds) : "t + " + Duration(passenger.KnownTrip.departureSeconds));
                Pair("Fin d’embarquement", world && world.IsDaily ? DayClock(world.rail.schedule.StartClockSeconds + passenger.KnownTrip.cutoffSeconds) : "t + " + Duration(passenger.KnownTrip.cutoffSeconds));
            }
            GUILayout.Space(8f);
            GUILayout.Label("TEMPS DISPONIBLE ESTIMÉ", headingStyle);
            Color previous = GUI.contentColor;
            GUI.contentColor = passenger.Finished ? Muted : passenger.IsAlighter && !passenger.IsAlightingServiceVisitor ? Ink : passenger.BudgetSeconds < 0f ? Red : Amber;
            GUILayout.Label(passenger.Finished ? "Trajet terminé" : passenger.IsAlighter && !passenger.IsAlightingServiceVisitor ? "Sortie directe" : Duration(passenger.BudgetSeconds), valueStyle);
            GUI.contentColor = previous;
            GUILayout.Label("Après accès au quai et marge de sécurité", mutedStyle);
            GUILayout.Space(10f);
            GUILayout.Label("BESOINS", headingStyle);
            if (passenger.needs != null)
            {
                Need("Soif", passenger.needs.thirst);
                Need("Toilettes", passenger.needs.toilet);
                Need("Fatigue", passenger.needs.fatigue);
                Need("Achats", passenger.needs.shopping);
                Need("Information", passenger.needs.information);
            }
            GUILayout.Space(9f);
            if (GUILayout.Button(followCamera && followCamera.IsFollowing && followCamera.FollowedPassenger == passenger ?
                "Recentrer sur ce voyageur" : "Suivre ce voyageur", buttonStyle, GUILayout.Height(32f)))
                SelectParticipant(selectedParticipant);
            if (passenger.Finished) GUILayout.Label("La caméra conserve sa dernière position.", mutedStyle);
        }

        string TimeScaleText() => world ? world.SimulationSpeed.ToString("0.#") : "1";
        static string DayClock(double seconds) => StationTimetableDisplay.FormatTime(seconds, Math.Abs(seconds % 60d) > .01d);

        string JourneyLabel(StationDecisionPassenger passenger)
        {
            if (!passenger) return "Voyageur";
            if (!world || !world.IsDaily) return passenger.passengerId;
            var service = FindService(passenger.serviceId);
            string id = passenger.passengerId ?? "";
            int slash = id.LastIndexOf('/');
            string shortId = slash >= 0 ? id.Substring(slash + 1) : id;
            return (passenger.IsAlighter ? "Arr. " : "Voy. ") + shortId + " · " + (service != null ? service.trainNumber : "TER");
        }

        void DrawDay()
        {
            if (!world || !world.rail || !world.rail.schedule) return;
            var schedule = world.rail.schedule;
            var flow = world.arrivalController;
            GUILayout.Label("Horaires du " + schedule.operatingDateIso, titleStyle);
            GUILayout.Label("Gare de Valmont · journée simulée", mutedStyle);
            Pair("Voies disponibles", string.Join(" / ", world.rail.trains.Where(t => t).Select(t => t.trackNumber.ToString())));
            Pair("Voies occupées", world.rail.trains.Count(t => t && t.State != StationTrainState.Offstage) + " / " + world.rail.trains.Length);
            Pair("Chiffre du jour", schedule.services.Length + " départs");
            if (flow && flow.scenario)
            {
                Pair("Population visible", (flow.scenario.visualActorRatio * 100f).ToString("0.#") + "% de la demande");
                Pair("En gare", flow.ActiveCount + " acteurs / " + flow.RepresentedActiveCount + " personnes estimées");
                Pair("Déjà embarqués", flow.BoardedCount + " acteurs / " + flow.RepresentedBoardedCount + " personnes estimées");
                Pair("À venir", flow.ScheduledCount + " arrivées d’acteurs");
                GUILayout.Label("Un acteur représente plusieurs voyageurs. Les files visibles restent une illustration de la densité réelle.", mutedStyle);
            }
            GUILayout.Space(10f);
            GUILayout.Label("VITESSE DE SIMULATION", headingStyle);
            GUILayout.BeginHorizontal();
            bool speedControlsEnabled = GUI.enabled;
            bool speedLocked = world.SimulationSpeedLocked;
            try
            {
                foreach (float speed in new[] { 1f, 4f, 8f })
                {
                    GUI.enabled = speedControlsEnabled && (!speedLocked || speed == 1f);
                    bool selected = speedLocked ? speed == 1f : Mathf.Abs(world.SimulationSpeed - speed) < .01f;
                    if (GUILayout.Button("×" + speed, selected ? activeButtonStyle : buttonStyle)) world.SetSimulationSpeed(speed);
                }
            }
            finally { GUI.enabled = speedControlsEnabled; }
            GUILayout.EndHorizontal();
            if (speedLocked) GUILayout.Label("×1 verrouillé pendant la collecte", mutedStyle);
            GUILayout.Space(12f);
            GUILayout.Label("PROCHAINS TRAINS", headingStyle);
            foreach (int index in world.rail.GetUpcomingServiceIndices().Take(12))
            {
                var service = schedule.services[index];
                var actual = world.ObserveTrip(service.serviceId);
                GUILayout.Space(8f);
                GUILayout.Label(service.trainNumber + " · " + service.destination, headingStyle);
                GUILayout.Label("Arr. " + DayClock(schedule.StartClockSeconds + service.arrivalOffsetSeconds) +
                    "  /  Dép. " + DayClock(schedule.StartClockSeconds + service.departureOffsetSeconds) +
                    "  ·  voie " + service.trackNumber, labelStyle);
                GUILayout.Label(TrainStatus(actual.status) + " · " + world.AdmittedPassengerCount(service.serviceId) + " / " +
                    service.seatedCapacity + " embarquements estimés", mutedStyle);
                GUILayout.Label("Estimation : " + service.expectedAlighting + " descendent · " + service.expectedBoarding + " montent", mutedStyle);
                GUILayout.Label(service.CoachCount + " voitures · " + string.Join(" / ", service.coachSeatCapacities ?? Array.Empty<int>()) + " places", mutedStyle);
                var gate = StationDecisionPlatformGate.For(world, service.trackNumber);
                if (gate)
                    GUILayout.Label("Accès dès " + DayClock(schedule.StartClockSeconds + actual.departureSeconds - service.platformAccessLeadSeconds) +
                        " si train à quai · " + (gate.OpenServiceId == service.serviceId ? "ouvert" : "fermé") +
                        " · file " + gate.CurrentQueuedCount + " acteurs", mutedStyle);
            }
            GUILayout.Space(12f);
            GUILayout.Label("Arrivées à la gare calculées depuis le départ prévu : avance, arrivée habituelle ou proche du départ. Les groupes se déplacent ensemble quand le temps et les places le permettent.", mutedStyle);
        }

        void DrawDecision(StationDecisionPassenger passenger)
        {
            GUILayout.Label("POURQUOI CE CHOIX ?", headingStyle);
            GUILayout.Label(Explain(passenger.LastDecisionReason), labelStyle);
            GUILayout.Space(13f);
            Pair("Destination", DestinationName(passenger));
            Pair("État", ExecutionName(passenger.ExecutionState));
            Pair("Évaluations", passenger.DecisionCount.ToString());
            Pair("Activités terminées", passenger.CompletedActivities.ToString());
            Pair("Tableau consulté", passenger.HasCheckedDisplay ? "Oui" : "Pas encore");
            if (world) Pair("État réel du train", TrainStatus(world.ObserveTrip(passenger.serviceId).status));
            GUILayout.Space(13f);
            GUILayout.Label("PARCOURS RÉCENT", headingStyle);
            GUILayout.Label(History(passenger), labelStyle);
            if (!string.IsNullOrWhiteSpace(passenger.NavigationIssue))
            {
                GUILayout.Space(13f);
                GUILayout.Label("INCIDENT ENREGISTRÉ", headingStyle);
                GUILayout.Label(Explain(passenger.NavigationIssue), labelStyle);
                GUILayout.Label("Ce message reste visible après une reprise du déplacement.", mutedStyle);
            }
            GUILayout.Space(12f);
            GUILayout.Label("Les choix combinent temps restant, besoins et informations connues. Les paramètres de cette expérience sont hypothétiques.", mutedStyle);
        }

        void DrawLogs(StationDecisionPassenger passenger)
        {
            if (!world) return;
            GUILayout.Label(world.IsCollecting ? "ENREGISTREMENT ACTIF" : "COLLECTE ARRÊTÉE", headingStyle);
            GUILayout.Label("Options vidéo et données synchronisées", labelStyle);
            if (CollectionPanel && GUILayout.Button("Configurer / gérer la collecte", buttonStyle, GUILayout.Height(32f))) CollectionPanel.Show();
            GUILayout.Space(8f);
            GUILayout.Label(TextOr(world.OutputDirectory, "Choisir les options puis démarrer la collecte."), mutedStyle);
            bool previous = GUI.enabled;
            GUI.enabled = !string.IsNullOrEmpty(world.OutputDirectory);
            if (GUILayout.Button(Time.unscaledTime < copiedUntil ? "Chemin copié" : "Copier le chemin des journaux",
                buttonStyle, GUILayout.Height(32f)))
            { GUIUtility.systemCopyBuffer = world.OutputDirectory; copiedUntil = Time.unscaledTime + 2f; }
            GUI.enabled = previous;
            GUILayout.Space(14f);
            GUILayout.Label("DERNIER ÉVÉNEMENT", headingStyle);
            GUILayout.Label(TextOr(world.LastEvent, "Aucun événement."), labelStyle);
            GUILayout.Space(14f);
            GUILayout.Label("SCÉNARIO", headingStyle);
            GUILayout.Label(TextOr(world.scenarioName, "DecisionLab"), labelStyle);
            Pair("Graine", world.seed.ToString());
            Pair("Voyageur", passenger.passengerId);
            GUILayout.Space(12f);
            GUILayout.Label("Le manifeste et sync.jsonl relient les images CCTV, positions, objectifs, animations et événements à la même horloge de simulation.", mutedStyle);
        }

        void DrawActualFrameRates()
        {
            if (!world || !world.IsCollecting || !CollectionPanel) return;
            Rect bounds = FrameRatesBounds;
            GUI.Box(bounds, GUIContent.none, panelStyle);
            GUI.BeginGroup(bounds);
            GUI.Label(new Rect(14f, 7f, 280f, 22f), "FPS jeu : " + StationDecisionCollectionPanel.FormatFps(CollectionPanel.ActualGameFps), headingStyle);
            var rates = CollectionPanel.ActualVideoRates;
            GUI.Label(new Rect(300f, 9f, bounds.width - 314f, 20f), rates.Count > 0 ? "FPS vidéo par caméra · mesures sur la dernière seconde" : CollectionPanel.IsVideoRecording ? "Initialisation des mesures vidéo…" : "Mesure sur la dernière seconde · vidéos désactivées", mutedStyle);
            float columnWidth = (bounds.width - 28f) / 6f;
            for (int i = 0; i < rates.Count; ++i)
                GUI.Label(new Rect(14f + (i % 6) * columnWidth, 32f + (i / 6) * 22f, columnWidth - 8f, 22f),
                    rates[i].cameraId + " : " + StationDecisionCollectionPanel.FormatFps(rates[i].videoFpsRolling1s) + " FPS vidéo", labelStyle);
            GUI.EndGroup();
        }

        void DrawCameraBar()
        {
            Rect bounds = BottomBounds;
            GUI.Box(bounds, GUIContent.none, panelStyle);
            GUI.BeginGroup(bounds);
            string status = followCamera && followCamera.IsFollowing ? "SUIVI  ·  " +
                (followCamera.FollowedJourneyLabel ?? "Voyageur") :
                viewControls ? viewControls.GetViewName(viewControls.selectedView) : "VUE DU HALL";
            float actionStart = bounds.width - 515f;
            GUI.Label(new Rect(14f, 5f, Mathf.Max(130f, actionStart - 26f), 23f), status, headingStyle);
            string hint = followCamera && followCamera.IsFollowing ? followCamera.Status + "  ·  clic droit : tourner  ·  molette : zoom  ·  Home : recentrer" :
                viewControls && viewControls.selectedView != 0 ? "Caméra fixe  ·  sélectionner un voyageur : suivre  ·  Hall : vue libre" :
                "Clic droit : tourner  ·  molette : zoom  ·  sélectionner un voyageur : suivre";
            GUI.Label(new Rect(14f, 29f, Mathf.Max(130f, actionStart - 26f), 19f), hint, mutedStyle);
            if (GUI.Button(new Rect(actionStart, 10f, 75f, 33f), "Hall", buttonStyle))
            { if (followCamera) followCamera.ReturnToOverview(); else if (viewControls) viewControls.ResetGameView(); }
            if (GUI.Button(new Rect(actionStart + 81f, 10f, 78f, 33f), "Suivre", buttonStyle)) SelectParticipant(selectedParticipant);
            if (GUI.Button(new Rect(actionStart + 165f, 10f, 98f, 33f), "Recentrer", buttonStyle))
            { if (followCamera && followCamera.IsFollowing) followCamera.ResetOrbit(); else if (viewControls) viewControls.ResetGameView(); }
            if (GUI.Button(new Rect(actionStart + 269f, 10f, 99f, 33f), "Caméras CCTV", buttonStyle) && viewControls)
            {
                if (viewControls.interfaceRoot) viewControls.interfaceRoot.SetActive(true);
                viewControls.ToggleCameraCatalog();
            }
            if (GUI.Button(new Rect(actionStart + 374f, 10f, 99f, 33f), "Toitures", buttonStyle) && viewControls)
                viewControls.ToggleRoofs();
            if (GUI.Button(new Rect(bounds.width - 35f, 10f, 25f, 33f), "›", smallButtonStyle) && viewControls)
                viewControls.NextView();
            GUI.EndGroup();
        }

        void Pair(string name, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(name, mutedStyle, GUILayout.Width(146f));
            GUILayout.Label(TextOr(value, "—"), labelStyle);
            GUILayout.EndHorizontal();
        }

        void Need(string name, float value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(name, mutedStyle, GUILayout.Width(87f));
            Rect bar = GUILayoutUtility.GetRect(24f, 20f, GUILayout.ExpandWidth(true));
            Fill(new Rect(bar.x, bar.y + 8f, bar.width, 5f), new Color(.13f, .23f, .25f));
            Fill(new Rect(bar.x, bar.y + 8f, bar.width * Mathf.Clamp01(value), 5f), Accent);
            GUILayout.Label(Mathf.RoundToInt(Mathf.Clamp01(value) * 100f) + "%", mutedStyle, GUILayout.Width(38f));
            GUILayout.EndHorizontal();
        }

        StationRailService FindService(string id)
        {
            if (world && world.rail && world.rail.schedule && world.rail.schedule.services != null)
                foreach (var service in world.rail.schedule.services)
                    if (service != null && service.serviceId == id) return service;
            return null;
        }

        string DestinationName(StationDecisionPassenger passenger)
        {
            if (string.IsNullOrWhiteSpace(passenger.CurrentTargetId)) return "—";
            if (world && world.pois != null)
                foreach (var poi in world.pois)
                    if (poi && poi.poiId == passenger.CurrentTargetId)
                        return poi.activity == DecisionActivity.Platform ? "Quai " + poi.trackNumber : ActivityName(poi.activity.ToString());
            return passenger.CurrentTargetId;
        }

        static Color PassengerColor(StationDecisionPassenger passenger) => passenger.Boarded ? Accent :
            passenger.Finished ? Muted : passenger.IsAlighter && !passenger.IsAlightingServiceVisitor ? Ink : passenger.BudgetSeconds < 0f ? Amber : Ink;
        static string History(StationDecisionPassenger passenger)
        {
            var history = passenger.ActivityHistory;
            if (history == null || history.Count == 0) return "En attente du premier choix.";
            var result = new StringBuilder();
            int start = Mathf.Max(0, history.Count - 10);
            if (start > 0) result.Append("… → ");
            for (int i = start; i < history.Count; ++i)
            { if (i > start) result.Append(" → "); result.Append(ActivityName(history[i])); }
            return result.ToString();
        }

        static string ActivityName(string value)
        {
            switch (value)
            {
                case "Display": return "Consulter le tableau";
                case "Cafe": return "Café";
                case "Toilet": return "Toilettes";
                case "Shop": return "Boutique";
                case "Rest": return "Repos";
                case "Info": return "Accueil / information";
                case "Platform": return "Rejoindre le quai";
                case "Exit": return "Quitter la gare";
                case "Board": return "Embarquer";
                case "None": return "Attendre puis réévaluer";
                default: return TextOr(value, "—");
            }
        }

        static string ExecutionName(string value)
        {
            switch (value)
            {
                case "Ready": return "Évalue la prochaine activité";
                case "Walking": return "En déplacement";
                case "GateApproach": return "Vers le contrôle d’accès";
                case "GateWaiting": return "En attente d’ouverture du quai";
                case "GateQueue": return "File de contrôle des billets";
                case "GateCrossing": return "Passage du contrôle";
                case "CoachApproach": return "Vers la file de sa voiture";
                case "CoachQueue": return "File d’embarquement à la voiture";
                case "WCApproach": return "Vers la file des toilettes";
                case "WCQueue": return "En file devant les toilettes";
                case "WCToUse": return "Entre aux toilettes";
                case "WCUsing": return "Utilise les toilettes";
                case "WCClearing": return "Quitte les toilettes";
                case "Using": return "Activité en cours";
                case "Service": return "File ou service en cours";
                case "ClearingCounter": return "Quitte le comptoir";
                case "WalkingToSeat": return "Rejoint sa place";
                case "SittingDown": return "S’installe";
                case "Seated": return "Activité assise sur place";
                case "StandingUp": return "Se lève pour repartir";
                case "WaitingForTrain": return "Attend l’embarquement";
                case "Boarded": return "À bord";
                case "Exited": return "A quitté la gare";
                case "Failed": return "Trajet interrompu";
                default: return TextOr(value, "Initialisation");
            }
        }

        static string SeatedActivityName(DecisionSeatedActivity value)
        {
            switch (value)
            {
                case DecisionSeatedActivity.Coffee: return "Pause café";
                case DecisionSeatedActivity.Reading: return "Lecture";
                case DecisionSeatedActivity.Rest: return "Repos";
                default: return "—";
            }
        }

        static string TrainStatus(DecisionTrainStatus status)
        {
            switch (status)
            {
                case DecisionTrainStatus.Scheduled: return "À l’heure";
                case DecisionTrainStatus.Delayed: return "Retard annoncé";
                case DecisionTrainStatus.Cancelled: return "Annulé";
                case DecisionTrainStatus.Boarding: return "Embarquement ouvert";
                case DecisionTrainStatus.Closed: return "Embarquement fermé";
                case DecisionTrainStatus.Departed: return "Parti";
                default: return status.ToString();
            }
        }

        static string Explain(string value)
        {
            switch (value)
            {
                case "Initial station entry": return "Arrivée en gare : consulter les informations du voyage.";
                case "Feasible; weighted by needs, familiarity, information and previous activity.":
                    return "Activité possible dans le temps restant. Le choix tient compte des besoins, de la connaissance de la gare et de l’activité précédente.";
                case "Obtain the missing platform information.": return "Trouver le quai avant de poursuivre le voyage.";
                case "Urgent direct train access.": return "Le temps est limité : rejoindre directement le train.";
                case "Direct train access remains possible but uses the safety reserve.":
                    return "Le train reste accessible en utilisant la marge de sécurité.";
                case "Replan after cancellation, closed boarding or missed cutoff.":
                    return "Le voyage initial n’est plus possible : chercher de l’aide ou sortir.";
                case "No feasible action; retry after capacity/path recovery":
                    return "Aucune activité possible pour le moment. Une nouvelle évaluation suivra.";
                case "Native navigation made no progress for 25 seconds.": return "Le déplacement n’a pas progressé pendant 25 secondes.";
                case "Activity route exceeded 240 seconds.": return "Le déplacement vers l’activité a dépassé quatre minutes.";
                case "Activity budget exhausted": return "Le temps réservé à l’activité est épuisé.";
                case "Destination became full before commitment": return "La destination s’est remplie avant le départ.";
                case "Service unavailable or FIFO visit timed out.": return "Le service est indisponible ou l’attente a dépassé le délai.";
                case "Virtual boarding at safe platform anchor before door closure":
                    return "Embarquement logique validé au quai avant la fermeture des portes.";
                case "Exited after replanning": return "Le voyageur a quitté la gare après avoir changé son plan.";
                case "Announcement: Delay": return "Une annonce de retard déclenche une nouvelle évaluation.";
                case "Announcement: Cancellation": return "L’annonce d’annulation déclenche une nouvelle évaluation.";
            }
            if (!string.IsNullOrEmpty(value) && value.StartsWith("Completed ", StringComparison.Ordinal))
                return "Activité terminée : " + ActivityName(value.Substring(10));
            return TextOr(value, "En attente du premier choix.");
        }

        static string Duration(float seconds)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds)) return "—";
            double absolute = Math.Abs((double)seconds);
            return (seconds < 0f ? "−" : "") + Math.Floor(absolute / 60d).ToString("00") + ":" + Math.Floor(absolute % 60d).ToString("00");
        }
        static string TextOr(string text, string fallback) => string.IsNullOrWhiteSpace(text) ? fallback : text;
        static void Fill(Rect bounds, Color color)
        {
            if (Event.current.type != EventType.Repaint) return;
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(bounds, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        void EnsureStyles()
        {
            if (panelStyle != null) return;
            panelTexture = Texture(new Color(.025f, .060f, .073f, .96f));
            buttonTexture = Texture(new Color(.08f, .17f, .19f));
            hoverTexture = Texture(new Color(.12f, .28f, .29f));
            activeTexture = Texture(new Color(.12f, .34f, .30f));
            rowTexture = Texture(new Color(.045f, .10f, .12f));
            panelStyle = new GUIStyle(GUI.skin.box) { normal = { background = panelTexture }, border = new RectOffset(), padding = new RectOffset() };
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true, richText = false, normal = { textColor = Ink },
                margin = new RectOffset(0, 0, 2, 2), padding = new RectOffset() };
            mutedStyle = new GUIStyle(labelStyle) { fontSize = 12, normal = { textColor = Muted } };
            headingStyle = new GUIStyle(labelStyle) { fontSize = 13, fontStyle = FontStyle.Bold, wordWrap = false, clipping = TextClipping.Clip };
            titleStyle = new GUIStyle(labelStyle) { fontSize = 21, fontStyle = FontStyle.Bold };
            valueStyle = new GUIStyle(labelStyle) { fontSize = 25, fontStyle = FontStyle.Bold, wordWrap = false };
            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13, wordWrap = true, padding = new RectOffset(7, 7, 4, 4), margin = new RectOffset(0, 0, 2, 2),
                normal = { background = buttonTexture, textColor = Ink }, hover = { background = hoverTexture, textColor = Color.white },
                active = { background = activeTexture, textColor = Color.white }, focused = { background = hoverTexture, textColor = Color.white },
                onNormal = { background = activeTexture, textColor = Color.white }, onHover = { background = activeTexture, textColor = Color.white }
            };
            activeButtonStyle = new GUIStyle(buttonStyle) { normal = { background = activeTexture, textColor = Color.white } };
            smallButtonStyle = new GUIStyle(buttonStyle) { fontSize = 12, padding = new RectOffset(3, 3, 3, 3) };
            rowStyle = new GUIStyle(buttonStyle) { normal = { background = rowTexture }, margin = new RectOffset(0, 0, 0, 5) };
            selectedRowStyle = new GUIStyle(rowStyle) { normal = { background = activeTexture } };
            activityButtonStyle = new GUIStyle(smallButtonStyle) { alignment = TextAnchor.MiddleLeft, wordWrap = false,
                clipping = TextClipping.Clip, padding = new RectOffset(7, 7, 3, 3) };
        }

        static Texture2D Texture(Color color)
        {
            var texture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixel(0, 0, color); texture.Apply(); return texture;
        }

        void CreatePointerSurfaces()
        {
            if (pointerCanvas) return;
            var canvasObject = new GameObject("Decision dashboard input", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            pointerCanvas = canvasObject.GetComponent<Canvas>();
            pointerCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            pointerCanvas.sortingOrder = 1000;
            for (int i = 0; i < pointerRects.Length; ++i)
            {
                var surface = new GameObject("Input region " + i, typeof(RectTransform), typeof(Image));
                surface.transform.SetParent(canvasObject.transform, false);
                pointerRects[i] = surface.GetComponent<RectTransform>();
                pointerRects[i].anchorMin = pointerRects[i].anchorMax = pointerRects[i].pivot = new Vector2(0f, 1f);
                pointerImages[i] = surface.GetComponent<Image>();
                pointerImages[i].color = Color.clear;
            }
        }

        void UpdatePointerSurfaces()
        {
            if (!pointerCanvas) return;
            for (int i = 0; i < pointerRects.Length; ++i)
            {
                Rect bounds = i == 0 ? (visible ? TopBounds : HiddenBounds) : i == 1 ? RosterBounds : i == 2 ? DetailBounds : BottomPointerBounds;
                bool modal = CollectionPanel && CollectionPanel.IsOpen;
                if (modal && i == 0) bounds = new Rect(0f, 0f, ViewSize.x, ViewSize.y);
                pointerImages[i].raycastTarget = modal ? i == 0 : !CatalogOpen && (visible || i == 0);
                pointerRects[i].anchoredPosition = new Vector2(bounds.x, -bounds.y) * Scale;
                pointerRects[i].sizeDelta = bounds.size * Scale;
            }
        }

        void OnDisable()
        {
            activityMenuOpen = false;
            if (viewControls) viewControls.SuppressKeyboardShortcuts = false;
            if (pointerCanvas) Destroy(pointerCanvas.gameObject);
            pointerCanvas = null;
        }

        void OnDestroy()
        {
            if (panelTexture) Destroy(panelTexture);
            if (buttonTexture) Destroy(buttonTexture);
            if (hoverTexture) Destroy(hoverTexture);
            if (activeTexture) Destroy(activeTexture);
            if (rowTexture) Destroy(rowTexture);
        }
    }
}
