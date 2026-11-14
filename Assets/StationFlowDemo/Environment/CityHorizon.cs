using UnityEngine;

namespace StationFlow.SyntheticTraffic
{
    public sealed partial class WorldBuilder
    {
        void ExtendStreetFronts()
        {
            var wall=Mat("Distant limestone",new Color(.60f,.59f,.53f));
            var roof=Mat("Distant zinc",new Color(.24f,.27f,.28f),.32f);
            var window=Mat("Distant glazing",new Color(.20f,.27f,.30f),.65f,.18f);
            for(int sx=-1;sx<=1;sx+=2)for(int sz=-1;sz<=1;sz+=2)
            for(int axis=0;axis<2;axis++)for(int block=0;block<28;block++)
            {
                float along=91+block*25, height=12+(block%3)*2.6f;
                Vector3 p=axis==0?new Vector3(sx*24,0,sz*along):new Vector3(sx*along,0,sz*24);
                Transform b=Group("Approach street block",p,axis==0?(sx>0?270:90):(sz>0?180:0));
                Box("Approach facade",new Vector3(0,height*.5f,0),new Vector3(22,height,17),wall,b);
                Box("Approach roof",new Vector3(0,height+.4f,0),new Vector3(22.4f,.8f,17.4f),roof,b);
                for(int row=0;row<4;row++)for(int col=0;col<7;col++)
                    Box("Approach window",new Vector3((col-3)*2.9f,2.0f+row*2.7f,8.52f),new Vector3(1.35f,1.75f,.045f),window,b);
            }
        }
    }
}
