#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace StationFlow.HumanAnimation.Editor
{
    /// <summary>Clip inspection without creating scene objects or writing sampled poses to assets.</summary>
    [CustomEditor(typeof(HumanAnimationPlayer))]
    public sealed class HumanAnimationPlayerEditor : UnityEditor.Editor
    {
        AnimationModeDriver previewDriver;
        int selectedMotionIndex;
        float normalizedTime;
        float previewSpeed = 1f;
        bool previewPlaying;
        double previousUpdate;
        string previewError = "";

        HumanAnimationPlayer Player => target as HumanAnimationPlayer;
        bool OwnsPreview => previewDriver && AnimationMode.InAnimationMode(previewDriver);

        void OnEnable()
        {
            EditorApplication.update += PreviewUpdate;
            EditorApplication.playModeStateChanged += PlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += StopPreview;
        }

        void OnDisable()
        {
            EditorApplication.update -= PreviewUpdate;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= StopPreview;
            StopPreview();
            if (previewDriver) DestroyImmediate(previewDriver);
        }

        void PlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode) StopPreview();
        }

        public override void OnInspectorGUI()
        {
            using (new EditorGUI.DisabledScope(OwnsPreview)) DrawDefaultInspector();
            var player = Player;
            if (!player) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Application.isPlaying ? "Runtime playback" : "Editor animation preview", EditorStyles.boldLabel);
            var library = player.Library;
            if (!library || library.motions == null || library.motions.Length == 0)
            {
                StopPreview();
                EditorGUILayout.HelpBox("Assign a saved HumanAnimationLibrary to inspect its motions.", MessageType.Info);
                return;
            }
            var options = new string[library.motions.Length];
            for (var i = 0; i < options.Length; i++)
            {
                var entry = library.motions[i];
                options[i] = entry == null ? "Empty catalog entry" :
                    $"{entry.Label} [{entry.source}]" + (entry.clip ? "" : " — source not imported");
            }
            selectedMotionIndex = Mathf.Clamp(selectedMotionIndex, 0, options.Length - 1);
            EditorGUI.BeginChangeCheck();
            var newIndex = EditorGUILayout.Popup("Motion", selectedMotionIndex, options);
            if (EditorGUI.EndChangeCheck())
            {
                StopPreview();
                selectedMotionIndex = newIndex;
                normalizedTime = 0f;
                previewError = "";
            }
            var motion = library.motions[selectedMotionIndex];
            if (motion == null) return;
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("Source clip", motion.clip, typeof(AnimationClip), false);
            EditorGUILayout.LabelField("Origin", motion.source == HumanMotionSource.Mixamo ? "Mixamo — imported source FBX" : "Diagnostic — authored test motion, not Mixamo");
            EditorGUILayout.LabelField("Playback", motion.loop ? "Loop" : "One shot");
            if (!string.IsNullOrEmpty(motion.sourceAssetPath))
                EditorGUILayout.LabelField("Asset", motion.sourceAssetPath, EditorStyles.wordWrappedMiniLabel);
            if (!motion.clip)
            {
                StopPreview();
                EditorGUILayout.HelpBox("This motion awaits its source clip. No substitute animation is played.", MessageType.Warning);
                return;
            }
            if (Application.isPlaying) DrawRuntimeControls(player, motion);
            else DrawEditorControls(player, motion);
        }

        void DrawRuntimeControls(HumanAnimationPlayer player, HumanMotion motion)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Play selected")) player.Play(motion.id, normalizedTime);
                if (GUILayout.Button(player.IsPaused ? "Resume" : "Pause"))
                { if (player.IsPaused) player.Resume(); else player.Pause(); }
                if (GUILayout.Button("Rewind")) player.Rewind();
            }
            normalizedTime = EditorGUILayout.Slider("Start position", normalizedTime, 0f, 1f);
            EditorGUI.BeginChangeCheck();
            var speed = EditorGUILayout.Slider("Playback speed", player.PlaybackSpeed, .05f, 4f);
            if (EditorGUI.EndChangeCheck()) player.SetPlaybackSpeed(speed);
            if (!string.IsNullOrEmpty(player.LastError)) EditorGUILayout.HelpBox(player.LastError, MessageType.Warning);
        }

        void DrawEditorControls(HumanAnimationPlayer player, HumanMotion motion)
        {
            if (!CanPreview(player, motion, out var reason))
            {
                StopPreview();
                EditorGUILayout.HelpBox(reason, MessageType.Warning);
                return;
            }
            var anotherPreviewActive = AnimationMode.InAnimationMode() && !OwnsPreview;
            if (anotherPreviewActive)
                EditorGUILayout.HelpBox("Another Animation or Timeline preview is active. Stop that preview before using this inspector.", MessageType.Info);
            using (new EditorGUI.DisabledScope(anotherPreviewActive))
            {
                EditorGUI.BeginChangeCheck();
                var position = EditorGUILayout.Slider("Preview position", normalizedTime, 0f, 1f);
                if (EditorGUI.EndChangeCheck())
                {
                    previewPlaying = false;
                    normalizedTime = position;
                    Sample(motion);
                }
                previewSpeed = EditorGUILayout.Slider("Preview speed", previewSpeed, .05f, 2f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(previewPlaying ? "Pause preview" : "Play preview"))
                    {
                        if (previewPlaying) previewPlaying = false;
                        else
                        {
                            if (normalizedTime >= 1f) normalizedTime = 0f;
                            previewPlaying = Sample(motion);
                            previousUpdate = EditorApplication.timeSinceStartup;
                        }
                    }
                    using (new EditorGUI.DisabledScope(!OwnsPreview))
                        if (GUILayout.Button("Stop / restore pose")) StopPreview();
                }
            }
            EditorGUILayout.HelpBox("Preview samples the selected model's Humanoid Avatar. Stop, deselect, or enter Play mode to restore its original pose. No pose is saved by these controls.", MessageType.Info);
            if (!string.IsNullOrEmpty(previewError)) EditorGUILayout.HelpBox(previewError, MessageType.Error);
        }

        static bool CanPreview(HumanAnimationPlayer player, HumanMotion motion, out string error)
        {
            if (!player.ValidateSetup(out error)) return false;
            if (EditorUtility.IsPersistent(player))
            { error = "Open this prefab in Prefab Mode, or select an instance in a scene, to preview safely."; return false; }
            if (!player.TargetAnimator.enabled || !player.TargetAnimator.gameObject.activeInHierarchy)
            { error = "Enable the model and its Animator before previewing."; return false; }
            if (!player.TargetAnimator.runtimeAnimatorController)
            { error = "Assign the saved shared controller to the model's Animator before previewing."; return false; }
            if (!motion.clip || motion.clip.legacy || !motion.clip.humanMotion)
            { error = "Editor retargeting preview requires an imported non-Legacy Humanoid clip."; return false; }
            if (motion.clip.length <= 0f)
            { error = "The selected clip has no duration."; return false; }
            error = "";
            return true;
        }

        bool Sample(HumanMotion motion)
        {
            var player = Player;
            if (!player || Application.isPlaying || !CanPreview(player, motion, out previewError)) return false;
            if (!OwnsPreview)
            {
                if (AnimationMode.InAnimationMode())
                { previewError = "Another editor owns the current animation preview."; return false; }
                if (!previewDriver)
                {
                    previewDriver = CreateInstance<AnimationModeDriver>();
                    previewDriver.hideFlags = HideFlags.HideAndDontSave;
                    previewDriver.name = "Human Animation Inspector Preview";
                }
                AnimationMode.StartAnimationMode(previewDriver);
                if (!OwnsPreview) return false;
            }
            try
            {
                AnimationMode.BeginSampling();
                try { AnimationMode.SampleAnimationClip(player.TargetAnimator.gameObject, motion.clip, normalizedTime * motion.clip.length); }
                finally { AnimationMode.EndSampling(); }
                previewError = "";
                SceneView.RepaintAll();
                Repaint();
                return true;
            }
            catch (System.Exception error)
            {
                previewError = "Preview stopped: " + error.Message;
                StopPreview();
                return false;
            }
        }

        void PreviewUpdate()
        {
            if (!OwnsPreview) { previewPlaying = false; return; }
            if (!previewPlaying) return;
            var player = Player;
            var library = player ? player.Library : null;
            if (!library || library.motions == null || selectedMotionIndex >= library.motions.Length)
            { StopPreview(); return; }
            var motion = library.motions[selectedMotionIndex];
            if (motion?.clip == null) { StopPreview(); return; }
            var now = EditorApplication.timeSinceStartup;
            normalizedTime += (float)(now - previousUpdate) * previewSpeed / Mathf.Max(.001f, motion.clip.length);
            previousUpdate = now;
            if (motion.loop) normalizedTime = Mathf.Repeat(normalizedTime, 1f);
            else if (normalizedTime >= 1f) { normalizedTime = 1f; previewPlaying = false; }
            Sample(motion);
        }

        void StopPreview()
        {
            previewPlaying = false;
            if (OwnsPreview) AnimationMode.StopAnimationMode(previewDriver);
            SceneView.RepaintAll();
        }
    }
}
#endif
