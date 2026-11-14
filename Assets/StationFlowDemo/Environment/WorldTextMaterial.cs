using System.Collections.Generic;
using UnityEngine;

namespace StationFlow.SyntheticTraffic
{
    /// <summary>World lettering needs depth testing; Unity's default GUI font material can draw through facades.</summary>
    public sealed class WorldTextMaterial : MonoBehaviour
    {
        static WorldTextMaterial instance;
        readonly Dictionary<Font,Material> materials=new Dictionary<Font,Material>();
        readonly List<TextMesh> labels=new List<TextMesh>();
        bool atlasChanged;

        public static void Bind(TextMesh label)
        {
            if(instance==null)instance=new GameObject("World lettering atlas binding").AddComponent<WorldTextMaterial>();
            instance.Register(label);
        }
        void OnEnable(){Font.textureRebuilt+=OnAtlasRebuilt;}
        void OnDisable(){Font.textureRebuilt-=OnAtlasRebuilt;}
        void OnAtlasRebuilt(Font font){if(materials.ContainsKey(font))atlasChanged=true;}
        void Register(TextMesh label)
        {
            var font=label.font;
            if(!materials.TryGetValue(font,out Material material))
            {
                material=new Material(Shader.Find("StationFlow/World Text")){name="Depth-tested world lettering"};
                material.SetTexture("_MainTex",font.material.mainTexture);materials.Add(font,material);
            }
            label.GetComponent<MeshRenderer>().sharedMaterial=material;
            labels.Add(label);atlasChanged=true;
        }
        void LateUpdate()
        {
            // TextMesh rebuilds its own glyph geometry when the dynamic atlas changes. We rebind our custom material.
            foreach(var pair in materials)
                if(pair.Key!=null&&pair.Value.mainTexture!=pair.Key.material.mainTexture)pair.Value.mainTexture=pair.Key.material.mainTexture;
            if(!atlasChanged)return;
            atlasChanged=false;
            foreach(var label in labels)
            {
                if(label==null)continue;
                string value=label.text;label.text="";label.text=value;
            }
        }
    }
}
