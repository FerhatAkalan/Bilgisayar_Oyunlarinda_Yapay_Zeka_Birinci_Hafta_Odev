#define WIDTH_OFFSET 0
#define HEIGHT_OFFSET 1
#define DEPTH_OFFSET 2
StructuredBuffer<uint> g_Dimensions;

#ifndef UNIFIED_RT_RAYGEN_FUNC_NAME
#define UNIFIED_RT_RAYGEN_FUNC_NAME RayGenExecute
#endif

#pragma kernel MainRayGenShader
[numthreads(UNIFIED_RT_GROUP_SIZE_X, UNIFIED_RT_GROUP_SIZE_Y, 1)]
void MainRayGenShader(
    in uint3 gidx: SV_DispatchThreadID,
    in uint lidx : SV_GroupIndex)
{
    if (gidx.x >= g_Dimensions[WIDTH_OFFSET] || gidx.y >= g_Dimensions[HEIGHT_OFFSET] || gidx.z >= g_Dimensions[DEPTH_OFFSET])
        return;

    UnifiedRT::DispatchInfo dispatchInfo;
    dispatchInfo.dispatchThreadID = gidx;
    dispatchInfo.dispatchDimensionsInThreads = int3(g_Dimensions[WIDTH_OFFSET], g_Dimensions[HEIGHT_OFFSET], g_Dimensions[DEPTH_OFFSET]);
    dispatchInfo.localThreadIndex = lidx;
    dispatchInfo.globalThreadIndex = gidx.x + gidx.y * g_Dimensions[WIDTH_OFFSET] + gidx.z * (g_Dimensions[WIDTH_OFFSET] * g_Dimensions[HEIGHT_OFFSET]);

    UNIFIED_RT_RAYGEN_FUNC_NAME(dispatchInfo);
}
