using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Offline French documentation generated from actual compiled DTOs and capture source contracts.</summary>
    [InitializeOnLoad]
    public sealed class StationDecisionDataSchemaDocumentation : IPreprocessBuildWithReport
    {
        const string GeneratorVersion = "fr-contract-2026-10-28.12";
        const string NS = "StationFlow.TrainStation.DecisionLab";
        const string Folder = "Assets/StreamingAssets/TrainStationData";
        const BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        public int callbackOrder => -10000;
        static StationDecisionDataSchemaDocumentation() => EditorApplication.update += DeferredGenerate;
        static void DeferredGenerate()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer) return;
            EditorApplication.update -= DeferredGenerate;
            try { Generate(); } catch (Exception e) { Debug.LogError("Data schema documentation: " + e); }
        }
        public void OnPreprocessBuild(BuildReport report) { Generate(); Validate(); }

        public static string Generate()
        {
            var document = Describe();
            string jsonPath = Folder + "/" + StationDecisionDataSchemaInfo.JsonFileName;
            string htmlPath = Folder + "/" + StationDecisionDataSchemaInfo.HtmlFileName;
            if (File.Exists(jsonPath) && File.Exists(htmlPath))
            {
                try
                {
                    var metadata = StationDecisionDataSchemaInfo.ValidateDocumentPair(File.ReadAllText(jsonPath, Encoding.UTF8), File.ReadAllText(htmlPath, Encoding.UTF8));
                    if (metadata.contractFingerprint == document.contractFingerprint) return document.contractFingerprint;
                }
                catch (Exception) { /* A stale or damaged packaged pair is regenerated. */ }
            }
            Directory.CreateDirectory(Folder);
            document.generatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            WriteChanged(jsonPath, JsonUtility.ToJson(document, true) + "\n");
            WriteChanged(htmlPath, Html(document));
            AssetDatabase.ImportAsset(jsonPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(htmlPath, ImportAssetOptions.ForceSynchronousImport);
            return document.contractFingerprint;
        }

        public static int Validate()
        {
            int checks = 0;
            var expected = Describe();
            string json = File.ReadAllText(Folder + "/" + StationDecisionDataSchemaInfo.JsonFileName, Encoding.UTF8);
            string html = File.ReadAllText(Folder + "/" + StationDecisionDataSchemaInfo.HtmlFileName, Encoding.UTF8);
            var actual = JsonUtility.FromJson<Document>(json);
            var metadata = StationDecisionDataSchemaInfo.ValidateDocumentPair(json, html); ++checks;
            Require(metadata.contractFingerprint == expected.contractFingerprint && actual.canonicalContract == expected.canonicalContract, "Packaged schema is stale relative to fields or sources."); ++checks;
            Require(actual.types.Length == expected.types.Length && actual.files.Length == expected.files.Length, "Missing reflected types or files."); ++checks;
            Require(actual.types.All(t => !t.id.Contains("<")), "Compiler-generated helper types are not data contracts."); ++checks;
            foreach (var type in expected.types)
            {
                var saved = actual.types.Single(t => t.id == type.id);
                Require(saved.fields.Length == type.fields.Length && saved.enumValues.SequenceEqual(type.enumValues), "Missing members of " + type.id); ++checks;
                Require(html.Contains("id=\"" + Anchor(type.id) + "\""), "Missing HTML type anchor."); ++checks;
                foreach (var field in type.fields)
                {
                    Require(saved.fields.Any(f => f.name == field.name && f.type == field.type), "Missing actual field " + type.id + "." + field.name); ++checks;
                    Require(html.Contains("data-field=\"" + Escape(type.id + "." + field.name) + "\""), "Missing HTML field row."); ++checks;
                }
            }
            foreach (var file in expected.files)
            {
                Require(html.Contains(Escape(file.path)), "Missing file map " + file.path); ++checks;
                foreach (string root in file.rootTypes) { Require(actual.types.Any(t => t.id == root), "Unknown root " + root); ++checks; }
            }
            Require(html.Contains("href=\"data-schema.json\"") && html.Contains("<svg") && html.Contains("<html lang=\"fr\""), "Missing offline link, diagram or French language."); ++checks;
            Require(!html.Contains("<script src=") && !html.Contains("<link rel=\"stylesheet\"") && !html.Contains("DataSchema.vi.html"), "Documentation must be self-contained and French."); ++checks;
            Require(actual.types.Single(t => t.id == "VideoFrameRecord").fields.Select(f => f.name).OrderBy(x => x).SequenceEqual(new[] { "cameraStateId", "captureFpsInstantaneous", "captureFpsRolling1s", "captureIntervalRealSeconds", "requestId", "segment", "syncSampleId", "videoFrame", "videoTimeSeconds" }), "Manual frame keys differ."); ++checks;
            Require(actual.types.Single(t => t.id == TypeId(Nested(typeof(StationDecisionCaptureSession), "FrameMatrices"))).fields.Any(f => f.name == "intrinsicMatrix"), "Missing camera-state intrinsics."); ++checks;
            Require(actual.types.Single(t => t.id == TypeId(Nested(typeof(StationDecisionCaptureSession), "SyncRecord"))).fields.Any(f => f.name == "measuredFrameRates" && f.type == NS + ".StationDecisionMeasuredFrameRates"), "Missing typed measured frame rates in sync records."); ++checks;
            Require(actual.types.Any(t => t.id == NS + ".StationDecisionMeasuredFrameRates") && actual.types.Any(t => t.id == NS + ".StationDecisionCameraFrameRate"), "Missing measured-rate payload contracts."); ++checks;
            Require(actual.types.Single(t => t.id == TypeId(Nested(typeof(StationDecisionCaptureSession), "Manifest"))).fields.Any(f => f.name == "storageLayout") &&
                actual.types.Any(t => t.id == NS + ".StationDecisionAgentsIndex") && actual.types.Any(t => t.id == NS + ".StationDecisionAgentDirectory"), "Missing per-agent storage/index contracts."); ++checks;
            Require(actual.types.Single(t => t.id == TypeId(Nested(typeof(StationDecisionWorld), "AgentProfileFile"))).fields.Select(f => f.name).OrderBy(x => x).SequenceEqual(new[] { "captureStamp", "journey", "profile" }), "Agent profile must contain one profile and one journey."); ++checks;
            Require(actual.files.Any(f => f.path == "agents/index.json") && actual.files.Any(f => f.path == "agents/<safeDir>/profile.json") && !actual.files.Any(f => f.path == "profiles.json"), "Current file map must use per-agent profiles."); ++checks;
            var pipelineFields = actual.types.Single(t => t.id == TypeId(Nested(typeof(StationDecisionCaptureSession), "Manifest"))).fields;
            Require(pipelineFields.Any(f => f.name == "readbackSlotCount" && f.type == "int32") &&
                pipelineFields.Any(f => f.name == "commonBatchAdmission" && f.type == "bool") &&
                pipelineFields.Any(f => f.name == "renderProfile" && f.type == "string"), "Missing effective capture-pipeline settings in manifest contract."); ++checks;
            Require(pipelineFields.Any(f => f.name == "dataWriterMode" && f.type == "string") &&
                pipelineFields.Any(f => f.name == "dataQueueMaxPendingCommands" && f.type == "int32") &&
                pipelineFields.Any(f => f.name == "dataQueueMaxRetainedBytes" && f.type == "int64") &&
                pipelineFields.Any(f => f.name == "dataCommandsAccepted" && f.type == "int64") &&
                pipelineFields.Any(f => f.name == "dataCommandsCompleted" && f.type == "int64"), "Missing background data-writer contract."); ++checks;
            Require(new[] { "skeletonDefinitionsFile", "skeletonPoseFileName", "skeletonBindingsFileName", "skeletonIndexFileName", "skeletonPoseFormat" }.All(name => pipelineFields.Any(f => f.name == name && f.type == "string")), "Missing skeleton manifest descriptors."); ++checks;
            Require(actual.files.Any(f => f.path == "skeleton-definitions.jsonl") && actual.files.Any(f => f.path == "agents/<safeDir>/skeleton-poses.bin") && actual.files.Any(f => f.path == "agents/<safeDir>/skeleton-bindings.jsonl") && actual.files.Any(f => f.path == "agents/<safeDir>/skeleton-index.jsonl"), "Missing skeleton physical file map."); ++checks;
            Require(actual.types.Single(t => t.id == "SkeletonBinaryPoseRecord").fields.Select(f => f.name).SequenceEqual(new[] { "byteLength", "syncSampleId", "sampleId", "bindingId", "jointCount", "maskByteCount", "time", "validMask", "jointPositionsWorld" }), "Skeleton binary field order differs."); ++checks;
            Require(StationDecisionSkeletonBinary.HeaderBytes == 16 && StationDecisionSkeletonBinary.RecordPrefixBytes == 32 && StationDecisionSkeletonBinary.CheckpointInterval == 256, "Skeleton binary documentation differs from codec constants."); ++checks;
            Require(actual.types.Single(t => t.id == "NormalizedAnimation").fields.Any(f => f.name == "locomotionStrideWeight"), "Missing dense stride weight."); ++checks;
            Require(actual.types.Single(t => t.id == TypeId(Nested(typeof(StationDecisionWorld), "Manifest"))).fields.Any(f => f.name == "avoidancePredictionSeconds"), "Missing native prediction horizon."); ++checks;
            return checks;
        }

        static Document Describe()
        {
            var types = new HashSet<Type>();
            foreach (var type in typeof(StationDecisionWorld).Assembly.GetTypes().Where(t => t.Namespace == NS && IsDto(t))) Collect(type, types);
            Collect(typeof(StationFlow.TrainStation.RailOperations.StationRailService), types);
            Collect(typeof(Vector3), types); Collect(typeof(Quaternion), types);
            var descriptors = types.OrderBy(TypeId, StringComparer.Ordinal).Select(DescribeType).ToList();
            descriptors.AddRange(NormalizedTypes());
            descriptors.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
            var files = FileMappings(); var sources = SourceFiles();
            var canonical = new StringBuilder().Append(StationDecisionDataSchemaInfo.SchemaVersion).Append('\n').Append(GeneratorVersion).Append('\n')
                .Append("mobility|").Append(DecisionMobilityProfiles.ModelVersion).Append('\n');
            foreach (var type in descriptors)
            {
                canonical.Append("type|").Append(type.id).Append('|').Append(type.kind).Append('\n');
                foreach (var field in type.fields) canonical.Append("field|").Append(field.name).Append('|').Append(field.type).Append('|').Append(field.defaultValue).Append('\n');
                foreach (var value in type.enumValues) canonical.Append("enum|").Append(value).Append('\n');
            }
            foreach (var file in files) canonical.Append("file|").Append(file.path).Append('|').Append(file.format).Append('|').Append(string.Join(",", file.rootTypes)).Append('|').Append(file.condition).Append('\n');
            foreach (var source in sources) canonical.Append("source|").Append(source.path).Append('|').Append(source.sha256).Append('\n');
            string contract = canonical.ToString();
            return new Document { schemaVersion = StationDecisionDataSchemaInfo.SchemaVersion, generatorVersion = GeneratorVersion, mobilityModelVersion = DecisionMobilityProfiles.ModelVersion,
                contractFingerprint = StationDecisionDataSchemaInfo.ComputeFingerprint(contract), canonicalContract = contract, language = "fr",
                htmlFile = StationDecisionDataSchemaInfo.HtmlFileName, files = files, types = descriptors.ToArray(), sources = sources };
        }
        static bool IsDto(Type type) => !type.IsGenericTypeDefinition && !type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false) &&
            !typeof(UnityEngine.Object).IsAssignableFrom(type) &&
            (type.IsEnum || type.IsDefined(typeof(SerializableAttribute), false));
        static void Collect(Type type, HashSet<Type> types)
        {
            if (type.IsArray) { Collect(type.GetElementType(), types); return; }
            if (type.IsGenericType) { foreach (var argument in type.GetGenericArguments()) Collect(argument, types); return; }
            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) || typeof(UnityEngine.Object).IsAssignableFrom(type)) return;
            if (!IsDto(type) || type.Namespace != NS && type != typeof(Vector3) && type != typeof(Quaternion) && type.Namespace != "StationFlow.TrainStation.RailOperations") return;
            if (!types.Add(type) || type.IsEnum) return;
            foreach (var field in SerializableFields(type)) Collect(field.FieldType, types);
        }
        static FieldInfo[] SerializableFields(Type type) => type.GetFields(Fields).Where(f => !f.IsStatic && !f.IsNotSerialized && !f.IsInitOnly &&
            (f.IsPublic || f.IsDefined(typeof(SerializeField), true) || f.IsDefined(typeof(SerializeReference), true))).OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();
        static TypeContract DescribeType(Type type) => new TypeContract { id = TypeId(type), name = type.DeclaringType != null ? type.DeclaringType.Name + "." + type.Name : type.Name,
            kind = type.IsEnum ? "Énumération" : "Objet sérialisé", fields = type.IsEnum ? Array.Empty<FieldContract>() : SerializableFields(type).Select(f => {
                var result = Field(f.Name, TypeName(f.FieldType));
                if (type == typeof(StationDecisionCaptureOptions))
                {
                    object value = f.GetValue(new StationDecisionCaptureOptions());
                    result.defaultValue = value is Array array ? "[" + string.Join(",", array.Cast<object>().Select(x => Convert.ToString(x, CultureInfo.InvariantCulture))) + "]" :
                        value is bool flag ? (flag ? "true" : "false") : Convert.ToString(value, CultureInfo.InvariantCulture);
                }
                return result;
            }).ToArray(),
            enumValues = type.IsEnum ? Enum.GetNames(type).Select(name => name + " = " + Convert.ToInt64(Enum.Parse(type, name), CultureInfo.InvariantCulture)).ToArray() : Array.Empty<string>() };
        static string TypeId(Type type) => type.FullName.Replace('+', '.');
        static string TypeName(Type type)
        {
            if (type.IsArray) return TypeName(type.GetElementType()) + "[]";
            if (type.IsGenericType) return type.Name.Split((char)96)[0] + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
            if (type == typeof(float)) return "float32"; if (type == typeof(double)) return "float64";
            if (type == typeof(int)) return "int32"; if (type == typeof(long)) return "int64";
            if (type == typeof(bool)) return "bool"; if (type == typeof(string)) return "string";
            return TypeId(type);
        }
        static Type Nested(Type owner, string name) => owner.GetNestedType(name, BindingFlags.NonPublic | BindingFlags.Public) ?? throw new InvalidOperationException("Missing DTO " + owner.Name + "." + name);
        static TypeContract[] NormalizedTypes() => new[] {
            new TypeContract { id="SkeletonDefinitionRecord", name="SkeletonDefinitionRecord", kind="Objet physique JSONL de squelette", enumValues=Array.Empty<string>(), fields=new[] { Field("schema","string"), Field("rigId","string"), Field("jointNames","string[]"), Field("parentIndices","int32[]"), Field("transformPaths","string[]"), Field("coordinateSystem","string"), Field("units","string"), Field("positionEncoding","string") } },
            new TypeContract { id="SkeletonBindingRecord", name="SkeletonBindingRecord", kind="Objet physique JSONL de liaison voyageur/rig", enumValues=Array.Empty<string>(), fields=new[] { Field("bindingId","int32"), Field("rigId","string"), Field("actorSlotId","string"), Field("avatarName","string"), Field("effectiveFromSyncSampleId","int64") } },
            new TypeContract { id="SkeletonBinaryHeader", name="SkeletonBinaryHeader", kind="En-t\u00eate physique binaire de 16 octets ; little-endian", enumValues=Array.Empty<string>(), fields=new[] { Field("magic","ASCII[8]"), Field("version","uint32"), Field("headerBytes","uint32") } },
            new TypeContract { id="SkeletonBinaryPoseRecord", name="SkeletonBinaryPoseRecord", kind="Record physique binaire ; champs dans cet ordre exact", enumValues=Array.Empty<string>(), fields=new[] { Field("byteLength","uint32"), Field("syncSampleId","int64"), Field("sampleId","int32"), Field("bindingId","int32"), Field("jointCount","int32"), Field("maskByteCount","int32"), Field("time","float32"), Field("validMask","uint8[]"), Field("jointPositionsWorld","float32[]") } },
            new TypeContract { id="SkeletonIndexRecord", name="SkeletonIndexRecord", kind="Objet physique JSONL d'index binaire", enumValues=Array.Empty<string>(), fields=new[] { Field("observationIndex","int64"), Field("syncSampleId","int64"), Field("byteOffset","int64"), Field("bindingId","int32") } },
            new TypeContract { id="SkeletonPoseLogical", name="SkeletonPoseLogical", kind="Objet logique retourn\u00e9 par le lecteur ; pas un JSONL brut du jeu", enumValues=Array.Empty<string>(), fields=new[] { Field("agentId","string"), Field("captureStamp", "StationFlow.TrainStation.DecisionLab.StationDecisionCaptureStamp"), Field("sampleId","int32"), Field("time","float32"), Field("bindingId","int32"), Field("rigId","string"), Field("jointPositionsWorld","float32[][]"), Field("valid","bool[]") } },
            new TypeContract { id="NormalizationHeader", name="NormalizationHeader", kind="Objet physique normalis\u00e9", enumValues=Array.Empty<string>(), fields=new[] { Field("schema","string"), Field("captureSchema","string"), Field("storageLayout","string"), Field("recordEncoding","string"), Field("runId","string"), Field("clockFile","string"), Field("definitionsFile","string"), Field("rostersFile","string"), Field("checkpointIndexFile","string"), Field("checkpointInterval","int32"), Field("profileJourneyAliases","JSON object<string,string>") } },
            new TypeContract { id="RecordDefinition", name="RecordDefinition", kind="Objet physique normalis\u00e9", enumValues=Array.Empty<string>(), fields=new[] { Field("definitionId","int64"), Field("type","string"), Field("stampField","string"), Field("defaults","JSON object") } },
            new TypeContract { id="PolicyDefinition", name="PolicyDefinition", kind="Objet physique normalis\u00e9", enumValues=Array.Empty<string>(), fields=new[] { Field("policyId","int64"), Field("policyParameters","JSON object") } },
            new TypeContract { id="ClockRecord", name="ClockRecord", kind="Objet physique normalis\u00e9", enumValues=Array.Empty<string>(), fields=new[] { Field("definitionId","int64"), Field("kind","string"), Field("stamp","StationFlow.TrainStation.DecisionLab.StationDecisionCaptureStamp") } },
            new TypeContract { id="ReferenceRecord", name="ReferenceRecord", kind="Objet physique normalis\u00e9 ; membres suppl\u00e9mentaires = surcharges du mod\u00e8le", enumValues=Array.Empty<string>(), fields=new[] { Field("definitionId","int64"), Field("syncSampleId","int64?"), Field("rosterId","int64?"), Field("profileRef","string?"), Field("servicesFile","string?") } },
            new TypeContract { id="RosterRevision", name="RosterRevision", kind="Objet physique normalis\u00e9", enumValues=Array.Empty<string>(), fields=new[] { Field("rosterId","int64"), Field("agentIds","string[]") } },
            new TypeContract { id="NormalizedProfile", name="NormalizedProfile", kind="Objet physique normalis\u00e9", enumValues=Array.Empty<string>(), fields=new[] { Field("definitionId","int64"), Field("syncSampleId","int64"), Field("profile","JSON object; policyId replaces policyParameters"), Field("journey","JSON object; aliases omitted when equal") } },
            new TypeContract { id="NormalizedTrajectory", name="NormalizedTrajectory", kind="Objet physique normalis\u00e9", enumValues=Array.Empty<string>(), fields=new[] { Field("syncSampleId","int64"), Field("stateId","int64"), Field("time","float32"), Field("sampleId","int32"), Field("position","UnityEngine.Vector3"), Field("rotation","UnityEngine.Quaternion"), Field("velocity","UnityEngine.Vector3"), Field("budget","float32"), Field("seatedSecondsRemaining","float32") } },
            new TypeContract { id="NormalizedAnimation", name="NormalizedAnimation", kind="Objet physique normalis\u00e9", enumValues=Array.Empty<string>(), fields=new[] { Field("syncSampleId","int64"), Field("stateId","int64"), Field("time","float32"), Field("sampleId","int32"), Field("normalizedTime","float32"), Field("nextNormalizedTime","float32"), Field("transitionProgress","float32"), Field("nativePlanarSpeed","float32"), Field("locomotionStrideWeight","float32") } },
            new TypeContract { id="AgentStateRevision", name="AgentStateRevision", kind="Objet physique normalis\u00e9", enumValues=Array.Empty<string>(), fields=new[] { Field("stream","string"), Field("stateId","int64"), Field("fromSyncSampleId","int64"), Field("observationIndex","int64"), Field("checkpoint","bool"), Field("values","JSON object?"), Field("changes","JSON object?") } },
            new TypeContract { id="StateCheckpointIndex", name="StateCheckpointIndex", kind="Objet physique normalis\u00e9", enumValues=Array.Empty<string>(), fields=new[] { Field("agentId","string"), Field("stream","string"), Field("stateId","int64"), Field("fromSyncSampleId","int64"), Field("observationIndex","int64"), Field("path","string"), Field("byteOffset","int64"), Field("densePath","string"), Field("denseByteOffset","int64") } },
            new TypeContract { id="CameraStateRevision", name="CameraStateRevision", kind="Objet physique normalis\u00e9", enumValues=Array.Empty<string>(), fields=new[] { Field("cameraStateId","int64"), Field("effectiveFromSyncSampleId","int64"), Field("camera","StationFlow.TrainStation.DecisionLab.StationDecisionCaptureSession.FrameMatrices") } },
            new TypeContract { id="VideoFrameRecord", name="VideoFrameRecord", kind="Objet physique normalis\u00e9", enumValues=Array.Empty<string>(), fields=new[] { Field("segment","int32"), Field("videoFrame","int32"), Field("videoTimeSeconds","float64"), Field("requestId","int64"), Field("syncSampleId","int64"), Field("cameraStateId","int64"), Field("captureIntervalRealSeconds","float64|null"), Field("captureFpsInstantaneous","float64|null"), Field("captureFpsRolling1s","float64") } },
        };

        static FileContract[] FileMappings()
        {
            string C(string name) => TypeId(Nested(typeof(StationDecisionCaptureSession), name));
            return new[] {
                Map("manifest.json", "JSON", "Toujours", "Capture v4, layout per_agent.v3, options, format de r\u00e9f\u00e9rences, r\u00e9sultats et empreinte du contrat.", C("Manifest")),
                Map("normalization.json", "JSON", "Toujours", "En-t\u00eate du codec et alias profil/voyage.", "NormalizationHeader"),
                Map("record-definitions.jsonl", "JSONL", "Toujours ; peut \u00eatre vide", "Mod\u00e8les par type et param\u00e8tres de politique partag\u00e9s.", "RecordDefinition", "PolicyDefinition"),
                Map("rosters.jsonl", "JSONL", "Toujours ; peut \u00eatre vide", "Une liste ordonn\u00e9e par r\u00e9vision, commune \u00e0 tous les flux.", "RosterRevision"),
                Map("state-checkpoints.jsonl", "JSONL", "Toujours ; peut \u00eatre vide", "Index des checkpoints ; offsets en octets UTF-8.", "StateCheckpointIndex"),
                Map("scenario.json", "JSON", "Toujours", "Configuration commune v6 ; servicesFile r\u00e9f\u00e9rence timetable.json si les horaires sont actifs.", "ReferenceRecord"),
                Map("sync.jsonl", "JSONL", "Toujours", "Clock canonique compl\u00e8te une fois ; autres lignes par r\u00e9f\u00e9rence.", "ClockRecord", "ReferenceRecord"),
                Map("timetable.json", "JSON", "Option timetable", "Horaire initial canonique ; lecture r\u00e9solue fournit les services au sc\u00e9nario.", "ReferenceRecord"),
                Map("train-states.jsonl", "JSONL", "Option timetable", "Initialisation et changements d\u2019\u00e9tat ou d\u2019horaire ; aucun taux fixe annonc\u00e9.", "ReferenceRecord"),
                Map("events.jsonl", "JSONL", "Option events", "\u00c9v\u00e9nements de station ; seuls les champs diff\u00e9rents du mod\u00e8le sont pr\u00e9sents.", "ReferenceRecord"),
                Map("agents/index.json", "JSON", "Une option de donn\u00e9es voyageurs", "Identit\u00e9s logiques exactes et dossiers relatifs.", NS + ".StationDecisionAgentsIndex"),
                Map("agents/<safeDir>/profile.json", "JSON", "Option profiles", "Profil canonique, politique par r\u00e9f\u00e9rence et voyage sans attributs en double.", "NormalizedProfile"),
                Map("agents/<safeDir>/state-changes.jsonl", "JSONL", "Premi\u00e8re observation trajectoire ou animation", "\u00c9tat complet initial, deltas exacts et checkpoints chaque 256 observations du flux.", "AgentStateRevision"),
                Map("agents/<safeDir>/trajectories.jsonl", "JSONL", "Option trajectories", "Toutes les observations de pose/mouvement ; \u00e9tat associ\u00e9 par stateId.", "NormalizedTrajectory"),
                Map("agents/<safeDir>/animations.jsonl", "JSONL", "Option animations", "Toutes les observations de phase, transition et vitesse native.", "NormalizedAnimation"),
                Map("agents/<safeDir>/decisions.jsonl", "JSONL", "Option decisions", "Contexte et choix ; mod\u00e8les et horloge par r\u00e9f\u00e9rence.", "ReferenceRecord"),
                Map("agents/<safeDir>/events.jsonl", "JSONL", "Option events", "\u00c9v\u00e9nements individuels, profil canonique r\u00e9f\u00e9renc\u00e9 seulement s\u2019il est disponible.", "ReferenceRecord"),
                Map("skeleton-definitions.jsonl", "JSONL UTF-8 brut", "Option skeletons ; peut \u00eatre vide", "Une d\u00e9finition par rig : ordre des joints, parents, chemins, unit\u00e9 et rep\u00e8re.", "SkeletonDefinitionRecord"),
                Map("agents/<safeDir>/skeleton-bindings.jsonl", "JSONL UTF-8 brut", "Option skeletons ; premi\u00e8re pose observ\u00e9e", "Liaisons du voyageur logique au slot, a l'avatar et au rig ; nouvelle revision en cas de changement.", "SkeletonBindingRecord"),
                Map("agents/<safeDir>/skeleton-poses.bin", "Binaire float32 little-endian", "Option skeletons ; premi\u00e8re pose observ\u00e9e", "XYZ monde de tous les joints Humanoid ; masque de validit\u00e9 et reference a la clock, sans quaternion ni projection 2D.", "SkeletonBinaryHeader", "SkeletonBinaryPoseRecord", "SkeletonPoseLogical"),
                Map("agents/<safeDir>/skeleton-index.jsonl", "JSONL UTF-8 brut", "Option skeletons ; premi\u00e8re pose observ\u00e9e", "Index aux poses 1,257,513... ; offsets binaires exacts, premier record a l'octet16.", "SkeletonIndexRecord"),
                Map("video/<cameraId>/calibration.json", "JSON", "Option video", "Calibration initiale et conventions de projection.", C("Calibration")),
                Map("video/<cameraId>/camera-states.jsonl", "JSONL", "Premi\u00e8re image ; puis changement exact", "R\u00e9visions effectives des matrices/param\u00e8tres, publi\u00e9es avant les images concern\u00e9es.", "CameraStateRevision"),
                Map("video/<cameraId>/frames.jsonl", "JSONL", "Option video", "Frame AVI, clock et cam\u00e9ra par r\u00e9f\u00e9rence ; trois champs FPS variables conserv\u00e9s.", "VideoFrameRecord"),
                Map("video/<cameraId>/segment-NNNN.avi", "AVI/MJPEG", "Option video", "Images r\u00e9ellement encod\u00e9es, sans audio. Cadence nominale de lecture seulement."),
                Map("README.txt", "Texte UTF-8", "Toujours", "Guide pratique de collecte et de synchronisation."),
                Map("TrainStationData/DataSchema.fr.html", "HTML autonome", "Toujours", "Contrat fran\u00e7ais actuel, champs physiques et objets logiques."),
                Map("TrainStationData/data-schema.json", "JSON", "Toujours", "M\u00eame contrat : fichiers, champs, types et empreintes de sources."),
                Map("TrainStationData/read_capture.py", "Python 3.10+", "Toujours", "Lecteur du nouveau format uniquement ; v\u00e9rification et vue d\u2019analyse d\u00e9velopp\u00e9e."),
                Map("TrainStationData/LireDonnees.fr.txt", "Texte UTF-8", "Toujours", "Instructions du lecteur, d\u00e9pendances standard et commandes."),
            };
        }

        static FileContract Map(string path, string format, string condition, string description, params string[] roots) =>
            new FileContract { path = path, format = format, condition = condition, description = description, rootTypes = roots };
        static FieldContract Field(string name, string type) => new FieldContract { name = name, type = type, description = Description(name, type) };
        static SourceContract[] SourceFiles()
        {
            var paths = new List<string>();
            foreach (string folder in new[] { "Assets/TrainStation/DecisionLab/Core", "Assets/TrainStation/DecisionLab/Runtime", "Assets/TrainStation/RailOperations/Runtime",
                "Assets/TrainStation/Social/Runtime", "Assets/HumanAnimation/Runtime", "Assets/TrainStation/Cameras/Runtime" })
                if (Directory.Exists(folder)) paths.AddRange(Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories));
            paths.Add("Assets/TrainStation/DecisionLab/Editor/StationDecisionDataSchemaDocumentation.cs");
            if (File.Exists("Assets/TrainStation/Social/Runtime/StationActorMotion.cs")) paths.Add("Assets/TrainStation/Social/Runtime/StationActorMotion.cs");
            foreach (string support in new[] { Folder + "/read_capture.py", Folder + "/LireDonnees.fr.txt" })
                if (File.Exists(support)) paths.Add(support);
            return paths.Select(p => p.Replace('\\', '/')).Distinct().OrderBy(p => p, StringComparer.Ordinal).Select(p => new SourceContract {
                path = p, sha256 = StationDecisionDataSchemaInfo.ComputeFingerprint(File.ReadAllText(p, Encoding.UTF8).Replace("\r\n", "\n")) }).ToArray();
        }

        static readonly Dictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["skeletons"] = "Option independante : positions 3D mondiales des joints Humanoid observes ; pas de projection image.",
            ["skeletonDefinitionsFile"] = "skeleton-definitions.jsonl si skeletons est actif ; chaine vide sinon.",
            ["skeletonPoseFileName"] = "skeleton-poses.bin dans le dossier d'un voyageur observe ; chaine vide si option inactive.",
            ["skeletonBindingsFileName"] = "skeleton-bindings.jsonl dans chaque dossier possedant des poses.",
            ["skeletonIndexFileName"] = "skeleton-index.jsonl : index tous les 256 records binaires.",
            ["skeletonPoseFormat"] = "stationflow.skeleton_pose_binary.v1 ; XYZ float32 little-endian et masque de validit\u00e9.",
            ["rigId"] = "rig- suivi du SHA-256 canonique de la definition ; reutilise entre avatars de meme structure.",
            ["jointNames"] = "Noms HumanBodyBones dans l'ordre enumere 0..LastBone-1, soit55 joints dans cette version.",
            ["parentIndices"] = "Indice du plus proche ancetre mappe ; -1 si absent ou racine. L'ordre numerique des indices n'est pas un ordre topologique.",
            ["transformPaths"] = "Chemins relatifs a l'Animator ; chaine vide si le joint n'est pas mappe.",
            ["coordinateSystem"] = "UnityWorld : XYZ monde Unity, meme rep\u00e8re que les trajectoires et extrinseques camera.",
            ["units"] = "metres pour les positions de squelette.",
            ["positionEncoding"] = "float32 natif Unity, sans quantification supplementaire.",
            ["bindingId"] = "Revision locale au voyageur, a partir de1 ; effective a son premier syncSampleId.",
            ["jointCount"] = "Nombre de joints dans la definition du rig ;55 pour le schema Humanoid actuel.",
            ["maskByteCount"] = "ceil(jointCount/8) ; bits LSB-first, bit0 du premier octet pour le joint0.",
            ["validMask"] = "Bit1 : joint reel mappe et position finie ; bit0 : XYZ nul, donnees indisponibles. Bits inutilises nuls.",
            ["jointPositionsWorld"] = "Tableau XYZ monde ordonne selon jointNames ; plat dans le binaire, [joint][xyz] dans la vue logique.",
            ["valid"] = "Validit\u00e9 de mesure du joint, distincte de sa visibilite/occlusion dans une camera.",
            ["magic"] = "Huit octets ASCII SFLPOSE1.",
            ["version"] = "Version de l'en-tete binaire, entier non signe1.",
            ["headerBytes"] = "16 :8 octets magic +uint32 version +uint32 headerBytes.",
            ["byteLength"] = "Taille totale du record, champ longueur inclus :32 +maskByteCount +12*jointCount.",
            ["recordEncoding"] = "references-and-state-revisions.v1 ; seul le format de capture v4 est pris en charge.",
            ["normalizationFile"] = "normalization.json : param\u00e8tres du codec et alias de profil.",
            ["definitionId"] = "Identifiant du mod\u00e8le de record-definitions.jsonl ; les champs absents prennent ses valeurs par d\u00e9faut.",
            ["rosterId"] = "R\u00e9vision de la liste ordonn\u00e9e d\u2019agents dans rosters.jsonl.",
            ["stateId"] = "R\u00e9vision propre \u00e0 cet agent et ce flux ; \u00e9tat initial et changements exacts.",
            ["cameraStateId"] = "R\u00e9vision effective de cam\u00e9ra, distincte de la version du mod\u00e8le pinhole.",
            ["checkpoint"] = "true : values est un \u00e9tat complet ; false : changes contient les valeurs modifi\u00e9es.",
            ["fromSyncSampleId"] = "Premier instant d\u2019effet de cette r\u00e9vision, li\u00e9 \u00e0 la clock originale.",
            ["effectiveFromSyncSampleId"] = "Instant d\u2019effet de la r\u00e9vision cam\u00e9ra ; enregistr\u00e9 avant sa premi\u00e8re image.",
            ["observationIndex"] = "Compteur \u00e0 partir de 1 dans le flux individuel ; checkpoints aux observations 1,257,513\u2026",
            ["byteOffset"] = "Offset z\u00e9ro en octets dans le fichier d\u00e9sign\u00e9 : UTF-8 pour les flux texte ou octets physiques pour le squelette binaire.",
            ["denseByteOffset"] = "Offset z\u00e9ro en octets UTF-8 du premier \u00e9chantillon dense correspondant.",
            ["checkpointInterval"] = "Nombre d\u2019observations entre checkpoints complets : 256.",
            ["railStateCaptureMode"] = "initial-and-state-changes : \u00e9tat initial puis \u00e9v\u00e9nements de changement ; aucune fr\u00e9quence fixe.",
            ["policyId"] = "R\u00e9f\u00e9rence aux param\u00e8tres de politique partag\u00e9s, conserv\u00e9s une fois.",
            ["profileRef"] = "profile.json#profile uniquement lorsque le profil canonique de cet agent est disponible.",
            ["servicesFile"] = "timetable.json, source canonique des horaires actifs.",

            ["captureStamp"] = "Horodatage partagé de l’observation ; jointure avec sync.jsonl par runId et syncSampleId.",
            ["stamp"] = "Horodatage exact du rendu ou du marqueur, même contrat que captureStamp.",
            ["runId"] = "Identifiant du lancement ou de la collecte selon le type parent ; celui du captureStamp est la clé de collecte.",
            ["syncSampleId"] = "Identifiant croissant d’horloge ; clé de jointure avec runId.",
            ["unityFrame"] = "Numéro de frame moteur ; plusieurs observations peuvent partager cette frame.",
            ["simulationSeconds"] = "Secondes simulées depuis le démarrage du monde ; gelées en pause.",
            ["stationClockSeconds"] = "Heure de gare, en secondes depuis minuit de simulationDate.",
            ["realElapsedSeconds"] = "Secondes réelles monotones depuis Start, pause comprise.",
            ["utc"] = "Horodatage absolu UTC ; employer l’horloge monotone pour les intervalles.",
            ["timeScale"] = "Vitesse temporelle du moteur à cet instant : pendant une collecte, 1 en marche et 0 en pause ; les accélérations ×4/×8 sont verrouillées jusqu’à l’arrêt de la collecte.", ["paused"] = "État de pause au moment de l’observation.",
            ["time"] = "Secondes écoulées dans la simulation ; les autres horloges figurent dans captureStamp.",
            ["agentId"] = "Identité logique exacte et sensible à la casse d’un voyage unique, conservée dans agents/index.json ; distincte du slot visuel réutilisé.",
            ["storageLayout"] = "per_agent.v3 : données individuelles sous agents/<safeDir>/ et données communes à la racine.",
            ["agentsDirectory"] = "agents si au moins une option de données voyageurs est active ; chaîne vide sinon.",
            ["agentIndexFile"] = "agents/index.json si au moins une option de données voyageurs est active ; chaîne vide sinon.",
            ["identityPolicy"] = "Règle d’identité de l’index : un dossier par agentId logique de voyage, jamais un regroupement par actorSlotId.",
            ["profile"] = "Profil individuel dans agents/<safeDir>/profile.json. Dans un événement, un objet profile aux valeurs par défaut avec agentId vide signifie absence de profil ; le profil du fichier individuel reste la référence.",
            ["directory"] = "Chemin relatif à la racine de collecte ; pour l’index d’agents, agents/agent-<préfixeASCII56>-<SHA256-ID16>.",
            ["journeyId"] = "Identifiant immuable d’un voyage pour une date, un service et un sens.",
            ["actorSlotId"] = "Slot du pool visuel réutilisable ; plusieurs voyages peuvent l’occuper successivement et doivent garder leurs dossiers agentId distincts.",
            ["serviceId"] = "Identifiant du service ferroviaire ; jointure avec les horaires.",
            ["groupId"] = "Identifiant du groupe ; chaîne vide pour un voyageur seul.",
            ["representedPassengers"] = "Poids de passagers représentés ; ne multiplie pas la densité physique visible.",
            ["position"] = "Position monde Unity en mètres ; le parent indique avatar, train ou caméra.",
            ["rotation"] = "Orientation monde ; Quaternion en x,y,z,w ou angles Euler pour un Vector3.",
            ["rotationQuaternion"] = "Quaternion de caméra, ordre x,y,z,w.",
            ["rotationEuler"] = "Angles Euler Unity en degrés, rotations appliquées Z puis X puis Y.",
            ["velocity"] = "Vitesse native observée, en mètres par seconde de simulation.",
            ["navigationTarget"] = "Destination native actuelle ; valide si hasNavigationTarget est true.",
            ["targetPoiPosition"] = "Position du point d’intérêt visé ; distincte du point précis de file ou de siège.",
            ["targetPoi"] = "Identifiant du point d’intérêt, jointure avec les POI du scénario.",
            ["activity"] = "Intention fonctionnelle, distincte de l’état d’exécution.",
            ["execution"] = "État d’exécution : déplacement, file, service, siège, etc.",
            ["sampleId"] = "Numéro local d’échantillon ; utiliser syncSampleId pour la jointure entre flux.",
            ["segment"] = "Numéro de segment à partir de 1 ; correspond à segment-NNNN.avi.",
            ["videoFrame"] = "Index d’image à partir de 0 dans le segment indiqué.",
            ["videoTimeSeconds"] = "Temps de lecture du segment : videoFrame / nominalPlaybackFps, distinct des instants réels de capture et du temps simulé.",
            ["captureCadence"] = "each_unity_frame : une tentative d’admission à chaque frame Unity exécutée ; le rendu et le snapshot vidéo ne sont lancés qu’après réservation du lot. La cadence réellement sauvegardée reste variable.",
            ["nominalPlaybackFps"] = "Cadence nominale constante du conteneur AVI ; définit la vitesse de lecture, pas le nombre réel d’images capturées par seconde.",
            ["readbackSlotCount"] = "Nombre effectif de places bornées par caméra, de 1 à 4. Chaque place conserve sa cible, sa demande et ses métadonnées jusqu’à l’issue finale ; ce nombre n’est pas un FPS cible.",
            ["commonBatchAdmission"] = "true : réserver ensemble les ressources de toutes les caméras sélectionnées avant de forcer le snapshot voyageurs. Si une caméra est indisponible, refuser le lot entier avant ce travail ; false permet les expériences d’admission indépendante.",
            ["renderProfile"] = "Profil de rendu effectif des demandes de capture : full, ao-half, ao-off ou balanced. Les changements temporaires de SSAO et d’ombres sont restaurés après la demande ; consulter la section Pipeline vidéo pour leurs paramètres.",
            ["actualVideoRateDefinition"] = "Définition des cadences réelles ; les horodatages des images sauvegardées font foi.",
            ["videoFps"] = "Cadence nominale de lecture AVI uniquement ; aucune sélection ni cadence fixe de capture.",
            ["fps"] = "Cadence nominale de lecture AVI dans les métadonnées vidéo ; pas une mesure du rythme réel de capture.",
            ["captureIntervalRealSeconds"] = "Écart monotone entre les stamps réels de cette image sauvegardée et de la précédente image de la même caméra ; null pour la première image.",
            ["captureFpsInstantaneous"] = "1 / captureIntervalRealSeconds pour deux images sauvegardées successives de la même caméra ; null pour la première image.",
            ["captureFpsRolling1s"] = "Nombre d’images sauvegardées de cette caméra dont le stamp réel appartient à ]t − 1 s, t], divisé par une seconde fixe ; t est le stamp de cette image.",
            ["measuredFrameRates"] = "Charge utile typée des lignes sync.jsonl de kind measured_frame_rates, reliée à l’horloge par stamp.",
            ["windowSeconds"] = "Largeur fixe de la fenêtre glissante de mesure, égale à une seconde réelle, y compris au démarrage.",
            ["gameFpsRolling1s"] = "Nombre de frames Unity réellement exécutées dans la dernière seconde monotone, divisé par une seconde fixe.",
            ["videoFpsRolling1s"] = "Au moment du relevé, nombre de stamps réels dans ]t − 1 s, t] parmi les framesWritten premières lignes finalisées de cette caméra, divisé par une seconde fixe ; les écritures retardées ne modifient pas ce relevé.",
            ["framesWritten"] = "Total cumulé d’images effectivement sauvegardées pour cette caméra, distinct des demandes de rendu.",
            ["requestsDropped"] = "Total cumulé de demandes de cette caméra non enregistrées ; ces demandes ne créent aucune image artificielle.",
            ["requestId"] = "Numéro de demande par caméra ; les écritures suivent l’ordre de rendu, indépendamment de l’ordre de retour des lectures GPU. Des trous indiquent des demandes non encodées.",
            ["camera"] = "Pose et matrices figées lors du rendu de cette image, conservées avec son stamp pendant le traitement asynchrone ; font foi si la caméra se déplace.",
            ["intrinsicMatrix"] = "K intrinsèque 3×3, ligne-major : row*3+column.",
            ["inverseIntrinsicMatrix"] = "Inverse de K, même ordre ligne-major 3×3.",
            ["worldToCvCamera"] = "Extrinsèque 4×4 : monde Unity vers caméra CV (+X droite, +Y bas, +Z avant).",
            ["worldToCvCameraMatrix"] = "Extrinsèque CV, tableau 4×4 colonne-major.",
            ["cvCameraToWorld"] = "Inverse de worldToCvCamera ; caméra CV vers monde Unity.",
            ["cvCameraToWorldMatrix"] = "Inverse de l’extrinsèque CV, 4×4 colonne-major.",
            ["projection"] = "Projection CPU Unity, à utiliser pour les calculs géométriques.",
            ["projectionMatrix"] = "Projection CPU Unity 4×4, colonne-major.",
            ["gpuProjection"] = "Projection adaptée à l’API graphique et à la cible de rendu.",
            ["gpuProjectionMatrix"] = "Projection GPU incluant les conventions de l’API et de la RenderTexture.",
            ["distortionCoefficients"] = "Cinq coefficients nuls ; rendu pinhole sans post-traitement.",
            ["capturedPostProcessing"] = "false : post-traitement désactivé temporairement pour le rendu de collecte. Les Renderer Features, notamment SSAO, sont indépendantes de ce drapeau et dépendent du profil de rendu utilisé.",
            ["readbackRowsFlipped"] = "Lignes inversées ou non après comparaison à une lecture de référence.",
            ["walkSpeed"] = "Allure initiale effective en m/s, plafonnée par le membre le plus lent du groupe.",
            ["fastWalkSpeed"] = "Allure initiale pressée, compatible avec la mobilité et le groupe.",
            ["preferredWalkSpeed"] = "Allure individuelle issue du profil synthétique d’âge et de santé, en m/s.",
            ["preferredFastWalkSpeed"] = "Allure individuelle pressée compatible avec le profil, en m/s.",
            ["usingGroupPace"] = "true tant que l’avatar utilise l’allure commune du groupe.",
            ["effectiveWalkSpeed"] = "Allure de marche actuelle ; peut redevenir individuelle après séparation.",
            ["requestedNavigationSpeed"] = "Plafond natif NavMeshAgent courant, distinct de la vitesse mesurée.",
            ["ageYears"] = "Âge synthétique de 18 à 85 ans, compatible avec les rigs adultes existants.",
            ["condition"] = "Classe synthétique de mobilité, sans mesure médicale réelle.",
            ["groupPaceFactor"] = "Coût de coordination appliqué à l’allure du membre le plus lent.",
            ["normalizedTime"] = "Phase d’animation normalisée ; la partie entière peut compter les boucles.",
            ["playbackSpeed"] = "Cadence du lecteur. En marche adaptative, la longueur du pas dépend aussi de locomotionStrideWeight ; cette cadence seule ne donne pas la vitesse de déplacement.",
            ["locomotionStrideWeight"] = "Valeur réelle du paramètre Animator StationWalkStride : 1 au pas complet, inférieur à 1 pour les petits pas. Peut rester mémorisée hors marche, notamment pendant une transition sortante. Échantillon dense ; ne multiplie ni la vitesse NavMesh ni la position racine.",
            ["adaptiveLocomotion"] = "Présentation adaptative disponible et activée pour cet avatar ; vérifier également walkPose et motionId.",
            ["avoidancePredictionSeconds"] = "Horizon global effectif de NavMesh.avoidancePredictionTime au début de la collecte, en secondes. Réglage du scénario, restauré à la sortie de la scène.",
            ["blendParameter"] = "Nom du paramètre float du BlendTree, vide pour un état à clip unique. Le clip principal sert de référence de cycle.",
            ["blendChildren"] = "Clips enfants du BlendTree avec noms, durées et seuils ; les poids intermédiaires interpolent ces enfants.",
            ["animationPaused"] = "Animation gelée au moment de l’observation.",
            ["stateLengthSeconds"] = "Durée de l’état d’animation observé, en secondes.",
            ["motionId"] = "Identifiant du mouvement dans animationCatalog, distinct de l’identité du voyage.",
            ["contractFingerprint"] = "SHA-256 du contrat canonique : fichiers, types, champs et sources.",
            ["schemaVersion"] = "Version du format du contrat.", ["seed"] = "Graine du lancement, conservée pour reproduction.",
            ["applicationProductName"] = "Nom du produit Unity exécuté (GareFlowDemo), distinct du projet source StationFlowLab.",
            ["applicationVersion"] = "Version du produit exécuté, par exemple 0.2.0 ; distincte de la version du format des données.",
            ["applicationBuildGuid"] = "Identifiant Unity de la construction exécutée, pour relier une capture à son binaire ; peut être vide dans l’éditeur et ne remplace pas runId.",
            ["dataWriterMode"] = "Mode effectif de sérialisation et d’écriture des données : background en production ; main pour les comparaisons de performance.",
            ["dataQueueMaxPendingCommands"] = "Limite des commandes admises et non terminées, commande en cours comprise.",
            ["dataQueueMaxRetainedBytes"] = "Budget maximal estimé des DTO retenus, en octets ; distinct de la RAM totale.",
            ["dataQueueHighWaterCommands"] = "Maximum observé des commandes admises simultanément et non terminées ; renseigné après Stop.",
            ["dataQueueHighWaterRetainedBytes"] = "Maximum observé du budget estimé retenu, y compris la réservation d’un lot en préparation ; renseigné après finalisation. Ce n’est pas une mesure de la RAM totale.",
            ["dataInitializationWaitRealSeconds"] = "Temps réel total passé à attendre les barrières de préparation des fichiers, avant la reprise de la simulation ; hors mesure FPS régulière.",
            ["dataCommandsAccepted"] = "Commandes FIFO acceptées, ouvertures et flush compris. Un lot de snapshot peut contenir plusieurs enregistrements ; ce nombre ne mesure pas le nombre de lignes.",
            ["dataCommandsCompleted"] = "Commandes terminées sans erreur ; doit égaler dataCommandsAccepted pour une fermeture complète.",
            ["dataRecordsAccepted"] = "Enregistrements logiques admis par les appels Write, y compris dans les lots ; distincts des commandes, lignes physiques et frames vidéo.",
            ["dataRecordsCompleted"] = "Enregistrements logiques traités sans erreur ; doit égaler dataRecordsAccepted pour une fermeture complète.",
            ["captureAdmissionPolicy"] = "Politique effective : réservation de la capacité des données et des caméras avant un lot vidéo. Le mode common est celui de production ; independent reste un diagnostic explicite.",
            ["dataEssentialHeadroomBytes"] = "Part du budget réservée aux flux obligatoires avant admission d’un lot vidéo : un quart de la limite par défaut, soit 16 Mio sur 64 Mio.",
            ["dataEssentialHeadroomCommands"] = "Nombre de places de commandes laissées disponibles pour les échantillons réguliers, événements, horloges et marqueurs : un quart de la limite par défaut.",
            ["optionalAdmissionBudgetBytes"] = "Plafond de budget disponible pour les lots vidéo optionnels après soustraction de la marge essentielle ; la place libre réelle dépend aussi des travaux déjà admis.",
            ["optionalAdmissionBudgetOverrideBytes"] = "Plafond optionnel de diagnostic explicitement demandé. Zéro en production ; ne modifie pas la limite totale de la file ni la préparation initiale.",
            ["captureBatchesRequested"] = "Nombre de demandes de lots vidéo. Pour une session complète : demandé = admis + refus données + refus vidéo.",
            ["captureBatchesAdmitted"] = "Lots vidéo ayant réservé la capacité nécessaire, puis publié leur snapshot optionnel. Les résultats caméra font foi pour les images effectivement finalisées.",
            ["captureBatchesRejectedData"] = "Lots non admis pour préserver la capacité des données et sa marge essentielle. Aucun snapshot optionnel ni rendu de ce lot n’est produit.",
            ["captureBatchesRejectedVideo"] = "Lots non admis faute de place côté caméra ; en mode common, toutes les caméras du lot sont refusées ensemble.",
            ["capturePressure"] = "Résumé typé des refus de capacité, au plus une fois par seconde réelle plus un solde final. Les refus ne sont pas une erreur d’écriture.",
            ["firstRejectedUnityFrame"] = "Première frame Unity refusée dans cette fenêtre de résumé.",
            ["lastRejectedUnityFrame"] = "Dernière frame Unity refusée dans cette fenêtre de résumé ; les refus ne sont pas nécessairement consécutifs.",
            ["firstRejectedRealElapsedSeconds"] = "Temps monotone de la première demande refusée dans la fenêtre, depuis Start.",
            ["lastRejectedRealElapsedSeconds"] = "Temps monotone de la dernière demande refusée dans la fenêtre, depuis Start.",
            ["dataDeniedBatches"] = "Nombre de lots refusés par la capacité des données dans cette fenêtre.",
            ["videoDeniedBatches"] = "Nombre de lots refusés par la capacité vidéo dans cette fenêtre.",
            ["partialVideoBatches"] = "Diagnostic independent uniquement : lots où au moins une caméra avait une place et au moins une autre non. Ce compteur peut coexister avec un refus ultérieur des données et ne s’ajoute pas aux refus complets de lots.",
            ["partialCameraDeniedRequests"] = "Diagnostic independent uniquement : demandes caméra refusées dans ces lots partiellement disponibles, agrégées sans marqueur par caméra et par frame.",
            ["maxEstimatedBatchBytes"] = "Plus grand budget estimé demandé par les lots refusés côté données dans cette fenêtre.",
            ["maxPendingDataBytes"] = "Plus grand budget retenu observé lors des refus de cette fenêtre ; ce n’est pas la RAM totale.",
            ["maxPendingDataCommands"] = "Plus grand nombre de commandes encore admises observé lors des refus de cette fenêtre.",
            ["stopRequestedRealElapsedSeconds"] = "Instant monotone de la demande de Stop depuis Start. L’observation peut reprendre après cette frontière sans ajouter de données à la session.",
            ["finalizationRealSeconds"] = "Durée réelle entre la demande de Stop et la fermeture des workers/fichiers. realDurationSeconds inclut cette durée et la préparation initiale.",
            ["fixedSeedReplay"] = "true pour une reprise explicite avec graine et date fixes."
        };
        static string Description(string name, string type)
        {
            if (Descriptions.TryGetValue(name, out string value)) return value;
            if (name.EndsWith("Seconds", StringComparison.Ordinal)) return "Durée ou instant en secondes ; voir le contexte du parent et les horloges.";
            if (name.EndsWith("Matrix", StringComparison.Ordinal) || name == "worldToCamera" || name == "cameraToWorld" || name == "inverseProjection")
                return "Matrice 4×4 colonne-major, sauf K et son inverse, 3×3 ligne-major.";
            return "Champ sérialisé « " + name + " », type exact « " + ShortName(type) + " ». Consulter l’objet parent et le fichier associé.";
        }

        static string Html(Document d)
        {
            var b = new StringBuilder(60000);
            b.Append("<!doctype html><html lang=\"fr\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta name=\"data-schema-version\" content=\"").Append(d.schemaVersion)
                .Append("\"><meta name=\"data-contract-fingerprint\" content=\"").Append(d.contractFingerprint).Append("\"><title>Contrat des données GareFlowDemo</title><style>")
                .Append("*{box-sizing:border-box}body{margin:0;background:#edf3f6;color:#183243;font:16px/1.6 Arial,sans-serif}main{max-width:1180px;margin:auto;padding:26px 20px}header{background:#15384b;color:white;padding:26px;border-radius:14px}h1{font-size:30px;line-height:1.25}h2{font-size:23px}section{background:white;padding:22px;margin:18px 0;border-radius:12px}.scroll{overflow:auto}table{width:100%;border-collapse:collapse;font-size:14px}th,td{text-align:left;vertical-align:top;padding:10px 8px;border-bottom:1px solid #dce6ec}th{background:#e4eef3}a{color:#156599}pre{background:#edf4f7;padding:14px;overflow:auto}code,pre{font:13px/1.55 Consolas,monospace}.note{border-left:4px solid #cf9227;padding:12px;background:#fff7e4}.schema-type{border:1px solid #dce6ec;margin:10px 0;border-radius:8px;overflow:auto}summary{padding:12px;cursor:pointer;font-weight:bold}.muted{color:#566d7a;font-size:13px}.hash{overflow-wrap:anywhere;font-family:monospace}svg{width:100%;min-width:760px;height:auto}input{padding:10px;max-width:100%;min-width:250px}button{padding:10px;margin:6px}footer{font-size:13px;color:#566d7a}@media print{details{break-inside:avoid}input,button{display:none}}</style></head><body><main><header><div>StationFlowLab · GareFlowDemo</div><h1>Contrat des données normalisées</h1><p>Capture <code>stationflow.station_capture.v4</code> · <code>per_agent.v3</code> · <code>references-and-state-revisions.v1</code>.</p><p>Un seul format actuel. Les objets complets sont reconstruits depuis les références ; aucun lecteur des captures antérieures n’est inclus.</p></header>");
            b.Append("<section id=\"structure\"><h2>Schéma des relations</h2><div class=\"scroll\"><svg viewBox=\"0 0 1000 230\" role=\"img\" aria-label=\"Relations entre horloge, états et observations\"><defs><marker id=\"arrow\" markerWidth=\"9\" markerHeight=\"9\" refX=\"8\" refY=\"3\" orient=\"auto\"><path d=\"M0,0 L0,6 L9,3 z\" fill=\"#39758f\"/></marker></defs><g fill=\"#e4eef3\" stroke=\"#6890a3\"><rect x=\"20\" y=\"20\" width=\"260\" height=\"65\" rx=\"8\"/><rect x=\"370\" y=\"20\" width=\"260\" height=\"65\" rx=\"8\"/><rect x=\"720\" y=\"20\" width=\"260\" height=\"65\" rx=\"8\"/><rect x=\"20\" y=\"145\" width=\"260\" height=\"65\" rx=\"8\"/><rect x=\"370\" y=\"145\" width=\"260\" height=\"65\" rx=\"8\"/><rect x=\"720\" y=\"145\" width=\"260\" height=\"65\" rx=\"8\"/></g><g font-family=\"Arial\" font-size=\"17\" fill=\"#183243\" text-anchor=\"middle\"><text x=\"150\" y=\"48\">Observations avatar</text><text x=\"150\" y=\"70\">syncSampleId + stateId</text><text x=\"500\" y=\"48\">Clock canonique</text><text x=\"500\" y=\"70\">temps réel / simulé / UTC</text><text x=\"850\" y=\"48\">Frames caméra</text><text x=\"850\" y=\"70\">syncSampleId + cameraStateId</text><text x=\"150\" y=\"174\">États avatar</text><text x=\"150\" y=\"197\">initial + deltas + checkpoints</text><text x=\"500\" y=\"174\">Définitions / rosters</text><text x=\"500\" y=\"197\">métadonnées partagées</text><text x=\"850\" y=\"174\">États caméra</text><text x=\"850\" y=\"197\">matrices et projection</text></g><g stroke=\"#39758f\" stroke-width=\"2\" marker-end=\"url(#arrow)\"><path d=\"M280,52 L365,52\"/><path d=\"M720,52 L635,52\"/><path d=\"M150,85 L150,140\"/><path d=\"M850,85 L850,140\"/><path d=\"M500,145 L500,90\"/></g></svg></div><p>La clé d’horloge est <code>(runId, syncSampleId)</code>. L’identité d’agent vient de <code>agents/index.json</code> et de son dossier ; l’identité caméra vient du dossier et du manifeste. Plusieurs syncSampleId peuvent appartenir à une même frame Unity, notamment autour d’une pause : ne pas les fusionner par unityFrame seul.</p><p>L’horloge conserve les valeurs UTC, temps réel monotone et temps simulé observés. Les autres lignes remplacent la copie du stamp complet par sa référence. Ne pas recalculer l’UTC exact depuis une seule origine Stopwatch et ne pas supposer temps réel = temps simulé à ×1.</p></section>");
            b.Append("<section id=\"files\"><h2>Fichiers physiques</h2><div class=\"scroll\"><table><thead><tr><th>Chemin</th><th>Condition</th><th>Contenu</th></tr></thead><tbody>");
            foreach (var f in d.files) b.Append("<tr><td><code>").Append(Escape(f.path)).Append("</code><br><small>").Append(Escape(f.format)).Append("</small></td><td>").Append(Escape(f.condition)).Append("</td><td>").Append(Escape(f.description)).Append("</td></tr>");
            b.Append("</tbody></table></div></section>");
            b.Append("<section id=\"normalization\"><h2>Résoudre les références</h2><p><code>record-definitions.jsonl</code> contient les valeurs par défaut d’un type logique, issues du DTO réel. Une ligne avec <code>definitionId</code> superpose ses membres aux defaults correspondants. <code>syncSampleId</code> remplace le champ stamp/captureStamp déclaré par <code>stampField</code>. Les champs non applicables ne sont donc pas répétés dans chaque événement.</p><p><code>rosterId</code> résout une liste ordonnée dans rosters.jsonl. Un roster est partagé entre événements et snapshots ; il reste disponible quand le toggle events est désactivé. Un profil contient une seule valeur pour les attributs égaux du voyage : les alias de normalization.json définissent les correspondances. Un attribut différent reste explicite dans journey. <code>policyId</code> résout les paramètres de politique partagés.</p><p>Les trajectoires et animations gardent une ligne par observation originale. Les champs d’état sont dans <code>state-changes.jsonl</code>, séparés par stream. La première observation puis chaque 256 observations produit un checkpoint complet ; les autres révisions stockent les valeurs modifiées exactement. La ligne dense référence la révision par stateId. Les offsets de state-checkpoints.jsonl sont en octets UTF-8, fins de ligne comprises.</p><p>Les paramètres caméra sont écrits dans camera-states.jsonl avant l’image qui les référence. Un changement exact de pose, projection ou intrinsics crée un nouvel ID, y compris lors d’un retour à une ancienne pose. <code>calibrationVersion=stationflow.pinhole.v1</code> désigne le modèle géométrique ; cameraStateId désigne une révision effective.</p><p>Les options continuent de contrôler les flux. Aucune référence ne doit viser un fichier désactivé ou un profil absent. Quand le profil canonique est disponible, un événement peut utiliser <code>profileRef=profile.json#profile</code> ; sinon son contenu nécessaire reste dans sa charge utile. Les dossiers restent organisés par agent logique ; les slots physiques peuvent être réutilisés.</p><p class=\"note\">Les paramètres observés qui changent rarement ne sont pas supposés immuables : vitesse de groupe, destination, association avatar, durée d’état et vitesse d’animation sont révisées lorsque leur valeur change. Positions, orientations, vitesses réelles, phases et transitions conservent tous leurs échantillons requis.</p></section>");
            b.Append("<section id=\"locomotion\"><h2>Navigation native et petits pas</h2><p>Le scénario conserve avoidancePredictionSeconds, valeur effective de l’horizon global de prédiction NavMesh. Ce réglage anticipe l’évitement local ; il ne planifie pas un trajet global selon la densité de foule. Aucun arrêt supplémentaire, détour ni vitesse de navigation imposée n’est ajouté pour la présentation.</p><p>La marche adaptative utilise le paramètre StationWalkStride entre une pose debout et le cycle de marche calibré, de même durée. À faible vitesse, la cadence reste au moins à 0,65 et le poids du pas diminue. La vitesse représentée nominale vaut cadence × poids du pas × walkReferenceSpeed ; ce modèle reste une approximation d’un mélange Humanoid. Les positions osseuses enregistrées sont la vérité géométrique. Le lissage bref peut produire un décalage transitoire pendant une accélération.</p><p>Le passage en posture d’attente est uniquement visuel lorsque la vitesse est presque nulle : sous 0,025 m/s pendant 0,15 seconde avec les réglages enregistrés ; reprise au-dessus de 0,05 m/s. NavMesh continue à résoudre l’évitement et peut reprendre sa marche. Les attentes de groupe, de file ou de service conservent leurs propres règles.</p><p>locomotionStrideWeight conserve la valeur réelle du paramètre Animator, y compris pendant une transition sortante, dans chaque observation d’animation ; adaptiveLocomotion appartient aux révisions d’état. animationCatalog décrit une fois les enfants du mélange, leurs seuils et la durée de référence. Pendant une transition, utiliser aussi les hash des états, les phases, transitionProgress et les poses 3D ; la formule nominale n’est pas une reconstruction exacte des os.</p></section>");
            b.Append("<section id=\"skeletons\"><h2>Squelettes 3D par voyageur</h2><p>Le toggle Squelettes 3D est indépendant des animations, trajectoires, profils et vidéos. Seuls les voyageurs effectivement entrés et encore actifs possèdent des poses. Le rig est défini une fois dans skeleton-definitions.jsonl ; la liaison effective au slot et à l'avatar appartient au voyageur logique. Un slot réutilisé ne fusionne jamais deux agentId.</p><p>Les 55 joints Humanoid sont ordonnés selon jointNames, doigts compris lorsque l'avatar les fournit. Une position invalide ou absente est accompagnée d'un bit 0 et de XYZ nuls ; ce n'est pas une position à l'origine du monde. valid ne signifie pas visible dans une image. Le format conserve uniquement les positions de joints : aucune rotation d'os, muscles, mesh, keypoint 2D ou étiquette d'occlusion.</p><p>Une pose commune est lue immédiatement avant le rendu du batch vidéo admis, avec le même syncSampleId que les images. Elle n'est pas réécrite pour chaque caméra. Sans vidéo, le flux suit la cadence régulière de 5 Hz simulés ; les horodatages originaux restent autoritaires. Une collecte seulement squelettes reste autonome, même sans trajectoires/profils/animations. Le passage Animator/IK doit être terminé avant la lecture des Transform ; tout futur constraint tardif impose de conserver ce point commun pose/rendu.</p><h3>Lecture du binaire</h3><p>Chaque fichier commence par SFLPOSE1, uint32 version=1, uint32 headerBytes=16. Tous les entiers et float32 sont little-endian. Chaque record contient dans l'ordre : uint32 byteLength, int64 syncSampleId, int32 sampleId, int32 bindingId, int32 jointCount, int32 maskByteCount, float32 time, le masque puis 3*jointCount float32 XYZ monde. La longueur inclut son propre champ : 32+maskByteCount+12*jointCount. Les bits du masque sont LSB-first ; les bits inutilisés sont nuls. Aucun padding supplémentaire.</p><p>skeleton-index.jsonl pointe les observations 1,257,513… par offsets physiques, premier record à l'octet 16. Les définitions, liaisons et index sont des JSONL simples, sans definitionId. Le lecteur vérifie magic, tailles, ordre, bindings, clocks, masque, valeurs finies, définition SHA et offsets ; il expose jointPositionsWorld[joint][xyz] et valid[joint]. Une vue d'analyse exportée contient skeleton-poses.jsonl développé ; le fichier source binaire reste intact.</p><p>Le rigId est rig- suivi du SHA-256 des octets UTF-8 de stationflow.skeleton_definition.v1 puis un LF, int32 nombre de joints, et pour chaque joint : int32 longueur UTF-8 du nom, nom, int32 parent, int32 longueur UTF-8 du chemin, chemin. Tous ces entiers sont little-endian ; les longueurs comptent des octets, pas des caractères.</p><p>Le thread principal ne transfère au worker que des tableaux numériques détachés. La file bornée, le LRU de fichiers ouverts et la finalisation Stop incluent le binaire. Aucun BakeMesh ni calcul de projection image n'est requis pour l'enregistrement.</p></section>");
            b.Append("<section id=\"reader\"><h2>Lire et vérifier les données</h2><p>Chaque capture contient <a href=\"read_capture.py\">read_capture.py</a> et <a href=\"LireDonnees.fr.txt\">LireDonnees.fr.txt</a>, avec le présent contrat. Le lecteur utilise la bibliothèque standard de Python 3.10 ou plus récent ; consulter les instructions pour les commandes d’inspection, de vérification et d’export d’une vue d’analyse développée. Cette vue contient les valeurs logiques complètes ; elle ne se présente pas comme un ancien format de capture.</p><p>Les références sont validées : horloge de la bonne session, identités distinctes, révisions initiales, séquences, checkpoint et offsets, calibration et comptes des images. Les écritures de workers différents peuvent devenir visibles à des instants différents. Pour une lecture complète, attendre la fermeture normale ; un lecteur en direct doit gérer les références encore en attente sans les inventer.</p></section>");
            b.Append("<section id=\"timing\"><h2>Cadence, précision et finalisation</h2><p>Le nom du dossier utilise l’instant UTC réel de Start : <code>run-yyyyMMdd-HHmmss-fff-identifiant</code>. Le manifeste indique le fuseau et le décalage local. La date de simulation est un autre champ ; elle est initialisée à la date locale du lancement.</p><p>À Start, la simulation revient à ×1 et les commandes ×4/×8 sont verrouillées jusqu’à Stop, même en pause. La collecte régulière trajectoire/animation est prévue à 5 Hz simulés. Les images vidéo admises ajoutent leur observation exacte sans repousser cette cadence. Les pas Unity et l’admission bornée peuvent modifier les intervalles : utiliser les temps originaux, sans interpolation ni frames artificielles.</p><p>Les images sont demandées par frame Unity admise. Chaque ligne caméra conserve captureIntervalRealSeconds, captureFpsInstantaneous et captureFpsRolling1s. Les deux premiers sont null pour sa première image ; les autres sont calculés depuis les images réellement sauvegardées de cette caméra. La télémétrie environ chaque seconde conserve les FPS du jeu et de ses caméras sur une fenêtre réelle d’une seconde, associés au préfixe framesWritten observé.</p><p>L’AVI/MJPEG garde une cadence nominale de lecture constante, usuellement 30 FPS. <code>videoTimeSeconds=videoFrame/nominalPlaybackFps</code> est local au segment ; il n’est pas l’instant de rendu réel. Les temps résolus depuis syncSampleId et les trois champs FPS décrivent la cadence variable. Les segments tournent après cinq minutes de lecture nominale ou avant 900 Mo.</p><p>Les DTO et les références sont sérialisés sur le worker FIFO de données ; les enregistrements d’un snapshot sont regroupés en une commande ordonnée. Le budget retient également le lot en préparation et ses frais estimés. Les demandes périodiques de flush sont fusionnées lorsqu’une précédente est encore en attente ; la fermeture finale vide toujours les buffers. Chaque caméra encode et écrit sur son worker ordonné. Les API Unity de scène sont lues sur le thread principal. La file de données est limitée à 32 768 commandes et 64 Mio estimés de DTO ; ce chiffre ne mesure pas toute la RAM. Aucun worker ne doit bloquer une frame active en attendant une place. Au Start, la préparation des profils reste en pause et sa durée est mesurée séparément.</p><p>Stop ferme immédiatement l’admission. Les frames suivantes permettent aux readbacks, aux workers vidéo puis au worker de données de terminer sans attente bloquante sur le thread principal. L’interface affiche la finalisation ; un nouveau Start attend sa fin, mais l’observation peut reprendre. L’instant simulé de fin est celui de la demande de Stop. Le manifeste reste ouvert jusqu’à la fermeture des fichiers, puis devient complete ou failed. Une erreur connue est signalée une fois et ne doit pas remettre en pause une observation déjà reprise. Un refus préventif de lot vidéo conserve la session valide et se compte dans captureBatchesRejectedData ou captureBatchesRejectedVideo. Une saturation des flux obligatoires, un dépassement du budget déclaré, une référence invalide ou une erreur d’écriture échoue explicitement la session. Un arrêt brutal peut laisser des fichiers incomplets : vérifier status/errors et les références, puis utiliser un checkpoint valide pour une récupération bornée. Les données ne sont pas compressées dans ce format ; JSONL reste lisible et indexable.</p><p>Le profil de rendu effectif est conservé dans le manifeste. Le défaut est <code>").Append(Escape(StationDecisionCaptureRenderProfile.DefaultProfile)).Append("</code>, avec quatre slots de readback par caméra, admission commune et données en arrière-plan. Les changements temporaires de caméra/rendu sont restaurés après chaque demande ; la normalisation ne change ni le rendu ni la géométrie.</p></section>");
            b.Append("<section id=\"capacity\"><h2>Admission des lots et surcharge</h2><p>En production, toutes les caméras choisies réservent leurs slots puis le worker de données réserve un budget conservateur pour le snapshot avatar complet. Les définitions de rigs, liaisons, poses, trajectoires, animations et roster ne sont publiés qu’après admission. Si une capacité manque, tout le lot optionnel est refusé avant rendu ; aucun échantillon partiel n’est accepté. Le prochain lot est réévalué à la frame Unity suivante, sans cible FPS fixe et sans fabrication d’images.</p><p>La limite totale reste de 64 Mio estimés et 32 768 commandes. Par défaut, un quart de chaque limite reste disponible pour les événements, horloges et échantillons réguliers. Leurs déclenchements à 5 Hz simulés restent indépendants de l’admission vidéo ; la quantification par les frames Unity peut allonger l’intervalle. Si même ces flux essentiels ne peuvent plus être écrits, la session échoue explicitement.</p><p>Les refus de capacité sont regroupés sous <code>kind=capture_capacity_pressure</code> dans sync.jsonl : <code>capturePressure</code> conserve leurs compteurs et les bornes de la fenêtre. Ces bornes ne décrivent pas chaque refus individuel. Le manifeste fournit le total de lots demandés, admis, refusés côté données et refusés côté vidéo. En mode common, chaque caméra compte aussi la demande non admise dans requestsDropped. Les erreurs effectives de readback ou d’encodage gardent leurs marqueurs et rendent la session incomplète.</p><p>Une session <code>complete</code> peut donc comporter des intervalles sans image parce que des demandes optionnelles ont été refusées. Cela ne signifie pas qu’une donnée déjà admise a été perdue. Lire les timestamps, les FPS mesurés et les compteurs ; ne jamais combler ces intervalles avec des images inventées. En diagnostic independent, partialVideoBatches et partialCameraDeniedRequests résument aussi les disponibilités partielles de caméras ; elles ne sont pas des refus complets de lots et ne modifient pas leur invariant comptable. Les budgets spéciaux de diagnostic sont explicites dans le manifeste et ne changent pas les réglages de lancement ordinaires.</p></section>");
            b.Append("<section id=\"geometry\"><h2>Repères caméra et mouvement</h2><p>Monde Unity : mètres, +X droite, +Y haut, +Z avant, repère gauche. Vue Unity : +X droite, +Y haut, −Z avant. Caméra CV : +X droite, +Y bas, +Z avant. Les positions avatar restent dans le monde Unity. Quaternions (x,y,z,w), Euler en degrés. Matrices 4×4 colonne-major, index=column*4+row ; intrinsics 3×3 ligne-major.</p><pre>worldToCvCamera = diag(1,-1,-1,1) * worldToCamera\nXcv = worldToCvCamera * Xworld\nu = (NDC.x + 1) * width/2 - 0.5\nv = (1 - NDC.y) * height/2 - 0.5\nfx = width * P00/2 ; fy = height * P11/2\ncx = width * (1-P02)/2 - 0.5\ncy = height * (1+P12)/2 - 0.5</pre><p>Le centre du pixel supérieur gauche est (0,0). La projection CPU fait foi pour la géométrie ; la projection GPU inclut les conventions de l’API et de la cible. JPEG redressé, distorsion nulle, post-traitement de capture désactivé. Pour chaque image, employer la révision cameraStateId qui lui est associée ; la calibration initiale seule ne suffit pas si la caméra change.</p></section>");
            b.Append("<section id=\"types\"><h2>Champs physiques et objets logiques résolus</h2><p>Les types physiques normalisés décrivent le disque. Les autres DTO reflétés décrivent les valeurs logiques après résolution, ainsi que les paramètres de simulation ; ils ne sont pas une promesse de répétition brute de tous ces champs sur chaque ligne. Les champs supplémentaires d’un ReferenceRecord sont les surcharges du modèle associé, écrites directement au niveau de l’objet.</p><input id=\"search\" type=\"search\" placeholder=\"Chercher un type ou un champ\"><button onclick=\"expandAll(true)\">Tout ouvrir</button><button onclick=\"expandAll(false)\">Tout fermer</button>");
            var ids=new HashSet<string>(d.types.Select(t=>t.id));
            foreach(var t in d.types)
            {
                b.Append("<details class=\"schema-type\" id=\"").Append(Anchor(t.id)).Append("\"><summary>").Append(Escape(t.name)).Append("</summary><p class=\"muted\">").Append(Escape(t.kind)).Append(" · ").Append(Escape(t.id)).Append("</p>");
                if(t.enumValues.Length>0)b.Append("<pre>").Append(Escape(string.Join("\n",t.enumValues))).Append("</pre>");
                if(t.fields.Length>0)
                {
                    b.Append("<table><thead><tr><th>Champ exact</th><th>Type</th><th>Interprétation</th></tr></thead><tbody>");
                    foreach(var f in t.fields)
                    {
                        string target=f.type.EndsWith("[]",StringComparison.Ordinal)?f.type.Substring(0,f.type.Length-2):f.type;
                        b.Append("<tr data-field=\"").Append(Escape(t.id+"."+f.name)).Append("\"><td><code>").Append(Escape(f.name)).Append("</code></td><td>");
                        if(ids.Contains(target))b.Append("<a href=\"#").Append(Anchor(target)).Append("\">");
                        b.Append("<code>").Append(Escape(ShortName(f.type))).Append("</code>");
                        if(ids.Contains(target))b.Append("</a>");
                        if(f.defaultValue!=null)b.Append("<br><small>Défaut : ").Append(Escape(f.defaultValue)).Append("</small>");
                        b.Append("</td><td>").Append(Escape(f.description)).Append("</td></tr>");
                    }
                    b.Append("</tbody></table>");
                }
                b.Append("</details>");
            }
            b.Append("</section><section id=\"version\"><h2>Version et maintenance</h2><p>Conteneur du contrat : <code>").Append(Escape(d.schemaVersion)).Append("</code> · générateur : <code>").Append(Escape(d.generatorVersion)).Append("</code> · mobilité : <code>").Append(Escape(d.mobilityModelVersion)).Append("</code>.</p><div class=\"hash\">").Append(d.contractFingerprint).Append("</div><p><a href=\"data-schema.json\">data-schema.json</a> fournit les mêmes fichiers, champs, types et empreintes des sources. Les champs C# logiques sont reflétés ; les formats physiques explicites doivent être maintenus dans ce générateur. Toute modification du code, du lecteur ou de ses instructions doit régénérer et valider le contrat avant build. Chaque capture emporte son lecteur et ses documents exacts.</p><p>UTC de génération : ").Append(Escape(d.generatedUtc)).Append(". Aucune police ni ressource externe.</p></section><footer>Données simulées · Contrat français du format actuel uniquement.</footer></main><script>const types=[...document.querySelectorAll('.schema-type')];function expandAll(o){types.forEach(t=>{if(!t.hidden)t.open=o})}document.getElementById('search').addEventListener('input',e=>{const q=e.target.value.toLocaleLowerCase('fr');types.forEach(t=>{t.hidden=q&&!t.textContent.toLocaleLowerCase('fr').includes(q);if(q&&!t.hidden)t.open=true})});document.querySelectorAll('a[href^=\"#type-\"]').forEach(a=>a.addEventListener('click',()=>{const t=document.getElementById(a.getAttribute('href').slice(1));if(t){t.hidden=false;t.open=true}}));addEventListener('beforeprint',()=>expandAll(true));</script></body></html>\n");
            return AsciiHtml(b.ToString());
        }
        static string AsciiHtml(string value)
        {
            var b=new StringBuilder(value.Length);
            for(int i=0;i<value.Length;i++)
            {
                int scalar=value[i];
                if(char.IsHighSurrogate(value[i]) && i+1<value.Length && char.IsLowSurrogate(value[i+1])) scalar=char.ConvertToUtf32(value[i],value[++i]);
                if(scalar>127)b.Append("&#").Append(scalar.ToString(CultureInfo.InvariantCulture)).Append(';');else b.Append((char)scalar);
            }
            return b.ToString();
        }
        static string ShortName(string s) => s.Replace(NS + ".", "").Replace("StationFlow.TrainStation.RailOperations.", "").Replace("UnityEngine.", "");
        static string Anchor(string s) => "type-" + s.Replace('.', '-').Replace('+', '-');
        static string Escape(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
        static void WriteChanged(string path, string content) { if (!File.Exists(path) || File.ReadAllText(path, Encoding.UTF8) != content) File.WriteAllText(path, content, new UTF8Encoding(false)); }
        [Serializable] sealed class Document { public string schemaVersion, generatorVersion, mobilityModelVersion, language, htmlFile, generatedUtc, contractFingerprint, canonicalContract; public FileContract[] files; public TypeContract[] types; public SourceContract[] sources; }
        [Serializable] sealed class FileContract { public string path, format, condition, description; public string[] rootTypes; }
        [Serializable] sealed class TypeContract { public string id, name, kind; public FieldContract[] fields; public string[] enumValues; }
        [Serializable] sealed class FieldContract { public string name, type, description, defaultValue; }
        [Serializable] sealed class SourceContract { public string path, sha256; }
    }
}

