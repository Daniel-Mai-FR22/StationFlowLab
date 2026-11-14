using StationFlow.TrainStation.Social;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace StationFlow.TrainStation
{
    /// <summary>Mouse navigation for the saved hall camera. Never changes a surveillance camera.</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
    public sealed class TrainStationOrbitCamera : MonoBehaviour
    {
        [Header("Saved hall camera and stationary focus anchor")]
        public Camera controlledCamera;
        public Transform focusPoint;

        [Header("Home view")]
        public float defaultYaw = 15f;
        [Range(1f, 89f)] public float defaultPitch = 25f;
        [Min(.5f)] public float defaultDistance = 19f;

        [Header("Hall exploration limits")]
        [Range(1f, 89f)] public float minimumPitch = 12f;
        [Range(1f, 89f)] public float maximumPitch = 75f;
        [Min(.5f)] public float minimumDistance = 5f;
        [Min(.5f)] public float maximumDistance = 35f;
        [Tooltip("World-space offsets from the saved focus anchor; keep the focus inside the hall.")]
        public Vector3 panBoundsMin = new Vector3(-18f, 0f, -18f);
        public Vector3 panBoundsMax = new Vector3(18f, 0f, 23f);

        [Header("Mouse response")]
        [Min(.01f)] public float orbitSensitivity = .18f;
        [Min(.0001f)] public float panSensitivity = .0015f;
        [Range(.01f, .8f)] public float zoomSensitivity = .12f;

        [Header("Architecture collision")]
        public LayerMask collisionMask = ~0;
        [Min(.02f)] public float collisionRadius = .28f;
        [Min(.01f)] public float collisionPadding = .08f;
        [Tooltip("Optional camera rigs or helpers excluded from the obstruction query. People are excluded automatically.")]
        public Transform[] ignoredCollisionRoots;

        [SerializeField] float currentYaw;
        [SerializeField] float currentPitch;
        [SerializeField] float currentDistance;
        [SerializeField] Vector3 currentFocusOffset;
        [SerializeField] float actualDistance;
        readonly RaycastHit[] obstructionHits = new RaycastHit[128];
        bool initialized;
        bool pointerCaptured;
        CursorLockMode previousCursorLock;
        bool previousCursorVisible;

        public float CurrentYaw => currentYaw;
        public float CurrentPitch => currentPitch;
        public float CurrentDistance => currentDistance;
        public float ActualDistance => actualDistance;
        public Vector3 CurrentFocusOffset => currentFocusOffset;
        public Vector3 FocusPosition => (focusPoint ? focusPoint.position : Vector3.zero) + currentFocusOffset;
        public bool IsPointerCaptured => pointerCaptured;

        void OnEnable()
        {
            if (!controlledCamera) controlledCamera = GetComponent<Camera>();
            if (Application.isPlaying) EnsureInitialized();
        }

        void Update()
        {
            if (!controlledCamera || !controlledCamera.enabled || !Application.isFocused)
            {
                ReleasePointer();
                return;
            }

            bool orbitHeld = false;
            bool panHeld = false;
            bool capturePressed = false;
            bool resetPressed = false;
            Vector2 delta = Vector2.zero;
            float scroll = 0f;
#if ENABLE_INPUT_SYSTEM
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                orbitHeld = mouse.rightButton.isPressed;
                panHeld = mouse.middleButton.isPressed;
                capturePressed = mouse.rightButton.wasPressedThisFrame || mouse.middleButton.wasPressedThisFrame;
                delta = mouse.delta.ReadValue();
                scroll = mouse.scroll.ReadValue().y;
                // Input System 1.20 normalizes a wheel notch to one by default. Only the
                // optional native Windows range still reports WHEEL_DELTA (120) per notch.
                if (InputSystem.settings.scrollDeltaBehavior == InputSettings.ScrollDeltaBehavior.KeepPlatformSpecificInputRange &&
                    (Application.platform == RuntimePlatform.WindowsEditor || Application.platform == RuntimePlatform.WindowsPlayer))
                    scroll /= 120f;
            }
            resetPressed = Keyboard.current != null && Keyboard.current.homeKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            orbitHeld = Input.GetMouseButton(1);
            panHeld = Input.GetMouseButton(2);
            capturePressed = Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2);
            // Legacy Mouse X/Y are scaled axes; recover a comparable pixel response.
            delta = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 20f;
            scroll = Input.mouseScrollDelta.y;
            resetPressed = Input.GetKeyDown(KeyCode.Home);
#endif
            if (!orbitHeld && !panHeld) ReleasePointer();
            bool overInterface = PointerIsOverInterface();
            if (capturePressed && !overInterface) CapturePointer();
            if (pointerCaptured)
            {
                if (orbitHeld) Orbit(delta);
                else if (panHeld) Pan(delta);
            }
            if (!overInterface && Mathf.Abs(scroll) > .0001f) Zoom(scroll);
            if (resetPressed && !TrainStationViewControls.IsEditingText()) ResetView();
        }

        void LateUpdate()
        {
            if (controlledCamera && controlledCamera.enabled) ReapplyView();
        }

        /// <summary>Horizontal/vertical mouse delta in pixels.</summary>
        public void Orbit(Vector2 pixels)
        {
            if (!Finite(pixels.x) || !Finite(pixels.y)) return;
            EnsureInitialized();
            currentYaw = Mathf.Repeat(currentYaw + pixels.x * orbitSensitivity, 360f);
            currentPitch = Mathf.Clamp(currentPitch - pixels.y * orbitSensitivity, minimumPitch, maximumPitch);
            ReapplyView();
        }

        /// <summary>Drag the focus horizontally within the authored hall bounds.</summary>
        public void Pan(Vector2 pixels)
        {
            if (!Finite(pixels.x) || !Finite(pixels.y)) return;
            EnsureInitialized();
            Quaternion horizontalRotation = Quaternion.Euler(0f, currentYaw, 0f);
            Vector3 right = horizontalRotation * Vector3.right;
            Vector3 forward = horizontalRotation * Vector3.forward;
            currentFocusOffset -= (right * pixels.x + forward * pixels.y) * (currentDistance * panSensitivity);
            ClampFocus();
            ReapplyView();
        }

        /// <summary>Positive scroll notches zoom towards the focus, independent of simulation speed.</summary>
        public void Zoom(float scrollNotches)
        {
            if (!Finite(scrollNotches)) return;
            EnsureInitialized();
            currentDistance = Mathf.Clamp(currentDistance * Mathf.Exp(-Mathf.Clamp(scrollNotches, -100f, 100f) * zoomSensitivity), minimumDistance, maximumDistance);
            ReapplyView();
        }

        [ContextMenu("Restore saved hall view")]
        public void ResetView()
        {
            ValidateLimits();
            if (!controlledCamera) controlledCamera = GetComponent<Camera>();
            currentYaw = Mathf.Repeat(defaultYaw, 360f);
            currentPitch = Mathf.Clamp(defaultPitch, minimumPitch, maximumPitch);
            currentDistance = Mathf.Clamp(defaultDistance, minimumDistance, maximumDistance);
            currentFocusOffset = Vector3.zero;
            ClampFocus();
            initialized = true;
            ReapplyView();
        }

        /// <summary>Resolve static geometry while keeping the requested distance for unobstructed views.</summary>
        public void ReapplyView()
        {
            EnsureInitialized();
            if (!controlledCamera) return;
            ValidateLimits();
            currentPitch = Mathf.Clamp(currentPitch, minimumPitch, maximumPitch);
            currentDistance = Mathf.Clamp(currentDistance, minimumDistance, maximumDistance);
            ClampFocus();
            Quaternion rotation = Quaternion.Euler(currentPitch, currentYaw, 0f);
            Vector3 backwards = rotation * Vector3.back;
            Vector3 focus = FocusPosition;
            actualDistance = currentDistance;

            int count = Physics.SphereCastNonAlloc(focus, collisionRadius, backwards, obstructionHits, currentDistance, collisionMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = obstructionHits[i];
                if (!hit.collider || IgnoreObstruction(hit.collider)) continue;
                actualDistance = Mathf.Min(actualDistance, Mathf.Max(controlledCamera.nearClipPlane + collisionRadius, hit.distance - collisionPadding));
            }
            controlledCamera.transform.SetPositionAndRotation(focus + backwards * actualDistance, rotation);
        }

        bool IgnoreObstruction(Collider obstacle)
        {
            Transform obstacleTransform = obstacle.transform;
            if (obstacleTransform == transform || obstacleTransform.IsChildOf(transform)) return true;
            if (obstacleTransform == controlledCamera.transform || obstacleTransform.IsChildOf(controlledCamera.transform)) return true;
            if (obstacle.GetComponentInParent<StationActorMotion>() != null) return true;
            if (ignoredCollisionRoots != null)
                foreach (Transform ignored in ignoredCollisionRoots)
                    if (ignored && (obstacleTransform == ignored || obstacleTransform.IsChildOf(ignored))) return true;
            return false;
        }

        void EnsureInitialized()
        {
            if (!initialized) ResetView();
        }

        void ClampFocus()
        {
            currentFocusOffset = new Vector3(
                Mathf.Clamp(currentFocusOffset.x, panBoundsMin.x, panBoundsMax.x),
                Mathf.Clamp(currentFocusOffset.y, panBoundsMin.y, panBoundsMax.y),
                Mathf.Clamp(currentFocusOffset.z, panBoundsMin.z, panBoundsMax.z));
        }

        void OnValidate() => ValidateLimits();

        void ValidateLimits()
        {
            minimumPitch = Mathf.Clamp(minimumPitch, 1f, 89f);
            maximumPitch = Mathf.Clamp(maximumPitch, minimumPitch, 89f);
            minimumDistance = Mathf.Max(.5f, minimumDistance);
            maximumDistance = Mathf.Max(minimumDistance, maximumDistance);
            panBoundsMax = Vector3.Max(panBoundsMin, panBoundsMax);
        }

        static bool PointerIsOverInterface()
        {
            EventSystem eventSystem = EventSystem.current;
            if (!eventSystem) return false;
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current != null && eventSystem.IsPointerOverGameObject(Mouse.current.deviceId)) return true;
#endif
            return eventSystem.IsPointerOverGameObject();
        }

        void CapturePointer()
        {
            if (pointerCaptured) return;
            previousCursorLock = Cursor.lockState;
            previousCursorVisible = Cursor.visible;
            pointerCaptured = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        void ReleasePointer()
        {
            if (!pointerCaptured) return;
            pointerCaptured = false;
            Cursor.lockState = previousCursorLock;
            Cursor.visible = previousCursorVisible;
        }

        void OnDisable() => ReleasePointer();
        void OnApplicationFocus(bool focused) { if (!focused) ReleasePointer(); }
        void OnApplicationPause(bool paused) { if (paused) ReleasePointer(); }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
