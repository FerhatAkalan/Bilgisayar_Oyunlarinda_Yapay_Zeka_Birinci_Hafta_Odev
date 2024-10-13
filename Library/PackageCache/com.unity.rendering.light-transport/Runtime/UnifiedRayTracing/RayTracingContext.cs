using System;
using System.IO;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityEngine.Rendering.UnifiedRayTracing
{
    internal enum RayTracingBackend { Hardware = 0, Compute = 1}

    internal class RayTracingContext : IDisposable
    {
        public RayTracingContext(RayTracingBackend backend, RayTracingResources resources)
        {
            if (!IsBackendSupported(backend))
                throw new System.InvalidOperationException("Unsupported backend: " + backend.ToString());

            BackendType = backend;
            if (backend == RayTracingBackend.Hardware)
                m_Backend = new HardwareRayTracingBackend(resources);
            else if (backend == RayTracingBackend.Compute)
                m_Backend = new ComputeRayTracingBackend(resources);

            var MB = 1024 * 1024;
            var geometryPoolDesc = new GeometryPoolDesc()
            {
                vertexPoolByteSize = 64 * MB,
                indexPoolByteSize = 32 * MB,
                meshChunkTablesByteSize = 4 * MB
            };

            Resources = resources;

            m_DispatchBuffer = RayTracingHelper.CreateDispatchDimensionBuffer();
        }
        public void Dispose()
        {
            if (m_AccelStructCounter.value != 0)
            {
                Debug.LogError("Memory Leak. Please call .Dispose() on all the IAccelerationStructure resources "+
                               "that have been created with this context before calling RayTracingContext.Dispose()");
            }
            m_DispatchBuffer?.Release();
        }

        public RayTracingResources Resources;

        static public bool IsBackendSupported(RayTracingBackend backend)
        {
            if (backend == RayTracingBackend.Hardware)
                return SystemInfo.supportsRayTracing;
            else if (backend == RayTracingBackend.Compute)
                return SystemInfo.supportsComputeShaders;

            return false;
        }

        public IRayTracingShader CreateRayTracingShader(Object shader) =>
            m_Backend.CreateRayTracingShader(shader, "MainRayGenShader", m_DispatchBuffer);

        public IRayTracingShader CreateRayTracingShader(RayTracingShader rtShader)
        {
            var shader = m_Backend.CreateRayTracingShader(rtShader, "MainRayGenShader", m_DispatchBuffer);
            return shader;
        }
        public IRayTracingShader CreateRayTracingShader(ComputeShader computeShader)
        {
            var shader = m_Backend.CreateRayTracingShader(computeShader, "MainRayGenShader", m_DispatchBuffer);
            return shader;
        }

        public IRayTracingAccelStruct CreateAccelerationStructure(AccelerationStructureOptions options)
        {
            var accelStruct = m_Backend.CreateAccelerationStructure(options, m_AccelStructCounter);
            return accelStruct;
        }

#if UNITY_EDITOR
        public IRayTracingShader LoadRayTracingShader(string fileName)
        {
            string fullFileName = BackendHelpers.GetFileNameOfShader(BackendType, fileName);
            Type shaderType = BackendHelpers.GetTypeOfShader(BackendType);
            Object asset = AssetDatabase.LoadAssetAtPath(fullFileName, shaderType);
            return CreateRayTracingShader(asset);
        }
#endif

        public RayTracingBackend BackendType { get; private set; }

        private IRayTracingBackend m_Backend;
        private GeometryPool m_GeometryPool;
        private ReferenceCounter m_AccelStructCounter = new ReferenceCounter();
        private GraphicsBuffer m_DispatchBuffer;
    }

    [System.Flags]
    internal enum BuildFlags
    {
        None = 0,
        PreferFastTrace = 1 << 0,
        PreferFastBuild = 1 << 1,
        MinimizeMemory = 1 << 2
    }

    internal class AccelerationStructureOptions
    {
        public BuildFlags buildFlags = 0;
        public bool enableCompaction = false;
        #if UNITY_EDITOR
        public bool useCPUBuild = false;
        #endif
    }

    internal class ReferenceCounter
    {
        public ulong value = 0;

        public void Inc() { value++; }
        public void Dec() { value--; }
    }

    internal class RayTracingHelper
    {
        public const GraphicsBuffer.Target ScratchBufferTarget = GraphicsBuffer.Target.Structured;

        static public readonly uint k_DimensionByteOffset = 0;  // offset into the DispatchDimensionBuffer where the dimensions reside
        static public readonly uint k_GroupSizeByteOffset = 12; // offset into the DispatchDimensionBuffer where the group sizes reside
        static public GraphicsBuffer CreateDispatchDimensionBuffer()
        {
            return new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource, 6, sizeof(uint));
        }

        static public GraphicsBuffer CreateScratchBufferForBuildAndDispatch(
            IRayTracingAccelStruct accelStruct,
            IRayTracingShader shader, uint dispatchWidth, uint dispatchHeight, uint dispatchDepth)
        {
            var sizeInBytes = System.Math.Max(accelStruct.GetBuildScratchBufferRequiredSizeInBytes(), shader.GetTraceScratchBufferRequiredSizeInBytes(dispatchWidth, dispatchHeight, dispatchDepth));
            if (sizeInBytes == 0)
                return null;

            return new GraphicsBuffer(GraphicsBuffer.Target.Structured, (int)(sizeInBytes / 4), 4);
        }

        static public GraphicsBuffer CreateScratchBufferForBuild(
            IRayTracingAccelStruct accelStruct)
        {
            var sizeInBytes = accelStruct.GetBuildScratchBufferRequiredSizeInBytes();
            return new GraphicsBuffer(GraphicsBuffer.Target.Structured, (int)(sizeInBytes / 4), 4);
        }

        static public void ResizeScratchBufferForTrace(
            IRayTracingShader shader, uint dispatchWidth, uint dispatchHeight, uint dispatchDepth, ref GraphicsBuffer scratchBuffer)
        {
            var sizeInBytes = shader.GetTraceScratchBufferRequiredSizeInBytes(dispatchWidth, dispatchHeight, dispatchDepth);
            if (sizeInBytes == 0)
                return;

            Debug.Assert(scratchBuffer == null || scratchBuffer.target == ScratchBufferTarget);

            if (scratchBuffer == null || (ulong)(scratchBuffer.count*scratchBuffer.stride) < sizeInBytes)
            {
                scratchBuffer?.Dispose();
                scratchBuffer = new GraphicsBuffer(ScratchBufferTarget, (int)(sizeInBytes / 4), 4);
            }
        }

        static public void ResizeScratchBufferForBuild(
            IRayTracingAccelStruct accelStruct, ref GraphicsBuffer scratchBuffer)
        {
            var sizeInBytes = accelStruct.GetBuildScratchBufferRequiredSizeInBytes();
            if (sizeInBytes == 0)
                return;

            Debug.Assert(scratchBuffer == null || scratchBuffer.target == ScratchBufferTarget);

            if (scratchBuffer == null || (ulong)(scratchBuffer.count * scratchBuffer.stride) < sizeInBytes)
            {
                scratchBuffer?.Dispose();
                scratchBuffer = new GraphicsBuffer(ScratchBufferTarget, (int)(sizeInBytes / 4), 4);
            }
        }
    }

}
