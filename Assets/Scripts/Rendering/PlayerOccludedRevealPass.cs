using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// HDRP custom pass that re-renders player meshes so they remain visible through transparent occluders.
/// </summary>
[System.Serializable]
public sealed class PlayerOccludedRevealPass : CustomPass
{
    [Tooltip("Layer mask containing player renderers that should be re-drawn through occluders.")]
    public LayerMask revealLayer = 0;

    protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd) { }

    protected override void Execute(CustomPassContext ctx)
    {
        if (revealLayer == 0) return;

        var desc = new RendererListDesc(HDShaderPassNames.s_ForwardAndForwardOnlyPassNames, ctx.cullingResults, ctx.hdCamera.camera)
        {
            sortingCriteria = SortingCriteria.CommonTransparent,
            renderQueueRange = RenderQueueRange.all,
            layerMask = revealLayer,
            stateBlock = new RenderStateBlock(RenderStateMask.Depth)
            {
                depthState = new DepthState(writeEnabled: false, compareFunction: CompareFunction.GreaterEqual)
            }
        };

        CustomPassUtils.DrawRenderers(ctx, desc);
    }

    protected override void Cleanup() { }
}
