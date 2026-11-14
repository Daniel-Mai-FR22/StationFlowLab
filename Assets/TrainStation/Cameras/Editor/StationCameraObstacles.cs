#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.Editor
{
    /// <summary>Saved collision geometry for roof parts that were authored with renderers only.</summary>
    public static class StationCameraObstacles
    {
        public const string ProxyName = "Camera collision — roof geometry (excluded from navigation)";

        /// <summary>Call during explicit scene authoring, before validation and saving. Existing roof colliders are preserved.</summary>
        public static void Ensure(Scene scene)
        {
            if (Application.isPlaying) throw new InvalidOperationException("Author camera obstacles in Edit mode.");
            if (!scene.IsValid() || !scene.isLoaded) throw new InvalidOperationException("Open the station scene before authoring camera obstacles.");
            var controls = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<TrainStationViewControls>(true)).Single();
            if (controls.roofGroups == null || controls.roofGroups.Length == 0)
                throw new InvalidOperationException("The station camera controls need saved roof group references.");

            var meshes = new HashSet<MeshFilter>();
            foreach (GameObject roofGroup in controls.roofGroups)
                if (roofGroup)
                    foreach (var filter in roofGroup.GetComponentsInChildren<MeshFilter>(true))
                        meshes.Add(filter);

            foreach (var filter in meshes)
            {
                var mesh = filter.sharedMesh;
                if (!mesh || !filter.GetComponent<MeshRenderer>()) continue;
                // Roof panels that already have physical geometry need no duplicate surface.
                if (filter.GetComponents<Collider>().Length != 0) continue;
                if (filter.GetComponentInParent<StationSurveillanceCamera>()) continue;

                Transform proxy = filter.transform.Find(ProxyName);
                if (!proxy)
                {
                    var child = new GameObject(ProxyName);
                    child.transform.SetParent(filter.transform, false);
                    proxy = child.transform;
                }
                proxy.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                proxy.localScale = Vector3.one;
                proxy.gameObject.layer = 2; // Ignore Raycast for ordinary picks; the orbit's explicit mask includes this layer.
                proxy.gameObject.isStatic = true;
                proxy.gameObject.SetActive(true);

                var modifier = proxy.GetComponent<NavMeshModifier>();
                if (!modifier) modifier = proxy.gameObject.AddComponent<NavMeshModifier>();
                modifier.ignoreFromBuild = true;
                modifier.overrideArea = false;

                if (IsBuiltInCube(mesh))
                {
                    var previousMeshCollider = proxy.GetComponent<MeshCollider>();
                    if (previousMeshCollider) UnityEngine.Object.DestroyImmediate(previousMeshCollider);
                    var collider = proxy.GetComponent<BoxCollider>();
                    if (!collider) collider = proxy.gameObject.AddComponent<BoxCollider>();
                    collider.center = mesh.bounds.center;
                    collider.size = mesh.bounds.size;
                    collider.isTrigger = false;
                    collider.enabled = true;
                    EditorUtility.SetDirty(collider);
                }
                else
                {
                    // The vault's tapered end panes are saved closed meshes. Their bounding boxes would
                    // invent opaque geometry above the sloping edge, so reuse their exact mesh surface.
                    var previousBoxCollider = proxy.GetComponent<BoxCollider>();
                    if (previousBoxCollider) UnityEngine.Object.DestroyImmediate(previousBoxCollider);
                    var collider = proxy.GetComponent<MeshCollider>();
                    if (!collider) collider = proxy.gameObject.AddComponent<MeshCollider>();
                    collider.sharedMesh = mesh;
                    collider.convex = false;
                    collider.isTrigger = false;
                    collider.enabled = true;
                    EditorUtility.SetDirty(collider);
                }

                EditorUtility.SetDirty(modifier);
                EditorUtility.SetDirty(proxy);
                EditorUtility.SetDirty(proxy.gameObject);
            }
            Physics.SyncTransforms();
        }

        static bool IsBuiltInCube(Mesh mesh)
        {
            string path = AssetDatabase.GetAssetPath(mesh);
            return mesh.name == "Cube" && mesh.vertexCount == 24 && mesh.subMeshCount == 1 &&
                mesh.bounds.center.sqrMagnitude < .000001f && (mesh.bounds.size - Vector3.one).sqrMagnitude < .000001f &&
                (path == "Library/unity default resources" || path == "Resources/unity_builtin_extra");
        }
    }
}
#endif
