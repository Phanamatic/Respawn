using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Custom pass that re-renders player meshes behind transparent occluders so they stay visible.
/// </summary>
public sealed class PlayerOccludedRevealPass : CustomPass
{
    [Tooltip("Only renderers on these layers are re-drawn.")]
    public LayerMask playerLayer = 0;

    static readonly ShaderTagId[] ShaderTags =
    {
        new ShaderTagId("Forward"),
        new ShaderTagId("ForwardOnly"),
        new ShaderTagId("SRPDefaultUnlit"),
    };

    FilteringSettings _filtering;
    RenderStateBlock _stateBlock;

    protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
    {
        _stateBlock = new RenderStateBlock(RenderStateMask.Depth)
        {
            depthState = new DepthState(writeEnabled: false, compareFunction: CompareFunction.GreaterEqual)
        };
        UpdateFiltering();
    }

    protected override void Execute(CustomPassContext ctx)
    {
        UpdateFiltering();
        if (_filtering.layerMask == 0) return;

        var drawingSettings = CreateDrawingSettings(ShaderTags, ctx.hdCamera, SortingCriteria.CommonTransparent);
        ctx.renderContext.DrawRenderers(ctx.cullingResults, ref drawingSettings, ref _filtering, ref _stateBlock);
    }

    protected override void Cleanup() { }

    void UpdateFiltering()
    {
        int mask = playerLayer.value;
        _filtering = new FilteringSettings(RenderQueueRange.transparent, mask);
    }
}
