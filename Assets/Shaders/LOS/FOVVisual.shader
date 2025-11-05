Shader "Custom/LOS/FOVVisual"
{
    Properties
    {
    _Tint("Tint", Color) = (0.92,0.97,1.0,0.62)
        _EdgeFade("Edge Fade", Range(0.02,1)) = 0.35
        _Radius("Radius", Float) = 12
    }
    SubShader
    {
        Tags{ "RenderPipeline"="HDRenderPipeline" "Queue"="Transparent-50" "RenderType"="Transparent" }
        Cull Off ZWrite Off ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Tags { "LightMode" = "SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"

            fixed4 _Tint;
            float  _EdgeFade;
            float  _Radius;

            struct appdata
            {
                float3 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos    : SV_POSITION;
                float2 local  : TEXCOORD0;
            };

            v2f Vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(float4(v.vertex, 1.0));
                o.local = v.vertex.xz;
                return o;
            }

            fixed4 Frag(v2f i) : SV_Target
            {
                float radius = max(1e-3, _Radius);
                float fade   = max(0.02, _EdgeFade);
                float dist   = length(i.local);

                float edge = saturate((radius - dist) / (fade * radius));
                edge = edge * edge * (3.0 - 2.0 * edge);

                float centerBoost = saturate(1.0 - dist / radius);
                float emissiveMul = lerp(0.85, 1.35, centerBoost);

                fixed3 rgb = _Tint.rgb * emissiveMul;
                fixed  a   = _Tint.a * edge;
                return fixed4(rgb, a);
            }
            ENDHLSL
        }
    }
}
