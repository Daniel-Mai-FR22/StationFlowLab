#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>One explicit maintenance command used to rename the saved local Unity project.</summary>
    public static class StationFlowProjectMaintenance
    {
        [Serializable] sealed class Receipt
        {
            public string checkedUtc, sourceDirectory, destinationDirectory, productName, version, dailyScene;
            public bool dirtyScenesAbsent, settingsSaved;
        }

        public static void CloseSavedProject()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || BuildPipeline.isBuildingPlayer)
                throw new InvalidOperationException("Close requires saved, stopped Edit mode.");
            for(int i=0;i<SceneManager.sceneCount;++i)
                if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("Save scenes before closing.");
            AssetDatabase.SaveAssets();
        }

        public static void PrepareForRename()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("Stop Play and finish compilation before renaming the project.");
            for (int i = 0; i < SceneManager.sceneCount; ++i)
                if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Save the scene before renaming the project.");
            PlayerSettings.productName = "GareFlowDemo";
            PlayerSettings.bundleVersion = "0.2.0";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, "fr.efrei.gareflowdemo");
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "fr.efrei.gareflowdemo");
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, "fr.efrei.gareflowdemo");
            const string daily = "Assets/TrainStation/Scenes/TrainStationDaily.unity";
            EditorBuildSettings.scenes = new[] {new EditorBuildSettingsScene(daily, true)}
                .Concat(EditorBuildSettings.scenes.Where(s => s.path != daily).Select(s => new EditorBuildSettingsScene(s.path, false))).ToArray();
            AssetDatabase.SaveAssets();
            var receipt = new Receipt {
                checkedUtc = DateTime.UtcNow.ToString("O"), sourceDirectory = Path.GetFullPath("."),
                destinationDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "StationFlowLab"),
                productName = PlayerSettings.productName, version = PlayerSettings.bundleVersion,
                dailyScene = daily, dirtyScenesAbsent = true, settingsSaved = true
            };
            Directory.CreateDirectory("Reports/TrainStationDaily/ProjectRename");
            File.WriteAllText("Reports/TrainStationDaily/ProjectRename/prepare.json", JsonUtility.ToJson(receipt, true));
            // Poll writes its own receipt before the delayed, graceful editor shutdown.
            EditorApplication.delayCall += () => EditorApplication.Exit(0);
        }
    }
}
#endif
