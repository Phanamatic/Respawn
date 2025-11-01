Shader "Custom/LOS/FOVStencilWrite"
{
    Properties
    {
        _FillColor("Fill Color", Color) = (1,0.98,0.85,0.40)   // warm bright
        _ShowFill("Show Fill", Float) = 1
        _FillMul("Fill Intensity", Float) = 1.15               // slight boost
        _Feather("Edge Feather (m)", Float) = 0.15
    }
    SubShader
    {
        Tags{ "RenderPipeline"="HDRenderPipeline" "Queue"="Transparent-100" "RenderType"="Transparent" }
        Cull Off ZWrite Off ZTest LEqual

        // PASS 0: stencil write only
        ColorMask 0
        Stencil { Ref 1 Comp Always Pass Replace }
        Pass
        {
            Tags { "LightMode" = "SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            struct appdata { float3 vertex : POSITION; };
            struct v2f     { float4 pos    : SV_POSITION; };
            v2f Vert(appdata v){ v2f o; o.pos = UnityObjectToClipPos(float4(v.vertex,1)); return o; }
            float4 Frag(v2f i) : SV_Target { return float4(0,0,0,0); }
            ENDHLSL
        }

        // PASS 1: visible fill inside FOV (additive brighten)
        Blend One One
        ColorMask RGBA
        ZWrite Off ZTest LEqual                   // draw on floor/geo only
        Stencil { Ref 1 Comp Always Pass Keep }
        Pass
        {
            Tags { "LightMode" = "SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment FragFill
            #include "UnityCG.cginc"
            fixed4 _FillColor;
            float  _ShowFill, _FillMul, _Feather;
            struct appdata { float3 vertex : POSITION; };
            struct v2f     { float4 pos    : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f Vert(appdata v){ v2f o; o.pos = UnityObjectToClipPos(float4(v.vertex,1)); o.uv = float2(0,0); return o; }
            fixed4 FragFill(v2f i) : SV_Target
            {
                // small edge feather to hide ray fan facets
                float a = saturate(_Feather > 0 ? 1.0 : 1.0);
                return (_ShowFill > 0.5) ? _FillColor * (_FillMul * a) : 0;
            }
            ENDHLSL
        }
    }
}