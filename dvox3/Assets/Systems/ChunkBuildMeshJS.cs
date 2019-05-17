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
    static Entity[] blockPrefab;
    
    static Mesh cubeMesh = null;
    static int TILEING = 16;
    static float UVPADDING = 0f;
    
    public static Mesh GetCubeMesh()
    {
        if (!cubeMesh) 
        { 
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            GameObject.Destroy(go);
            cubeMesh = mesh;
        }

//        List<Vector2> uvs = new List<Vector2>();
//        //Add UV coordinates for each side of the cube.
//        uvs.AddRange(GetUVs(0)); 
//        uvs.AddRange(GetUVs(1));
//        uvs.AddRange(GetUVs(2));
//        uvs.AddRange(GetUVs(3));
//        uvs.AddRange(GetUVs(4));
//        uvs.AddRange(GetUVs(5));
//        cubeMesh.SetUVs(0, uvs);
//
        return cubeMesh;

//        var mesh = new Mesh();
//
//        mesh.vertices = new[]
//        {
//            new Vector3(-0.5f, -0.5f,  0.5f), //0 
//            new Vector3(-0.5f,  0.5f,  0.5f), //1
//            new Vector3( 0.5f,  0.5f,  0.5f), //2
//            new Vector3( 0.5f, -0.5f,  0.5f), //3
//            
//            new Vector3(-0.5f, -0.5f, -0.5f), //4
//            new Vector3(-0.5f,  0.5f, -0.5f), //5
//            new Vector3( 0.5f,  0.5f, -0.5f), //6
//            new Vector3( 0.5f, -0.5f, -0.5f), //7
//        };
//        
//        mesh.triangles = new[]
//        {
//            0, 2, 1,
//            0, 3, 2,
//            
//            4, 5, 6,
//            4, 6, 7,
//
//            
//            1, 6, 5,
//            1, 2, 6,
//            
//            0, 4, 7,
//            0, 7, 3,
//
//            
//            3, 6, 2,
//            3, 7, 6,
//            
//            0, 1, 5,
//            0, 5, 4,
//        };
//
//        var uv = GetUVs(1);
//        mesh.uv = new[]
//        {
//            uv[0], uv[1], uv[2], uv[3], 
//            uv[0], uv[1], uv[2], uv[3],
//            uv[0], uv[1], uv[2], uv[3],
//            uv[0], uv[1], uv[2], uv[3],
//            uv[0], uv[1], uv[2], uv[3],
//            uv[0], uv[1], uv[2], uv[3],
//        };
//
//
//        mesh.normals = new[]
//        {
//            new Vector3(0, 0, 1), 
//            new Vector3(0, 0, 1),
//            new Vector3(0, 0, 1),
//            new Vector3(0, 0, 1),
//            
//            
//        };
//
//
//        return mesh;
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

    private static Vector2[] GetUVs(int id)
    {
        Vector2[] uv = new Vector2[4];
        float tiling = TILEING;
        int id2 = id + 1;
        float o = 1f / tiling;
        int i = 0;
        for (int y = 0; y < tiling; y++)
        {
            for (int x = 0; x < tiling; x++)
            {
                i++;
                if (i == id2)
                {
                    float padding = UVPADDING / tiling; // Adding a little padding to prevent UV bleeding (to fix)
                    uv[0] = new Vector2(x / tiling + padding, 1f - (y / tiling) - padding);
                    uv[1] = new Vector2(x / tiling + o - padding, 1f - (y / tiling) - padding);
                    uv[2] = new Vector2(x / tiling + o - padding, 1f - (y / tiling + o) + padding);
                    uv[3] = new Vector2(x / tiling + padding, 1f - (y / tiling + o) + padding);
                    return uv;
                }
            }
        }
        uv[0] = Vector2.zero;
        uv[1] = Vector2.zero;
        uv[2] = Vector2.zero;
        uv[3] = Vector2.zero;
        return uv;
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

        var entity = ecb.Instantiate(index, blockPrefab[id]);
        var translation = new Translation { Value = worldPos };
        ecb.SetComponent(index, entity, translation);
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
