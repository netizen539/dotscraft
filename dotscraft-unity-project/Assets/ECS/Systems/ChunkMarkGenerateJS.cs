using System;
using System.Collections;
using System.Collections.Concurrent;
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
    public static ConcurrentDictionary<float3, Entity> chunks;
    public static readonly float generationDistance = 128.0f;
    public static readonly float generationDistanceSquared = generationDistance * generationDistance;
    
    public static readonly float degenerationDistance = 150.0f;
    public static readonly float degenerationDistanceSquared = generationDistance * generationDistance;
    
    public static readonly int ChunkSize = 16;

    private float3 lastPlayerPosition;
    
    BeginSimulationEntityCommandBufferSystem ecbSystem;
    static GameObject playerGameObject = null;
    
    protected override void OnCreate()
    {
        ecbSystem = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
        chunks = new ConcurrentDictionary<float3, Entity>();
    }

    protected override void OnDestroy()
    {
    }

    private static int ChunkRound(float v, int ChunkSize)
    {
        return ((int)(math.floor(v / ChunkMarkGenerateJS.ChunkSize)) * ChunkSize);
    }

    struct DeSpawnChunksAround : IJobForEachWithEntity<ChunkPosition, ChunkSpawned>
    {
        public EntityCommandBuffer.Concurrent commandBuffer;
        public float3 playerPosition;
        [ReadOnly]
        public BufferFromEntity<ChunkChildBlock> childBuffer;
        
        public void Execute(Entity entity, int index, [ReadOnly] ref ChunkPosition chPos, [ReadOnly] ref ChunkSpawned sTag)
        {
            float distanceSquared = math.distancesq(chPos.pos, playerPosition);

            if (distanceSquared > degenerationDistanceSquared)
            {
                Entity _d;
                if (chunks.TryRemove(chPos.pos, out _d))
                {
                    var children = childBuffer[entity];
                    for (var i = 0; i < children.Length; ++i)
                    { 
                        commandBuffer.DestroyEntity(index, children[i].Value);
                    }
                    commandBuffer.DestroyEntity(index, entity);
                }
            }
        }
    }
    
    struct SpawnChunksAround : IJob
    {
        public EntityCommandBuffer commandBuffer;
        public float3 playerPosition;

        public void Execute()
        {
            int lowX = ChunkRound(playerPosition.x - generationDistance, ChunkSize);
            int lowY = ChunkRound(playerPosition.y - generationDistance, ChunkSize);
            int lowZ = ChunkRound(playerPosition.z - generationDistance, ChunkSize);

            int highX = ChunkRound(playerPosition.x + generationDistance, ChunkSize);
            int highY = ChunkRound(playerPosition.y + generationDistance, ChunkSize);
            int highZ = ChunkRound(playerPosition.z + generationDistance, ChunkSize);

            for (int x = lowX; x <= highX; x += ChunkSize)
            {
                for (int y = lowY; y <= highY; y += ChunkSize)
                {
                    for (int z = lowZ; z <= highZ; z += ChunkSize)
                    {
                        var v = new float3(x, y, z);
                        float distanceSquared = math.distancesq(v, playerPosition);

                        Entity _d;
                        if (distanceSquared <= generationDistanceSquared && !chunks.TryGetValue(v, out _d))
                        {
                            var entity = commandBuffer.CreateEntity();
                            var position = new ChunkPosition { pos = v };
                            var pending = new ChunkPendingGenerate();
                            commandBuffer.AddComponent(entity, pending);
                            commandBuffer.AddComponent(entity, position);
                            commandBuffer.AddBuffer<ChunkBufferData>(entity);
                            commandBuffer.AddBuffer<ChunkChildBlock>(entity);
                            commandBuffer.AddBuffer<ChunkBufferBlockToCreateData>(entity);
                            
                            chunks.TryAdd(v, entity);
                        }
                    }
                }
            }
        }
    }

    private static float3 GetPlayerPosition()
    {
        if (!playerGameObject)
        {
            playerGameObject = GameObject.Find("/Player");
        }

        return new float3(playerGameObject.transform.position);
    }
    
    protected override JobHandle OnUpdate(JobHandle inputDeps)
    {
        var handle = inputDeps;
        
        if (PlayerPositionChanged())
        {
            var jobDespawn = new DeSpawnChunksAround
            {
                childBuffer = GetBufferFromEntity<ChunkChildBlock>(true),
                commandBuffer = ecbSystem.CreateCommandBuffer().ToConcurrent(),
                playerPosition = GetPlayerPosition()
            };
            handle = jobDespawn.Schedule(this, handle);
            
            var job = new SpawnChunksAround
            {
                commandBuffer = ecbSystem.CreateCommandBuffer(),
                playerPosition = GetPlayerPosition()
            };
            handle = job.Schedule(handle);
            ecbSystem.AddJobHandleForProducer(handle);
        }

        return handle;
    }

    bool PlayerPositionChanged()
    {
        var curPos = GetPlayerPosition();

        var changed = !lastPlayerPosition.Equals(curPos); 
        if (changed)
            lastPlayerPosition = curPos;
        return changed;
    }
}
