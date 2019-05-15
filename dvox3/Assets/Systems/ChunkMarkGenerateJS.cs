using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Transforms;

public class ChunkMarkGenerateJS : JobComponentSystem
{
    public static Dictionary<float3, int> chunks = new Dictionary<float3, int>();
    public static float generationDistance = 128.0f;
    public static int ChunkSize = 16;
    
    BeginSimulationEntityCommandBufferSystem ecbSystem;
    
    protected override void OnCreate()
    {
        ecbSystem = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
    }
    
    private struct Pair
    {
        public float distance;
        public float3 pos;

        public Pair(float distance, float3 pos)
        {
            this.distance = distance;
            this.pos = pos;
        }
    }

    private static int ChunkRound(float v, int ChunkSize)
    {
        return Mathf.FloorToInt(v / ChunkSize) * ChunkSize;
    }

    private static float3[] GetChunkGrid(float3 pos, int chunkSize)
    {
        List<Pair> grid = new List<Pair>();

        int lowX = ChunkRound(pos.x - generationDistance, chunkSize);
        int lowY = ChunkRound(pos.y - generationDistance, chunkSize);
        int lowZ = ChunkRound(pos.z - generationDistance, chunkSize);

        int highX = ChunkRound(pos.x + generationDistance, chunkSize);
        int highY = ChunkRound(pos.y + generationDistance, chunkSize);
        int highZ = ChunkRound(pos.z + generationDistance, chunkSize);

        for (int x = lowX; x <= highX; x += chunkSize)
        {
            for (int y = lowY; y <= highY; y += chunkSize)
            {
                for (int z = lowZ; z <= highZ; z += chunkSize)
                {
                    float3 v = new float3(x, y, z);
                    float distance = Vector3.Distance(new Vector3(x,y,z), new Vector3(pos.x, pos.y, pos.z));

                    if (distance <= generationDistance && !chunks.ContainsKey(v))
                    {
                        grid.Add(new Pair(distance, v));
                    }
                }
            }
        }

        return grid.OrderBy(o => o.distance).Select(o => o.pos).ToArray();
    }

    private static void CreateChunkEntity(float3 v, EntityCommandBuffer.Concurrent ecb, int index)
    {
        Entity entity = ecb.CreateEntity(index);
        var pending = new ChunkPendingGenerate();
        pending.pos = v;
        ecb.AddComponent(index, entity, pending);
    }
    
    struct SpawnChunksAround : IJobForEachWithEntity<Translation, ChunkGenerator>
    {
        [ReadOnly]
        public EntityCommandBuffer.Concurrent commandBuffer;

        public void Execute(Entity entity, int index, ref Translation c0, ref ChunkGenerator c1)
        {

            float3 curPos = c0.Value;
            float3 lastPos = c1.lastPos;

            if (!lastPos.Equals(curPos))
            {
                c1.lastPos = curPos;
                float3[] chunkGrid = GetChunkGrid(lastPos, ChunkSize);

                for (int i = 0; i < chunkGrid.Length; i++)
                {
                    CreateChunkEntity(chunkGrid[i], commandBuffer, index);                    
                }
            }
        }
    }
    
    protected override JobHandle OnUpdate(JobHandle inputDeps)
    {
        var job = new SpawnChunksAround();
        job.commandBuffer = ecbSystem.CreateCommandBuffer().ToConcurrent();
        var handle = job.Schedule(this, inputDeps);
        ecbSystem.AddJobHandleForProducer(handle);
        return handle;   
    }
}
