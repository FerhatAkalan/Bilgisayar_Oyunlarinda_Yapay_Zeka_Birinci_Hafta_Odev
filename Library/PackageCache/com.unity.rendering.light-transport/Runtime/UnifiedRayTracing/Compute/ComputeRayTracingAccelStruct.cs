using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine.Assertions;
using UnityEngine.Rendering.RadeonRays;

#if UNITY_EDITOR
using UnityEditor.Embree;
#endif

namespace UnityEngine.Rendering.UnifiedRayTracing
{

    internal class ComputeRayTracingAccelStruct : IRayTracingAccelStruct
    {
        internal ComputeRayTracingAccelStruct(
            AccelerationStructureOptions options, RayTracingResources resources,
            ReferenceCounter counter, int blasBufferInitialSizeBytes = 64 * 1024 * 1024)
        {
            m_CopyShader = resources.copyBuffer;

            RadeonRaysShaders shaders = new RadeonRaysShaders();
            shaders.bitHistogram = resources.bitHistogram;
            shaders.blockReducePart = resources.blockReducePart;
            shaders.blockScan = resources.blockScan;
            shaders.buildHlbvh = resources.buildHlbvh;
            shaders.reorderTriangleIndices = resources.reorderTriangleIndices;
            shaders.restructureBvh = resources.restructureBvh;
            shaders.scatter = resources.scatter;
            shaders.topLevelIntersector = resources.topLevelIntersector;
            shaders.intersector = resources.intersector;
            m_RadeonRaysAPI = new RadeonRaysAPI(shaders);

            m_AccelStructBuildFlags = ConvertFlagsToGpuBuild(options.buildFlags);

            #if UNITY_EDITOR
                m_UseCpuBuild = options.useCPUBuild;
                m_CpuBuildOptions = ConvertFlagsToCpuBuild(options.buildFlags);
            #endif

            m_Blases = new Dictionary<(int mesh, int subMeshIndex), MeshBlas>();

            var blasNodeCount = blasBufferInitialSizeBytes / RadeonRaysAPI.BvhNodeSizeInBytes();
            m_BlasBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, blasNodeCount, RadeonRaysAPI.BvhNodeSizeInBytes());
            m_BlasPositions = new BLASPositionsPool(resources.copyPositions, resources.copyBuffer);

            m_BlasAllocator = new BlockAllocator();
            m_BlasAllocator.Initialize(blasNodeCount);

            m_Counter = counter;
            m_Counter.Inc();

        }

        internal GraphicsBuffer topLevelBvhBuffer { get { return m_TopLevelAccelStruct?.topLevelBvh; } }
        internal GraphicsBuffer bottomLevelBvhBuffer { get { return m_TopLevelAccelStruct?.bottomLevelBvhs; } }
        internal GraphicsBuffer instanceInfoBuffer { get { return m_TopLevelAccelStruct?.instanceInfos; } }

        public void Dispose()
        {
            m_Counter.Dec();
            m_RadeonRaysAPI.Dispose();
            m_BlasBuffer.Dispose();
            m_BlasPositions.Dispose();
            m_BlasAllocator.Dispose();
            m_TopLevelAccelStruct?.Dispose();
        }

        public int AddInstance(MeshInstanceDesc meshInstance)
        {
            var blas = GetOrAllocateMeshBlas(meshInstance.mesh, meshInstance.subMeshIndex);
            blas.IncRef();

            FreeTopLevelAccelStruct();

            int handle = NewHandle();
            (new RadeonRaysInstance()).GetHashCode();
            m_RadeonInstances.Add(handle, new RadeonRaysInstance
            {
                geomKey = (meshInstance.mesh.GetHashCode(), meshInstance.subMeshIndex),
                blas = blas,
                instanceMask = meshInstance.mask,
                triangleCullingEnabled = meshInstance.enableTriangleCulling,
                invertTriangleCulling = meshInstance.frontTriangleCounterClockwise,
                userInstanceID = meshInstance.instanceID == 0xFFFFFFFF ? (uint)handle : meshInstance.instanceID,
                localToWorldTransform = ConvertTranform(meshInstance.localToWorldMatrix)
            });

            return handle;
        }

        public void RemoveInstance(int instanceHandle)
        {
            ReleaseHandle(instanceHandle);

            m_RadeonInstances.Remove(instanceHandle, out RadeonRaysInstance entry);
            var meshBlas = entry.blas;
            meshBlas.DecRef();
            if (meshBlas.IsUnreferenced())
                DeleteMeshBlas(entry.geomKey, meshBlas);

            FreeTopLevelAccelStruct();
        }

        public void ClearInstances()
        {
            m_FreeHandles.Clear();
            m_RadeonInstances.Clear();
            m_Blases.Clear();
            m_BlasPositions.Clear();
            var currentCapacity = m_BlasAllocator.capacity;
            m_BlasAllocator.Dispose();
            m_BlasAllocator = new BlockAllocator();
            m_BlasAllocator.Initialize(currentCapacity);

            FreeTopLevelAccelStruct();
        }

        public void UpdateInstanceTransform(int instanceHandle, Matrix4x4 localToWorldMatrix)
        {
            m_RadeonInstances[instanceHandle].localToWorldTransform = ConvertTranform(localToWorldMatrix);
            FreeTopLevelAccelStruct();
        }

        public void UpdateInstanceID(int instanceHandle, uint instanceID)
        {
            m_RadeonInstances[instanceHandle].userInstanceID = instanceID;
            FreeTopLevelAccelStruct();
        }

        public void UpdateInstanceMask(int instanceHandle, uint mask)
        {
            m_RadeonInstances[instanceHandle].instanceMask = mask;
            FreeTopLevelAccelStruct();
        }

        public void Build(CommandBuffer cmd, GraphicsBuffer scratchBuffer)
        {
            var requiredScratchSize = GetBuildScratchBufferRequiredSizeInBytes();
            if (requiredScratchSize > 0 && (scratchBuffer == null || ((ulong)(scratchBuffer.count * scratchBuffer.stride) < requiredScratchSize)))
            {
                throw new System.ArgumentException("scratchBuffer size is too small");
            }

            if (requiredScratchSize > 0 && scratchBuffer.stride != 4)
            {
                throw new System.ArgumentException("scratchBuffer stride must be 4");
            }

            if (m_TopLevelAccelStruct != null)
                return;

            CreateBvh(cmd, scratchBuffer);
        }

        public ulong GetBuildScratchBufferRequiredSizeInBytes()
        {
            return GetBvhBuildScratchBufferSizeInDwords() * 4;
        }

        private void FreeTopLevelAccelStruct()
        {
            m_TopLevelAccelStruct?.Dispose();
            m_TopLevelAccelStruct = null;
        }

        private MeshBlas GetOrAllocateMeshBlas(Mesh mesh, int subMeshIndex)
        {
            MeshBlas blas;
            if (m_Blases.TryGetValue((mesh.GetHashCode(), subMeshIndex), out blas))
                return blas;

            blas = new MeshBlas();
            AllocateBlas(mesh, subMeshIndex, blas);

            m_Blases[(mesh.GetHashCode(), subMeshIndex)] = blas;

            return blas;
        }

        void AllocateBlas(Mesh mesh, int submeshIndex, MeshBlas blas)
        {
            var bvhNodeSizeInDwords = RadeonRaysAPI.BvhNodeSizeInDwords();

            mesh.indexBufferTarget |= GraphicsBuffer.Target.Raw;
            mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            SubMeshDescriptor submeshDescriptor = mesh.GetSubMesh(submeshIndex);
            using var vertexBuffer = LoadPositionBuffer(mesh, out int stride, out int offset);
            using var indexBuffer = LoadIndexBuffer(mesh);

            var inputInfo = new MeshBuildInfo();
            inputInfo.vertices = vertexBuffer;
            inputInfo.verticesStartOffset = offset;
            inputInfo.baseVertex = submeshDescriptor.baseVertex + submeshDescriptor.firstVertex;
            inputInfo.triangleIndices = indexBuffer;
            inputInfo.indexFormat = mesh.indexFormat == IndexFormat.UInt32 ? RadeonRays.IndexFormat.Int32 : RadeonRays.IndexFormat.Int16;
            inputInfo.vertexCount = (uint)submeshDescriptor.vertexCount;
            inputInfo.triangleCount = (uint)submeshDescriptor.indexCount / 3;
            inputInfo.indicesStartOffset = submeshDescriptor.indexStart;
            inputInfo.baseIndex = -submeshDescriptor.firstVertex;
            inputInfo.vertexStride = (uint)stride;
            m_BlasPositions.Add(inputInfo, out blas.blasIndices, out blas.blasVertices);

            var meshBuildInfo = new MeshBuildInfo();
            meshBuildInfo.vertices = m_BlasPositions.VertexBuffer;
            meshBuildInfo.verticesStartOffset = blas.blasVertices.block.offset;
            meshBuildInfo.baseVertex = 0;
            meshBuildInfo.triangleIndices = m_BlasPositions.IndexBuffer;
            meshBuildInfo.vertexCount = (uint)blas.blasVertices.block.count/3;
            meshBuildInfo.triangleCount = (uint)blas.blasIndices.block.count/3;
            meshBuildInfo.indicesStartOffset = blas.blasIndices.block.offset;
            meshBuildInfo.baseIndex = 0;
            meshBuildInfo.vertexStride = 3;
            blas.buildInfo = meshBuildInfo;

            #if UNITY_EDITOR
            if (m_UseCpuBuild)
            {
                blas.indicesForCpuBuild = new List<int>();
                mesh.GetTriangles(blas.indicesForCpuBuild, submeshIndex, false);
                blas.verticesForCpuBuild = new List<Vector3>();
                mesh.GetVertices(blas.verticesForCpuBuild);
                blas.blasAllocation = BlockAllocator.Allocation.Invalid;
            }
            else
            #endif
            {
                var requirements = m_RadeonRaysAPI.GetMeshBuildMemoryRequirements(meshBuildInfo, m_AccelStructBuildFlags);
                var allocationNodeCount = (int)(requirements.resultSizeInDwords / (ulong)bvhNodeSizeInDwords);
                blas.blasAllocation = AllocateBlas(allocationNodeCount);
            }
        }

        private GraphicsBuffer LoadIndexBuffer(Mesh mesh)
        {
            if ((mesh.indexBufferTarget & GraphicsBuffer.Target.Raw) == 0 && (mesh.GetIndices(0) == null || mesh.GetIndices(0).Length == 0))
            {
                throw new Exception("Cant use a mesh buffer that is not raw and has no CPU index information.");
            }

            return mesh.GetIndexBuffer();
        }

        GraphicsBuffer LoadPositionBuffer(Mesh mesh, out int stride, out int offset)
        {
            VertexAttribute attribute = VertexAttribute.Position;

            if (!mesh.HasVertexAttribute(attribute))
            {
                throw new Exception("Cant use a mesh buffer that has no positions.");
            }

            int stream = mesh.GetVertexAttributeStream(attribute);

            stride = mesh.GetVertexBufferStride(stream) / 4;
            offset = mesh.GetVertexAttributeOffset(attribute) / 4;
            return mesh.GetVertexBuffer(stream);
        }

        private void DeleteMeshBlas((int mesh, int subMeshIndex) geomKey, MeshBlas blas)
        {
            m_BlasAllocator.FreeAllocation(blas.blasAllocation);
            m_BlasPositions.Remove(ref blas.blasIndices, ref blas.blasVertices);
            blas.blasAllocation = BlockAllocator.Allocation.Invalid;

            m_Blases.Remove(geomKey);
        }

        private ulong GetBvhBuildScratchBufferSizeInDwords()
        {
            #if UNITY_EDITOR
            if (m_UseCpuBuild)
                return 0;
            #endif

            var bvhNodeSizeInDwords = RadeonRaysAPI.BvhNodeSizeInDwords();
            ulong scratchBufferSize = 0;

            foreach (var meshBlas in m_Blases)
            {
                if (meshBlas.Value.bvhBuilt)
                    continue;

                var requirements = m_RadeonRaysAPI.GetMeshBuildMemoryRequirements(meshBlas.Value.buildInfo, m_AccelStructBuildFlags);
                Assert.AreEqual(requirements.resultSizeInDwords / (ulong)bvhNodeSizeInDwords, (ulong)meshBlas.Value.blasAllocation.block.count);
                scratchBufferSize = math.max(scratchBufferSize, requirements.buildScratchSizeInDwords);
            }

            var topLevelScratchSize = m_RadeonRaysAPI.GetSceneBuildMemoryRequirements((uint)m_RadeonInstances.Count).buildScratchSizeInDwords;
            scratchBufferSize = math.max(scratchBufferSize, topLevelScratchSize);
            scratchBufferSize = math.max(4, scratchBufferSize);

            return scratchBufferSize;
        }

        private void CreateBvh(CommandBuffer cmd, GraphicsBuffer scratchBuffer)
        {
            BuildMissingBottomLevelAccelStructs(cmd, scratchBuffer);
            BuildTopLevelAccelStruct(cmd, scratchBuffer);
        }

        private void BuildMissingBottomLevelAccelStructs(CommandBuffer cmd, GraphicsBuffer scratchBuffer)
        {
            foreach (var meshBlas in m_Blases.Values)
            {
                if (meshBlas.bvhBuilt)
                    continue;

                meshBlas.buildInfo.vertices = m_BlasPositions.VertexBuffer;
                meshBlas.buildInfo.triangleIndices = m_BlasPositions.IndexBuffer;

                #if UNITY_EDITOR
                if (m_UseCpuBuild)
                {
                    CpuBuildForBottomLevelAccelStruct(cmd, meshBlas);
                }
                else
                #endif
                {
                    m_RadeonRaysAPI.BuildMeshAccelStruct(
                    cmd,
                    meshBlas.buildInfo, m_AccelStructBuildFlags,
                    scratchBuffer, m_BlasBuffer, (uint)meshBlas.blasAllocation.block.offset, (uint)meshBlas.blasAllocation.block.count);
                }

                meshBlas.bvhBuilt = true;
            }

        }
        private void BuildTopLevelAccelStruct(CommandBuffer cmd, GraphicsBuffer scratchBuffer)
        {
            var radeonRaysInstances = new RadeonRays.Instance[m_RadeonInstances.Count];
            int i = 0;
            foreach (var instance in m_RadeonInstances.Values)
            {
                radeonRaysInstances[i].meshAccelStructOffset = (uint)instance.blas.blasAllocation.block.offset;
                radeonRaysInstances[i].localToWorldTransform = instance.localToWorldTransform;
                radeonRaysInstances[i].instanceMask = instance.instanceMask;
                radeonRaysInstances[i].vertexOffset = (uint)instance.blas.blasVertices.block.offset;
                radeonRaysInstances[i].indexOffset = (uint)instance.blas.blasIndices.block.offset;
                radeonRaysInstances[i].triangleCullingEnabled = instance.triangleCullingEnabled;
                radeonRaysInstances[i].invertTriangleCulling = instance.invertTriangleCulling;
                radeonRaysInstances[i].userInstanceID = instance.userInstanceID;
                i++;
            }

            m_TopLevelAccelStruct?.Dispose();

            #if UNITY_EDITOR
            if (m_UseCpuBuild)
                m_TopLevelAccelStruct = CpuBuildForTopLevelAccelStruct(cmd, radeonRaysInstances);
            else
            #endif
                m_TopLevelAccelStruct = m_RadeonRaysAPI.BuildSceneAccelStruct(cmd, m_BlasBuffer, radeonRaysInstances, m_AccelStructBuildFlags, scratchBuffer);
        }

#if UNITY_EDITOR
        void CpuBuildForBottomLevelAccelStruct(CommandBuffer cmd, MeshBlas blas)
        {
            var vertices = blas.verticesForCpuBuild;
            var indices = blas.indicesForCpuBuild;

            var prims = new GpuBvhPrimitiveDescriptor[blas.buildInfo.triangleCount];
            for (int i = 0; i < blas.buildInfo.triangleCount; ++i)
            {
                var triangleIndices = GetFaceIndices(indices, i);
                var triangle = GetTriangle(vertices, triangleIndices);

                AABB aabb = new AABB();
                aabb.Encapsulate(triangle.v0);
                aabb.Encapsulate(triangle.v1);
                aabb.Encapsulate(triangle.v2);

                prims[i].primID = (uint)i;
                prims[i].lowerBound = aabb.Min;
                prims[i].upperBound = aabb.Max;
            }

            blas.indicesForCpuBuild = null;
            blas.verticesForCpuBuild = null;

            var bvhBlob = GpuBvh.Build(m_CpuBuildOptions, prims);
            var bvhSizeInDwords = bvhBlob.Length;
            var bvhSizeInNodes = bvhBlob.Length / RadeonRaysAPI.BvhNodeSizeInDwords();
            blas.blasAllocation = AllocateBlas(bvhSizeInNodes);

            var bvhStartInDwords = blas.blasAllocation.block.offset * RadeonRaysAPI.BvhNodeSizeInDwords();
            cmd.SetBufferData(m_BlasBuffer, bvhBlob, 0, bvhStartInDwords, bvhSizeInDwords);

            // read mesh aabb from bvh header.
            blas.aabbForCpuBuild = new AABB();
            blas.aabbForCpuBuild.Min.x = math.asfloat(bvhBlob[4]);
            blas.aabbForCpuBuild.Min.y = math.asfloat(bvhBlob[5]);
            blas.aabbForCpuBuild.Min.z = math.asfloat(bvhBlob[6]);
            blas.aabbForCpuBuild.Max.x = math.asfloat(bvhBlob[7]);
            blas.aabbForCpuBuild.Max.y = math.asfloat(bvhBlob[8]);
            blas.aabbForCpuBuild.Max.z = math.asfloat(bvhBlob[9]);
        }

        TopLevelAccelStruct CpuBuildForTopLevelAccelStruct(CommandBuffer cmd, RadeonRays.Instance[] radeonRaysInstances)
        {
            var prims = new GpuBvhPrimitiveDescriptor[m_RadeonInstances.Count];
            int i = 0;
            foreach (var instance in m_RadeonInstances.Values)
            {
                var blas = instance.blas;
                AABB aabb = blas.aabbForCpuBuild;

                var m = ConvertTranform(instance.localToWorldTransform);
                var bounds = GeometryUtility.CalculateBounds(new Vector3[]
                { new Vector3(aabb.Min.x, aabb.Min.y, aabb.Max.z),
                  new Vector3(aabb.Min.x, aabb.Max.y, aabb.Min.z),
                  new Vector3(aabb.Min.x, aabb.Max.y, aabb.Max.z),
                  new Vector3(aabb.Max.x, aabb.Min.y, aabb.Max.z),
                  new Vector3(aabb.Max.x, aabb.Max.y, aabb.Min.z),
                  new Vector3(aabb.Max.x, aabb.Max.y, aabb.Max.z)
                  }, m);

                prims[i].primID = (uint)i;
                prims[i].lowerBound = bounds.min;
                prims[i].upperBound = bounds.max;
                i++;
            }

            if (m_RadeonInstances.Count != 0)
            {
                var bvhBlob = GpuBvh.Build(m_CpuBuildOptions, prims);
                var bvhSizeInDwords = bvhBlob.Length;
                var result = m_RadeonRaysAPI.CreateSceneAccelStructBuffers(m_BlasBuffer, (uint)bvhSizeInDwords, radeonRaysInstances);
                cmd.SetBufferData(result.topLevelBvh, (bvhBlob));

                return result;
            }
            else
            {
                return m_RadeonRaysAPI.CreateSceneAccelStructBuffers(m_BlasBuffer, 0, radeonRaysInstances);
            }
        }

        GpuBvhBuildOptions ConvertFlagsToCpuBuild(BuildFlags flags)
        {
            GpuBvhBuildQuality quality = GpuBvhBuildQuality.Medium;

            if ((flags & BuildFlags.PreferFastBuild) != 0 && (flags & BuildFlags.PreferFastTrace) == 0)
                quality = GpuBvhBuildQuality.Low;
            else if ((flags & BuildFlags.PreferFastTrace) != 0 && (flags & BuildFlags.PreferFastBuild) == 0)
                quality = GpuBvhBuildQuality.High;

            return new GpuBvhBuildOptions
            {
                quality = quality,
                minLeafSize = 1,
                maxLeafSize = 1,
                allowPrimitiveSplits = (quality == GpuBvhBuildQuality.High)
            };
        }
#endif

        RadeonRays.BuildFlags ConvertFlagsToGpuBuild(BuildFlags flags)
        {
            if ((BuildFlags.PreferFastBuild) != 0 && (flags & BuildFlags.PreferFastTrace) == 0)
                return RadeonRays.BuildFlags.PreferFastBuild;
            else
                return RadeonRays.BuildFlags.None;
        }

        public void Bind(CommandBuffer cmd, string name, IRayTracingShader shader)
        {
            shader.SetBufferParam(cmd, Shader.PropertyToID(name + "bvh"), topLevelBvhBuffer);
            shader.SetBufferParam(cmd, Shader.PropertyToID(name + "bottomBvhs"), bottomLevelBvhBuffer);
            shader.SetBufferParam(cmd, Shader.PropertyToID(name + "instanceInfos"), instanceInfoBuffer);
            shader.SetBufferParam(cmd, Shader.PropertyToID(name + "indexBuffer"), m_BlasPositions.IndexBuffer);
            shader.SetBufferParam(cmd, Shader.PropertyToID(name + "vertexBuffer"), m_BlasPositions.VertexBuffer);
            shader.SetIntParam(cmd, Shader.PropertyToID(name + "vertexStride"), 3);
        }

        static private RadeonRays.Transform ConvertTranform(Matrix4x4 input)
        {
            return new RadeonRays.Transform()
            {
                row0 = input.GetRow(0),
                row1 = input.GetRow(1),
                row2 = input.GetRow(2)
            };
        }

        static private Matrix4x4 ConvertTranform(RadeonRays.Transform input)
        {
            var m = new Matrix4x4();
            m.SetRow(0, input.row0);
            m.SetRow(1, input.row1);
            m.SetRow(2, input.row2);
            m.SetRow(3, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
            return m;
        }

        static int3 GetFaceIndices(List<int> indices, int triangleIdx)
        {
            return new int3(
                indices[3 * triangleIdx],
                indices[3 * triangleIdx + 1],
                indices[3 * triangleIdx + 2]);
        }

        struct Triangle
        {
            public float3 v0;
            public float3 v1;
            public float3 v2;
        };

        static Triangle GetTriangle(List<Vector3> vertices, int3 idx)
        {
            Triangle tri;
            tri.v0 = vertices[idx.x];
            tri.v1 = vertices[idx.y];
            tri.v2 = vertices[idx.z];
            return tri;
        }

        private BlockAllocator.Allocation AllocateBlas(int allocationNodeCount)
        {
            var allocation = m_BlasAllocator.Allocate(allocationNodeCount);
            if (!allocation.valid)
            {
                var oldBvhNodeCount = m_BlasAllocator.capacity;
                var newBvhNodeCount = m_BlasAllocator.Grow(m_BlasAllocator.capacity + allocationNodeCount);
                if ((ulong)newBvhNodeCount > ((ulong)2 * 1024 * 1024 * 1024) / (ulong)RadeonRaysAPI.BvhNodeSizeInBytes())
                    throw new System.OutOfMemoryException("Out of memory in BLAS buffer");

                var newBlasBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newBvhNodeCount, RadeonRaysAPI.BvhNodeSizeInBytes());
                GraphicsHelpers.CopyBuffer(m_CopyShader, m_BlasBuffer, 0, newBlasBuffer, 0, oldBvhNodeCount * RadeonRaysAPI.BvhNodeSizeInDwords());

                m_BlasBuffer.Dispose();
                m_BlasBuffer = newBlasBuffer;
                allocation = m_BlasAllocator.Allocate(allocationNodeCount);
                Assertions.Assert.IsTrue(allocation.valid);
            }

            return allocation;
        }

        uint m_HandleObfuscation = (uint)Random.Range(int.MinValue, int.MaxValue);

        int NewHandle()
        {
            if (m_FreeHandles.Count != 0)
                return (int)(m_FreeHandles.Dequeue() ^ m_HandleObfuscation);
            else
                return (int)((uint)m_RadeonInstances.Count ^ m_HandleObfuscation);
        }

        void ReleaseHandle(int handle)
        {
            m_FreeHandles.Enqueue((uint)handle ^ m_HandleObfuscation);
        }


        RadeonRaysAPI m_RadeonRaysAPI;
        RadeonRays.BuildFlags m_AccelStructBuildFlags = 0;
        #if UNITY_EDITOR
            bool m_UseCpuBuild = false;
            UnityEditor.Embree.GpuBvhBuildOptions m_CpuBuildOptions;
        #endif
        ReferenceCounter m_Counter;

        Dictionary<(int mesh, int subMeshIndex), MeshBlas> m_Blases;
        BlockAllocator m_BlasAllocator;
        GraphicsBuffer m_BlasBuffer;
        BLASPositionsPool m_BlasPositions;

        TopLevelAccelStruct? m_TopLevelAccelStruct = null;
        ComputeShader m_CopyShader;

        Dictionary<int, RadeonRaysInstance> m_RadeonInstances = new ();
        Queue<uint> m_FreeHandles = new();

        class RadeonRaysInstance
        {
            public (int mesh, int subMeshIndex) geomKey;
            public MeshBlas blas;
            public uint instanceMask;
            public bool triangleCullingEnabled;
            public bool invertTriangleCulling;
            public uint userInstanceID;
            public RadeonRays.Transform localToWorldTransform;
        }

        private class MeshBlas
        {
            public MeshBuildInfo buildInfo;
            public BlockAllocator.Allocation blasAllocation;
            public BlockAllocator.Allocation blasIndices;
            public BlockAllocator.Allocation blasVertices;
            #if UNITY_EDITOR
                public AABB aabbForCpuBuild;
                public List<int> indicesForCpuBuild;
                public List<Vector3> verticesForCpuBuild;
            #endif
            public bool bvhBuilt = false;

            private uint refCount = 0;
            public void IncRef() { refCount++; }
            public void DecRef() { refCount--; }
            public bool IsUnreferenced() { return refCount == 0; }
        }
    }

}
