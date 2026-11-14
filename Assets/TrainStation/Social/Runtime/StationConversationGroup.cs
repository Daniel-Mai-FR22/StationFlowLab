using System;
using UnityEngine;
using UnityEngine.Events;

namespace StationFlow.TrainStation.Social
{
    [Serializable] public sealed class StationConversationMemberEvent : UnityEvent<StationConversationMember> { }

    /// <summary>Alternating conversation among authored standing actors. Never creates actors or paths.</summary>
    [DisallowMultipleComponent]
    public sealed class StationConversationGroup : MonoBehaviour
    {
        public StationConversationMember[] members = Array.Empty<StationConversationMember>();
        public int randomSeed = 3101;
        public Vector2 initialDelay = new Vector2(.3f, 3f);
        public Vector2 speakingDuration = new Vector2(5f, 9f);
        public Vector2 pauseDuration = new Vector2(.8f, 2f);
        public bool automaticConversation = true;
        public StationConversationMemberEvent speakerStarted = new StationConversationMemberEvent();
        public StationConversationMemberEvent speakerEnded = new StationConversationMemberEvent();
        [SerializeField] StationConversationMember activeSpeaker;
        [SerializeField] int activeSpeakerIndex = -1;
        [SerializeField] int completedTurns;
        [SerializeField] float nextChangeAt;
        System.Random random;
        bool initialized;
        int previousSpeakerIndex = -1;

        public StationConversationMember ActiveSpeaker => activeSpeaker;
        public StationConversationMember CurrentSpeaker => activeSpeaker;
        public int ActiveSpeakerIndex => activeSpeakerIndex;
        public int CompletedTurns => completedTurns;

        void Start() => InitializeIfNeeded();
        void OnEnable()
        {
            if (!initialized || !Application.isPlaying) return;
            nextChangeAt = Time.time + Sample(initialDelay);
        }

        void InitializeIfNeeded()
        {
            if (initialized || !Application.isPlaying) return;
            initialized = true;
            random = new System.Random(randomSeed);
            previousSpeakerIndex = members.Length > 0 ? random.Next(members.Length) - 1 : -1;
            foreach (var member in members)
            {
                if (!member || !member.isActiveAndEnabled || (member.group && member.group != this)) continue;
                member.group = this;
                member.InitializeIfNeeded();
                member.SetSpeaking(false);
            }
            nextChangeAt = Time.time + Sample(initialDelay);
        }

        void Update()
        {
            if (activeSpeaker)
            {
                if (!Available(activeSpeaker))
                {
                    EndTurn(false);
                    nextChangeAt = Time.time + Sample(pauseDuration);
                }
                else if (Time.time >= nextChangeAt)
                {
                    EndTurn(true);
                    nextChangeAt = Time.time + Sample(pauseDuration);
                }
                return;
            }
            if (automaticConversation && Time.time >= nextChangeAt) BeginNextTurn();
        }

        [ContextMenu("Begin next conversation turn (Play mode)")]
        public void BeginNextTurn()
        {
            if (!Application.isPlaying || !isActiveAndEnabled) return;
            InitializeIfNeeded();
            if (activeSpeaker) EndTurn(true);
            if (!this || !isActiveAndEnabled) return;
            int availableCount = 0;
            foreach (var member in members) if (Available(member)) ++availableCount;
            if (availableCount < 2) { nextChangeAt = Time.time + 1f; return; }
            int selected = -1;
            for (int offset = 1; offset <= members.Length; ++offset)
            {
                int index = (previousSpeakerIndex + offset) % members.Length;
                if (!Available(members[index])) continue;
                selected = index;
                break;
            }
            if (selected < 0) { nextChangeAt = Time.time + 1f; return; }
            activeSpeakerIndex = selected;
            previousSpeakerIndex = selected;
            activeSpeaker = members[selected];
            foreach (var member in members)
                if (Available(member)) member.SetSpeaking(member == activeSpeaker);
            nextChangeAt = Time.time + Mathf.Max(.1f, Sample(speakingDuration));
            speakerStarted.Invoke(activeSpeaker);
        }

        public void PauseConversation()
        {
            automaticConversation = false;
            if (Application.isPlaying) EndTurn(false);
        }

        public void ResumeConversation()
        {
            automaticConversation = true;
            if (!Application.isPlaying) return;
            InitializeIfNeeded();
            if (!activeSpeaker) nextChangeAt = Time.time + Sample(pauseDuration);
        }

        void EndTurn(bool completed)
        {
            var previous = activeSpeaker;
            activeSpeaker = null;
            activeSpeakerIndex = -1;
            if (!previous) return;
            if (completed) ++completedTurns;
            if (previous.isActiveAndEnabled) previous.SetSpeaking(false);
            speakerEnded.Invoke(previous);
        }

        public void NotifyMemberUnavailable(StationConversationMember member)
        {
            if (activeSpeaker != member) return;
            EndTurn(false);
            nextChangeAt = Time.time + Sample(pauseDuration);
        }

        public Vector3 GetFacingTarget(StationConversationMember member)
        {
            if (activeSpeaker && activeSpeaker != member) return activeSpeaker.transform.position;
            Vector3 centre = Vector3.zero;
            int count = 0;
            foreach (var listener in members)
            {
                if (!Available(listener) || listener == member) continue;
                centre += listener.transform.position;
                ++count;
            }
            return count > 0 ? centre / count : transform.position;
        }

        bool Available(StationConversationMember member) => member && member.isActiveAndEnabled &&
            member.group == this && member.motion && member.NavigationReady && member.IsAvailableForConversation;

        float Sample(Vector2 range)
        {
            if (random == null) random = new System.Random(randomSeed);
            float minimum = Mathf.Max(0f, Mathf.Min(range.x, range.y));
            float maximum = Mathf.Max(minimum, Mathf.Max(range.x, range.y));
            return minimum + (float)random.NextDouble() * (maximum - minimum);
        }

        void OnDisable()
        {
            if (!Application.isPlaying) return;
            EndTurn(false);
            foreach (var member in members)
                if (member && member.group == this && member.isActiveAndEnabled) member.SetSpeaking(false);
        }
    }
}
