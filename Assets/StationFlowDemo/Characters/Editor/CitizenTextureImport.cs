#if UNITY_EDITOR
using UnityEditor;
namespace StationFlow.SyntheticTraffic.Editor
{
    public sealed class CitizenTextureImport:AssetPostprocessor
    {
        void OnPreprocessTexture()
        {
            if(!assetPath.Contains("Characters/Resources/Citizens/"))return;
            var t=(TextureImporter)assetImporter;t.maxTextureSize=1024;t.mipmapEnabled=true;t.anisoLevel=4;
            if(assetPath.Contains("_normal")){t.textureType=TextureImporterType.NormalMap;t.sRGBTexture=false;}
            t.textureCompression=TextureImporterCompression.CompressedHQ;
        }
    }
}
#endif
