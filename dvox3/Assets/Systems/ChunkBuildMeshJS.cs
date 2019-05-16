using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using UnityEngine.Analytics;
using VoxelMaster;

public class ChunkBuildMeshJS : JobComponentSystem
{
    
    BeginSimulationEntityCommandBufferSystem ecbSystem;
    static Entity blockPrefab;
    static RenderMesh cubeRenderMesh;
    static Mesh cubeMesh = null;

    private static Mesh GetCubeMesh()
    {
        if (!cubeMesh) 
        { 
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            GameObject.Destroy(go);
            cubeMesh = mesh;
        }
    
        return cubeMesh;
    }

    private static Material GetCubeMaterial()
    {
        return Resources.Load("cubeMaterial", typeof(Material)) as Material;
    }

    private static int FlattenToIdx(int x, int y, int z, int ChunkSize)
    {
        if (x < 0 || x >= ChunkSize)
            return -1; //out of bounds

        if (y < 0 || y >= ChunkSize)
            return -1; //out of bounds
       
        if (z < 0 || z >= ChunkSize)
            return -1; //out of bounds
        
        int idx = z + (y * ChunkSize) + (x * ChunkSize * ChunkSize);
        return idx;
    }
    
    struct ChunkBuildMeshJob : IJobForEachWithEntity<ChunkPosition, ChunkFinishedGenerate>
    {
        [ReadOnly]
        public EntityCommandBuffer.Concurrent commandBuffer;
        public int ChunkSize;
        
        [NativeDisableParallelForRestriction]
        public BufferFromEntity<ChunkBufferData> cbd;

        public void Execute(Entity entity, int index, [ReadOnly] ref ChunkPosition chunkPos, [ReadOnly] ref ChunkFinishedGenerate chunk)
        {
            DynamicBuffer<ChunkBufferData> buffer = cbd[entity];
            
            for (int x = 0; x < ChunkSize; x++)
            {
                for (int y = 0; y < ChunkSize; y++)
                {
                    for (int z = 0; z < ChunkSize; z++)
                    {
                        int idx = FlattenToIdx(x, y, z, ChunkSize);
                        short id = buffer[idx].blockId;

                        var pos = chunkPos.pos;
                        pos.x += x;
                        pos.y += y;
                        pos.z += z;

                        bool visible = false;

                        
                        for (int tx = -1; tx <= 1; tx++)
                        {
                            for (int ty = -1; ty <= 1; ty++)
                            {
                                for (int tz = -1; tz <= 1; tz++)
                                {
                                    if (tx == 0 && ty == 0 && tz == 0)
                                        continue;
                                    
                                    int tidx = FlattenToIdx(x+tx, y+ty, z+tz, ChunkSize);
                                    
                                    // When tidx is 1, that means we've on a this chunk's border. We dont
                                    // have visibility into our neighbors, so just assume this block is visible.
                                    if (tidx == -1)
                                    {
                                        visible = true;
                                        break;
                                    }
                                    
                                    short tid = buffer[tidx].blockId;                    
                                    if (tid == -1) //Found one air block around this block. Block is visible.
                                    {
                                        visible = true;
                                        break;
                                    }
                                }
                            }
                        }

                        if (visible)
                        {
                            CreateBlockEntity(id, pos, commandBuffer, index);
                        }
                    }
                }
                
            }
            
            
            commandBuffer.RemoveComponent<ChunkFinishedGenerate>(index,entity);
        }
    }
    
    private static void CreateBlockEntity(short id, float3 worldPos, EntityCommandBuffer.Concurrent ecb, int index)
    {
        if (id == -1)
            return; //Dont generate air blocks.

        var entity = ecb.Instantiate(index, blockPrefab);
        var translation = new Translation() { Value = worldPos };
        ecb.SetComponent(index, entity, translation);
    }

    protected override void OnCreate()
    {
        ecbSystem = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
        
        cubeRenderMesh = new RenderMesh
        {
            mesh = GetCubeMesh(),
            material = GetCubeMaterial()
            
        };
          
        EntityManager em = ecbSystem.EntityManager;
        blockPrefab = em.CreateEntity();
        em.AddSharedComponentData(blockPrefab, cubeRenderMesh);
        em.AddComponentData(blockPrefab, new Rotation { Value = Quaternion.identity });
        em.AddComponentData(blockPrefab, new LocalToWorld());
        em.AddComponentData(blockPrefab, new BlockTagComponent());
        em.AddComponentData(blockPrefab, new Translation { Value = float3.zero});
    }

    protected override JobHandle OnUpdate(JobHandle inputDeps)
    {
        var job = new ChunkBuildMeshJob()
        {
            commandBuffer = ecbSystem.CreateCommandBuffer().ToConcurrent(),
            ChunkSize = ChunkMarkGenerateJS.ChunkSize,
            cbd = GetBufferFromEntity<ChunkBufferData>()
        };
        var handle = job.Schedule(this, inputDeps);
        ecbSystem.AddJobHandleForProducer(handle);
        return handle;
    }
}
