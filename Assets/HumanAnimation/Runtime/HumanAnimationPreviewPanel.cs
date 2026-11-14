using TMPro;
using UnityEngine;

namespace StationFlow.HumanAnimation
{
    /// <summary>Serialized UI hooks for a reusable animation inspection scene.</summary>
    public sealed class HumanAnimationPreviewPanel : MonoBehaviour
    {
        public HumanAnimationLibrary library;
        public HumanAnimationPlayer[] players;
        public TMP_Text selectionLabel;
        public TMP_Text statusLabel;
        public TMP_Text modelLabel;
        public int selectedMotionIndex;
        public int selectedPlayerIndex;
        public bool synchronizeAll = true;

        string status = "Select a motion, then Play. Each model uses its own Humanoid Avatar.";
        float nextRefresh;

        void Start() => RefreshLabels();

        void Update()
        {
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + .2f;
            RefreshLabels();
        }

        HumanMotion SelectedMotion
        {
            get
            {
                if (!library || library.motions == null || library.motions.Length == 0) return null;
                selectedMotionIndex = Mathf.Clamp(selectedMotionIndex, 0, library.motions.Length - 1);
                return library.motions[selectedMotionIndex];
            }
        }

        public void SelectNextMotion() { ChangeSelection(1); }
        public void SelectPreviousMotion() { ChangeSelection(-1); }

        void ChangeSelection(int step)
        {
            if (!library || library.motions == null || library.motions.Length == 0) return;
            selectedMotionIndex = (selectedMotionIndex + step + library.motions.Length) % library.motions.Length;
            status = "Selection changed. Press Play to apply it.";
            RefreshLabels();
        }

        public void SelectMotion(string id)
        {
            if (!library || library.motions == null) return;
            for (var i = 0; i < library.motions.Length; i++)
                if (library.motions[i] != null && library.motions[i].id == id)
                { selectedMotionIndex = i; RefreshLabels(); return; }
            status = "Unknown catalog ID: " + id;
            RefreshLabels();
        }

        public void PlayMotion(string id) { SelectMotion(id); if (SelectedMotion?.id == id) PlaySelected(); }

        public void PlaySelected()
        {
            var motion = SelectedMotion;
            if (motion == null) { status = "Assign a library and select a catalog entry."; RefreshLabels(); return; }
            if (!motion.clip)
            {
                status = motion.Label + ": " + motion.source + " clip awaiting import. No substitute animation is used.";
                RefreshLabels();
                return;
            }
            var successes = 0;
            var attempts = 0;
            var lastError = "";
            ForTargets(player => { attempts++; if (player.Play(motion.id)) successes++; else lastError = player.LastError; });
            status = attempts == 0 ? "No preview models assigned." :
                successes == attempts ? $"Playing {motion.Label} on {successes} model(s). Source: {motion.source}." :
                $"Playback succeeded on {successes}/{attempts} models. {lastError}";
            RefreshLabels();
        }

        public void Pause() { ForTargets(player => player.Pause()); status = "Playback paused."; RefreshLabels(); }
        public void Resume() { ForTargets(player => player.Resume()); status = "Playback resumed."; RefreshLabels(); }
        public void Rewind() { ForTargets(player => player.Rewind()); status = "Current motion rewound."; RefreshLabels(); }
        public void SetHalfSpeed() => SetSpeed(.5f);
        public void SetNormalSpeed() => SetSpeed(1f);
        public void SetDoubleSpeed() => SetSpeed(2f);
        public void SetSpeed(float value)
        {
            ForTargets(player => player.SetPlaybackSpeed(value));
            status = $"Playback speed: {value:0.##}×.";
            RefreshLabels();
        }
        public void SetAllModels(bool value) { synchronizeAll = value; RefreshLabels(); }
        public void ToggleAllModels() { synchronizeAll = !synchronizeAll; RefreshLabels(); }
        public void SelectNextModel()
        {
            if (players != null && players.Length > 0) selectedPlayerIndex = (selectedPlayerIndex + 1) % players.Length;
            synchronizeAll = false;
            RefreshLabels();
        }

        void ForTargets(System.Action<HumanAnimationPlayer> action)
        {
            if (players == null || players.Length == 0) return;
            selectedPlayerIndex = Mathf.Clamp(selectedPlayerIndex, 0, players.Length - 1);
            for (var i = 0; i < players.Length; i++)
                if (players[i] && (synchronizeAll || i == selectedPlayerIndex)) action(players[i]);
        }

        public void RefreshLabels()
        {
            var motion = SelectedMotion;
            if (selectionLabel) selectionLabel.text = motion == null ? "NO ANIMATION LIBRARY" :
                $"{selectedMotionIndex + 1:00} / {library.motions.Length:00}   {motion.Label}\n" +
                $"{motion.source.ToString().ToUpperInvariant()}  ·  {motion.category}  ·  " +
                (motion.clip ? (motion.loop ? "LOOP" : "ONE SHOT") : "AWAITING SOURCE IMPORT");
            if (statusLabel) statusLabel.text = status;
            if (modelLabel)
            {
                var player = players != null && players.Length > 0 ? players[Mathf.Clamp(selectedPlayerIndex, 0, players.Length - 1)] : null;
                modelLabel.text = synchronizeAll ? "TARGET: ALL MODELS  ·  shared clips / separate Avatars" :
                    "TARGET: " + (player ? player.name : "unassigned");
            }
        }
    }
}
