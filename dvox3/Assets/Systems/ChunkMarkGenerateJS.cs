using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using Unity.Burst;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Transforms;

public class ChunkMarkGenerateJS : JobComponentSystem
{
    //public static Dictionary<float3, int> chunks = new Dictionary<float3, int>();
    public static NativeHashMap<float3, int> chunks;
    public static readonly float generationDistance = 128.0f;
    public static readonly float generationDistanceSquared = generationDistance * generationDistance;
    public static readonly int ChunkSize = 16;
    
    BeginSimulationEntityCommandBufferSystem ecbSystem;
    
    protected override void OnCreate()
    {
        ecbSystem = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
        chunks = new NativeHashMap<float3, int>(4096, Allocator.Persistent);
    }

    protected override void OnDestroy()
    {
        chunks.Dispose();
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
        return ((int)(math.floor(v / ChunkMarkGenerateJS.ChunkSize)) * ChunkSize);
    }

    private static void BuiildChunkGrid(float3 pos, int chunkSize, EntityCommandBuffer.Concurrent ecb, int index)
    {        
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

                    float distanceSquared = math.distancesq(v, pos);

                    int _d;
                    if (distanceSquared <= generationDistanceSquared && !chunks.TryGetValue(v, out _d))
                    {
                        int id = CreateChunkEntity(v, ecb, index);
                        chunks.ToConcurrent().TryAdd(v, id);
                    }
                }
            }
        }
    }

    private static int CreateChunkEntity(float3 v, EntityCommandBuffer.Concurrent ecb, int index)
    {
        var entity = ecb.CreateEntity(index);
        var pending = new ChunkPendingGenerate { pos = v };
        ecb.AddComponent(index, entity, pending);
        return entity.Index;
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
                BuiildChunkGrid(lastPos, ChunkSize, commandBuffer, index);
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
