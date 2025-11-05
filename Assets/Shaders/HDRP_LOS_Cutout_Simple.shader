Shader "Game/HDRP_LOS_Cutout_Simple"
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
        // Key: tag SRP to HDRP, but use simple CG includes that work everywhere.
    Tags { "RenderPipeline"="HDRenderPipeline" "RenderType"="Opaque" }

    ZWrite On
    Cull Back
    Blend Off

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode"="ForwardOnly" }

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _BaseMap;
            float4 _BaseMap_ST;
            float4 _BaseColor;
            float4 _LosCenter;   // xy = viewport center
            float  _LosRadius;
            float  _LosFeather;
            float  _CutAlpha;

            struct appdata {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f {
                float4 pos  : SV_POSITION;
                float2 uv   : TEXCOORD0;
                float4 clip : TEXCOORD1;
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
                fixed4 albedo = tex2D(_BaseMap, i.uv) * _BaseColor;
                return fixed4(albedo.rgb, 1);
            }
            ENDCG
        }
    }

    FallBack Off
}
// Minimal unlit transparent shader that works under HDRP. Viewport-circle lowers alpha only near the player.