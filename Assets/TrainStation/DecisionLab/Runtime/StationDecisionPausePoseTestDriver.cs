#if UNITY_EDITOR
using System;
using UnityEngine;
namespace StationFlow.TrainStation.DecisionLab
{
    // Transient Editor verification driver; omitted entirely from Player builds.
    [DefaultExecutionOrder(-32000)]
    public sealed class StationDecisionPausePoseTestDriver : MonoBehaviour
    {
        public Action repaint;
        public bool early;
        void Update() { if (early) repaint?.Invoke(); }
        void OnGUI() { if (!early && Event.current.type == EventType.Repaint) repaint?.Invoke(); }
    }
}
#endif