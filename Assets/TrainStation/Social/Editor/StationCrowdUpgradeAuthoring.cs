#if UNITY_EDITOR
namespace StationFlow.TrainStation.Social.Editor
{
    /// <summary>Compatibility entry point: apply native settings without custom crowd control.</summary>
    public static class StationCrowdUpgradeAuthoring
    {
        public static void Apply() => StationNativeNavigationAuthoring.Apply();
        public static void Configure(StationActorMotion actor) => StationNativeNavigationAuthoring.ConfigureMotion(actor);
        public static void Configure(StationWalkingGroup group) { }
    }
}
#endif
