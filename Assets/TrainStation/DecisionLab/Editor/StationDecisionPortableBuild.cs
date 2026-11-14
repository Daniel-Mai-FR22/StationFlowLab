#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>A fresh numbered Desktop release of the saved daily scene, with unchanged global settings.</summary>
    public static class StationDecisionPortableBuild
    {
        public const string ProjectName = "StationFlowLab";
        public const string ProductName = "GareFlowDemo";
        public const string ScenePath = "Assets/TrainStation/Scenes/TrainStationDaily.unity";
        public const string VersionConfigPath = "BuildRequests/station-portable-build-version.json";
        public const string ReadmeTemplatePath = "BuildRequests/TrainStationDaily.README.fr.txt";
        public const string ReportPath = "Reports/TrainStationDaily/RuntimeInitialization/portable-build.json";
        const string OwnershipFile = "station-portable-build-owner.json";

        [Serializable] sealed class VersionConfig
        {
            public string schema = "stationflow.station_portable_build_version.v1", projectName = ProjectName, productName = ProductName, releaseVersion = "0.2.0";
            public int nextBuildNumber = 1, lastReservedBuildNumber;
            public string lastReservedUtc = "", lastReservedDirectory = "";
        }
        [Serializable] sealed class Owner
        { public string schema = "stationflow.station_portable_build.v2", projectRoot, projectName, productName, releaseVersion, buildLabel, executable, scene; public int buildNumber; }
        [Serializable] sealed class FileEvidence { public string path, before, after; }
        [Serializable] sealed class DocumentationEvidence { public string source, packaged, before, after; public bool identical; }
        [Serializable] sealed class BuildOutput { public string path, role; public long size; }
        [Serializable] sealed class Message { public string step, type, content; }
        [Serializable] sealed class BuildIdentity
        {
            public string schema = "stationflow.station_portable_build_info.v1", projectName, productName, releaseVersion, buildLabel, applicationBuildGuid,
                createdUtc, unityVersion, target, sourceScene, executable, dataDirectory, captureDirectory, dataSchemaHtml, dataSchemaJson, sourceFingerprint;
            public int buildNumber;
            public bool succeeded;
        }
        [Serializable] sealed class Report
        {
            public bool success, sourceFilesPreserved, playerSettingsPreserved, buildSettingsPreserved, dataSchemaPackaged;
            public string startedUtc, completedUtc, unityVersion, target, result, executable, outputDirectory, desktopDirectory, projectRoot,
                projectName, productName, releaseVersion, buildLabel, applicationBuildGuid, error;
            public int buildNumber;
            public long totalBytes;
            public double totalSeconds;
            public int totalErrors, totalWarnings;
            public string[] scenes;
            public List<FileEvidence> sourceFiles = new List<FileEvidence>();
            public List<BuildOutput> outputs = new List<BuildOutput>();
            public List<Message> messages = new List<Message>();
            public List<DocumentationEvidence> documentation = new List<DocumentationEvidence>();
        }

        public static void Build()
        {
            var report = new Report { startedUtc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion,
                target = BuildTarget.StandaloneWindows64.ToString(), scenes = new[] { ScenePath }, projectName = ProjectName,
                productName = ProductName, projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..")) };
            try
            {
                Require(!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !BuildPipeline.isBuildingPlayer,
                    "Portable build requires stopped, compiled Edit mode with no active build.");
                for (int i = 0; i < SceneManager.sceneCount; ++i)
                    Require(!SceneManager.GetSceneAt(i).isDirty, "Save dirty scenes before building the saved daily scene.");
                Require(string.Equals(Path.GetFileName(report.projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), ProjectName, StringComparison.Ordinal),
                    "Open the renamed StationFlowLab project before creating the GareFlowDemo release.");
                Require(PlayerSettings.productName == ProductName, "Set the saved Unity product name to GareFlowDemo before building.");
                Require(File.Exists(ScenePath), "The saved daily scene is missing.");
                Require(BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64), "This editor does not include Windows64 build support.");
                var config = ReadVersionConfig();
                Require(PlayerSettings.bundleVersion == config.releaseVersion, "The saved Unity bundle version must match the releaseVersion in " + VersionConfigPath + ".");
                report.releaseVersion = config.releaseVersion;
                ReserveFreshDesktopOutput(config, report);
                // Reserve the number and generate documentation before snapshotting; neither changes during BuildPlayer.
                StationDecisionDataSchemaDocumentation.Generate();
                report.sourceFiles = SourceSnapshot();
                var native = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { ScenePath }, locationPathName = report.executable,
                    target = BuildTarget.StandaloneWindows64, options = BuildOptions.None });
                report.result = native.summary.result.ToString(); report.totalBytes = checked((long)native.summary.totalSize);
                report.applicationBuildGuid = native.summary.guid.ToString();
                report.totalSeconds = native.summary.totalTime.TotalSeconds; report.totalErrors = (int)native.summary.totalErrors; report.totalWarnings = (int)native.summary.totalWarnings;
                report.outputs = native.GetFiles().Select(f => new BuildOutput { path = f.path, role = f.role, size = checked((long)f.size) }).ToList();
                foreach (var step in native.steps)
                foreach (var message in step.messages)
                    if (message.type == LogType.Error || message.type == LogType.Exception || message.type == LogType.Assert || message.type == LogType.Warning)
                        report.messages.Add(new Message { step = step.name, type = message.type.ToString(), content = message.content });
                Require(native.summary.result == BuildResult.Succeeded && File.Exists(report.executable), "Windows portable build failed: " + native.summary.result);
                VerifyPackagedDocumentation(report);
                WriteFrenchReadme(report);
                report.success = true;
            }
            catch (Exception error) { report.error = error.ToString(); report.success = false; throw; }
            finally
            {
                foreach (var file in report.sourceFiles) file.after = File.Exists(file.path) ? Digest(file.path) : "missing";
                report.sourceFilesPreserved = report.sourceFiles.Count > 0 && report.sourceFiles.All(f => f.before == f.after);
                report.playerSettingsPreserved = report.sourceFiles.Any(f => f.path == "ProjectSettings/ProjectSettings.asset" && f.before == f.after);
                report.buildSettingsPreserved = report.sourceFiles.Any(f => f.path == "ProjectSettings/EditorBuildSettings.asset" && f.before == f.after);
                if (report.success && !report.sourceFilesPreserved)
                { report.success = false; report.error = "A source asset or global setting changed during the build; inspect sourceFiles evidence."; }
                report.completedUtc = DateTime.UtcNow.ToString("O"); Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
                string json = JsonUtility.ToJson(report, true); File.WriteAllText(ReportPath, json);
                if (!string.IsNullOrEmpty(report.outputDirectory) && Directory.Exists(report.outputDirectory) && File.Exists(Path.Combine(report.outputDirectory, OwnershipFile)))
                {
                    File.WriteAllText(Path.Combine(report.outputDirectory, "portable-build-report.json"), json);
                    WriteBuildIdentity(report);
                }
            }
            Require(report.success, report.error ?? "Portable release verification failed.");
        }

        static VersionConfig ReadVersionConfig()
        {
            Require(File.Exists(VersionConfigPath), "The portable build version configuration is missing: " + VersionConfigPath);
            var config = JsonUtility.FromJson<VersionConfig>(File.ReadAllText(VersionConfigPath));
            Require(config != null && config.schema == "stationflow.station_portable_build_version.v1" && config.projectName == ProjectName && config.productName == ProductName,
                "The version configuration must identify StationFlowLab and GareFlowDemo.");
            Require(Version.TryParse(config.releaseVersion, out var version) && version.Build >= 0 && version.Revision < 0 && version.ToString(3) == config.releaseVersion,
                "Use a three-part releaseVersion such as 0.2.0.");
            Require(config.nextBuildNumber >= 1 && config.nextBuildNumber < int.MaxValue && config.lastReservedBuildNumber >= 0,
                "Build numbers must be positive and leave room for the next reservation.");
            return config;
        }

        static void ReserveFreshDesktopOutput(VersionConfig config, Report report)
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Require(!string.IsNullOrWhiteSpace(desktop) && Directory.Exists(desktop), "The current user's Desktop directory is unavailable.");
            report.desktopDirectory = Path.GetFullPath(desktop);
            int number = Math.Max(config.nextBuildNumber, checked(config.lastReservedBuildNumber + 1));
            string directory, label;
            do
            {
                Require(number < int.MaxValue, "No further build number can be reserved.");
                label = "b" + number.ToString("D3", CultureInfo.InvariantCulture);
                directory = Path.GetFullPath(Path.Combine(report.desktopDirectory, ProductName + "_v" + config.releaseVersion + "_" + label));
                if (!Directory.Exists(directory) && !File.Exists(directory)) break;
                ++number;
            } while (true);
            Require(string.Equals(Path.GetDirectoryName(directory), report.desktopDirectory, StringComparison.OrdinalIgnoreCase),
                "A versioned portable build must be a direct child of the current user's Desktop.");
            Directory.CreateDirectory(directory);
            Require(!Directory.EnumerateFileSystemEntries(directory).Any(), "The reserved build directory is no longer empty; no files were overwritten.");
            report.outputDirectory = directory; report.executable = Path.Combine(directory, ProductName + ".exe");
            report.buildNumber = number; report.buildLabel = label;
            using (var writer = new StreamWriter(new FileStream(Path.Combine(directory, OwnershipFile), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)))
                writer.Write(JsonUtility.ToJson(new Owner { projectRoot = report.projectRoot, projectName = ProjectName, productName = ProductName,
                    releaseVersion = config.releaseVersion, buildLabel = label, buildNumber = number, executable = report.executable, scene = ScenePath }, true));
            config.lastReservedBuildNumber = number; config.nextBuildNumber = checked(number + 1);
            config.lastReservedDirectory = directory; config.lastReservedUtc = DateTime.UtcNow.ToString("O");
            // A failed attempt consumes its number, preserving every earlier binary and dataset.
            File.WriteAllText(VersionConfigPath, JsonUtility.ToJson(config, true) + "\n", new UTF8Encoding(false));
        }

        static void VerifyPackagedDocumentation(Report report)
        {
            string sourceRoot = "Assets/StreamingAssets/TrainStationData";
            string packagedRoot = Path.Combine(report.outputDirectory, ProductName + "_Data", "StreamingAssets", "TrainStationData");
            foreach (string source in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)))
            {
                string relative = source.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string packaged = Path.Combine(packagedRoot, relative);
                var document = new DocumentationEvidence { source = source.Replace('\\', '/'), packaged = packaged, before = Digest(source), after = File.Exists(packaged) ? Digest(packaged) : "missing" };
                document.identical = document.before == document.after; report.documentation.Add(document);
            }
            report.dataSchemaPackaged = report.documentation.Count >= 2 && report.documentation.All(d => d.identical) &&
                report.documentation.Any(d => Path.GetExtension(d.source) == ".html") && report.documentation.Any(d => Path.GetExtension(d.source) == ".json");
            Require(report.dataSchemaPackaged, "The portable player is missing matching French HTML or JSON data-schema documentation.");
        }

        static void WriteFrenchReadme(Report report)
        {
            string introduction = ProductName + " — version " + report.releaseVersion + " / " + report.buildLabel + "\nProjet Unity : " + ProjectName + "\n\n";
            string body = File.Exists(ReadmeTemplatePath) ? File.ReadAllText(ReadmeTemplatePath, Encoding.UTF8) :
                "Lancez GareFlowDemo.exe. Conservez tout le dossier lors d'une copie vers un autre ordinateur.\n\n" +
                "Choisissez les données et les caméras avant de démarrer la collecte. Les données sont enregistrées dans Captures/TrainStationDaily, à côté du programme.\n\n" +
                "Documentation : GareFlowDemo_Data/StreamingAssets/TrainStationData/DataSchema.fr.html ; contrat JSON : data-schema.json dans le même dossier.\n";
            File.WriteAllText(Path.Combine(report.outputDirectory, "README.fr.txt"), introduction + body, new UTF8Encoding(false));
        }

        static void WriteBuildIdentity(Report report)
        {
            string canonical = string.Join("\n", report.sourceFiles.Select(f => f.path + "|" + f.before));
            var identity = new BuildIdentity { projectName = ProjectName, productName = ProductName, releaseVersion = report.releaseVersion,
                buildNumber = report.buildNumber, buildLabel = report.buildLabel, applicationBuildGuid = report.applicationBuildGuid,
                createdUtc = report.completedUtc, unityVersion = report.unityVersion, target = report.target, sourceScene = ScenePath,
                executable = ProductName + ".exe", dataDirectory = ProductName + "_Data", captureDirectory = "Captures/TrainStationDaily",
                dataSchemaHtml = ProductName + "_Data/StreamingAssets/TrainStationData/DataSchema.fr.html",
                dataSchemaJson = ProductName + "_Data/StreamingAssets/TrainStationData/data-schema.json", sourceFingerprint = DigestBytes(Encoding.UTF8.GetBytes(canonical)), succeeded = report.success };
            File.WriteAllText(Path.Combine(report.outputDirectory, "build-info.json"), JsonUtility.ToJson(identity, true), new UTF8Encoding(false));
        }

        static List<FileEvidence> SourceSnapshot()
        {
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".asset", ".unity", ".prefab", ".controller", ".mat", ".shader", ".compute", ".meta" };
            var paths = Directory.GetFiles("Assets/TrainStation", "*", SearchOption.AllDirectories).Where(p => extensions.Contains(Path.GetExtension(p))).Select(p => p.Replace('\\', '/')).ToList();
            // Capture rendering assets belong to the reproducible build identity, alongside simulation sources.
            if (Directory.Exists("Assets/Settings")) paths.AddRange(Directory.GetFiles("Assets/Settings", "*", SearchOption.AllDirectories).Where(p => extensions.Contains(Path.GetExtension(p))).Select(p => p.Replace('\\', '/')));
            paths.Add("ProjectSettings/GraphicsSettings.asset"); paths.Add("ProjectSettings/QualitySettings.asset");
            if (Directory.Exists("Assets/StreamingAssets/TrainStationData")) paths.AddRange(Directory.GetFiles("Assets/StreamingAssets/TrainStationData", "*", SearchOption.AllDirectories).Select(p => p.Replace('\\', '/')));
            paths.Add("ProjectSettings/ProjectSettings.asset"); paths.Add("ProjectSettings/EditorBuildSettings.asset"); paths.Add("ProjectSettings/NavMeshAreas.asset");
            paths.Add(VersionConfigPath); paths.Add(ReadmeTemplatePath);
            return paths.Where(File.Exists).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).Select(p => new FileEvidence { path = p, before = Digest(p) }).ToList();
        }
        static string Digest(string path) => DigestBytes(File.ReadAllBytes(path));
        static string DigestBytes(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
#endif
