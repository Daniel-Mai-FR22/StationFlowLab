#if UNITY_EDITOR
using System;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace StationFlow.TrainStation.RailOperations.Editor
{
    /// <summary>Edits existing station boards and authors the saved rail control panel once.</summary>
    public static class StationRailPresentation
    {
        static readonly Color White = new Color(.94f,.96f,.96f);
        static readonly Color Teal = new Color(.44f,.90f,.78f);
        static readonly Color Muted = new Color(.65f,.75f,.78f);

        public static void RefreshSavedPresentation()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play before updating saved timetable presentation.");
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (scene.isDirty) throw new InvalidOperationException("Save current scene edits before updating timetable presentation.");
            var operations = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<StationRailOperations>(true)).Single();
            Ensure(operations);
            foreach (var label in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<TMP_Text>(true))) label.ForceMeshUpdate(true);
            var traffic = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<StationTimetableDisplay>(true))
                .Single(d => d.mode == StationTimetableMode.Traffic);
            // The former arrow separator used a fallback material and generated extra TMP objects.
            // All five time labels now use the primary font; remove their obsolete fallback children.
            foreach (var label in traffic.GetComponentsInChildren<TMP_Text>(true)
                .Where(t => t.name == "Entêtes horaires" || t.name.StartsWith("Horaire ", StringComparison.Ordinal)))
                foreach (var submesh in label.GetComponentsInChildren<TMP_SubMeshUI>(true)) UnityEngine.Object.DestroyImmediate(submesh.gameObject);
            Canvas.ForceUpdateCanvases();
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save timetable presentation.");
        }

        public static void Ensure(StationRailOperations operations)
        {
            var roots = operations.gameObject.scene.GetRootGameObjects();
            var transforms = roots.SelectMany(r => r.GetComponentsInChildren<Transform>(true)).ToArray();
            var board = transforms.Single(t => t.name.StartsWith("Tableau_departs_double_face", StringComparison.Ordinal));
            board.name = "Tableau_departs_double_face — horaires synchronisés";
            var departure = ConfigureHallFace(board.Find("Face_hall"), operations, StationTimetableMode.Departures);
            ConfigureHallFace(board.Find("Face_quais"), operations, StationTimetableMode.Arrivals);
            foreach (var sign in transforms.Where(t => t.name.StartsWith("Passenger information / Secteur ", StringComparison.Ordinal)))
                ConfigurePlatform(sign, operations);
            ConfigureClock(transforms.First(t => t.name.StartsWith("Horloge_analogique", StringComparison.Ordinal)), departure);
            var canvas = roots.SelectMany(r => r.GetComponentsInChildren<Canvas>(true)).First(c => c.name.Contains("Interface"));
            ConfigureHud(canvas, operations);
            foreach (var display in roots.SelectMany(r => r.GetComponentsInChildren<StationTimetableDisplay>(true)))
            {
                display.RefreshDisplay(); EditorUtility.SetDirty(display);
                foreach (var label in display.GetComponentsInChildren<TMP_Text>(true)) EditorUtility.SetDirty(label);
            }
        }

        static StationTimetableDisplay ConfigureHallFace(Transform face, StationRailOperations operations, StationTimetableMode mode)
        {
            if (!face) throw new InvalidOperationException("Saved hall timetable face is missing.");
            var display = face.GetComponent<StationTimetableDisplay>() ?? face.gameObject.AddComponent<StationTimetableDisplay>();
            display.operations = operations; display.mode = mode; display.trackFilter = Array.Empty<int>();
            TextAt(face,"DEPARTS").text = mode == StationTimetableMode.Departures ? "DÉPARTS / DEPARTURES" : "ARRIVÉES / ARRIVALS";
            display.clockLabel = TextAt(face,"Horloge_affichee");
            SetWorld(display.clockLabel, new Vector3(4.9f,1.35f,-.2f),2.4f,.7f,3.0f);
            TextAt(face,"Colonnes").text = "";
            WorldLabel(face,"Colonne heure","HEURE",new Vector3(-5.5f,.77f,-.22f),1.15f,.4f,2.1f,Muted);
            WorldLabel(face,"Colonne lieu",mode == StationTimetableMode.Arrivals ? "PROVENANCE" : "DESTINATION",new Vector3(-2.8f,.77f,-.22f),3.9f,.4f,2.1f,Muted);
            WorldLabel(face,"Colonne train","TRAIN",new Vector3(.55f,.77f,-.22f),1.8f,.4f,2.1f,Muted);
            WorldLabel(face,"Colonne voie","VOIE",new Vector3(2.15f,.77f,-.22f),.7f,.4f,2.1f,Muted);
            WorldLabel(face,"Colonne état","ÉTAT",new Vector3(4.4f,.77f,-.22f),3.05f,.4f,2.1f,Muted);
            display.rows = new StationTimetableRow[5];
            for (int i = 0; i < display.rows.Length; ++i)
            {
                float y = .29f - i * .38f;
                var row = new StationTimetableRow
                {
                    time = TextAt(face,"Heure_" + i), location = TextAt(face,"Destination_" + i),
                    train = TextAt(face,"Train_" + i), track = TextAt(face,"Voie_" + i),
                    status = WorldLabel(face,"Etat_" + i,"",new Vector3(4.4f,y,-.22f),3.05f,.35f,2.1f,Teal)
                };
                SetWorld(row.time,new Vector3(-5.5f,y,-.22f),1.15f,.35f,2.7f);
                SetWorld(row.location,new Vector3(-2.8f,y,-.22f),3.9f,.35f,2.45f);
                SetWorld(row.train,new Vector3(.55f,y,-.22f),1.8f,.35f,2.35f);
                SetWorld(row.track,new Vector3(2.15f,y,-.22f),.7f,.35f,2.7f);
                display.rows[i] = row;
            }
            TextAt(face,"Mention_statique").text = "HORAIRES SIMULÉS · SUIVI DES TRAINS EN TEMPS RÉEL";
            return display;
        }

        static void ConfigurePlatform(Transform sign, StationRailOperations operations)
        {
            var left = TextAt(sign,"Voie left"); var right = TextAt(sign,"Voie right");
            int first = int.Parse(left.text), second = int.Parse(right.text);
            var panel = sign.Find("Departure display");
            float x = panel.localPosition.x, z = panel.localPosition.z - .04f;
            panel.localPosition = new Vector3(x,4.12f,panel.localPosition.z);
            panel.localScale = new Vector3(3.45f,.80f,.03f);
            var old = sign.Find("Static departure legend"); if (old) old.GetComponent<TMP_Text>().text = "";
            var display = sign.GetComponent<StationTimetableDisplay>() ?? sign.gameObject.AddComponent<StationTimetableDisplay>();
            display.operations = operations; display.mode = StationTimetableMode.Platform;
            display.trackFilter = new[] { first, second }; display.rows = new StationTimetableRow[2];
            display.clockLabel = WorldLabel(sign,"Heure synchronisée","14:20:00",new Vector3(x+1.35f,4.66f,z),.98f,.18f,1.0f,Muted);
            for (int i = 0; i < 2; ++i)
            {
                float y = 4.38f - i * .38f;
                display.rows[i] = new StationTimetableRow
                {
                    time = WorldLabel(sign,"Horaire " + i,"",new Vector3(x-1.31f,y,z),.64f,.18f,1.25f,White),
                    location = WorldLabel(sign,"Destination " + i,"",new Vector3(x+.27f,y,z),2.35f,.18f,1.22f,White),
                    train = WorldLabel(sign,"Train " + i,"",new Vector3(x-.95f,y-.17f,z),1.25f,.18f,1.10f,Muted),
                    track = WorldLabel(sign,"Voie active " + i,"",new Vector3(x-.09f,y-.17f,z),.25f,.18f,1.10f,White),
                    status = WorldLabel(sign,"Etat " + i,"",new Vector3(x+.85f,y-.17f,z),1.58f,.18f,1.05f,Teal)
                };
            }
        }

        static void ConfigureClock(Transform clock, StationTimetableDisplay display)
        {
            clock.name = "Horloge_analogique — heure de simulation";
            display.hourHand = Hand(clock,"Pivot des heures","Aiguille_heures",-.18f);
            display.minuteHand = Hand(clock,"Pivot des minutes","Aiguille_minutes",-.19f);
        }
        static Transform Hand(Transform clock, string name, string beamName, float z)
        {
            var pivot = clock.Find(name); if (pivot) return pivot;
            var beam = clock.Find(beamName); if (!beam) throw new InvalidOperationException("Clock hand is missing: " + beamName);
            pivot = new GameObject(name).transform; pivot.SetParent(clock,false); pivot.localPosition = new Vector3(0,0,z);
            float length = beam.localScale.y;
            beam.SetParent(pivot,false); beam.localPosition = new Vector3(0,length*.5f,0); beam.localRotation = Quaternion.identity;
            beam.gameObject.isStatic = false;
            return pivot;
        }

        static void ConfigureHud(Canvas canvas, StationRailOperations operations)
        {
            foreach (var label in canvas.GetComponentsInChildren<TMP_Text>(true))
            {
                if (label.name == "État statique") label.text = "CIRCULATIONS & VIE DE LA GARE";
                if (label.name == "Note de contexte") label.text = "Trains, accueil et voyageurs.\nHoraires et acteurs éditables.";
            }
            var existing = canvas.transform.Find("Circulations — horaires et commandes éditables");
            if (existing)
            {
                existing.GetComponent<StationTimetableDisplay>().operations = operations;
                existing.Find("Entêtes horaires").GetComponent<TMP_Text>().text = "ARR. / DÉP.";
                return;
            }
            var panel = Rect("Circulations — horaires et commandes éditables",canvas.transform,0,0,510,341);
            panel.anchorMin = panel.anchorMax = new Vector2(1,1); panel.pivot = new Vector2(1,1); panel.anchoredPosition = new Vector2(-20,-428);
            panel.gameObject.AddComponent<Image>().color = new Color(.025f,.047f,.063f,.97f);
            var display = panel.gameObject.AddComponent<StationTimetableDisplay>(); display.operations = operations; display.mode = StationTimetableMode.Traffic;
            UiText(panel,"Titre","CIRCULATIONS",17,12,230,25,17,Teal);
            display.clockLabel = UiText(panel,"Heure de simulation","14:20:00",382,13,111,24,17,White);
            UiText(panel,"Contexte","HORAIRES SIMULÉS  ·  VOIES 2, 4, 5 ET 7",18,42,472,18,10,Muted);
            UiText(panel,"Entêtes horaires","ARR. / DÉP.",18,69,118,19,11,Muted);
            UiText(panel,"Entêtes lieu","TRAIN / DESTINATION",143,69,241,19,11,Muted);
            UiText(panel,"Entêtes voie","VOIE",441,69,46,19,11,Muted);
            display.rows = new StationTimetableRow[4];
            for (int i = 0; i < 4; ++i)
            {
                float y = 93 + i * 47;
                var line = Rect("Séparateur " + i,panel,18,y-3,474,1); line.gameObject.AddComponent<Image>().color = new Color(.18f,.27f,.31f);
                display.rows[i] = new StationTimetableRow
                {
                    time = UiText(panel,"Horaire " + i,"",18,y,120,22,12,White),
                    train = UiText(panel,"Train " + i,"",143,y,280,20,12,White),
                    location = UiText(panel,"Destination " + i,"",143,y+21,280,19,11,Muted),
                    track = UiText(panel,"Voie " + i,"",448,y,39,22,14,Teal),
                    status = UiText(panel,"Etat " + i,"",18,y+22,121,18,9,Teal)
                };
            }
            display.pauseLabel = Button(panel,"Pause",18,291,120,34,"PAUSE",display.TogglePause);
            Button(panel,"Recommencer",147,291,148,34,"RECOMMENCER",display.ResetSimulation);
            display.rateLabel = UiText(panel,"Vitesse","1 s réelle = 3 s gare",310,296,179,22,11,Muted);
        }

        static TMP_Text TextAt(Transform parent,string name)
        { var child = parent.Find(name); if (!child || !child.TryGetComponent<TMP_Text>(out var label)) throw new InvalidOperationException("Saved timetable label missing: " + name); return label; }
        static TMP_Text WorldLabel(Transform parent,string name,string value,Vector3 position,float width,float height,float size,Color color)
        {
            var child = parent.Find(name); var label = child ? child.GetComponent<TextMeshPro>() : new GameObject(name).AddComponent<TextMeshPro>();
            if (!child) label.transform.SetParent(parent,false);
            label.font = TMP_Settings.defaultFontAsset; label.text = value; label.color = color;
            SetWorld(label,position,width,height,size); return label;
        }
        static void SetWorld(TMP_Text label,Vector3 position,float width,float height,float size)
        {
            label.gameObject.isStatic = false; label.transform.localPosition = position; label.transform.localRotation = Quaternion.identity;
            label.rectTransform.sizeDelta = new Vector2(width,height); label.fontSize = size; label.fontSizeMax = size; label.fontSizeMin = size*.7f;
            label.enableAutoSizing = true; label.alignment = TextAlignmentOptions.Center; label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Truncate; label.raycastTarget = false;
        }
        static TMP_Text UiText(Transform parent,string name,string value,float x,float y,float w,float h,float size,Color color)
        {
            var label = Rect(name,parent,x,y,w,h).gameObject.AddComponent<TextMeshProUGUI>();
            label.font = TMP_Settings.defaultFontAsset; label.text = value; label.fontSize = size; label.color = color;
            label.raycastTarget = false; label.textWrappingMode = TextWrappingModes.NoWrap; label.overflowMode = TextOverflowModes.Truncate;
            return label;
        }
        static TMP_Text Button(Transform parent,string name,float x,float y,float w,float h,string caption,UnityAction callback)
        {
            var rect = Rect(name,parent,x,y,w,h); var image = rect.gameObject.AddComponent<Image>(); image.color = new Color(.09f,.28f,.30f);
            var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image; UnityEventTools.AddPersistentListener(button.onClick,callback);
            var label = UiText(rect,"Libellé",caption,4,0,w-8,h,11,White); label.alignment = TextAlignmentOptions.Center; return label;
        }
        static RectTransform Rect(string name,Transform parent,float x,float y,float width,float height)
        {
            var rect = (RectTransform)new GameObject(name,typeof(RectTransform)).transform;
            rect.SetParent(parent,false); rect.anchorMin = rect.anchorMax = new Vector2(0,1); rect.pivot = new Vector2(0,1);
            rect.anchoredPosition = new Vector2(x,-y); rect.sizeDelta = new Vector2(width,height); return rect;
        }
    }
}
#endif
