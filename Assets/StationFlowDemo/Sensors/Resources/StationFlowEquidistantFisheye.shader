Shader "StationFlow/EquidistantFisheye"
{
    Properties
    {
        _Front ("Front", 2D) = "black" {}
        _Right ("Right", 2D) = "black" {}
        _Back ("Back", 2D) = "black" {}
        _Left ("Left", 2D) = "black" {}
        _Up ("Up", 2D) = "black" {}
        _Down ("Down", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _Front, _Right, _Back, _Left, _Up, _Down;

            float4 frag(v2f_img input) : SV_Target
            {
                // UV and camera-local Y both point up here. PNG labels use top-left Y down.
                float2 p = 2.0 * input.uv - 1.0;
                float radius = length(p);
                if (radius > 1.0) return float4(0, 0, 0, 1);
                float theta = radius * 1.5707963267948966;
                float2 radial = radius > 0.000001 ? p / radius : float2(0, 0);
                float3 d = float3(radial * sin(theta), cos(theta));
                float3 a = abs(d);
                float2 uv;
                if (a.z >= a.x && a.z >= a.y)
                {
                    if (d.z >= 0)
                    {
                        uv = 0.5 * (float2(d.x, d.y) / a.z + 1.0);
                        return tex2D(_Front, uv);
                    }
                    uv = 0.5 * (float2(-d.x, d.y) / a.z + 1.0);
                    return tex2D(_Back, uv);
                }
                if (a.x >= a.y)
                {
                    if (d.x >= 0)
                    {
                        uv = 0.5 * (float2(-d.z, d.y) / a.x + 1.0);
                        return tex2D(_Right, uv);
                    }
                    uv = 0.5 * (float2(d.z, d.y) / a.x + 1.0);
                    return tex2D(_Left, uv);
                }
                if (d.y >= 0)
                {
                    uv = 0.5 * (float2(d.x, -d.z) / a.y + 1.0);
                    return tex2D(_Up, uv);
                }
                uv = 0.5 * (float2(d.x, d.z) / a.y + 1.0);
                return tex2D(_Down, uv);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
