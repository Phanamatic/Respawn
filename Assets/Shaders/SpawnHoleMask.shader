Shader "Hidden/SpawnHoleMask"
{
    SubShader
    {
        Tags { "Queue"="Transparent+10" "RenderType"="Transparent" }
        ZWrite Off
        ZTest LEqual
        Blend One One // no color written (ColorMask 0), blend irrelevant
        ColorMask 0
        Stencil
        {
            Ref 1
            Comp Always
            Pass Replace
            Fail Replace
            ZFail Replace
        }
        Pass { }
    }
    Fallback Off
}
// Writes stencil only; used by hole quads to punch out the area.