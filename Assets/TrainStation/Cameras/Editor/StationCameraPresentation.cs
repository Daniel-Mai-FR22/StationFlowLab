#if UNITY_EDITOR
using System;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace StationFlow.TrainStation.Editor
{
    /// <summary>Authors the complete camera selector into the existing Canvas; runtime only toggles saved objects.</summary>
    public static class StationCameraPresentation
    {
        const string CatalogName = "Camera catalogue — all saved observation views";
        const string FooterName = "Camera controls — saved mouse and CCTV navigation";
        static readonly Color Panel = new Color(.025f,.047f,.063f,.985f);
        static readonly Color Teal = new Color(.44f,.90f,.78f);

        public static void Ensure(TrainStationViewControls controls)
        {
            if (Application.isPlaying || !controls || !controls.interfaceRoot) throw new InvalidOperationException("Camera presentation needs the saved station Canvas in Edit mode.");
            var canvas = controls.interfaceRoot.transform;
            foreach(Transform child in canvas)
                if(child.name.StartsWith("01 ·",StringComparison.Ordinal)||child.name.StartsWith("02 ·",StringComparison.Ordinal)||
                   child.name.StartsWith("03 ·",StringComparison.Ordinal)||child.name.StartsWith("04 ·",StringComparison.Ordinal))
                {var panelImage=child.GetComponent<Image>();if(panelImage)panelImage.raycastTarget=true;}
            var footer = canvas.GetComponentsInChildren<RectTransform>(true).Single(t=>t.name=="04 · Navigation entre points de vue");
            foreach (var button in footer.GetComponentsInChildren<Button>(true).Where(b=>b.name.StartsWith("Vue ·",StringComparison.Ordinal)))
                button.gameObject.SetActive(false);
            var previous = footer.Find(FooterName);
            if (previous) UnityEngine.Object.DestroyImmediate(previous.gameObject);
            var bar = Rect(FooterName,footer,Vector2.zero,new Vector2(1130,85));
            Button(bar,"Vue libre du hall",176,16,156,34,"CAMERA LIBRE",controls.SetHall);
            Button(bar,"Camera precedente",341,16,146,34,"PRECEDENTE",controls.PreviousView);
            Button(bar,"Camera suivante",496,16,146,34,"SUIVANTE",controls.NextView);
            Button(bar,"Choisir une camera",651,16,222,34,"CHOISIR UNE CAMERA",controls.ToggleCameraCatalog);
            Button(bar,"Recentrer la camera libre",882,16,168,34,"RECENTRER",controls.ResetGameView);
            var title = footer.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(t=>t.name=="Titre navigation");
            if(title) title.text="CAMERAS";
            var shortcuts = footer.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(t=>t.name=="Raccourcis navigation");
            if(shortcuts) shortcuts.text="PgPrec / PgSuiv";
            controls.controlHintLabel = footer.GetComponentsInChildren<TextMeshProUGUI>(true).Single(t=>t.name=="Conseil d’édition");

            var old = canvas.Find(CatalogName);
            if(old) UnityEngine.Object.DestroyImmediate(old.gameObject);
            int rows = Mathf.CeilToInt(controls.viewpoints.Length / 3f);
            float panelHeight = 152f + rows * 76f;
            var panel = Rect(CatalogName,canvas,Vector2.zero,new Vector2(820,panelHeight));
            panel.anchorMin=panel.anchorMax=new Vector2(.5f,.5f);panel.pivot=new Vector2(.5f,.5f);
            panel.gameObject.AddComponent<Image>().color=Panel;
            controls.cameraCatalog=panel.gameObject;
            Text("Titre",panel,"CHOISIR UNE CAMERA",22,15,620,30,20,Teal);
            Text("Explication",panel,"Vue libre au centre du hall / cameras fixes sur supports de la gare",22,49,700,27,12,Color.white);
            Button(panel,"Fermer le catalogue",741,17,57,31,"X",controls.ToggleCameraCatalog);
            controls.viewButtons=new Image[controls.viewpoints.Length];
            for(int i=0;i<controls.viewpoints.Length;i++)
            {
                int column=i%3,row=i/3;
                float x=22+column*262,y=92+row*76;
                var rect=Rect("Select camera "+i.ToString("00"),panel,new Vector2(x,-y),new Vector2(252,65));
                var background=rect.gameObject.AddComponent<Image>();background.color=controls.inactiveButtonColor;
                var button=rect.gameObject.AddComponent<Button>();button.targetGraphic=background;
                UnityEventTools.AddIntPersistentListener(button.onClick,controls.SelectView,i);
                controls.viewButtons[i]=background;
                Text("Nom",rect,controls.GetViewName(i),11,6,230,24,13,Color.white);
                var metadata=controls.viewpoints[i].GetComponentInParent<StationSurveillanceCamera>();
                string purpose=metadata ? metadata.purpose : "Souris : rotation, deplacement et zoom";
                Text("Zone observee",rect,purpose,11,31,230,29,10,new Color(.66f,.77f,.80f));
            }
            Text("Statut des vues",panel,controls.viewpoints.Length+" cameras enregistrees / une seule vue active",22,panelHeight-42f,744,23,12,Teal);
            panel.SetAsLastSibling();
            controls.viewDropdown=null;
            controls.SelectView(0);
            controls.cameraCatalog.SetActive(false);
            foreach(var component in canvas.GetComponentsInChildren<Component>(true)) if(component) EditorUtility.SetDirty(component);
            EditorUtility.SetDirty(controls);
        }

        static Button Button(Transform parent,string name,float x,float y,float width,float height,string title,UnityAction callback)
        {
            var rect=Rect(name,parent,new Vector2(x,-y),new Vector2(width,height));
            var background=rect.gameObject.AddComponent<Image>();background.color=new Color(.09f,.28f,.30f);
            var button=rect.gameObject.AddComponent<Button>();button.targetGraphic=background;
            UnityEventTools.AddPersistentListener(button.onClick,callback);
            var label=Text("Libelle",rect,title,5,0,width-10,height,11,Color.white);label.alignment=TextAlignmentOptions.Center;
            return button;
        }
        static TextMeshProUGUI Text(string name,Transform parent,string content,float x,float y,float width,float height,float size,Color color)
        {
            var rect=Rect(name,parent,new Vector2(x,-y),new Vector2(width,height));
            var text=rect.gameObject.AddComponent<TextMeshProUGUI>();text.font=TMP_Settings.defaultFontAsset;
            text.text=content;text.fontSize=size;text.color=color;text.raycastTarget=false;
            text.textWrappingMode=TextWrappingModes.Normal;text.overflowMode=TextOverflowModes.Ellipsis;
            return text;
        }
        static RectTransform Rect(string name,Transform parent,Vector2 position,Vector2 size)
        {
            var rect=(RectTransform)new GameObject(name,typeof(RectTransform)).transform;rect.gameObject.layer=LayerMask.NameToLayer("UI");
            rect.SetParent(parent,false);rect.anchorMin=rect.anchorMax=new Vector2(0,1);rect.pivot=new Vector2(0,1);
            rect.anchoredPosition=position;rect.sizeDelta=size;return rect;
        }
    }
}
#endif
