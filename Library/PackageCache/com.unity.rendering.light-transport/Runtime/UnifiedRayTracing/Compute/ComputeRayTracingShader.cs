using Unity.Mathematics;
using UnityEngine.Assertions;

namespace UnityEngine.Rendering.UnifiedRayTracing
{
    internal class ComputeRayTracingShader : IRayTracingShader
    {
        ComputeShader m_Shader;
        int m_KernelIndex;
        uint3 m_ThreadGroupSizes;
        private GraphicsBuffer m_DispatchBuffer; // This is a buffer to hold dispatch arguments when using non indirect dispatch

        internal ComputeRayTracingShader(ComputeShader shader, string dispatchFuncName, GraphicsBuffer dispatchBuffer)
        {
            m_Shader = shader;
            m_KernelIndex = m_Shader.FindKernel(dispatchFuncName);
            m_Shader.GetKernelThreadGroupSizes(m_KernelIndex,
                out m_ThreadGroupSizes.x, out m_ThreadGroupSizes.y, out m_ThreadGroupSizes.z);
            m_DispatchBuffer = dispatchBuffer;
        }

        public uint3 GetThreadGroupSizes()
        {
            return m_ThreadGroupSizes;
        }

        public void PopulateDispatchDimensionBuffer(CommandBuffer cmd, GraphicsBuffer dispatchDimensionsBuffer, uint3 dimensions)
        {
            Assert.IsTrue((dispatchDimensionsBuffer.target & GraphicsBuffer.Target.IndirectArguments) != 0);
            Assert.IsTrue((dispatchDimensionsBuffer.target & GraphicsBuffer.Target.Structured) != 0);
            Assert.IsTrue(dispatchDimensionsBuffer.count * dispatchDimensionsBuffer.stride == 24);
            uint3 workgroups = GraphicsHelpers.DivUp(dimensions, m_ThreadGroupSizes);
            cmd.SetBufferData(dispatchDimensionsBuffer, new uint[] { dimensions.x, dimensions.y, dimensions.z, workgroups.x, workgroups.y, workgroups.z });
        }

        public void SetAccelerationStructure(CommandBuffer cmd, string name, IRayTracingAccelStruct accelStruct)
        {
            var computeAccelStruct = accelStruct as ComputeRayTracingAccelStruct;
            Assert.IsNotNull(computeAccelStruct);

            computeAccelStruct.Bind(cmd, name, this);
        }

        public void SetIntParam(CommandBuffer cmd, int nameID, int val)
        {
            cmd.SetComputeIntParam(m_Shader, nameID, val);
        }

        public void SetFloatParam(CommandBuffer cmd, int nameID, float val)
        {
            cmd.SetComputeFloatParam(m_Shader, nameID, val);
        }

        public void SetVectorParam(CommandBuffer cmd, int nameID, Vector4 val)
        {
            cmd.SetComputeVectorParam(m_Shader, nameID, val);
        }

        public void SetMatrixParam(CommandBuffer cmd, int nameID, Matrix4x4 val)
        {
            cmd.SetComputeMatrixParam(m_Shader, nameID, val);
        }

        public void SetTextureParam(CommandBuffer cmd, int nameID, RenderTargetIdentifier rt)
        {
            cmd.SetComputeTextureParam(m_Shader, m_KernelIndex, nameID, rt);
        }

        public void SetBufferParam(CommandBuffer cmd, int nameID, GraphicsBuffer buffer)
        {
            cmd.SetComputeBufferParam(m_Shader, m_KernelIndex, nameID, buffer);
        }
        public void SetBufferParam(CommandBuffer cmd, int nameID, ComputeBuffer buffer)
        {
            cmd.SetComputeBufferParam(m_Shader, m_KernelIndex, nameID, buffer);
        }

        public void Dispatch(CommandBuffer cmd, GraphicsBuffer scratchBuffer, uint width, uint height, uint depth)
        {
            var requiredScratchSize = GetTraceScratchBufferRequiredSizeInBytes(width, height, depth);
            if (requiredScratchSize > 0 && (scratchBuffer == null || ((ulong)(scratchBuffer.count * scratchBuffer.stride) < requiredScratchSize)))
            {
                throw new System.ArgumentException("scratchBuffer size is too small");
            }

            if (requiredScratchSize > 0 && scratchBuffer.stride != 4)
            {
                throw new System.ArgumentException("scratchBuffer stride must be 4");
            }

            cmd.SetComputeBufferParam(m_Shader, m_KernelIndex, RadeonRays.SID.g_stack, scratchBuffer);

            uint workgroupsX = (uint)GraphicsHelpers.DivUp((int)width, m_ThreadGroupSizes.x);
            uint workgroupsY = (uint)GraphicsHelpers.DivUp((int)height, m_ThreadGroupSizes.y);
            uint workgroupsZ = (uint)GraphicsHelpers.DivUp((int)depth, m_ThreadGroupSizes.z);

            PopulateDispatchDimensionBuffer(cmd, m_DispatchBuffer, new uint3(width, height, depth));
            SetBufferParam(cmd, RadeonRays.SID.g_dispatch_dimensions, m_DispatchBuffer);
            cmd.DispatchCompute(m_Shader, m_KernelIndex, (int)workgroupsX, (int)workgroupsY, (int)workgroupsZ);
        }

        public ulong GetTraceScratchBufferRequiredSizeInBytes(uint width, uint height, uint depth)
        {
            uint rayCount = width * height * depth;
            return (RadeonRays.RadeonRaysAPI.GetTraceMemoryRequirements(rayCount) * 4);
        }

        public void Dispatch(CommandBuffer cmd, GraphicsBuffer scratchBuffer, GraphicsBuffer argsBuffer)
        {
            Assert.IsTrue((argsBuffer.target & GraphicsBuffer.Target.IndirectArguments) != 0);
            Assert.IsTrue((argsBuffer.target & GraphicsBuffer.Target.Structured) != 0);
            Assert.IsTrue(argsBuffer.count * argsBuffer.stride == 24);
            cmd.SetComputeBufferParam(m_Shader, m_KernelIndex, RadeonRays.SID.g_stack, scratchBuffer);
            SetBufferParam(cmd, RadeonRays.SID.g_dispatch_dimensions, argsBuffer);
            cmd.DispatchCompute(m_Shader, m_KernelIndex, argsBuffer, RayTracingHelper.k_GroupSizeByteOffset);
        }
    }
}


