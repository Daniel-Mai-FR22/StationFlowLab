using StationFlow.TrainStation.Social;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Lab-only third-person control of the existing hall camera; never creates a camera or moves an actor.</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(250)]
    public sealed class StationDecisionFollowCamera : MonoBehaviour
    {
        public StationDecisionWorld world;
        public TrainStationViewControls viewControls;
        public Camera controlledCamera;
        public TrainStationOrbitCamera overviewOrbit;
        public StationDecisionDashboard dashboard;

        [Header("Passenger framing")]
        [Min(.1f)] public float focusHeight = 1.15f;
        [Min(.1f)] public float seatedFocusHeight = 1.5f;
        [Min(.5f)] public float defaultDistance = 6f;
        [Range(1f, 80f)] public float defaultPitch = 8.5f;
        [Range(25f, 90f)] public float followFieldOfView = 58f;
        [Min(0f)] public float translationSmoothTime = .12f;
        [Min(.5f)] public float minimumDistance = 2.2f;
        [Min(.5f)] public float maximumDistance = 12f;
        [Range(1f, 80f)] public float minimumPitch = 5f;
        [Range(1f, 80f)] public float maximumPitch = 60f;
        [Min(.01f)] public float orbitSensitivity = .18f;
        [Range(.01f, .8f)] public float zoomSensitivity = .12f;

        [Header("Architecture visibility")]
        public LayerMask collisionMask = ~0;
        [Min(.02f)] public float collisionRadius = .18f;
        [Min(.01f)] public float collisionPadding = .08f;
        public Transform[] ignoredCollisionRoots;

        public bool IsFollowing { get; private set; }
        StationDecisionPassenger followedPassenger;
        // Pool reuse must never expose the replacement traveller through the public target.
        public StationDecisionPassenger FollowedPassenger => followedPassenger && followedPassenger.passengerId == followedId ? followedPassenger : null;
        public int LastFocusUpdateFrame { get; private set; } = -1;
        public string FollowedJourneyId => followedId;
        public string FollowedJourneyLabel { get; private set; }
        public bool TargetFrozen { get; private set; }
        public bool OcclusionLimited { get; private set; }
        public bool IsPointerCaptured => pointerCaptured;
        public float CurrentYaw => yaw;
        public float CurrentPitch => pitch;
        public float RequestedDistance => requestedDistance;
        public float ActualDistance { get; private set; }
        public Vector3 FocusPosition => focus;
        public string Status
        {
            get
            {
                if (!IsFollowing) return viewControls && viewControls.selectedView != 0 ? "Caméra fixe" : "Vue d’ensemble";
                string id = FollowedJourneyLabel ?? followedId;
                if (TargetFrozen)
                    return (FollowedPassenger && FollowedPassenger.Boarded ? "Embarqué" :
                        FollowedPassenger && FollowedPassenger.ExecutionState == "Exited" ? "Sorti" : "Trajet terminé") +
                        " · dernière position de " + id;
                return "Suivi de " + id + (OcclusionLimited ? " · obstacle proche" : "");
            }
        }

        readonly RaycastHit[] hits = new RaycastHit[256];
        readonly Collider[] overlaps = new Collider[128];
        float yaw, pitch, requestedDistance, previousFieldOfView, previousNearClip;
        Vector3 focus, lastTargetFocus, focusVelocity;
        string followedId;
        bool orbitInitialized, pointerCaptured, savedLens;
        CursorLockMode previousCursorLock;
        bool previousCursorVisible;

        void OnEnable() { FindReferences(); }

        void FindReferences()
        {
            if (!viewControls)
                foreach (var candidate in FindObjectsByType<TrainStationViewControls>(FindObjectsSortMode.None))
                    if (candidate.gameObject.scene == gameObject.scene) { viewControls = candidate; break; }
            if (!controlledCamera && viewControls && viewControls.ViewCount > 0) controlledCamera = viewControls.viewpoints[0];
            if (!overviewOrbit && controlledCamera) overviewOrbit = controlledCamera.GetComponent<TrainStationOrbitCamera>();
            if (!dashboard) dashboard = GetComponent<StationDecisionDashboard>();
            if (!world) world = GetComponent<StationDecisionWorld>();
        }

        public void Follow(StationDecisionPassenger traveller)
        {
            if (!traveller) return;
            FindReferences();
            if (!controlledCamera || !viewControls || viewControls.ViewCount == 0 || viewControls.viewpoints[0] != controlledCamera) return;
            ValidateLimits();
            if (!savedLens)
            {
                previousFieldOfView = controlledCamera.fieldOfView;
                previousNearClip = controlledCamera.nearClipPlane;
                savedLens = true;
            }
            // Selection enables the existing camera/listener. Its overview controller relinquishes the transform immediately.
            viewControls.SelectView(0);
            if (overviewOrbit) overviewOrbit.enabled = false;
            followedPassenger = traveller;
            followedId = traveller.passengerId;
            FollowedJourneyLabel = traveller.ShortJourneyLabel;
            IsFollowing = true;
            TargetFrozen = false;
            lastTargetFocus = PassengerFocus(traveller);
            focus = lastTargetFocus;
            focusVelocity = Vector3.zero;
            if (!orbitInitialized)
            {
                yaw = traveller.transform.eulerAngles.y;
                pitch = defaultPitch;
                requestedDistance = defaultDistance;
                orbitInitialized = true;
            }
            controlledCamera.fieldOfView = followFieldOfView;
            controlledCamera.nearClipPlane = Mathf.Min(previousNearClip, .08f);
            RefreshFocus(0f, true);
            ReapplyView();
        }

        public void ReturnToOverview()
        {
            StopFollowing();
            FindReferences();
            if (viewControls) viewControls.SelectView(0);
            if (overviewOrbit) { overviewOrbit.enabled = true; overviewOrbit.ReapplyView(); }
        }

        void StopFollowing()
        {
            ReleasePointer();
            IsFollowing = false;
            followedPassenger = null;
            TargetFrozen = false;
            OcclusionLimited = false;
            if (controlledCamera && savedLens)
            {
                controlledCamera.fieldOfView = previousFieldOfView;
                controlledCamera.nearClipPlane = previousNearClip;
            }
            savedLens = false;
            if (overviewOrbit && controlledCamera && controlledCamera.enabled)
            { overviewOrbit.enabled = true; overviewOrbit.ReapplyView(); }
        }

        public void ResetOrbit()
        {
            if (!IsFollowing) return;
            ValidateLimits();
            yaw = FollowedPassenger ? FollowedPassenger.transform.eulerAngles.y : yaw;
            pitch = defaultPitch;
            requestedDistance = defaultDistance;
            focusVelocity = Vector3.zero;
            RefreshFocus(0f, true);
            ReapplyView();
        }

        public void Orbit(Vector2 pixels)
        {
            if (!IsFollowing || !Finite(pixels.x) || !Finite(pixels.y)) return;
            yaw = Mathf.Repeat(yaw + pixels.x * orbitSensitivity, 360f);
            pitch = Mathf.Clamp(pitch - pixels.y * orbitSensitivity, minimumPitch, maximumPitch);
            ReapplyView();
        }

        public void Zoom(float notches)
        {
            if (!IsFollowing || !Finite(notches)) return;
            requestedDistance = Mathf.Clamp(requestedDistance * Mathf.Exp(-Mathf.Clamp(notches, -100f, 100f) * zoomSensitivity),
                minimumDistance, maximumDistance);
            ReapplyView();
        }

        void Update()
        {
            if (!IsFollowing) return;
            // SelectView also re-enables the hall orbit on an explicit Hall/Home choice, even when index stays zero.
            if (!controlledCamera || !controlledCamera.enabled || !viewControls || viewControls.SelectedCamera != controlledCamera ||
                (overviewOrbit && overviewOrbit.enabled))
            { StopFollowing(); return; }
            if (!Application.isFocused) { ReleasePointer(); return; }
            bool held = false, pressed = false, reset = false;
            Vector2 delta = Vector2.zero, pointer = Vector2.zero;
            float scroll = 0f;
#if ENABLE_INPUT_SYSTEM
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                held = mouse.rightButton.isPressed; pressed = mouse.rightButton.wasPressedThisFrame;
                delta = mouse.delta.ReadValue(); pointer = mouse.position.ReadValue(); scroll = mouse.scroll.ReadValue().y;
                if (InputSystem.settings.scrollDeltaBehavior == InputSettings.ScrollDeltaBehavior.KeepPlatformSpecificInputRange &&
                    (Application.platform == RuntimePlatform.WindowsEditor || Application.platform == RuntimePlatform.WindowsPlayer)) scroll /= 120f;
            }
            reset = Keyboard.current != null && Keyboard.current.homeKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            held = Input.GetMouseButton(1); pressed = Input.GetMouseButtonDown(1);
            delta = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 20f;
            pointer = Input.mousePosition; scroll = Input.mouseScrollDelta.y; reset = Input.GetKeyDown(KeyCode.Home);
#endif
            if (!held) ReleasePointer();
            bool blocked = ShouldBlockPointer(pointer);
            if (pressed && !blocked) CapturePointer();
            if (pointerCaptured && held) Orbit(delta);
            if (!blocked && Mathf.Abs(scroll) > .0001f) Zoom(scroll);
            if (reset && !TrainStationViewControls.IsEditingText() && !(dashboard && dashboard.IsTextInputFocused)) ResetOrbit();
        }

        void LateUpdate()
        {
            if (!IsFollowing || !controlledCamera || !controlledCamera.enabled) return;
            // The follow target updates on simulation time; camera input and interpolation remain responsive during pause.
            RefreshFocus(Time.unscaledDeltaTime, false);
            ReapplyView();
        }

        void RefreshFocus(float deltaTime, bool snap)
        {
            if (followedPassenger && followedPassenger.passengerId != followedId)
            { followedPassenger = null; TargetFrozen = true; }
            LastFocusUpdateFrame = Time.frameCount;
            if (!TargetFrozen)
            {
                if (FollowedPassenger)
                {
                    // Read the final transform once even when boarding just deactivated the actor this frame.
                    lastTargetFocus = PassengerFocus(FollowedPassenger);
                    if (FollowedPassenger.Finished || !FollowedPassenger.gameObject.activeInHierarchy) TargetFrozen = true;
                }
                else TargetFrozen = true;
            }
            if (snap || translationSmoothTime <= 0f) focus = lastTargetFocus;
            else if (deltaTime > 0f)
                focus = Vector3.SmoothDamp(focus, lastTargetFocus, ref focusVelocity, translationSmoothTime, Mathf.Infinity,
                    Mathf.Min(deltaTime, .1f));
        }

        Vector3 PassengerFocus(StationDecisionPassenger traveller)
        {
            // The calibrated seated root is lower than the standing root. Keep the
            // camera cast above chair backs throughout sitting, dwelling and standing.
            bool atChair = traveller.ReservedSeat && (traveller.ExecutionState == "SittingDown" ||
                traveller.ExecutionState == "Seated" || traveller.ExecutionState == "StandingUp");
            float height = atChair ? Mathf.Max(focusHeight, seatedFocusHeight) : focusHeight;
            return traveller.transform.position + Vector3.up * height;
        }

        /// <summary>Apply the orbit immediately; only target translation is smoothed by LateUpdate.</summary>
        public void ReapplyView()
        {
            if (!IsFollowing || !controlledCamera) return;
            ValidateLimits();
            pitch = Mathf.Clamp(pitch, minimumPitch, maximumPitch);
            requestedDistance = Mathf.Clamp(requestedDistance, minimumDistance, maximumDistance);
            var rotation = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 backwards = rotation * Vector3.back;
            float resolved = ResolveArchitectureDistance(focus, backwards, requestedDistance);
            // A wall beside a moving actor can separate its real position from the smoothed focus.
            // Snap the translation in that exceptional case rather than looking through the wall.
            if ((lastTargetFocus - focus).sqrMagnitude > .0001f &&
                HasArchitectureBetween(lastTargetFocus, focus + backwards * resolved))
            {
                focus = lastTargetFocus; focusVelocity = Vector3.zero;
                resolved = ResolveArchitectureDistance(focus, backwards, requestedDistance);
            }
            ActualDistance = resolved;
            OcclusionLimited = resolved < requestedDistance - .05f;
            controlledCamera.transform.SetPositionAndRotation(focus + backwards * resolved, rotation);
        }

        float ResolveArchitectureDistance(Vector3 origin, Vector3 direction, float distance)
        {
            int count = Physics.SphereCastNonAlloc(origin, collisionRadius, direction, hits, distance, collisionMask, QueryTriggerInteraction.Ignore);
            float resolved = distance;
            for (int i = 0; i < count; ++i)
                if (!IgnoreObstruction(hits[i].collider)) resolved = Mathf.Min(resolved, Mathf.Max(.1f, hits[i].distance - collisionPadding));
            if (count == hits.Length)
                foreach (var hit in Physics.SphereCastAll(origin, collisionRadius, direction, distance, collisionMask, QueryTriggerInteraction.Ignore))
                    if (!IgnoreObstruction(hit.collider)) resolved = Mathf.Min(resolved, Mathf.Max(.1f, hit.distance - collisionPadding));
            // Sphere casts do not report a collider already overlapping the origin. Also verify the camera volume itself.
            if (OverlapsArchitecture(origin + direction * resolved))
            {
                float safe = 0f;
                for (int step = 1; step <= 24; ++step)
                {
                    float candidate = resolved * (1f - step / 24f);
                    if (!OverlapsArchitecture(origin + direction * candidate)) { safe = candidate; break; }
                }
                resolved = Mathf.Max(.1f, safe);
            }
            return resolved;
        }

        bool HasArchitectureBetween(Vector3 origin, Vector3 end)
        {
            Vector3 direction = end - origin;
            float distance = direction.magnitude;
            if (distance <= .001f) return false;
            int count = Physics.RaycastNonAlloc(origin, direction / distance, hits, distance, collisionMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; ++i) if (!IgnoreObstruction(hits[i].collider)) return true;
            if (count == hits.Length)
                foreach (var hit in Physics.RaycastAll(origin, direction / distance, distance, collisionMask, QueryTriggerInteraction.Ignore))
                    if (!IgnoreObstruction(hit.collider)) return true;
            return false;
        }

        bool OverlapsArchitecture(Vector3 position)
        {
            int count = Physics.OverlapSphereNonAlloc(position, collisionRadius, overlaps, collisionMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; ++i) if (!IgnoreObstruction(overlaps[i])) return true;
            return count == overlaps.Length;
        }

        bool IgnoreObstruction(Collider obstacle)
        {
            if (!obstacle) return true;
            Transform root = obstacle.transform;
            if ((controlledCamera && (root == controlledCamera.transform || root.IsChildOf(controlledCamera.transform))) ||
                obstacle.GetComponentInParent<StationActorMotion>() || obstacle.GetComponentInParent<StationDecisionPassenger>()) return true;
            if (ignoredCollisionRoots != null)
                foreach (var ignored in ignoredCollisionRoots)
                    if (ignored && (root == ignored || root.IsChildOf(ignored))) return true;
            return false;
        }

        public bool ShouldBlockPointer(Vector2 screenPosition)
        {
            if (dashboard && dashboard.IsPointerOverDashboard(screenPosition)) return true;
            var eventSystem = EventSystem.current;
            if (!eventSystem) return false;
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current != null && eventSystem.IsPointerOverGameObject(Mouse.current.deviceId)) return true;
#endif
            return eventSystem.IsPointerOverGameObject();
        }

        void CapturePointer()
        {
            if (pointerCaptured) return;
            previousCursorLock = Cursor.lockState; previousCursorVisible = Cursor.visible;
            pointerCaptured = true; Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        }

        void ReleasePointer()
        {
            if (!pointerCaptured) return;
            pointerCaptured = false; Cursor.lockState = previousCursorLock; Cursor.visible = previousCursorVisible;
        }

        void OnValidate() { ValidateLimits(); }
        void ValidateLimits()
        {
            seatedFocusHeight = Finite(seatedFocusHeight) ? Mathf.Max(focusHeight, seatedFocusHeight) : Mathf.Max(focusHeight, 1.5f);
            minimumDistance = Mathf.Max(.5f, minimumDistance); maximumDistance = Mathf.Max(minimumDistance, maximumDistance);
            minimumPitch = Mathf.Clamp(minimumPitch, 1f, 80f); maximumPitch = Mathf.Clamp(maximumPitch, minimumPitch, 80f);
            defaultPitch = Mathf.Clamp(defaultPitch, minimumPitch, maximumPitch); defaultDistance = Mathf.Clamp(defaultDistance, minimumDistance, maximumDistance);
            collisionRadius = Mathf.Max(.02f, collisionRadius); collisionPadding = Mathf.Max(.01f, collisionPadding);
        }

        void OnDisable() { if (IsFollowing || savedLens) StopFollowing(); else ReleasePointer(); }
        void OnApplicationFocus(bool focused) { if (!focused) ReleasePointer(); }
        void OnApplicationPause(bool paused) { if (paused) ReleasePointer(); }
        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }
}
