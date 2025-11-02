Shader "Custom/LOS/FOVStencilWrite"
{
    Properties
    {
    _FillColor("Fill Color", Color) = (0.95,0.97,1.0,0.45)   // cool bright
        _ShowFill("Show Fill", Float) = 1
        _FillMul("Fill Intensity", Float) = 1.15               // slight boost
    _Feather("Edge Feather (m)", Float) = 0.15
    _Radius("Radius", Float) = 12
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
            v2f Vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(float4(v.vertex,1));
                return o;
            }
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
            float  _ShowFill, _FillMul, _Feather, _Radius;
            struct appdata { float3 vertex : POSITION; };
            struct v2f
            {
                float4 pos     : SV_POSITION;
                float2 localXZ : TEXCOORD0;
            };
            v2f Vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(float4(v.vertex,1));
                o.localXZ = v.vertex.xz;
                return o;
            }
            fixed4 FragFill(v2f i) : SV_Target
            {
                if (_ShowFill < 0.5) return 0;

                float feather = max(1e-3, _Feather);
                float radius  = max(1e-3, _Radius);
                float dist    = length(i.localXZ);

                // Edge softness toward the boundary ring.
                float edge    = saturate((radius - dist) / feather);
                edge = edge * edge * (3.0 - 2.0 * edge); // smoothstep

                // Slight center boost for more natural falloff.
                float center  = saturate(1.0 - (dist * dist) / (radius * radius + 1e-4));
                float fillMul = _FillMul * lerp(0.75, 1.15, center);

                return _FillColor * (fillMul * edge);
            }
            ENDHLSL
// Brief dev comment: swap SRP transforms for UnityCG path. Fixes 'TransformObjectToHClip' and 'UNITY_MATRIX_M/GetObjectToWorldMatrix' errors.
        }
    }
}