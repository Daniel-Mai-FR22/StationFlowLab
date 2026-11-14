#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace StationFlow.TrainStation.Social.Editor
{
    [CustomEditor(typeof(StationActorMotion))]
    public sealed class StationActorMotionInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            if (!Application.isPlaying) return;
            var motion = (StationActorMotion)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Requested travel speed", motion.RequestedTravelSpeed.ToString("F2") + " m/s");
            if (motion.agent) EditorGUILayout.LabelField("Agent speed limit", motion.agent.speed.ToString("F2") + " m/s");
            EditorGUILayout.LabelField("Actual movement", motion.ActualPlanarSpeed.ToString("F2") + " m/s");
            EditorGUILayout.LabelField("Extra crowd speed factor", motion.CurrentCrowdSpeedFactor.ToString("F2"));
            EditorGUILayout.LabelField("Walk speed at 1x", motion.EffectiveWalkReferenceSpeed.ToString("F2") + " m/s");
            EditorGUILayout.LabelField("Walk playback", motion.AppliedWalkPlaybackSpeed.ToString("F2") + "x");
            EditorGUILayout.LabelField("Locomotion", !motion.LocomotionActive ? "Action" : motion.IsWalkPose ? "Walking" : "Standing");
            Repaint();
        }
    }

    [CustomEditor(typeof(StationPassenger))]
    public sealed class StationPassengerInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var passenger = (StationPassenger)target;
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button("Request information now")) passenger.RequestInformation();
                if (GUILayout.Button("Visit a random service now")) passenger.TryRequestRandomActivity();
                if (passenger.world != null)
                    foreach (var shop in passenger.world.shopDesks)
                        if (shop && GUILayout.Button("Visit " + shop.serviceName)) passenger.TryVisitService(shop);
            }
            if (Application.isPlaying)
            {
                EditorGUILayout.LabelField("Current state", passenger.CurrentState.ToString());
                EditorGUILayout.LabelField("Completed conversations", passenger.CompletedConversations.ToString());
                EditorGUILayout.ObjectField("Current service", passenger.CurrentService, typeof(StationInformationDesk), true);
                EditorGUILayout.LabelField("Completed shop visits", passenger.CompletedShopVisits.ToString());
                int queueIndex = passenger.CurrentService ? passenger.CurrentService.GetQueueIndex(passenger) : -1;
                EditorGUILayout.LabelField("Queue position", queueIndex >= 0 ? (queueIndex + 1).ToString() : "Not in queue");
                EditorGUILayout.ObjectField("Current seat", passenger.CurrentSeat, typeof(StationSeat), true);
            }
        }
    }

    [CustomEditor(typeof(StationConversationGroup))]
    public sealed class StationConversationGroupInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var group = (StationConversationGroup)target;
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button("Begin next conversation turn")) group.BeginNextTurn();
                if (GUILayout.Button(group.automaticConversation ? "Pause conversation" : "Resume conversation"))
                {
                    if (group.automaticConversation) group.PauseConversation();
                    else group.ResumeConversation();
                }
            }
            if (!Application.isPlaying) return;
            EditorGUILayout.ObjectField("Current speaker", group.ActiveSpeaker, typeof(StationConversationMember), true);
            EditorGUILayout.LabelField("Completed turns", group.CompletedTurns.ToString());
            Repaint();
        }
    }

    [CustomEditor(typeof(StationWalkingGroup))]
    public sealed class StationWalkingGroupInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            if (!Application.isPlaying) return;
            var group = (StationWalkingGroup)target;
            EditorGUILayout.Space();
            EditorGUILayout.ObjectField("Current leader", group.CurrentLeader, typeof(StationPassenger), true);
            EditorGUILayout.LabelField("Route point", (group.CurrentWaypointIndex + 1).ToString());
            EditorGUILayout.LabelField("Completed route points", group.CompletedWaypoints.ToString());
            EditorGUILayout.LabelField("Regrouping", group.WaitingAtWaypoint ? "Yes" : "No");
            EditorGUILayout.LabelField("Waiting for companions", group.WaitingForCompanions ? "Yes" : "No");
            EditorGUILayout.LabelField("Regroup recoveries", group.RegroupRecoveryCount.ToString());
            Repaint();
        }
    }

    [CustomEditor(typeof(StationInformationDesk))]
    public sealed class StationInformationDeskInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var desk = (StationInformationDesk)target;
            if (!Application.isPlaying) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Queue (including current service)", desk.QueueCount.ToString());
            EditorGUILayout.ObjectField("Customer being served", desk.ActiveCustomer, typeof(StationPassenger), true);
        }
    }
}
#endif
