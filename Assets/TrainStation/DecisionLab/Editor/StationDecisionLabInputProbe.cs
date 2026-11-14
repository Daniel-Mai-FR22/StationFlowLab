#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.LowLevel;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.UI;
#endif

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Opt-in observation of native input delivery. Never queues input or changes controller/settings state.</summary>
    [InitializeOnLoad]
    public static class StationDecisionLabInputProbe
    {
        public const string ReportPath = "BuildRequests/station-decision-input-probe.json";
        [Serializable] public sealed class Frame
        {
            public string phase, inputUpdate, focusedWindow, mouse, keyboard, inputModule, uiPointControl, uiScrollControl;
            public int frame, mouseId, keyboardId;
            public double realtime;
            public float timeScale, wheel, requestedDistance, actualDistance, yaw, pitch;
            public bool playing, editorPaused, applicationFocused, gameViewFocused, mouseEnabled, keyboardEnabled;
            public bool f7Pressed, f7Held, rightPressed, rightHeld, dashboardVisible, dashboardHit, eventSystemDefaultHit, eventSystemDeviceHit;
            public bool followActive, cameraEnabled, overviewEnabled, pointerBlocked, occlusionLimited;
            public Vector2 pointer, delta;
            public Vector3 cameraPosition;
            public string[] raycastHits;
        }
        [Serializable] public sealed class RawEvent
        {
            public double realtime, inputTime;
            public int frame, deviceId;
            public string device, update, type;
            public float wheel, f7;
            public bool currentMouse, currentKeyboard;
        }
        [Serializable] public sealed class Report
        {
            public bool active, inputSystemCompiled, hooksInstalled;
            public string startedUtc, checkedUtc, stoppedUtc, scene, updateMode, scrollDeltaBehavior, backgroundBehavior, editorInputBehavior;
            public string explanation = "Raw events are observed without modifying them. Before/after frames surround ScriptRunBehaviourUpdate and read the same runtime state as MonoBehaviour.Update; editor-status reads may use the separate Editor input state. Probe hooks are removed on stop, Play exit, or assembly reload.";
            public long totalEvents, mouseEvents, keyboardEvents, wheelEvents, f7DownEvents, runtimeBeforeFrames, runtimeAfterFrames;
            public long dynamicInputUpdates, fixedInputUpdates, editorInputUpdates;
            public Frame latestBefore, latestAfter, latestEditor;
            public List<RawEvent> significantEvents = new List<RawEvent>();
            public List<Frame> inputFrames = new List<Frame>(), recentFrames = new List<Frame>();
            public List<string> devices = new List<string>(), errors = new List<string>();
        }

        sealed class BeforeBehaviourInputProbe { }
        sealed class AfterBehaviourInputProbe { }
        static Report report;
        static StationDecisionFollowCamera follow;
        static StationDecisionDashboard dashboard;
        static double nextWrite, nextRecent;
        static bool frameHadInput;
        static readonly List<RaycastResult> raycasts = new List<RaycastResult>();

        static StationDecisionLabInputProbe()
        {
            EditorApplication.playModeStateChanged += state => { if (state == PlayModeStateChange.ExitingPlayMode) Stop(); };
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
        }

        public static void Start()
        {
            if (!EditorApplication.isPlaying || SceneManager.GetActiveScene().path != StationDecisionLabAuthoring.ScenePath)
                throw new InvalidOperationException("Start the input probe while playing the isolated Decision Lab scene.");
            Stop();
            follow = UnityEngine.Object.FindAnyObjectByType<StationDecisionFollowCamera>();
            dashboard = UnityEngine.Object.FindAnyObjectByType<StationDecisionDashboard>();
            if (!follow || !dashboard) throw new InvalidOperationException("The saved Lab follow camera and dashboard are required.");
            report = new Report { active = true, startedUtc = DateTime.UtcNow.ToString("O"), scene = SceneManager.GetActiveScene().path };
#if ENABLE_INPUT_SYSTEM
            report.inputSystemCompiled = true;
            InputSystem.onEvent += OnEvent;
            InputSystem.onAfterUpdate += OnInputUpdate;
#endif
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            report.hooksInstalled = Install(ref loop);
            if (!report.hooksInstalled) { Stop(); throw new InvalidOperationException("Could not find ScriptRunBehaviourUpdate in the current PlayerLoop."); }
            PlayerLoop.SetPlayerLoop(loop);
            nextWrite = nextRecent = 0;
            EditorApplication.update += WritePeriodically;
            WriteStatus();
        }

        public static void Stop()
        {
            if (report == null || !report.active) return;
            report.active = false; report.stoppedUtc = DateTime.UtcNow.ToString("O");
#if ENABLE_INPUT_SYSTEM
            InputSystem.onEvent -= OnEvent;
            InputSystem.onAfterUpdate -= OnInputUpdate;
#endif
            EditorApplication.update -= WritePeriodically;
            var loop = PlayerLoop.GetCurrentPlayerLoop(); Remove(ref loop); PlayerLoop.SetPlayerLoop(loop);
            WriteStatus();
        }

        public static void WriteStatus()
        {
            if (report == null) throw new InvalidOperationException("No input probe has been started in this editor session.");
            report.checkedUtc = DateTime.UtcNow.ToString("O");
            report.latestEditor = ReadFrame("editor-status");
#if ENABLE_INPUT_SYSTEM
            report.updateMode = InputSystem.settings.updateMode.ToString();
            report.scrollDeltaBehavior = InputSystem.settings.scrollDeltaBehavior.ToString();
            report.backgroundBehavior = InputSystem.settings.backgroundBehavior.ToString();
            report.editorInputBehavior = InputSystem.settings.editorInputBehaviorInPlayMode.ToString();
            report.devices = InputSystem.devices.Select(d => d.deviceId + ": " + d.displayName + " [" + d.layout + "] native=" + d.native +
                " enabled=" + d.enabled + " interface=" + d.description.interfaceName).ToList();
#endif
            StationDecisionLabAuthoring.Write(ReportPath, report);
        }

        static void WritePeriodically()
        {
            if (report == null || !report.active || EditorApplication.timeSinceStartup < nextWrite) return;
            nextWrite = EditorApplication.timeSinceStartup + 1d;
            try { WriteStatus(); } catch (Exception error) { RememberError(error); }
        }

        static void BeforeUpdate()
        {
            if (report == null || !report.active) return;
            try
            {
                ++report.runtimeBeforeFrames;
                report.latestBefore = ReadFrame("before-MonoBehaviour.Update");
                Frame frame = report.latestBefore;
                frameHadInput = Mathf.Abs(frame.wheel) > .0001f || frame.f7Pressed || frame.rightPressed;
                if (frameHadInput) AddBounded(report.inputFrames, frame, 400);
            }
            catch (Exception error) { RememberError(error); }
        }

        static void AfterUpdate()
        {
            if (report == null || !report.active) return;
            try
            {
                ++report.runtimeAfterFrames;
                Frame frame = ReadFrame("after-MonoBehaviour.Update");
                Frame before = report.latestBefore;
                if (frameHadInput || (before != null && (before.dashboardVisible != frame.dashboardVisible ||
                    Mathf.Abs(before.requestedDistance - frame.requestedDistance) > .0001f || before.followActive != frame.followActive)))
                    AddBounded(report.inputFrames, frame, 400);
                report.latestAfter = frame;
                if (EditorApplication.timeSinceStartup >= nextRecent)
                { nextRecent = EditorApplication.timeSinceStartup + .5d; AddBounded(report.recentFrames, frame, 80); }
            }
            catch (Exception error) { RememberError(error); }
        }

        static Frame ReadFrame(string phase)
        {
            var focused = EditorWindow.focusedWindow;
            var frame = new Frame { phase = phase, frame = Time.frameCount, realtime = EditorApplication.timeSinceStartup,
                playing = EditorApplication.isPlaying, editorPaused = EditorApplication.isPaused, timeScale = Time.timeScale,
                applicationFocused = Application.isFocused, focusedWindow = focused ? focused.GetType().FullName : "none",
                gameViewFocused = focused && focused.GetType().Name == "GameView", dashboardVisible = dashboard && dashboard.visible };
#if ENABLE_INPUT_SYSTEM
            frame.inputUpdate = InputState.currentUpdateType.ToString();
            var mouse = Mouse.current; var keyboard = Keyboard.current;
            if (mouse != null)
            {
                frame.mouse = mouse.displayName; frame.mouseId = mouse.deviceId; frame.mouseEnabled = mouse.enabled;
                frame.pointer = mouse.position.ReadValue(); frame.delta = mouse.delta.ReadValue(); frame.wheel = mouse.scroll.ReadValue().y;
                frame.rightPressed = mouse.rightButton.wasPressedThisFrame; frame.rightHeld = mouse.rightButton.isPressed;
            }
            if (keyboard != null)
            {
                frame.keyboard = keyboard.displayName; frame.keyboardId = keyboard.deviceId; frame.keyboardEnabled = keyboard.enabled;
                frame.f7Pressed = keyboard.f7Key.wasPressedThisFrame; frame.f7Held = keyboard.f7Key.isPressed;
            }
#endif
            frame.dashboardHit = dashboard && dashboard.IsPointerOverDashboard(frame.pointer);
            if (follow)
            {
                frame.followActive = follow.IsFollowing; frame.pointerBlocked = follow.ShouldBlockPointer(frame.pointer);
                frame.requestedDistance = follow.RequestedDistance; frame.actualDistance = follow.ActualDistance;
                frame.yaw = follow.CurrentYaw; frame.pitch = follow.CurrentPitch; frame.occlusionLimited = follow.OcclusionLimited;
                frame.cameraEnabled = follow.controlledCamera && follow.controlledCamera.enabled;
                frame.overviewEnabled = follow.overviewOrbit && follow.overviewOrbit.enabled;
                if (follow.controlledCamera) frame.cameraPosition = follow.controlledCamera.transform.position;
            }
            var eventSystem = EventSystem.current;
            if (eventSystem)
            {
                frame.inputModule = eventSystem.currentInputModule ? eventSystem.currentInputModule.GetType().FullName : "none";
                frame.eventSystemDefaultHit = eventSystem.IsPointerOverGameObject();
                frame.eventSystemDeviceHit = eventSystem.IsPointerOverGameObject(frame.mouseId);
                raycasts.Clear(); eventSystem.RaycastAll(new PointerEventData(eventSystem) { position = frame.pointer, pointerId = frame.mouseId }, raycasts);
                frame.raycastHits = raycasts.Take(6).Select(hit => Path(hit.gameObject.transform) + " (" + hit.module.GetType().Name + ")").ToArray();
#if ENABLE_INPUT_SYSTEM
                var module = eventSystem.currentInputModule as InputSystemUIInputModule;
                if (module)
                {
                    frame.uiPointControl = module.point != null && module.point.action != null && module.point.action.activeControl != null ? module.point.action.activeControl.path : "none";
                    frame.uiScrollControl = module.scrollWheel != null && module.scrollWheel.action != null && module.scrollWheel.action.activeControl != null ? module.scrollWheel.action.activeControl.path : "none";
                }
#endif
            }
            return frame;
        }

#if ENABLE_INPUT_SYSTEM
        static void OnInputUpdate()
        {
            if (report == null || !report.active) return;
            switch (InputState.currentUpdateType)
            {
                case InputUpdateType.Dynamic: ++report.dynamicInputUpdates; break;
                case InputUpdateType.Fixed: ++report.fixedInputUpdates; break;
                case InputUpdateType.Editor: ++report.editorInputUpdates; break;
            }
        }

        static void OnEvent(InputEventPtr inputEvent, InputDevice device)
        {
            if (report == null || !report.active) return;
            ++report.totalEvents;
            if (device is Mouse) ++report.mouseEvents;
            if (device is Keyboard) ++report.keyboardEvents;
            if (!inputEvent.IsA<StateEvent>() && !inputEvent.IsA<DeltaStateEvent>()) return;
            try
            {
                float wheel = 0f, f7 = 0f;
                if (device is Mouse mouse && mouse.scroll.ReadValueFromEvent(inputEvent, out Vector2 scroll)) wheel = scroll.y;
                if (device is Keyboard keyboard) keyboard.f7Key.ReadValueFromEvent(inputEvent, out f7);
                if (Mathf.Abs(wheel) <= .0001f && f7 <= .5f) return;
                if (Mathf.Abs(wheel) > .0001f) ++report.wheelEvents;
                if (f7 > .5f) ++report.f7DownEvents;
                AddBounded(report.significantEvents, new RawEvent { realtime = EditorApplication.timeSinceStartup, inputTime = inputEvent.time,
                    frame = Time.frameCount, deviceId = device.deviceId, device = device.displayName, update = InputState.currentUpdateType.ToString(),
                    type = inputEvent.type.ToString(), wheel = wheel, f7 = f7, currentMouse = device == Mouse.current, currentKeyboard = device == Keyboard.current }, 300);
            }
            catch (Exception error) { RememberError(error); }
        }
#endif

        static bool Install(ref PlayerLoopSystem loop)
        {
            if (loop.subSystemList == null) return false;
            var children = loop.subSystemList.ToList();
            for (int i = 0; i < children.Count; ++i)
            {
                if (children[i].type == typeof(UnityEngine.PlayerLoop.Update.ScriptRunBehaviourUpdate))
                {
                    children.Insert(i, new PlayerLoopSystem { type = typeof(BeforeBehaviourInputProbe), updateDelegate = BeforeUpdate });
                    children.Insert(i + 2, new PlayerLoopSystem { type = typeof(AfterBehaviourInputProbe), updateDelegate = AfterUpdate });
                    loop.subSystemList = children.ToArray(); return true;
                }
                var child = children[i];
                if (Install(ref child)) { children[i] = child; loop.subSystemList = children.ToArray(); return true; }
            }
            return false;
        }

        static void Remove(ref PlayerLoopSystem loop)
        {
            if (loop.subSystemList == null) return;
            var children = new List<PlayerLoopSystem>();
            foreach (var original in loop.subSystemList)
            {
                if (original.type == typeof(BeforeBehaviourInputProbe) || original.type == typeof(AfterBehaviourInputProbe)) continue;
                var child = original; Remove(ref child); children.Add(child);
            }
            loop.subSystemList = children.ToArray();
        }

        static void AddBounded<T>(List<T> values, T value, int limit) { values.Add(value); if (values.Count > limit) values.RemoveAt(0); }
        static void RememberError(Exception error) { if (report != null && report.errors.Count < 12) report.errors.Add(error.ToString()); }
        static string Path(Transform item) => item.parent ? Path(item.parent) + "/" + item.name : item.name;
    }
}
#endif
