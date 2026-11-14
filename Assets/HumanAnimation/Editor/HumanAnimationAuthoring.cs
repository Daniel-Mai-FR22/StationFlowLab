#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StationFlow.HumanAnimation;
using TMPro;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace StationFlow.HumanAnimation.Editor
{
    /// <summary>Explicit authoring only: assets, prefabs, controller graphs and lab all remain editable.</summary>
    public static class HumanAnimationAuthoring
    {
        public const string Root = "Assets/HumanAnimation";
        public const string ResourcesPath = Root + "/Resources/HumanAnimation";
        public const string ScenePath = Root + "/Scenes/HumanAnimationLab.unity";
        public const string MixamoScenePath = Root + "/Scenes/MixamoAnimationLab.unity";
        public const string DiagnosticLibraryPath = ResourcesPath + "/Diagnostics/DiagnosticLibrary.asset";
        public const string MixamoLibraryPath = ResourcesPath + "/Mixamo/MixamoLibrary.asset";
        public const string StandardPlayerPath = Root + "/Prefabs/Human_Standard_Diagnostic.prefab";
        public const string TallPlayerPath = Root + "/Prefabs/Human_Tall_Diagnostic.prefab";
        public const string XBotPlayerPath = Root + "/Prefabs/Human_XBot_Mixamo.prefab";
        public const string TallMixamoPlayerPath = Root + "/Prefabs/Human_Tall_Mixamo.prefab";

        [MenuItem("Efrei Research Lab/Human Animation/Create reusable resources and lab")]
        public static void BuildAll()
        {
            MakeFolders();
            var diagnostic = BuildDiagnostics();
            RebuildMixamoLibrary();
            var models = HumanDiagnosticModels.EnsurePrefabs();
            var prefabs = new[] { EnsurePlayerPrefab(models[0], diagnostic, StandardPlayerPath), EnsurePlayerPrefab(models[1], diagnostic, TallPlayerPath) };
            if (!File.Exists(ScenePath)) BuildLab(prefabs);
            AssetDatabase.SaveAssets();
            Validate();
        }

        static void MakeFolders()
        {
            foreach (string folder in new[] {"Editor", "Prefabs", "Scenes", "Materials", "Source/Mixamo", "Resources/HumanAnimation/Diagnostics/Clips", "Resources/HumanAnimation/Mixamo/Clips", "Resources/HumanAnimation/Masks"})
                Directory.CreateDirectory(Root + "/" + folder);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        [MenuItem("Efrei Research Lab/Human Animation/Open animation lab")]
        public static void OpenLab()
        {
            if (!File.Exists(ScenePath)) BuildAll();
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(File.Exists(MixamoScenePath)?MixamoScenePath:ScenePath);
        }

        public static void RebuildMixamoLibrary()
        {
            Directory.CreateDirectory(ResourcesPath + "/Mixamo");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var motions = HumanMixamoImporter.CollectImportedMotions();
            var priorLibrary=AssetDatabase.LoadAssetAtPath<HumanAnimationLibrary>(MixamoLibraryPath);
            if(priorLibrary!=null)
            {
                // Only reconcile records managed by our explicit source importer; retain external custom additions.
                var available=motions.Select(m=>m.id).ToHashSet();
                var removed=(priorLibrary.motions??Array.Empty<HumanMotion>()).Where(m=>m!=null &&
                    (m.sourceAssetPath??"").StartsWith(HumanMixamoImporter.SourceDirectory+"/",StringComparison.Ordinal)&&!available.Contains(m.id)).Select(m=>m.id).ToHashSet();
                if(priorLibrary.controller is AnimatorController priorController)
                {
                    var machine=priorController.layers[0].stateMachine;
                    foreach(var state in machine.states.Where(s=>removed.Contains(s.state.name)).ToArray())machine.RemoveState(state.state);
                    EditorUtility.SetDirty(priorController);
                }
                priorLibrary.motions=(priorLibrary.motions??Array.Empty<HumanMotion>()).Where(m=>m!=null &&
                    (!(m.sourceAssetPath??"").StartsWith(HumanMixamoImporter.SourceDirectory+"/",StringComparison.Ordinal)||available.Contains(m.id))).ToArray();
            }
            var library=SaveLibrary(MixamoLibraryPath, "Mixamo shared library", motions);
            AssetDatabase.SaveAssets();
            if(motions.Length>0)EnsureMixamoPreview(library);
        }

        static void EnsureMixamoPreview(HumanAnimationLibrary library)
        {
            var source=AssetDatabase.LoadAssetAtPath<GameObject>(HumanMixamoImporter.SourceDirectory+"/Models/XBot.fbx");
            if(source==null)return;
            var models=HumanDiagnosticModels.EnsurePrefabs();
            var prefabs=new[]{EnsurePlayerPrefab(source,library,XBotPlayerPath),EnsurePlayerPrefab(models[1],library,TallMixamoPlayerPath)};
            if(!File.Exists(MixamoScenePath))BuildLab(prefabs,MixamoScenePath,true);
            AssetDatabase.SaveAssets();
        }

        static HumanAnimationLibrary BuildDiagnostics()
        {
            // These authored muscle-space tests are original diagnostics, never presented as Mixamo downloads.
            string[] ids = { "diagnostic_neutral", "diagnostic_reach", "diagnostic_stride" };
            string[] titles = { "Neutral pose / diagnostic", "Arm reach / diagnostic", "Alternating limbs / diagnostic" };
            var motions = new List<HumanMotion>();
            for (int i = 0; i < ids.Length; i++)
            {
                string path = ResourcesPath + "/Diagnostics/Clips/" + ids[i] + ".anim";
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip == null)
                {
                    clip = new AnimationClip { name = ids[i], frameRate = 30, wrapMode = WrapMode.Loop };
                    // Explicit neutral values prevent state changes retaining muscles from the preceding clip.
                    foreach (string muscle in HumanTrait.MuscleName)
                        Curve(clip, muscle, AnimationCurve.Constant(0, 2, 0));
                    Curve(clip, "Left Arm Down-Up", AnimationCurve.Constant(0, 2, -.7f));
                    Curve(clip, "Right Arm Down-Up", AnimationCurve.Constant(0, 2, -.7f));
                    if (i == 1)
                    {
                        Curve(clip, "Right Arm Down-Up", Wave(-.7f, .35f, -.7f));
                        Curve(clip, "Right Forearm Stretch", Wave(0, -.6f, 0));
                        Curve(clip, "Head Turn Left-Right", Wave(0, -.15f, 0));
                    }
                    if (i == 2)
                    {
                        Curve(clip, "Left Upper Leg Front-Back", Wave(-.25f, .3f, -.25f));
                        Curve(clip, "Right Upper Leg Front-Back", Wave(.3f, -.25f, .3f));
                        Curve(clip, "Left Lower Leg Stretch", Wave(-.1f, -.45f, -.1f));
                        Curve(clip, "Right Lower Leg Stretch", Wave(-.45f, -.1f, -.45f));
                        Curve(clip, "Left Arm Front-Back", Wave(.25f, -.25f, .25f));
                        Curve(clip, "Right Arm Front-Back", Wave(-.25f, .25f, -.25f));
                    }
                    var settings = AnimationUtility.GetAnimationClipSettings(clip);
                    settings.loopTime = true; settings.loopBlend = true;
                    settings.keepOriginalPositionY = true; settings.keepOriginalPositionXZ = true;
                    settings.keepOriginalOrientation = true;
                    AnimationUtility.SetAnimationClipSettings(clip, settings);
                    AssetDatabase.CreateAsset(clip, path);
                }
                motions.Add(new HumanMotion { id = ids[i], displayName = titles[i], stateName = "Base Layer." + ids[i], clip = clip, loop = true,
                    blendIn = .18f, source = HumanMotionSource.Diagnostic, category = HumanMotionCategory.Diagnostic, sourceAssetPath = path });
            }
            return SaveLibrary(DiagnosticLibraryPath, "Original diagnostic library", motions.ToArray());
        }

        static void Curve(AnimationClip clip, string muscle, AnimationCurve curve)
        {
            if (!HumanTrait.MuscleName.Contains(muscle)) throw new InvalidOperationException("Unknown humanoid muscle: " + muscle);
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), muscle), curve);
        }
        static AnimationCurve Wave(float a, float b, float c) => new AnimationCurve(new Keyframe(0,a),new Keyframe(1,b),new Keyframe(2,c));

        static HumanAnimationLibrary SaveLibrary(string path, string title, HumanMotion[] incoming)
        {
            var library = AssetDatabase.LoadAssetAtPath<HumanAnimationLibrary>(path);
            if (library == null) { library = ScriptableObject.CreateInstance<HumanAnimationLibrary>(); library.name = title; AssetDatabase.CreateAsset(library,path); }
            string controllerPath = Path.ChangeExtension(path, ".controller").Replace('\\','/');
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (controller == null) controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            var existing = (library.motions ?? Array.Empty<HumanMotion>()).Where(m => m != null).ToDictionary(m => m.id);
            foreach (var motion in incoming)
            {
                if (!existing.TryGetValue(motion.id, out var saved)) existing.Add(motion.id, motion);
                else if(motion.source==HumanMotionSource.Mixamo)existing[motion.id]=motion;
                else { saved.clip = motion.clip; saved.sourceAssetPath = motion.sourceAssetPath; }
            }
            library.motions = existing.Values.ToArray();
            var machine = controller.layers[0].stateMachine;
            var ready=machine.states.Select(s=>s.state).FirstOrDefault(s=>s.name=="Ready");
            if(ready==null)ready=machine.AddState("Ready",new Vector3(0,40));
            machine.defaultState=ready;
            foreach (var motion in library.motions)
            {
                var state = machine.states.Select(s => s.state).FirstOrDefault(s => s.name == motion.id);
                if (state == null) state = machine.AddState(motion.id, new Vector3(280, 70 + machine.states.Length * 65));
                if (string.IsNullOrEmpty(motion.blendParameter)) state.motion = motion.clip;
                else
                {
                    if (motion.blendChildren == null || motion.blendChildren.Length < 2)
                        throw new InvalidOperationException("Declared blend has no source clips: " + motion.id);
                    if (!controller.parameters.Any(p => p.name == motion.blendParameter))
                        controller.AddParameter(motion.blendParameter, AnimatorControllerParameterType.Float);
                    var tree = state.motion as BlendTree;
                    if (!tree) { tree = new BlendTree { name = motion.id + " blend", hideFlags = HideFlags.HideInHierarchy }; AssetDatabase.AddObjectToAsset(tree, controller); }
                    tree.blendType = BlendTreeType.Simple1D; tree.blendParameter = motion.blendParameter; tree.useAutomaticThresholds = false;
                    tree.children = motion.blendChildren.Select(c => new ChildMotion { motion = c.clip, threshold = c.threshold, timeScale = 1f }).ToArray();
                    state.motion = tree; EditorUtility.SetDirty(tree);
                }
                state.iKOnFeet = UsesFootIK(motion); state.writeDefaultValues = true;
                motion.stateName = "Base Layer." + motion.id;
            }
            library.controller = controller; library.upperBodyMask = EnsureMask();
            EditorUtility.SetDirty(library); EditorUtility.SetDirty(controller);
            return library;
        }

        /// <summary>Enable retargeted foot contacts only for the reviewed forward walking clips.</summary>
        public static bool UsesFootIK(HumanMotion motion) => motion != null &&
            (motion.id == "mixamo_walking_9af7b48b6f" || motion.id == "mixamo_male_standard_walking_ef7424170f" || motion.id == "station_adaptive_walk");

        static AvatarMask EnsureMask()
        {
            string path = ResourcesPath + "/Masks/UpperBody.mask";
            var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(path);
            if (mask != null) return mask;
            mask = new AvatarMask { name = "Upper body / optional layer mask" };
            for (int i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++) mask.SetHumanoidBodyPartActive((AvatarMaskBodyPart)i, false);
            foreach (var part in new[] {AvatarMaskBodyPart.Body,AvatarMaskBodyPart.Head,AvatarMaskBodyPart.LeftArm,AvatarMaskBodyPart.RightArm,AvatarMaskBodyPart.LeftFingers,AvatarMaskBodyPart.RightFingers})
                mask.SetHumanoidBodyPartActive(part, true);
            AssetDatabase.CreateAsset(mask, path); return mask;
        }

        static GameObject EnsurePlayerPrefab(GameObject model, HumanAnimationLibrary library, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;
            var root = new GameObject(Path.GetFileNameWithoutExtension(path));
            try
            {
                var visual = (GameObject)PrefabUtility.InstantiatePrefab(model);
                visual.transform.SetParent(root.transform, false);
                var animator = visual.GetComponentInChildren<Animator>();
                animator.runtimeAnimatorController = library.controller;
                animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                foreach(var renderer in visual.GetComponentsInChildren<Renderer>(true))
                {
                    renderer.sharedMaterials=renderer.sharedMaterials.Select(m=>ConvertMaterial(m)).ToArray();
                }
                root.AddComponent<HumanAnimationPlayer>().Configure(animator, library);
                return PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        static Material ConvertMaterial(Material source)
        {
            if(source==null||source.shader.name.StartsWith("Universal Render Pipeline/",StringComparison.Ordinal))return source;
            string safeName=new string(source.name.Select(c=>char.IsLetterOrDigit(c)||c=='_'?c:'_').ToArray());
            string path=Root+"/Materials/Mixamo_"+safeName+".mat";
            var material=AssetDatabase.LoadAssetAtPath<Material>(path);if(material!=null)return material;
            material=new Material(Shader.Find("Universal Render Pipeline/Lit")){name="Mixamo_"+safeName};
            material.color=source.HasProperty("_Color")?source.GetColor("_Color"):new Color(.45f,.6f,.64f);
            if(source.HasProperty("_MainTex"))material.SetTexture("_BaseMap",source.GetTexture("_MainTex"));
            material.SetFloat("_Smoothness",.32f);AssetDatabase.CreateAsset(material,path);return material;
        }

        [MenuItem("Efrei Research Lab/Human Animation/Create reusable prefab from selected Humanoid")]
        public static void CreateCharacterPrefab()
        {
            var model = Selection.activeObject as GameObject;
            if (model == null || !EditorUtility.IsPersistent(model)) throw new InvalidOperationException("Select an imported Humanoid model or prefab in the Project window.");
            var animator = model.GetComponentInChildren<Animator>(true);
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman || !animator.avatar.isValid)
                throw new InvalidOperationException("Configure this model's own valid Humanoid Avatar in its Rig import settings first.");
            var library = AssetDatabase.LoadAssetAtPath<HumanAnimationLibrary>(MixamoLibraryPath);
            if (library == null || library.motions == null || library.motions.Length == 0)
                throw new InvalidOperationException("Import Mixamo clips before creating a production character prefab. Diagnostic prefabs are available separately.");
            string path = AssetDatabase.GenerateUniqueAssetPath(Root + "/Prefabs/Human_" + model.name + ".prefab");
            Selection.activeObject = EnsurePlayerPrefab(model, library, path); AssetDatabase.SaveAssets();
        }

        static void BuildLab(GameObject[] prefabs,string scenePath=ScenePath,bool mixamo=false)
        {
            var prior = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            try
            {
                var root = new GameObject("Human Animation Lab — authored preview");
                var stage = Child("01 Stage", root.transform);
                Material floor = MaterialAsset("Stage", new Color(.065f,.095f,.13f));
                Material grid = MaterialAsset("Grid",new Color(.13f,.21f,.28f));
                Shape("Floor",PrimitiveType.Cube,stage,new Vector3(0,-.13f,0),new Vector3(12,.2f,10),floor);
                for(int i=-5;i<=5;i++)
                {
                    Shape("Grid X " + i,PrimitiveType.Cube,stage,new Vector3(i,-.024f,0),new Vector3(.012f,.01f,10),grid);
                    Shape("Grid Z " + i,PrimitiveType.Cube,stage,new Vector3(0,-.023f,i),new Vector3(12,.01f,.012f),grid);
                }
                var specimens=Child("02 Reusable prefab instances",root.transform);
                var players=new HumanAnimationPlayer[2];
                for(int i=0;i<2;i++)
                {
                    var instance=(GameObject)PrefabUtility.InstantiatePrefab(prefabs[i],scene);
                    instance.transform.SetParent(specimens,false); instance.transform.localPosition=new Vector3(i==0?1.1f:-1.1f,.12f,0);
                    players[i]=instance.GetComponent<HumanAnimationPlayer>();
                    Shape("Display plinth " + (i+1),PrimitiveType.Cylinder,stage,new Vector3(instance.transform.position.x,.02f,0),new Vector3(1.25f,.09f,1.25f),MaterialAsset(i==0?"Teal":"Orange",i==0?new Color(.05f,.55f,.47f):new Color(.83f,.35f,.19f)));
                }
                var lightRoot=Child("03 Lighting",root.transform);
                var sun=new GameObject("Key light");sun.transform.SetParent(lightRoot);sun.transform.rotation=Quaternion.Euler(38,-32,0);
                var light=sun.AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.8f;light.shadows=LightShadows.Soft;
                var fill=new GameObject("Fill light");fill.transform.SetParent(lightRoot);fill.transform.rotation=Quaternion.Euler(30,140,0);
                var fillLight=fill.AddComponent<Light>();fillLight.type=LightType.Directional;fillLight.intensity=.65f;fillLight.color=new Color(.65f,.8f,1);
                RenderSettings.ambientMode=UnityEngine.Rendering.AmbientMode.Flat;RenderSettings.ambientLight=new Color(.37f,.43f,.52f);
                var cameraObject=new GameObject("04 Comparison camera");cameraObject.transform.SetParent(root.transform);
                cameraObject.transform.position=new Vector3(.8f,2.25f,7.7f);cameraObject.transform.LookAt(new Vector3(.8f,1.08f,0));
                var camera=cameraObject.AddComponent<Camera>();camera.tag="MainCamera";camera.fieldOfView=35;camera.clearFlags=CameraClearFlags.SolidColor;
                camera.backgroundColor=new Color(.035f,.054f,.075f);camera.nearClipPlane=.1f;camera.farClipPlane=80;cameraObject.AddComponent<AudioListener>();
                BuildLabUI(root.transform,players,mixamo);
                var eventSystem=new GameObject("EventSystem",typeof(EventSystem),typeof(InputSystemUIInputModule));eventSystem.transform.SetParent(root.transform);
                var module=eventSystem.GetComponent<InputSystemUIInputModule>(); module.AssignDefaultActions();
                string workbenchPath=Root+"/Prefabs/"+(mixamo?"MixamoRetargetWorkbench":"DiagnosticRetargetWorkbench")+".prefab";
                if(!File.Exists(workbenchPath))PrefabUtility.SaveAsPrefabAsset(root,workbenchPath);
                EditorSceneManager.SaveScene(scene,scenePath);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene,true);
                if(prior.IsValid() && prior.isLoaded)SceneManager.SetActiveScene(prior);
            }
        }

        static void BuildLabUI(Transform parent,HumanAnimationPlayer[] players,bool mixamo)
        {
            var ui=new GameObject("05 Interface — editable Canvas",typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster));ui.transform.SetParent(parent,false);
            ui.GetComponent<Canvas>().renderMode=RenderMode.ScreenSpaceOverlay;
            var scaler=ui.GetComponent<CanvasScaler>();scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;scaler.referenceResolution=new Vector2(1600,900);scaler.matchWidthOrHeight=.5f;
            var control=new GameObject("06 Preview controls — serialized references");control.transform.SetParent(parent,false);
            var panel=control.AddComponent<HumanAnimationPreviewPanel>();
            // Runtime panel references are assigned by the companion authoring partial once its API is present.
            ConfigurePanel(panel,players,ui.transform,mixamo);
        }

        static void ConfigurePanel(HumanAnimationPreviewPanel panel,HumanAnimationPlayer[] players,Transform ui,bool mixamo)
        {
            var background=Rect("Control panel",ui,new Vector2(34,-120),new Vector2(350,720));background.gameObject.AddComponent<Image>().color=new Color(.035f,.06f,.083f,.97f);
            Label("Title",ui,"HUMAN / MOTION LAB",new Vector2(40,-34),new Vector2(980,44),32,new Color(.92f,.96f,.98f));
            Label("Subtitle",ui,"SHARED HUMANOID ANIMATION  /  RETARGET WORKBENCH",new Vector2(42,-82),new Vector2(1080,28),14,new Color(.42f,.71f,.7f));
            Label("Panel heading",background,"CLIP LIBRARY",new Vector2(24,-24),new Vector2(300,27),16,Color.white);
            var motion=Label("Selected clip",background,mixamo?"Mixamo clips":"Diagnostic clips",new Vector2(24,-67),new Vector2(300,70),23,Color.white);
            var status=Label("Status",background,mixamo?"Official Mixamo exports. Two models, one shared controller.":"Original test motions. Use the Mixamo lab for downloaded motion capture.",new Vector2(24,-150),new Vector2(300,96),15,new Color(.67f,.75f,.8f));
            // Assign API by serialized field discovery to keep the resource author independent of field visibility.
            var serialized=new SerializedObject(panel);
            SetPlayers(serialized,"players",players);
            SetObject(serialized,"library",players[0].Library);
            SetObject(serialized,"selectionLabel",motion); SetObject(serialized,"statusLabel",status);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            AddPanelButton("Previous",background,new Vector2(24,-270),new Vector2(145,45),panel,"SelectPreviousMotion");
            AddPanelButton("Next",background,new Vector2(181,-270),new Vector2(145,45),panel,"SelectNextMotion");
            AddPanelButton("Play on both models",background,new Vector2(24,-332),new Vector2(302,49),panel,"PlaySelected");
            AddPanelButton("Pause",background,new Vector2(24,-396),new Vector2(145,44),panel,"Pause");
            AddPanelButton("Resume",background,new Vector2(181,-396),new Vector2(145,44),panel,"Resume");
            AddPanelButton("Rewind",background,new Vector2(24,-455),new Vector2(302,44),panel,"Rewind");
            AddPanelButton("0.5x",background,new Vector2(24,-518),new Vector2(94,41),panel,"SetHalfSpeed");
            AddPanelButton("1x",background,new Vector2(128,-518),new Vector2(94,41),panel,"SetNormalSpeed");
            AddPanelButton("2x",background,new Vector2(232,-518),new Vector2(94,41),panel,"SetDoubleSpeed");
            Label("Help",background,"EDIT MODE\nSelect a prefab's HumanAnimationPlayer to scrub clips in Inspector.\n\nPLAY MODE\nUse the controls above to compare both Avatars.",new Vector2(24,-583),new Vector2(302,120),13,new Color(.66f,.74f,.8f));
            Label("Models",ui,mixamo?"01  X BOT / SOURCE AVATAR                     02  TALL / RETARGET AVATAR":"01  STANDARD AVATAR                        02  TALL AVATAR",new Vector2(530,-786),new Vector2(990,36),20,new Color(.9f,.94f,.96f));
            Label("Provenance",ui,mixamo?"MIXAMO MOTION CAPTURE  •  INDEPENDENT HUMANOID AVATARS  •  MANUAL PREVIEW":"DIAGNOSTIC MANNEQUINS  •  ORIGINAL TEST CLIPS  •  MANUAL PREVIEW",new Vector2(530,-832),new Vector2(1010,28),12,new Color(.52f,.67f,.73f));
        }
        static void SetPlayers(SerializedObject serialized,string property,HumanAnimationPlayer[] players)
        {
            var p=serialized.FindProperty(property);if(p==null)throw new InvalidOperationException("Preview panel API missing: "+property);
            p.arraySize=players.Length;for(int i=0;i<players.Length;i++)p.GetArrayElementAtIndex(i).objectReferenceValue=players[i];
        }
        static void SetObject(SerializedObject serialized,string property,UnityEngine.Object value)
        {var p=serialized.FindProperty(property);if(p==null)throw new InvalidOperationException("Preview panel API missing: "+property);p.objectReferenceValue=value;}
        static void AddPanelButton(string text,Transform parent,Vector2 position,Vector2 size,HumanAnimationPreviewPanel target,string method)
        {
            var rect=Rect("Button — "+text,parent,position,size);rect.gameObject.AddComponent<Image>().color=new Color(.095f,.24f,.27f);
            var button=rect.gameObject.AddComponent<Button>();var call=(UnityAction)Delegate.CreateDelegate(typeof(UnityAction),target,method);
            UnityEventTools.AddPersistentListener(button.onClick,call);
            var label=Label("Label",rect,text,Vector2.zero,size,16,Color.white);label.alignment=TextAlignmentOptions.Center;
        }
        static RectTransform Rect(string name,Transform parent,Vector2 position,Vector2 size)
        {var g=new GameObject(name,typeof(RectTransform));g.transform.SetParent(parent,false);var r=g.GetComponent<RectTransform>();r.anchorMin=r.anchorMax=r.pivot=new Vector2(0,1);r.anchoredPosition=position;r.sizeDelta=size;return r;}
        static TextMeshProUGUI Label(string name,Transform parent,string text,Vector2 position,Vector2 size,float fontSize,Color color)
        {var r=Rect(name,parent,position,size);var label=r.gameObject.AddComponent<TextMeshProUGUI>();label.text=text;label.fontSize=fontSize;label.color=color;label.raycastTarget=false;label.textWrappingMode=TextWrappingModes.Normal;return label;}
        static Transform Child(string name,Transform parent){var g=new GameObject(name);g.transform.SetParent(parent,false);return g.transform;}
        static void Shape(string name,PrimitiveType type,Transform parent,Vector3 position,Vector3 size,Material material)
        {var g=GameObject.CreatePrimitive(type);g.name=name;g.transform.SetParent(parent,false);g.transform.localPosition=position;g.transform.localScale=size;g.GetComponent<Renderer>().sharedMaterial=material;UnityEngine.Object.DestroyImmediate(g.GetComponent<Collider>());}
        static Material MaterialAsset(string name,Color color)
        {string path=Root+"/Materials/"+name+".mat";var mat=AssetDatabase.LoadAssetAtPath<Material>(path);if(mat!=null)return mat;mat=new Material(Shader.Find("Universal Render Pipeline/Lit")){name=name,color=color};mat.SetFloat("_Smoothness",.32f);AssetDatabase.CreateAsset(mat,path);return mat;}

        public static void RefineLabViews()
        {
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("Save open scene edits before applying the lab layout.");
            var prior=EditorSceneManager.GetSceneManagerSetup();
            try
            {
                foreach(string path in new[]{ScenePath,MixamoScenePath})
                {
                    if(!File.Exists(path))continue;
                    var scene=EditorSceneManager.OpenScene(path);
                    var root=scene.GetRootGameObjects().Single();ApplyViewLayout(root);
                    EditorSceneManager.SaveScene(scene);
                }
                foreach(string name in new[]{"DiagnosticRetargetWorkbench","MixamoRetargetWorkbench"})
                {
                    string path=Root+"/Prefabs/"+name+".prefab";if(!File.Exists(path))continue;
                    var root=PrefabUtility.LoadPrefabContents(path);
                    try{ApplyViewLayout(root);PrefabUtility.SaveAsPrefabAsset(root,path);}finally{PrefabUtility.UnloadPrefabContents(root);}
                }
            }
            finally{EditorSceneManager.RestoreSceneManagerSetup(prior);}
        }
        static void ApplyViewLayout(GameObject root)
        {
            var players=root.GetComponentsInChildren<HumanAnimationPlayer>(true);
            for(int i=0;i<players.Length;i++)players[i].transform.localPosition=new Vector3(i==0?1.1f:-1.1f,.12f,0);
            var transforms=root.GetComponentsInChildren<Transform>(true);
            for(int i=0;i<2;i++)
            {
                var plinth=transforms.Single(t=>t.name=="Display plinth "+(i+1));
                plinth.localPosition=new Vector3(i==0?1.1f:-1.1f,.02f,0);
            }
            var camera=root.GetComponentInChildren<Camera>();camera.transform.localPosition=new Vector3(.8f,2.25f,7.7f);
            camera.transform.LookAt(root.transform.TransformPoint(new Vector3(.8f,1.08f,0)));
        }

        [Serializable]sealed class ValidationReport {public string unityVersion;public bool infrastructureReady,mixamoReady;public int diagnosticClips,mixamoClips,prefabs;public string[] errors;}
        [MenuItem("Efrei Research Lab/Human Animation/Validate saved resources")]
        public static void Validate()
        {
            var errors=new List<string>();var diagnostic=AssetDatabase.LoadAssetAtPath<HumanAnimationLibrary>(DiagnosticLibraryPath);var mixamo=AssetDatabase.LoadAssetAtPath<HumanAnimationLibrary>(MixamoLibraryPath);
            if(diagnostic==null||diagnostic.controller==null)errors.Add("Diagnostic library/controller missing");
            if(diagnostic!=null)errors.AddRange(diagnostic.Validate(true));
            var mixamoErrors=mixamo!=null?mixamo.Validate(true):new List<string>{"Mixamo library missing"};
            if(mixamo?.motions?.Length>0)errors.AddRange(mixamoErrors);
            if(diagnostic!=null)foreach(var motion in diagnostic.motions??Array.Empty<HumanMotion>())if(motion.clip==null||!motion.clip.humanMotion)errors.Add("Non-Humanoid diagnostic clip: "+motion.id);
            int count=0;
            var paths=new List<string>{StandardPlayerPath,TallPlayerPath};
            if(mixamo?.motions?.Length>0){paths.Add(XBotPlayerPath);paths.Add(TallMixamoPlayerPath);}
            foreach(string path in paths)
            {
                var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(path);if(prefab==null){errors.Add("Missing prefab: "+path);continue;}count++;
                var animator=prefab.GetComponentInChildren<Animator>(true);
                if(animator==null||animator.avatar==null||!animator.avatar.isValid||!animator.avatar.isHuman)errors.Add("Invalid Humanoid Avatar: "+path);
                var expectedLibrary=path==XBotPlayerPath||path==TallMixamoPlayerPath?mixamo:diagnostic;
                if(animator!=null&&expectedLibrary!=null&&animator.runtimeAnimatorController!=expectedLibrary.controller)errors.Add("Missing shared controller: "+path);
                if(prefab.GetComponent<HumanAnimationPlayer>()==null)errors.Add("Missing player: "+path);
            }
            if(!File.Exists(ScenePath))errors.Add("Saved laboratory scene missing");
            var report=new ValidationReport{unityVersion=Application.unityVersion,infrastructureReady=errors.Count==0,mixamoReady=mixamoErrors.Count==0,diagnosticClips=diagnostic?.motions?.Length??0,mixamoClips=mixamo?.motions?.Length??0,prefabs=count,errors=errors.ToArray()};
            Directory.CreateDirectory("Reports/HumanAnimation");File.WriteAllText("Reports/HumanAnimation/resources-check.json",JsonUtility.ToJson(report,true));
            if(errors.Count>0)throw new InvalidOperationException(string.Join("\n",errors));
        }
    }
}
#endif
