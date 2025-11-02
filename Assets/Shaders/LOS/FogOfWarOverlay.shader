Shader "Custom/LOS/FogOfWarOverlay"
{
    Properties
    {
        _Darkness("Darkness", Range(0,1)) = 0.6
    }
    SubShader
    {
        Tags{ "RenderPipeline"="HDRenderPipeline" "Queue"="Transparent+500" "RenderType"="Transparent" }
        Cull Off ZWrite Off ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha
        Stencil
        {
            Ref 1
            Comp NotEqual // darken only where FOV stencil != 1
            Pass Keep
        }
        Pass
        {
            Tags { "LightMode" = "SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"   // gives UnityObjectToClipPos & standard matrices

            float _Darkness;

            struct appdata { float3 vertex : POSITION; };
            struct v2f     { float4 pos    : SV_POSITION; };

            v2f Vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(float4(v.vertex, 1.0));
                return o;
            }

            float4 Frag(v2f i) : SV_Target { return float4(0,0,0,_Darkness); }
            ENDHLSL
// Brief dev comment: remove SRP buffers; rely on UnityCG + UnityObjectToClipPos. Fixes 'CBUFFER_START' and matrix macro errors.
        }
    }
}