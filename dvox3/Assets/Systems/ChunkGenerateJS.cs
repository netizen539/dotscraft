using System.Collections;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

public class ChunkGenerateJS : JobComponentSystem
{
    BeginInitializationEntityCommandBufferSystem ecbSystem;
    static RenderMesh cubeRenderMesh;
    static Entity blockPrefab;

    private static Mesh GetCubeMesh()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var mesh = go.GetComponent<MeshFilter>().sharedMesh;
        GameObject.Destroy(go);
        return mesh;
    }

    private static Material GetCubeMaterial()
    {
        return Resources.Load("cubeMaterial", typeof(Material)) as Material;
    }
    
    protected override void OnCreate()
    {
        ecbSystem = World.GetOrCreateSystem<BeginInitializationEntityCommandBufferSystem>();
        cubeRenderMesh = new RenderMesh
        {
            mesh = GetCubeMesh(),
            material = GetCubeMaterial()
        };

        EntityArchetype arch;
        
        EntityManager em = ecbSystem.EntityManager;
        blockPrefab = em.CreateEntity();
        em.AddSharedComponentData(blockPrefab, cubeRenderMesh);
        em.AddComponentData(blockPrefab, new Rotation { Value = Quaternion.identity });
        em.AddComponentData(blockPrefab, new LocalToWorld());
        em.AddComponentData(blockPrefab, new BlockTagComponent());
        em.AddComponentData(blockPrefab, new Translation { Value = float3.zero});
    }

    private static float Remap(float value, float iMin, float iMax, float oMin, float oMax) // A remap function to help you in your procedural generation.
    {
        return Mathf.Lerp(oMin, oMax, Mathf.InverseLerp(iMin, iMax, value));
    }
    
    private static short generateBlock(ChunkPendingGenerate chunk, float x, float y, float z)
    {
        // Maths ahead! A lot of perlin noise mixed together to make some cool generation!

        x += chunk.pos.x;
        y += chunk.pos.y;
        z += chunk.pos.z;
        
        float height = 0f;

        // Height data, regardless of biome
        float mountainContrib = Remap(Mathf.PerlinNoise(x / 150f, z / 150f), 0.33f, 0.66f, 0, 1) * 40f;
        float desertContrib = 0f;
        float oceanContrib = 0f;
        float detailContrib = Remap(Mathf.PerlinNoise(x / 20f, z / 20f), 0, 1, -1, 1) * 5f;

        // Biomes
        float detailMult = Remap(Mathf.PerlinNoise(x / 30f, z / 30f), 0.33f, 0.66f, 0, 1);
        float mountainBiome = Remap(Mathf.PerlinNoise(x / 100f, z / 100f), 0.33f, 0.66f, 0, 1);
        float desertBiome = Remap(Mathf.PerlinNoise(x / 300f, z / 300f), 0.33f, 0.66f, 0, 1) * Remap(Mathf.PerlinNoise(x / 25f, z / 25f), 0.33f, 0.66f, 0.95f, 1.05f);
        float oceanBiome = Remap(Mathf.PerlinNoise(x / 500f, z / 500f), 0.33f, 0.66f, 0, 1);

        // Add biome contrib
        float mountainFinal = (mountainContrib * mountainBiome) + (detailContrib * detailMult) + 20;
        float desertFinal = (desertContrib * desertBiome) + (detailContrib * detailMult) + 20;
        float oceanFinal = (oceanContrib * oceanBiome);

        // Final contrib
        height = Mathf.Lerp(mountainFinal, desertFinal, desertBiome); // Decide between mountain biome or desert biome
        height = Mathf.Lerp(height, oceanFinal, oceanBiome); // Decide between the previous biome or ocean biome (aka ocean biome overrides all biomes)

        height = Mathf.Floor(height);

        // Trees!
        float treeTrunk = Mathf.PerlinNoise(x / 0.3543f, z / 0.3543f);
        float treeLeaves = Mathf.PerlinNoise(x / 5f, z / 5f);

        if (y > height)
        {
            if (treeTrunk >= 0.75f && oceanBiome < 0.4f && desertBiome < 0.4f && height > 15 && y <= height + 5)
                return 5;
            else if (treeLeaves * Mathf.Clamp01(1 - Vector2.Distance(new Vector2(y, 0), new Vector2(height + 7, 0)) / 5f) >= 0.25f && treeTrunk <= 0.925f && oceanBiome < 0.4f && desertBiome < 0.4f && height > 15)
                return 6;
        }
        if (y <= height && y >= 0)
        {
            if (y == height && height > 2) // Grass or sand layer
            {
                if (oceanBiome >= 0.1f && height < 16)
                    return 3;
                else
                    return (desertBiome >= 0.5f ? (short)3 : (short)0);
            }
            else if (y >= height - 6 && height > 6) // Dirt or sand layer
            {
                return (desertBiome >= 0.5f ? (short)3 : (short)2);
            }
            else if (y > 0) // Stone layer
            {
                return 1;
            }
            else // Bedrock layer
            {
                return 4;
            }
        }
        else // Else there shall be nothing!
        {
            return -1;
        }
    }
    
    struct GenerateChunkJob : IJobForEachWithEntity<ChunkPendingGenerate>
    {
        public EntityCommandBuffer.Concurrent commandBuffer;
        public int ChunkSize;
        
        public void Execute(Entity e, int index, ref ChunkPendingGenerate chunk)
        {
            for (int x = 0; x < ChunkSize; x++)
            {
                for (int y = 0; y < ChunkSize; y++)
                {
                    for (int z = 0; z < ChunkSize; z++)
                    {
                        short id = generateBlock(chunk, x, y, z);

                        if (id != -1) //Dont create air blocks
                        {
                            var entity = commandBuffer.Instantiate(index, blockPrefab);    
                            var p = new float3(chunk.pos.x + x, chunk.pos.y + y, chunk.pos.z + z);
                            var translation = new Translation { Value = p };
                            commandBuffer.SetComponent(index, entity, translation);
                        }
                    }
                }
            }

            commandBuffer.RemoveComponent<ChunkPendingGenerate>(index, e);
        }
    }

    
    protected override JobHandle OnUpdate(JobHandle inputDeps)
    {
        var job = new GenerateChunkJob { commandBuffer = ecbSystem.CreateCommandBuffer().ToConcurrent(), ChunkSize = ChunkMarkGenerateJS.ChunkSize };
        var handle = job.Schedule(this, inputDeps);
        ecbSystem.AddJobHandleForProducer(handle);
        return handle;   
    }
}
