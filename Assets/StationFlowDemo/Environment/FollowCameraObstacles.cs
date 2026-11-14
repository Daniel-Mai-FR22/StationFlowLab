using System.Collections.Generic;
using UnityEngine;

namespace StationFlow.SyntheticTraffic
{
    public sealed partial class WorldBuilder
    {
        readonly List<Bounds> cameraObstacles=new List<Bounds>();
        void CacheCameraObstacles()
        {
            // Preserve compact static envelopes before the visual meshes are combined.
            // Simulation collision is handled separately by the sidewalk navigation model.
            var names=new HashSet<string>{"Masonry","Dressed stone shop storey","Approach facade","Signal housing",
                "Pedestrian signal case","Signal pole","Sign plate","Street-sign post","Street light","Tree trunk","Sign pole",
                "Lower tree trunk","Upper tree trunk","Red speed-sign ring","Canvas awning stripe","Awning valance",
                "Shelter canopy","Shelter back glass"};
            foreach(var renderer in Root.GetComponentsInChildren<MeshRenderer>())
            {
                if(!names.Contains(renderer.name))continue;
                Bounds b=renderer.bounds;b.Expand(.4f);cameraObstacles.Add(b);
            }
        }
        public Vector3 KeepFollowCameraClear(Vector3 focus,Vector3 desired)
        {
            Vector3 offset=desired-focus;float distance=offset.magnitude;
            if(distance<.001f)return desired;
            var ray=new Ray(focus,offset/distance);float permitted=distance;
            foreach(var bounds in cameraObstacles)
            {
                if(bounds.Contains(focus))continue;
                if(bounds.IntersectRay(ray,out float entry)&&entry<permitted)permitted=Mathf.Max(.2f,entry-.24f);
            }
            return focus+ray.direction*permitted;
        }
    }
}
