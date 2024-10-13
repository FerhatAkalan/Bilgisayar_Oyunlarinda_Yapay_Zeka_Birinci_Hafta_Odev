using System;
using Unity.Mathematics;
using UnityEngine.Assertions;
using UnityEngine.Rendering.RadeonRays;

namespace UnityEngine.Rendering.UnifiedRayTracing
{
    internal class BLASPositionsPool : IDisposable
    {
        public BLASPositionsPool(ComputeShader copyPositionsShader, ComputeShader copyShader)
        {
            m_VerticesBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, intialVertexCount*3, 4);
            m_VerticesAllocator = new BlockAllocator();
            m_VerticesAllocator.Initialize(intialVertexCount);

            m_IndicesBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, initialIndexCount, 4);
            m_IndicesAllocator = new BlockAllocator();
            m_IndicesAllocator.Initialize(initialIndexCount);

            m_CopyPositionsShader = copyPositionsShader;
            m_CopyIndicesKernel = m_CopyPositionsShader.FindKernel("CopyIndexBuffer");
            m_CopyIndices16Kernel = m_CopyPositionsShader.FindKernel("CopyIndexBuffer16");
            m_CopyVerticesKernel = m_CopyPositionsShader.FindKernel("CopyVertexBuffer");
            m_CopyShader = copyShader;
        }

        public void Dispose()
        {
            m_VerticesBuffer.Dispose();
            m_IndicesBuffer.Dispose();
            m_VerticesAllocator.Dispose();
            m_IndicesAllocator.Dispose();
        }

        public GraphicsBuffer IndexBuffer { get { return m_IndicesBuffer; } }
        public GraphicsBuffer VertexBuffer { get { return m_VerticesBuffer; } }

        public void Clear()
        {
            m_IndicesBuffer.Dispose();
            m_IndicesBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, initialIndexCount, 4);
            m_IndicesAllocator.Dispose();
            m_IndicesAllocator = new BlockAllocator();
            m_IndicesAllocator.Initialize(initialIndexCount);

            m_VerticesBuffer.Dispose();
            m_VerticesBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, intialVertexCount * 3, 4);
            m_VerticesAllocator.Dispose();
            m_VerticesAllocator = new BlockAllocator();
            m_VerticesAllocator.Initialize(intialVertexCount*3);
        }

        const int initialIndexCount = 1000;
        const int intialVertexCount = 1000;

        GraphicsBuffer m_VerticesBuffer;
        BlockAllocator m_VerticesAllocator;
        GraphicsBuffer m_IndicesBuffer;
        BlockAllocator m_IndicesAllocator;
        ComputeShader m_CopyPositionsShader;
        int m_CopyIndicesKernel;
        int m_CopyIndices16Kernel;
        int m_CopyVerticesKernel;
        ComputeShader m_CopyShader;
        const uint kItemsPerWorkgroup = 48u * 128u;


        public void Add(RadeonRays.MeshBuildInfo info, out BlockAllocator.Allocation indicesAllocation, out BlockAllocator.Allocation verticesAllocation)
        {
            indicesAllocation = m_IndicesAllocator.Allocate((int)info.triangleCount*3);
            if (!indicesAllocation.valid)
            {
                indicesAllocation = m_IndicesAllocator.GrowAndAllocate((int)info.triangleCount * 3, out int oldCapacity, out int newCapacity);

                var newIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newCapacity, 4);
                GraphicsHelpers.CopyBuffer(m_CopyShader, m_IndicesBuffer, 0, newIndexBuffer, 0, oldCapacity);
                m_IndicesBuffer.Dispose();
                m_IndicesBuffer = newIndexBuffer;
            }

            verticesAllocation = m_VerticesAllocator.Allocate((int)info.vertexCount*3);
            if (!verticesAllocation.valid)
            {
                verticesAllocation = m_VerticesAllocator.GrowAndAllocate((int)info.vertexCount * 3, out int oldCapacity, out int newCapacity);

                var newVertexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newCapacity, 4);
                GraphicsHelpers.CopyBuffer(m_CopyShader, m_VerticesBuffer, 0, newVertexBuffer, 0, oldCapacity);
                m_VerticesBuffer.Dispose();
                m_VerticesBuffer = newVertexBuffer;
            }

            var cmd = new CommandBuffer();

            var copyIndicesKernel = info.indexFormat == RadeonRays.IndexFormat.Int32 ? m_CopyIndicesKernel : m_CopyIndices16Kernel;
            uint indexCount = info.triangleCount * 3;
            cmd.SetComputeIntParam(m_CopyPositionsShader, "_InputIndexCount", (int)indexCount);
            cmd.SetComputeIntParam(m_CopyPositionsShader, "_InputIndexBufferOffset", info.indicesStartOffset);
            cmd.SetComputeIntParam(m_CopyPositionsShader, "_InputBaseIndex", info.baseIndex);
            cmd.SetComputeIntParam(m_CopyPositionsShader, "_OutputIndexBufferOffset", indicesAllocation.block.offset);
            cmd.SetComputeBufferParam(m_CopyPositionsShader, copyIndicesKernel, "_InputIndexBuffer", info.triangleIndices);
            cmd.SetComputeBufferParam(m_CopyPositionsShader, copyIndicesKernel, "_OutputIndexBuffer", m_IndicesBuffer);
            cmd.DispatchCompute(m_CopyPositionsShader, copyIndicesKernel, (int)Common.CeilDivide(indexCount, kItemsPerWorkgroup), 1, 1);

            cmd.SetComputeIntParam(m_CopyPositionsShader, "_InputPosBufferCount", (int)info.vertexCount);
            cmd.SetComputeIntParam(m_CopyPositionsShader, "_InputPosBufferOffset", info.verticesStartOffset);
            cmd.SetComputeIntParam(m_CopyPositionsShader, "_InputBaseVertex", info.baseVertex);
            cmd.SetComputeIntParam(m_CopyPositionsShader, "_InputPosBufferStride", (int)info.vertexStride);
            cmd.SetComputeIntParam(m_CopyPositionsShader, "_OutputPosBufferOffset", verticesAllocation.block.offset);
            cmd.SetComputeBufferParam(m_CopyPositionsShader, m_CopyVerticesKernel, "_InputPosBuffer", info.vertices);
            cmd.SetComputeBufferParam(m_CopyPositionsShader, m_CopyVerticesKernel, "_OutputPosBuffer", m_VerticesBuffer);
            cmd.DispatchCompute(m_CopyPositionsShader, m_CopyVerticesKernel, (int)Common.CeilDivide(info.vertexCount, kItemsPerWorkgroup), 1, 1);

            Graphics.ExecuteCommandBuffer(cmd);
        }

        public void Remove(ref BlockAllocator.Allocation indicesAllocation, ref BlockAllocator.Allocation verticesAllocation)
        {
            m_IndicesAllocator.FreeAllocation(indicesAllocation);
            m_VerticesAllocator.FreeAllocation(verticesAllocation);

            indicesAllocation = BlockAllocator.Allocation.Invalid;
            verticesAllocation = BlockAllocator.Allocation.Invalid;
        }
    }
}


