Shader "Game/Builtin_LOS_Cutout"
{
    Properties
    {
        _BaseMap   ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _CutAlpha  ("Alpha Inside Circle", Range(0,1)) = 0.3
        _LosCenter ("LOS Center (Viewport)", Vector) = (0.5,0.5,0,0)
        _LosRadius ("LOS Radius (Viewport)", Range(0,1)) = 0.12
        _LosFeather("LOS Feather (Viewport)", Range(0,0.5)) = 0.06
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        LOD 100

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _BaseMap;
            float4 _BaseMap_ST;
            float4 _BaseColor;
            float4 _LosCenter;
            float  _LosRadius;
            float  _LosFeather;
            float  _CutAlpha;

            struct appdata {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f {
                float4 pos    : SV_POSITION;
                float2 uv     : TEXCOORD0;
                float4 clip   : TEXCOORD1;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos  = UnityObjectToClipPos(v.vertex);
                o.clip = o.pos;
                o.uv   = TRANSFORM_TEX(v.uv, _BaseMap);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Calculate viewport position for cutout
                float2 vpPos = (i.clip.xy / i.clip.w) * 0.5 + 0.5;
                float2 diff = vpPos - _LosCenter.xy;
                float dist = length(diff);
                
                // Smooth circular cutout
                float cutout = smoothstep(_LosRadius - _LosFeather, _LosRadius, dist);
                float finalAlpha = lerp(_CutAlpha, 1.0, cutout);
                
                fixed4 albedo = tex2D(_BaseMap, i.uv) * _BaseColor;
                return fixed4(albedo.rgb, albedo.a * finalAlpha);
            }
            ENDCG
        }
    }
    FallBack Off
}
// Built-in render pipeline cutout. No URP/HDRP includes needed.