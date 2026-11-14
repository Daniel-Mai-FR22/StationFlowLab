Shader "StationFlow/ValidationUnlit"
{
    Properties { _Color ("Color", Color) = (1,1,1,1) }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Color;
            struct Input { float4 vertex : POSITION; };
            struct Output { float4 position : SV_POSITION; };
            Output vert(Input input) { Output output; output.position = UnityObjectToClipPos(input.vertex); return output; }
            float4 frag(Output input) : SV_Target { return _Color; }
            ENDHLSL
        }
    }
    Fallback Off
}
