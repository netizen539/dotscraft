using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Unity.Entities
{
    internal unsafe struct EntityChunkData : IComparable<EntityChunkData>, IEquatable<EntityChunkData>
    {
        public Chunk* Chunk;
        public int IndexInChunk;
        
        public int CompareTo(EntityChunkData other)
        {
            ulong lhs = (ulong) Chunk;
            ulong rhs = (ulong) other.Chunk;
            int chunkCompare = (int)(lhs - rhs);
            int indexCompare = IndexInChunk - other.IndexInChunk;
            return (lhs != rhs) ? chunkCompare : indexCompare;
        }
        
        public bool Equals(EntityChunkData other)
        {
            return CompareTo(other) == 0;
        }
        
        [BurstCompile]
        struct GatherEntityChunkDataForEntities : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Entity> Entities;
            [ReadOnly] [NativeDisableUnsafePtrRestriction]
            public EntityChunkData* GlobalEntityChunkData;
            public NativeArray<EntityChunkData> EntityChunkData;

            public void Execute(int index)
            {
                var entity = Entities[index];
                EntityChunkData[index] = new EntityChunkData
                {
                    Chunk = GlobalEntityChunkData[entity.Index].Chunk,
                    IndexInChunk = GlobalEntityChunkData[entity.Index].IndexInChunk
                };
            }
        }

        internal static JobHandle GatherEntityChunkDataForEntitiesJob(EntityChunkData* globalEntityChunkData,
            NativeArray<Entity> entities,
            NativeArray<EntityChunkData> entityChunkData, JobHandle inputDeps = new JobHandle())
        {
            var gatherEntityChunkDataForEntitiesJob = new GatherEntityChunkDataForEntities
            {
                Entities = entities,
                GlobalEntityChunkData = globalEntityChunkData,
                EntityChunkData = entityChunkData
            };
            var gatherEntityChunkDataForEntitiesJobHandle =
                gatherEntityChunkDataForEntitiesJob.Schedule(entities.Length, 32, inputDeps);
            return gatherEntityChunkDataForEntitiesJobHandle;
        }
    }
}
