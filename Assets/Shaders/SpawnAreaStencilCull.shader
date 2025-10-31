Shader "Unlit/SpawnAreaStencilCull"
{
    Properties
    {
        _Color     ("Color", Color) = (0,1,0,0.6)
        _Glow      ("Glow Strength", Range(0,3)) = 0.85
        _GlowWidth ("Glow Width (UV)", Range(0.001,0.2)) = 0.02
    }
    SubShader
    {
        Tags { "Queue"="Transparent+10" "RenderType"="Transparent" "IgnoreProjector"="True" }
        LOD 100

        Cull Off
        ZWrite Off
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha

        Stencil
        {
            Ref 1
            Comp NotEqual
            Pass Keep
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
            };

            fixed4 _Color;
            float  _Glow;
            float  _GlowWidth;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 col = _Color;

                float2 uv = i.uv;
                float d = min(min(uv.x, uv.y), min(1.0 - uv.x, 1.0 - uv.y));
                float glow = saturate(1.0 - d / max(1e-4, _GlowWidth));
                col.rgb += col.rgb * glow * _Glow;

                return col;
            }
            ENDCG
        }
    }

    Fallback "Unlit/Color"
}
// SRP-agnostic transparent fill with stencil cut-out and subtle neon rim.