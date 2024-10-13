using Unity.Mathematics;
using UnityEngine.Assertions;

namespace UnityEngine.Rendering.UnifiedRayTracing
{
    internal class HardwareRayTracingShader : IRayTracingShader
    {
        RayTracingShader m_Shader;
        string m_ShaderDispatchFuncName;

        internal HardwareRayTracingShader(RayTracingShader shader, string dispatchFuncName, GraphicsBuffer unused)
        {
            m_Shader = shader;
            m_ShaderDispatchFuncName = dispatchFuncName;
        }

        public uint3 GetThreadGroupSizes()
        {
            return new uint3(0, 0, 0);
        }

        public void PopulateDispatchDimensionBuffer(CommandBuffer cmd, GraphicsBuffer dispatchDimensionsBuffer, uint3 dimensions)
        {
            Assert.IsTrue((dispatchDimensionsBuffer.target & GraphicsBuffer.Target.IndirectArguments) != 0);
            Assert.IsTrue((dispatchDimensionsBuffer.target & GraphicsBuffer.Target.Structured) != 0);
            Assert.IsTrue(dispatchDimensionsBuffer.count * dispatchDimensionsBuffer.stride == 24);
            cmd.SetBufferData(dispatchDimensionsBuffer, new uint[] { dimensions.x, dimensions.y, dimensions.z, 0, 0, 0 });
        }

        public void SetAccelerationStructure(CommandBuffer cmd, string name, IRayTracingAccelStruct accelStruct)
        {
            cmd.SetRayTracingShaderPass(m_Shader, "RayTracing");

            var hwAccelStruct = accelStruct as HardwareRayTracingAccelStruct;
            Assert.IsNotNull(hwAccelStruct);
            cmd.SetRayTracingAccelerationStructure(m_Shader, Shader.PropertyToID(name+"accelStruct"), hwAccelStruct.accelStruct);
        }

        public void SetIntParam(CommandBuffer cmd, int nameID, int val)
        {
            cmd.SetRayTracingIntParam(m_Shader, nameID, val);
        }

        public void SetFloatParam(CommandBuffer cmd, int nameID, float val)
        {
            cmd.SetRayTracingFloatParam(m_Shader, nameID, val);
        }

        public void SetVectorParam(CommandBuffer cmd, int nameID, Vector4 val)
        {
            cmd.SetRayTracingVectorParam(m_Shader, nameID, val);
        }

        public void SetMatrixParam(CommandBuffer cmd, int nameID, Matrix4x4 val)
        {
            cmd.SetRayTracingMatrixParam(m_Shader, nameID, val);
        }

        public void SetTextureParam(CommandBuffer cmd, int nameID, RenderTargetIdentifier rt)
        {
            cmd.SetRayTracingTextureParam(m_Shader, nameID, rt);
        }

        public void SetBufferParam(CommandBuffer cmd, int nameID, GraphicsBuffer buffer)
        {
            cmd.SetRayTracingBufferParam(m_Shader, nameID, buffer);
        }

        public void SetBufferParam(CommandBuffer cmd, int nameID, ComputeBuffer buffer)
        {
            cmd.SetRayTracingBufferParam(m_Shader, nameID, buffer);
        }

        public void Dispatch(CommandBuffer cmd, GraphicsBuffer scratchBuffer, uint width, uint height, uint depth)
        {
            cmd.DispatchRays(m_Shader, m_ShaderDispatchFuncName, width, height, depth, null);
        }

        public ulong GetTraceScratchBufferRequiredSizeInBytes(uint width, uint height, uint depth)
        {
            return 0;
        }

        public void Dispatch(CommandBuffer cmd, GraphicsBuffer scratchBuffer, GraphicsBuffer argsBuffer)
        {
            Assert.IsTrue((argsBuffer.target & GraphicsBuffer.Target.IndirectArguments) != 0);
            Assert.IsTrue((argsBuffer.target & GraphicsBuffer.Target.Structured) != 0);
            Assert.IsTrue(argsBuffer.count * argsBuffer.stride == 24);
            cmd.DispatchRays(m_Shader, m_ShaderDispatchFuncName, argsBuffer, RayTracingHelper.k_DimensionByteOffset);
        }
    }
}


