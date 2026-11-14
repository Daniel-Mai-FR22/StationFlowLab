#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace StationFlow.HumanAnimation.Editor
{
    /// <summary>Explicit, project-local import of user-supplied animation exports. No global import hook.</summary>
    public static class HumanMixamoImporter
    {
        public const string SourceDirectory = "Assets/HumanAnimation/Source/Mixamo";
        public const string ClipDirectory = "Assets/HumanAnimation/Resources/HumanAnimation/Mixamo/Clips";
        public const string ManifestPath = SourceDirectory + "/mixamo-import-manifest.json";
        const string Provenance = "user-supplied Mixamo export, verify via download";

        [Serializable]
        public sealed class ImportManifest
        {
            public int version = 1;
            public string source = Provenance;
            public string exportRecommendation = "Mixamo FBX Binary, Without Skin, 30 fps. For locomotion, select In Place when available.";
            public string optionsNote = "Review inferred labels/category/loop. Source kind: auto (Models folder means model-only), model (Avatar only), or animation. Edit options, then Reapply manifest options. inPlace bakes root XZ; it does not synthesize or replace motion. Retired model-only clips remain on disk but are excluded from the motion library.";
            public bool defaultInPlace = true;
            public bool defaultLoopLocomotion = true;
            public float defaultBlendIn = .18f;
            public List<SourceRecord> sources = new List<SourceRecord>();
            public List<MotionRecord> motions = new List<MotionRecord>();
        }

        [Serializable]
        public sealed class SourceRecord
        {
            public string assetPath;
            public string originalFilename;
            public string originalRelativePath;
            public string sha256;
            public string source = Provenance;
            public string kind = "auto";
            public string resolvedKind;
            public string importedUtc;
            public string avatarName;
            public bool humanoidAvatarValid;
            public string status;
            public string error;
        }

        [Serializable]
        public sealed class MotionRecord
        {
            public string id;
            public string displayName;
            public string stateName;
            public string sourceAssetPath;
            public string originalClipName;
            public string originalTakeName;
            public int sourceClipIndex;
            public string importedClipName;
            public string extractedAssetPath;
            public string category;
            public bool loop;
            public bool loopPose = true;
            public bool inPlace = true;
            public bool bakeRootRotation = true;
            public bool bakeRootY = true;
            public bool keepOriginalOrientation = true;
            public bool keepOriginalPositionXZ = true;
            public bool keepOriginalPositionY;
            public bool heightFromFeet = true;
            public float blendIn = .18f;
            public float frameRate;
            public float duration;
            public string status;
            public string error;
        }

        [MenuItem("Efrei Research Lab/Human Animation/Import Mixamo FBX folder")]
        public static void OpenImportWindow() => EditorWindow.GetWindow<HumanMixamoImportWindow>("Mixamo import");

        /// <summary>
        /// Copies only FBXs from the selected directory, configures their own humanoid avatars, and extracts actual clips.
        /// Existing conflicting source files are retained under distinct hash-suffixed names.
        /// Failures are recorded and thrown; valid imports are kept for inspection, without substituting animations.
        /// </summary>
        public static HumanMotion[] ImportFolder(string absoluteSourceFolder)
        {
            if (string.IsNullOrWhiteSpace(absoluteSourceFolder) || !Path.IsPathRooted(absoluteSourceFolder))
                throw new ArgumentException("Choose an absolute source folder containing downloaded FBX files.", nameof(absoluteSourceFolder));
            string sourceFolder = Path.GetFullPath(absoluteSourceFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(sourceFolder)) throw new DirectoryNotFoundException(sourceFolder);
            string destinationFolder = FullAssetPath(SourceDirectory);
            if (SameOrUnder(sourceFolder, destinationFolder) || SameOrUnder(destinationFolder, sourceFolder))
                throw new ArgumentException("Choose an export folder outside the managed Mixamo source directory and its parent folders. Use Reapply manifest options for existing imports.");

            string[] files = Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories)
                .Where(p => string.Equals(Path.GetExtension(p), ".fbx", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length == 0) throw new InvalidOperationException("The selected folder contains no FBX files. Download actual Mixamo exports first.");

            ImportManifest manifest = ReadManifest();
            EnsureDirectories();
            var failures = new List<string>();
            try
            {
                for (int i = 0; i < files.Length; i++)
                {
                    EditorUtility.DisplayProgressBar("Import Mixamo animations", Path.GetFileName(files[i]), (float)i / files.Length);
                    SourceRecord source = null;
                    try
                    {
                        string relative = files[i].Substring(sourceFolder.Length + 1).Replace('\\', '/');
                        string hash = HashFile(files[i]);
                        string assetPath = CopySourceWithoutOverwrite(files[i], relative, hash);
                        source = manifest.sources.FirstOrDefault(s => s.assetPath == assetPath);
                        if (source == null)
                        {
                            source = new SourceRecord
                            {
                                assetPath = assetPath, originalFilename = Path.GetFileName(files[i]),
                                originalRelativePath = relative, sha256 = hash,
                                kind = HasModelsFolder(files[i]) ? "model" : "auto"
                            };
                            manifest.sources.Add(source);
                        }
                        ImportSource(source, manifest);
                    }
                    catch (Exception error)
                    {
                        RecordFailure(source, manifest, error.Message);
                        failures.Add(Path.GetFileName(files[i]) + ": " + error.Message);
                    }
                    WriteManifest(manifest);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
                WriteManifest(manifest);
            }
            ThrowFailures(failures);
            return CollectImportedMotions();
        }

        /// <summary>Returns only valid extracted humanoid clips; it never imports, creates, or substitutes a clip.</summary>
        public static HumanMotion[] CollectImportedMotions()
        {
            ImportManifest manifest = ReadManifest();
            var motions = new List<HumanMotion>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (MotionRecord entry in manifest.motions.Where(m => m.status == "imported"))
            {
                SourceRecord source = manifest.sources.FirstOrDefault(s => s.assetPath == entry.sourceAssetPath);
                // Also excludes a model-only source from an older manifest before it has been reapplied.
                if (source != null && IsModelOnlySource(source)) continue;
                ValidateEntry(entry);
                if (!ids.Add(entry.id)) throw new InvalidOperationException("Duplicate motion id in Mixamo manifest: " + entry.id);
                AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(entry.extractedAssetPath);
                if (clip == null || !clip.humanMotion || clip.legacy || clip.length <= 0)
                    throw new InvalidOperationException("Missing or invalid extracted humanoid clip: " + entry.extractedAssetPath + ". Reapply the manifest to repair this import.");
                if (source == null || !source.humanoidAvatarValid || source.status != "imported")
                    throw new InvalidOperationException("A validated source avatar is missing for " + entry.displayName);
                ValidateSavedSourceAvatar(entry.sourceAssetPath);
                motions.Add(new HumanMotion
                {
                    id = entry.id, displayName = entry.displayName, stateName = entry.stateName,
                    sourceAssetPath = entry.sourceAssetPath, clip = clip, loop = entry.loop,
                    blendIn = entry.blendIn, source = HumanMotionSource.Mixamo,
                    category = (HumanMotionCategory)Enum.Parse(typeof(HumanMotionCategory), entry.category)
                });
            }
            return motions.OrderBy(m => m.category).ThenBy(m => m.displayName, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public static HumanMotion[] ReapplyManifestOptions()
        {
            ImportManifest manifest = ReadManifest();
            if (manifest.sources.Count == 0) throw new InvalidOperationException("No Mixamo source FBX has been imported yet.");
            var failures = new List<string>();
            try
            {
                for (int i = 0; i < manifest.sources.Count; i++)
                {
                    SourceRecord source = manifest.sources[i];
                    EditorUtility.DisplayProgressBar("Reapply Mixamo manifest", source.originalFilename, (float)i / manifest.sources.Count);
                    try { ImportSource(source, manifest); }
                    catch (Exception error)
                    {
                        RecordFailure(source, manifest, error.Message);
                        failures.Add(source.originalFilename + ": " + error.Message);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
                WriteManifest(manifest);
            }
            ThrowFailures(failures);
            return CollectImportedMotions();
        }

        static void ImportSource(SourceRecord source, ImportManifest manifest)
        {
            if (!IsManagedAssetPath(source.assetPath, SourceDirectory) || !File.Exists(FullAssetPath(source.assetPath)))
                throw new InvalidOperationException("Managed source FBX is missing or outside the import directory: " + source.assetPath);
            if (!string.Equals(HashFile(FullAssetPath(source.assetPath)), source.sha256, StringComparison.Ordinal))
                throw new InvalidOperationException("The source FBX content changed after import. Import the changed export from its download folder so original provenance is preserved: " + source.assetPath);

            AssetDatabase.ImportAsset(source.assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            ModelImporter importer = AssetImporter.GetAtPath(source.assetPath) as ModelImporter;
            if (importer == null) throw new InvalidOperationException("Unity did not recognize this file as a model.");
            bool modelOnly = IsModelOnlySource(source);
            if (string.IsNullOrWhiteSpace(source.kind)) source.kind = "auto";
            source.resolvedKind = modelOnly ? "model" : "animation";
            importer.importAnimation = !modelOnly;
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.sourceAvatar = null;
            importer.animationCompression = ModelImporterAnimationCompression.Off;
            importer.resampleCurves = true;
            importer.optimizeGameObjects = false;
            importer.importCameras = false;
            importer.importLights = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.SaveAndReimport();

            Avatar avatar = ValidateSavedSourceAvatar(source.assetPath);
            source.avatarName = avatar.name;
            source.humanoidAvatarValid = true;
            if (modelOnly)
            {
                RecordModelOnly(source, manifest);
                return;
            }
            ModelImporterClipAnimation[] definitions = importer.defaultClipAnimations;
            if (definitions == null || definitions.Length == 0 || definitions.All(d => d.lastFrame <= d.firstFrame))
            {
                RecordModelOnly(source, manifest);
                return;
            }

            var entries = new List<MotionRecord>();
            for (int i = 0; i < definitions.Length; i++)
            {
                ModelImporterClipAnimation definition = definitions[i];
                MotionRecord entry = manifest.motions.FirstOrDefault(m => m.sourceAssetPath == source.assetPath && m.sourceClipIndex == i);
                if (entry == null)
                {
                    entry = NewMotion(source, definition, i, definitions.Length, manifest);
                    manifest.motions.Add(entry);
                }
                ValidateEntry(entry);
                definition.name = entry.importedClipName;
                definition.loopTime = entry.loop;
                definition.loopPose = entry.loop && entry.loopPose;
                definition.lockRootPositionXZ = entry.inPlace;
                definition.lockRootRotation = entry.bakeRootRotation;
                definition.lockRootHeightY = entry.bakeRootY;
                definition.keepOriginalOrientation = entry.keepOriginalOrientation;
                definition.keepOriginalPositionXZ = entry.keepOriginalPositionXZ;
                definition.keepOriginalPositionY = entry.keepOriginalPositionY;
                definition.heightFromFeet = entry.heightFromFeet;
                entries.Add(entry);
            }
            importer.clipAnimations = definitions;
            importer.SaveAndReimport();
            AnimationClip[] importedClips = AssetDatabase.LoadAllAssetsAtPath(source.assetPath).OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal)).ToArray();
            if (importedClips.Length == 0 || importedClips.All(c => c.length <= 0 || (!c.humanMotion
                && AnimationUtility.GetCurveBindings(c).Length == 0 && AnimationUtility.GetObjectReferenceCurveBindings(c).Length == 0)))
            {
                RecordModelOnly(source, manifest);
                return;
            }
            foreach (MotionRecord entry in entries)
            {
                AnimationClip clip = importedClips.FirstOrDefault(c => c.name == entry.importedClipName);
                if (clip == null || !clip.humanMotion || clip.legacy || clip.length <= 0)
                    throw new InvalidOperationException("Take '" + entry.originalClipName + "' did not produce a non-empty Humanoid AnimationClip. Check the FBX skeleton and animation export in the Model Importer.");
                ExtractClip(entry, clip);
                entry.frameRate = clip.frameRate;
                entry.duration = clip.length;
                entry.status = "imported";
                entry.error = "";
            }
            source.status = "imported";
            source.error = "";
            source.importedUtc = DateTime.UtcNow.ToString("O");
        }

        static void RecordModelOnly(SourceRecord source, ImportManifest manifest)
        {
            source.status = "model-only";
            source.resolvedKind = "model";
            source.error = "";
            source.importedUtc = DateTime.UtcNow.ToString("O");
            foreach (MotionRecord motion in manifest.motions.Where(m => m.sourceAssetPath == source.assetPath))
            {
                motion.status = "model-only";
                motion.error = "Unused: model-only source. Humanoid Avatar and any previously extracted clip file are retained; this record is excluded from the motion library.";
            }
            Debug.Log("Imported Humanoid model/Avatar without animation clips: " + source.assetPath);
        }

        static bool IsModelOnlySource(SourceRecord source)
        {
            string kind = string.IsNullOrWhiteSpace(source.kind) ? "auto" : source.kind.Trim().ToLowerInvariant();
            switch (kind)
            {
                case "model":
                case "model-only": return true;
                case "animation": return false;
                case "auto": return HasModelsFolder(source.originalRelativePath) || HasModelsFolder(source.assetPath);
                default: throw new InvalidOperationException("Unknown source kind '" + source.kind + "' for " + source.assetPath + ". Use auto, model, or animation.");
            }
        }

        static bool HasModelsFolder(string path) => !string.IsNullOrWhiteSpace(path)
            && path.Replace('\\', '/').Split('/').Any(segment => string.Equals(segment, "Models", StringComparison.OrdinalIgnoreCase));

        static Avatar ValidateSavedSourceAvatar(string assetPath)
        {
            ModelImporter importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null || importer.animationType != ModelImporterAnimationType.Human || importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
                throw new InvalidOperationException("Source must use Humanoid / Create From This Model: " + assetPath);
            Avatar avatar = AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<Avatar>().FirstOrDefault(a => a.isValid && a.isHuman);
            if (avatar == null)
                throw new InvalidOperationException("Unity could not create a valid Humanoid Avatar from this FBX's own skeleton: " + assetPath + ". Check Configure Avatar; do not copy the destination character's Avatar onto an animation export.");
            return avatar;
        }

        static MotionRecord NewMotion(SourceRecord source, ModelImporterClipAnimation definition, int index, int count, ImportManifest manifest)
        {
            string filename = Path.GetFileNameWithoutExtension(source.originalFilename);
            string label = PrettyName(count == 1 ? filename : filename + " " + definition.name);
            string identity = Slug(label) + "_" + HashText(source.assetPath + "|" + source.sha256 + "|" + index).Substring(0, 10);
            HumanMotionCategory category = GuessCategory(label);
            return new MotionRecord
            {
                id = "mixamo_" + identity, stateName = "Base Layer.mixamo_" + identity,
                displayName = PrettyName(label), sourceAssetPath = source.assetPath,
                originalClipName = definition.name, originalTakeName = definition.takeName, sourceClipIndex = index,
                importedClipName = "Mixamo_" + identity, category = category.ToString(),
                loop = manifest.defaultLoopLocomotion && GuessLoop(label),
                inPlace = manifest.defaultInPlace, blendIn = Mathf.Clamp(manifest.defaultBlendIn, 0, 2),
                bakeRootY = !Regex.IsMatch(label, @"\b(jump|jumping|fall|falling)\b", RegexOptions.IgnoreCase),
                status = "pending"
            };
        }

        static void ValidateEntry(MotionRecord entry)
        {
            if (string.IsNullOrWhiteSpace(entry.id) || string.IsNullOrWhiteSpace(entry.stateName) || string.IsNullOrWhiteSpace(entry.displayName) || string.IsNullOrWhiteSpace(entry.importedClipName))
                throw new InvalidOperationException("A manifest motion is missing id, stateName, displayName, or importedClipName.");
            HumanMotionCategory category;
            if (!Enum.TryParse(entry.category, false, out category) || !Enum.IsDefined(typeof(HumanMotionCategory), category))
                throw new InvalidOperationException("Unknown motion category '" + entry.category + "' for " + entry.id);
            if (float.IsNaN(entry.blendIn) || entry.blendIn < 0 || entry.blendIn > 2)
                throw new InvalidOperationException("blendIn must be between 0 and 2 seconds for " + entry.id);
            if (!string.IsNullOrEmpty(entry.extractedAssetPath) && !IsManagedAssetPath(entry.extractedAssetPath, ClipDirectory))
                throw new InvalidOperationException("Extracted clip path is outside the managed clip directory: " + entry.extractedAssetPath);
        }

        static void ExtractClip(MotionRecord entry, AnimationClip sourceClip)
        {
            bool ownsExistingPath = !string.IsNullOrEmpty(entry.extractedAssetPath);
            if (!ownsExistingPath)
                entry.extractedAssetPath = AssetDatabase.GenerateUniqueAssetPath(ClipDirectory + "/" + entry.id + ".anim");
            AnimationClip existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(entry.extractedAssetPath);
            UnityEngine.Object other = AssetDatabase.LoadMainAssetAtPath(entry.extractedAssetPath);
            if (other != null && existing == null)
                throw new InvalidOperationException("An unrelated asset occupies extracted clip path " + entry.extractedAssetPath);
            AnimationClip copy = UnityEngine.Object.Instantiate(sourceClip);
            copy.name = entry.displayName;
            copy.hideFlags = HideFlags.None;
            if (existing == null) AssetDatabase.CreateAsset(copy, entry.extractedAssetPath);
            else
            {
                EditorUtility.CopySerialized(copy, existing);
                existing.name = entry.displayName;
                EditorUtility.SetDirty(existing);
                UnityEngine.Object.DestroyImmediate(copy);
            }
        }

        static string CopySourceWithoutOverwrite(string absoluteFile, string relativePath, string hash)
        {
            string cleanRelative = string.Join("/", relativePath.Split('/').Select(SafePathPart));
            string assetPath = SourceDirectory + "/" + cleanRelative;
            if (!IsManagedAssetPath(assetPath, SourceDirectory)) throw new InvalidOperationException("Invalid relative FBX path.");
            string fullPath = FullAssetPath(assetPath);
            if (File.Exists(fullPath) && HashFile(fullPath) != hash)
            {
                string parent = Path.GetDirectoryName(assetPath).Replace('\\', '/');
                assetPath = parent + "/" + Path.GetFileNameWithoutExtension(assetPath) + "__" + hash.Substring(0, 12) + ".fbx";
                fullPath = FullAssetPath(assetPath);
                if (File.Exists(fullPath) && HashFile(fullPath) != hash)
                    throw new IOException("A different file already occupies the collision-safe import path: " + assetPath);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            if (!File.Exists(fullPath)) File.Copy(absoluteFile, fullPath, false);
            return assetPath;
        }

        static HumanMotionCategory GuessCategory(string name)
        {
            if (Regex.IsMatch(name, @"\b(walk|walking|run|running|jog|jogging|sprint|sprinting|strafe|strafing|crawl|crawling|jump|jumping|turn|turning)\b", RegexOptions.IgnoreCase)) return HumanMotionCategory.Locomotion;
            if (Regex.IsMatch(name, @"\b(idle|sit|sitting|seated|stand|standing|crouch|crouching|kneel|kneeling|pose)\b", RegexOptions.IgnoreCase)) return HumanMotionCategory.Posture;
            if (Regex.IsMatch(name, @"\b(pick|pickup|carry|carrying|door|push|pull|lift|lifting|use|grab|hold|holding)\b", RegexOptions.IgnoreCase)) return HumanMotionCategory.Interaction;
            return HumanMotionCategory.Gesture;
        }

        static bool GuessLoop(string name) => Regex.IsMatch(name, @"\b(idle|walk|walking|run|running|jog|jogging|sprint|sprinting|strafe|strafing|crawl|crawling)\b", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(name, @"\b(start|stop|end|jump|jumping|fall|falling|turn|turning|down|up|transition)\b", RegexOptions.IgnoreCase);

        static string PrettyName(string value) => Regex.Replace(value.Replace('_', ' ').Replace('-', ' '), @"\s+", " ").Trim();
        static string Slug(string value)
        {
            string slug = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
            if (slug.Length == 0) slug = "motion";
            return slug.Length > 60 ? slug.Substring(0, 60) : slug;
        }

        static string SafePathPart(string value)
        {
            if (value == "." || value == ".." || string.IsNullOrEmpty(value)) throw new InvalidOperationException("Invalid source directory component.");
            foreach (char character in Path.GetInvalidFileNameChars()) value = value.Replace(character, '_');
            return value;
        }

        static string FullAssetPath(string assetPath) => Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
        static bool SameOrUnder(string child, string parent) => string.Equals(child.TrimEnd('\\', '/'), parent.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            || child.StartsWith(parent.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        static bool IsManagedAssetPath(string path, string root) => !string.IsNullOrEmpty(path) && SameOrUnder(FullAssetPath(path), FullAssetPath(root)) && !string.Equals(FullAssetPath(path), FullAssetPath(root), StringComparison.OrdinalIgnoreCase);

        static string HashFile(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        static string HashText(string text)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        internal static ImportManifest ReadManifest()
        {
            if (!File.Exists(FullAssetPath(ManifestPath))) return new ImportManifest();
            ImportManifest manifest;
            try { manifest = JsonUtility.FromJson<ImportManifest>(File.ReadAllText(FullAssetPath(ManifestPath))); }
            catch (Exception error) { throw new InvalidOperationException("Cannot read Mixamo manifest: " + error.Message, error); }
            if (manifest == null || manifest.version != 1 || manifest.sources == null || manifest.motions == null)
                throw new InvalidOperationException("Mixamo manifest is missing required fields or has an unsupported version.");
            return manifest;
        }

        internal static void WriteManifest(ImportManifest manifest)
        {
            EnsureDirectories();
            File.WriteAllText(FullAssetPath(ManifestPath), JsonUtility.ToJson(manifest, true), new UTF8Encoding(false));
            AssetDatabase.ImportAsset(ManifestPath, ImportAssetOptions.ForceSynchronousImport);
        }

        static void EnsureDirectories()
        {
            Directory.CreateDirectory(FullAssetPath(SourceDirectory));
            Directory.CreateDirectory(FullAssetPath(ClipDirectory));
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        static void RecordFailure(SourceRecord source, ImportManifest manifest, string error)
        {
            if (source == null) return;
            source.status = "error";
            source.error = error;
            source.humanoidAvatarValid = false;
            foreach (MotionRecord motion in manifest.motions.Where(m => m.sourceAssetPath == source.assetPath))
            {
                motion.status = "error";
                motion.error = error;
            }
        }

        static void ThrowFailures(List<string> failures)
        {
            if (failures.Count > 0)
                throw new InvalidOperationException("Mixamo import failed for " + failures.Count + " file(s). Valid files remain imported; no replacement animation was generated.\n\n" + string.Join("\n", failures) + "\n\nDetails: " + ManifestPath);
        }

        internal static void RebuildAuthoredLibrary()
        {
            Type authoring = typeof(HumanMixamoImporter).Assembly.GetType("StationFlow.HumanAnimation.Editor.HumanAnimationAuthoring");
            MethodInfo rebuild = authoring?.GetMethod("RebuildMixamoLibrary", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (rebuild == null)
            {
                Debug.LogWarning("Mixamo clips imported. Run the Human Animation library rebuild command when HumanAnimationAuthoring is available.");
                return;
            }
            try { rebuild.Invoke(null, null); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
    }

    public sealed class HumanMixamoImportWindow : EditorWindow
    {
        [SerializeField] string sourceFolder;
        [SerializeField] bool inPlace = true;
        [SerializeField] bool loopLocomotion = true;
        [SerializeField] float blendIn = .18f;
        string status;
        bool failed;

        void OnEnable()
        {
            minSize = new Vector2(520, 375);
            try
            {
                HumanMixamoImporter.ImportManifest manifest = HumanMixamoImporter.ReadManifest();
                inPlace = manifest.defaultInPlace;
                loopLocomotion = manifest.defaultLoopLocomotion;
                blendIn = manifest.defaultBlendIn;
            }
            catch (Exception error) { status = error.Message; failed = true; }
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Import downloaded Mixamo animations", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Export FBX Binary / Without Skin / 30 fps from Mixamo. For locomotion, choose In Place when available. Select the local folder containing those exports; subfolders are included.", MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                sourceFolder = EditorGUILayout.TextField("Source folder", sourceFolder);
                if (GUILayout.Button("Browse…", GUILayout.Width(84)))
                {
                    string selected = EditorUtility.OpenFolderPanel("Select Mixamo FBX export folder", sourceFolder ?? "", "");
                    if (!string.IsNullOrEmpty(selected)) sourceFolder = selected;
                }
            }
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Defaults for newly discovered clips", EditorStyles.boldLabel);
            inPlace = EditorGUILayout.Toggle("Bake root XZ (in place)", inPlace);
            loopLocomotion = EditorGUILayout.Toggle("Infer locomotion / idle loops", loopLocomotion);
            blendIn = EditorGUILayout.Slider("Blend in (seconds)", blendIn, 0, 2);
            EditorGUILayout.HelpBox("Each FBX uses its own Humanoid avatar. Files in a Models folder are Avatar-only and never enter the motion library. Set source kind in the JSON manifest to override this. Per-clip category, label, loop and root settings are also editable; reapply the manifest to update imports.", MessageType.None);
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(sourceFolder)))
                if (GUILayout.Button("Import folder & rebuild Mixamo library", GUILayout.Height(32)))
                    Run(() =>
                    {
                        HumanMixamoImporter.ImportManifest manifest = HumanMixamoImporter.ReadManifest();
                        manifest.defaultInPlace = inPlace;
                        manifest.defaultLoopLocomotion = loopLocomotion;
                        manifest.defaultBlendIn = blendIn;
                        HumanMixamoImporter.WriteManifest(manifest);
                        return HumanMixamoImporter.ImportFolder(sourceFolder);
                    });
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Open manifest"))
                {
                    TextAsset manifest = AssetDatabase.LoadAssetAtPath<TextAsset>(HumanMixamoImporter.ManifestPath);
                    if (manifest != null) AssetDatabase.OpenAsset(manifest);
                    else { status = "Import a source folder first to create the manifest."; failed = false; }
                }
                if (GUILayout.Button("Reapply manifest options")) Run(HumanMixamoImporter.ReapplyManifestOptions);
            }
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, failed ? MessageType.Error : MessageType.Info);
        }

        void Run(Func<HumanMotion[]> operation)
        {
            try
            {
                HumanMotion[] motions = operation();
                HumanMixamoImporter.RebuildAuthoredLibrary();
                status = motions.Length == 0
                    ? "No animation clips were found. Valid Humanoid models/Avatars are retained with model-only status in the manifest. Import animation FBXs to populate the library."
                    : "Imported and validated " + motions.Length + " actual Humanoid clip(s). Source provenance and per-clip settings are recorded in the manifest.";
                failed = false;
            }
            catch (Exception error)
            {
                status = error.Message;
                failed = true;
                Debug.LogError(error);
            }
        }
    }
}
#endif
