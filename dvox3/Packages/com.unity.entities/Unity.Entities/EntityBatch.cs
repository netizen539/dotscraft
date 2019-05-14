using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine.Profiling;


namespace Unity.Entities
{
    [BurstCompile]
    unsafe struct EntityBatchFromEntityChunkDataShared : IJob
    {
        [ReadOnly] public NativeArraySharedValues<EntityChunkData> EntityChunkDataShared;
        public NativeList<EntityBatch> EntityBatchList;
        
        public void Execute()
        {
            var entityChunkData = EntityChunkDataShared.SourceBuffer;
            var sortedEntityInChunks = EntityChunkDataShared.GetSortedIndices();
            
            var sortedEntityIndex = 0;
            var entityIndex = sortedEntityInChunks[sortedEntityIndex];
            var entityBatch = new EntityBatch
            {
                Chunk = new ArchetypeChunk {m_Chunk = entityChunkData[entityIndex].Chunk},
                StartIndex = entityChunkData[entityIndex].IndexInChunk,
                Count = 1
            };
            sortedEntityIndex++;
            while (sortedEntityIndex < sortedEntityInChunks.Length)
            {
                entityIndex = sortedEntityInChunks[sortedEntityIndex];
                var chunk = new ArchetypeChunk {m_Chunk = entityChunkData[entityIndex].Chunk};
                var indexInChunk = entityChunkData[entityIndex].IndexInChunk;
                var chunkBreak = (chunk != entityBatch.Chunk);
                var indexBreak = (indexInChunk != (entityBatch.StartIndex + entityBatch.Count));
                var runBreak = chunkBreak || indexBreak;
                if (runBreak)
                {
                    EntityBatchList.Add(entityBatch);
                    entityBatch = new EntityBatch
                    {
                        Chunk = chunk,
                        StartIndex = indexInChunk,
                        Count = 1
                    };
                }
                else
                {
                    entityBatch = new EntityBatch
                    {
                        Chunk = entityBatch.Chunk,
                        StartIndex = entityBatch.StartIndex,
                        Count = entityBatch.Count + 1
                    };
                }
                sortedEntityIndex++;
            }
            EntityBatchList.Add(entityBatch);
        }
    }

    [BurstCompile]
    struct EntityBatchFromArchetypeChunks : IJob
    {
        [ReadOnly] public NativeArray<ArchetypeChunk> ArchetypeChunks;
        public NativeList<EntityBatch> EntityBatchList;

        public void Execute()
        {
            for (int i = 0; i < ArchetypeChunks.Length; i++)
            {
                var entityBatch = new EntityBatch
                {
                    Chunk = ArchetypeChunks[i],
                    StartIndex = 0,
                    Count = ArchetypeChunks[i].Count
                };
                EntityBatchList.Add(entityBatch);
            }
        }
    }


    public unsafe struct EntityBatch
    {
        public ArchetypeChunk Chunk;
        public int StartIndex;
        public int Count;

        internal static NativeList<EntityBatch> Create(NativeArray<Entity> entities, EntityData entityData)
        {
            var entityChunkData = new NativeArray<EntityChunkData>(entities.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var gatherEntityChunkDataForEntitiesJobHandle =
                EntityChunkData.GatherEntityChunkDataForEntitiesJob(entityData.ChunkData, entities, entityChunkData);

            var entityChunkDataShared = new NativeArraySharedValues<EntityChunkData>(entityChunkData, Allocator.TempJob);
            var entityChunkDataSharedJobHandle = entityChunkDataShared.Schedule(gatherEntityChunkDataForEntitiesJobHandle);

            var entityBatchList = new NativeList<EntityBatch>(Allocator.Persistent);
            var entityBatchFromEntityInChunksSharedJob = new EntityBatchFromEntityChunkDataShared
            {
                EntityChunkDataShared = entityChunkDataShared,
                EntityBatchList = entityBatchList
            };
            var entityBatchFromEntityInChunksSharedJobHandle =
                entityBatchFromEntityInChunksSharedJob.Schedule(entityChunkDataSharedJobHandle);
            entityBatchFromEntityInChunksSharedJobHandle.Complete();   
            
            entityChunkData.Dispose();
            entityChunkDataShared.Dispose();

            return entityBatchList;
        }

        internal static NativeList<EntityBatch> Create(NativeArray<ArchetypeChunk> archetypeChunks, EntityData entityData)
        {
            var entityBatchList = new NativeList<EntityBatch>(Allocator.Persistent);
            var entityBatchFromArchetypeChunksJob = new EntityBatchFromArchetypeChunks
            {
                ArchetypeChunks = archetypeChunks,
                EntityBatchList = entityBatchList
            };
            var entityBatchFromArchetypeChunksJobHandle =
                entityBatchFromArchetypeChunksJob.Schedule();
            entityBatchFromArchetypeChunksJobHandle.Complete();   
            
            return entityBatchList;
        }
    }
}
