Shader "Hidden/SpawnHoleMask"
{
    SubShader
    {
        Tags { "Queue"="Transparent-20" "RenderType"="Transparent" "IgnoreProjector"="True" }

        Cull Off
        ZWrite Off
        ZTest Always
        ColorMask 0

        Stencil
        {
            Ref 1
            Comp Always
            Pass Replace
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
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return 0;
            }
            ENDCG
        }
    }
}
// SRP-agnostic stencil writer. Same render queue and behavior as before.