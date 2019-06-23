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

public class ChunkBuildMeshJS : JobComponentSystem
{
    
    BeginSimulationEntityCommandBufferSystem ecbSystem;
    static Entity[] blockPrefab;
    
    static Mesh cubeMesh = null;
    
    public static Mesh GetCubeMesh()
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

    private static Material GetCubeMaterial(short id)
    {
        switch (id)
        {
            case 0: return Resources.Load("grass", typeof(Material)) as Material;
            case 1: return Resources.Load("stone", typeof(Material)) as Material;
            case 2: return Resources.Load("dirt", typeof(Material)) as Material;
            case 3: return Resources.Load("sand", typeof(Material)) as Material;
            case 4: return Resources.Load("bedrock", typeof(Material)) as Material;
            case 5: return Resources.Load("wood", typeof(Material)) as Material;
            case 6: return Resources.Load("leaves", typeof(Material)) as Material;
        }

        return Resources.Load("cubeMaterial", typeof(Material)) as Material;        
    }
    
    public static int FlattenToIdx(int x, int y, int z, int ChunkSize)
    {
        if (x < 0 || x >= ChunkSize)
            return -1; //out of bounds

        if (y < 0 || y >= ChunkSize)
            return -1; //out of bounds
       
        if (z < 0 || z >= ChunkSize)
            return -1; //out of bounds
        
        return z + (y * ChunkSize) + (x * ChunkSize * ChunkSize);
    }
    
    public static float3 IdxToFlatten(int idx, int ChunkSize)
    {
        var z = idx / (ChunkSize * ChunkSize);
        idx -= z * ChunkSize * ChunkSize;
        var y = idx / ChunkSize;
        var x = idx % ChunkSize;
        return new float3(x, y, z);
    }
    
    struct ChunkBuildMeshJob : IJobForEachWithEntity<ChunkPosition, ChunkFinishedGenerate>
    {
        public int ChunkSize;
        
        [NativeDisableParallelForRestriction]
        public BufferFromEntity<ChunkBufferData> cbd;

        [NativeDisableParallelForRestriction]
        public BufferFromEntity<ChunkBufferBlockToCreateData> cbdOut;
        
        public void Execute(Entity entity, int index, [ReadOnly] ref ChunkPosition chunkPos, [ReadOnly] ref ChunkFinishedGenerate chunk)
        {
            DynamicBuffer<ChunkBufferData> buffer = cbd[entity];
            DynamicBuffer<ChunkBufferBlockToCreateData> bufferOut = cbdOut[entity];
            
            for (int x = 0; x < ChunkSize; x++)
            {
                for (int y = 0; y < ChunkSize; y++)
                {
                    for (int z = 0; z < ChunkSize; z++)
                    {
                        int idx = FlattenToIdx(x, y, z, ChunkSize);
                        short id = buffer[idx].blockId;

                        if (id == -1)
                            continue;
                        
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
                            bufferOut.Add(new ChunkBufferBlockToCreateData { blockId = id, position = pos});
                        }
                    }
                }
            }
        }
    }
    
    struct ChunkBuildMeshAfterJob : IJobForEachWithEntity<ChunkPosition, ChunkFinishedGenerate>
    {
        public EntityCommandBuffer.Concurrent commandBuffer;
        
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public BufferFromEntity<ChunkBufferBlockToCreateData> cbd;
        
        public void Execute(Entity entity, int index, [ReadOnly] ref ChunkPosition chunkPos, [ReadOnly] ref ChunkFinishedGenerate chunk)
        {
            DynamicBuffer<ChunkBufferBlockToCreateData> buffer = cbd[entity];

            DynamicBuffer<ChunkChildBlock> children = commandBuffer.SetBuffer<ChunkChildBlock>(index, entity);
            children.Clear();
            
            for (int i = 0; i < buffer.Length; ++i)
            {
                var e = buffer[i];

                var ent = commandBuffer.Instantiate(index, blockPrefab[e.blockId]);
                commandBuffer.SetComponent(index, ent, new Translation { Value = e.position });
                
                children.Add(new ChunkChildBlock {Value = ent});
            }
            commandBuffer.RemoveComponent<ChunkFinishedGenerate>(index, entity);
            commandBuffer.AddComponent(index, entity, new ChunkSpawned());
        }
    }
    
    protected override void OnCreate()
    {
        ecbSystem = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
        
        EntityManager em = ecbSystem.EntityManager;

        blockPrefab = new Entity[10];

        for (short i = 0; i < 10; ++i)
        {
            var prefab = em.CreateEntity();
            em.AddSharedComponentData(prefab, new RenderMesh
            {
                mesh = GetCubeMesh(),
                material = GetCubeMaterial(i)
            });
            em.AddComponentData(prefab, new Rotation {Value = Quaternion.identity});
            em.AddComponentData(prefab, new LocalToWorld());
            em.AddComponentData(prefab, new BlockTagComponent());
            em.AddComponentData(prefab, new Translation {Value = float3.zero});

            blockPrefab[i] = prefab;
        }
    }

    protected override JobHandle OnUpdate(JobHandle inputDeps)
    {
        var job = new ChunkBuildMeshJob
        {
            ChunkSize = ChunkMarkGenerateJS.ChunkSize,
            cbd = GetBufferFromEntity<ChunkBufferData>(),
            cbdOut = GetBufferFromEntity<ChunkBufferBlockToCreateData>()
        };
        var handle = job.Schedule(this, inputDeps);
        
        var jobAfter = new ChunkBuildMeshAfterJob
        {
            commandBuffer = ecbSystem.CreateCommandBuffer().ToConcurrent(),
            cbd = GetBufferFromEntity<ChunkBufferBlockToCreateData>()
        };
        handle = jobAfter.Schedule(this, handle);
        
        ecbSystem.AddJobHandleForProducer(handle);

        return handle;
    }
}
