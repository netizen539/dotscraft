//#define USE_BURST_DESTROY

using System;
using System.Diagnostics;
using Unity.Assertions;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Profiling;

namespace Unity.Entities
{
    internal unsafe struct EntityDataManager
    {
#if USE_BURST_DESTROY
        private delegate Chunk* DeallocateDataEntitiesInChunkDelegate(EntityDataManager* entityDataManager, Entity* entities, int count, out int indexInChunk, out int batchCount);
        static DeallocateDataEntitiesInChunkDelegate ms_DeallocateDataEntitiesInChunkDelegate;
#endif

        private EntityData m_Entities;

        public int Version => m_Entities.EntityOrderVersion;

        public uint GlobalSystemVersion
        {
            get { return m_Entities.GlobalSystemVersion; }
            set { m_Entities.GlobalSystemVersion = value; }
        }

        public void IncrementGlobalSystemVersion()
        {
            m_Entities.IncrementGlobalSystemVersion();
        }

        public void OnCreate()
        {
            m_Entities = EntityData.Create(10);

#if USE_BURST_DESTROY
            if (ms_DeallocateDataEntitiesInChunkDelegate == null)
            {
                ms_DeallocateDataEntitiesInChunkDelegate = DeallocateDataEntitiesInChunk;
                ms_DeallocateDataEntitiesInChunkDelegate =
 Burst.BurstDelegateCompiler.CompileDelegate(ms_DeallocateDataEntitiesInChunkDelegate);
            }
#endif
        }

        public void OnDestroy()
        {
            m_Entities.Dispose();
        }

        void IncreaseCapacity()
        {
            m_Entities.IncreaseCapacity();
        }

        public int Capacity
        {
            get { return m_Entities.Capacity; }
            set { m_Entities.Capacity = value; }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void ValidateEntity(Entity entity)
        {
            m_Entities.ValidateEntity(entity);
        }

        public bool Exists(Entity entity)
        {
            return m_Entities.Exists(entity);
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertEntitiesExist(Entity* entities, int count)
        {
            m_Entities.AssertEntitiesExist(entities, count);
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanDestroy(Entity* entities, int count)
        {
            m_Entities.AssertCanDestroy(entities, count);
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertEntityHasComponent(Entity entity, ComponentType componentType)
        {
            m_Entities.AssertEntityHasComponent(entity, componentType);
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertEntityHasComponent(Entity entity, int componentType)
        {
            m_Entities.AssertEntityHasComponent(entity, componentType);
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanAddComponent(Entity entity, ComponentType componentType)
        {
            m_Entities.AssertCanAddComponent(entity, componentType);
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanRemoveComponent(Entity entity, ComponentType componentType)
        {
            m_Entities.AssertCanRemoveComponent(entity, componentType);
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanAddComponent(Entity entity, int componentType)
        {
            m_Entities.AssertCanAddComponent(entity, componentType);
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanAddComponents(Entity entity, ComponentTypes types)
        {
            m_Entities.AssertCanAddComponents(entity, types);
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanRemoveComponents(Entity entity, ComponentTypes types)
        {
            m_Entities.AssertCanRemoveComponents(entity, types);
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanAddComponent(NativeArray<ArchetypeChunk> chunkArray, ComponentType componentType)
        {
            m_Entities.AssertCanAddComponent(chunkArray, componentType);
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanRemoveComponent(NativeArray<ArchetypeChunk> chunkArray, ComponentType componentType)
        {
            m_Entities.AssertCanRemoveComponent(chunkArray, componentType);
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanDestroy(NativeArray<ArchetypeChunk> chunkArray)
        {
            m_Entities.AssertCanDestroy(chunkArray);
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanAddChunkComponent(NativeArray<ArchetypeChunk> chunkArray, ComponentType componentType)
        {
            m_Entities.AssertCanAddChunkComponent(chunkArray, componentType);
        }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        public int CheckInternalConsistency()
        {
            return m_Entities.CheckInternalConsistency();
        }
#endif

        public void AllocateConsecutiveEntitiesForLoading(int count)
        {
            int newCapacity = count + 1; // make room for Entity.Null
            Capacity = newCapacity + 1; // the last entity is used to indicate we ran out of space
            m_Entities.FreeIndex = newCapacity;
            for (int i = 1; i < newCapacity; ++i)
            {
                if (m_Entities.ChunkData[i].Chunk != null)
                {
                    throw new ArgumentException("loading into non-empty entity manager is not supported");
                }

                m_Entities.ChunkData[i].IndexInChunk = 0;
                m_Entities.Version[i] = 0;
#if UNITY_EDITOR
                m_Entities.Name[i] = new NumberedWords();
#endif
            }
        }

        public void AddExistingChunk(Chunk* chunk)
        {
            for (int iEntity = 0; iEntity < chunk->Count; ++iEntity)
            {
                var entity = (Entity*) ChunkDataUtility.GetComponentDataRO(chunk, iEntity, 0);
                m_Entities.ChunkData[entity->Index].Chunk = chunk;
                m_Entities.ChunkData[entity->Index].IndexInChunk = iEntity;
                m_Entities.Archetype[entity->Index] = chunk->Archetype;
            }
        }

        public void AllocateEntitiesForRemapping(EntityDataManager* srcEntityDataManager,
            ref NativeArray<EntityRemapUtility.EntityRemapInfo> entityRemapping)
        {
            var srcEntityData = srcEntityDataManager->m_Entities;
            var count = srcEntityDataManager->Capacity;
            for (var i = 0; i != count; i++)
            {
                if (srcEntityData.ChunkData[i].Chunk != null)
                {
                    var entityIndexInChunk = m_Entities.ChunkData[m_Entities.FreeIndex].IndexInChunk;
                    if (entityIndexInChunk == -1)
                    {
                        IncreaseCapacity();
                        entityIndexInChunk = m_Entities.ChunkData[m_Entities.FreeIndex].IndexInChunk;
                    }

                    var entityVersion = m_Entities.Version[m_Entities.FreeIndex];

                    EntityRemapUtility.AddEntityRemapping(ref entityRemapping,
                        new Entity {Version = srcEntityData.Version[i], Index = i},
                        new Entity {Version = entityVersion, Index = m_Entities.FreeIndex});
                    m_Entities.FreeIndex = entityIndexInChunk;
                }
            }
        }

        public void AllocateEntitiesForRemapping(Chunk* chunk,
            ref NativeArray<EntityRemapUtility.EntityRemapInfo> entityRemapping)
        {
            var count = chunk->Count;
            var entities = (Entity*) chunk->Buffer;
            for (var i = 0; i != count; i++)
            {
                var entityIndexInChunk = m_Entities.ChunkData[m_Entities.FreeIndex].IndexInChunk;
                if (entityIndexInChunk == -1)
                {
                    IncreaseCapacity();
                    entityIndexInChunk = m_Entities.ChunkData[m_Entities.FreeIndex].IndexInChunk;
                }

                var entityVersion = m_Entities.Version[m_Entities.FreeIndex];

                EntityRemapUtility.AddEntityRemapping(ref entityRemapping,
                    new Entity {Version = entities[i].Version, Index = entities[i].Index},
                    new Entity {Version = entityVersion, Index = m_Entities.FreeIndex});
                m_Entities.FreeIndex = entityIndexInChunk;
            }
        }

        public void RemapChunk(Archetype* arch, Chunk* chunk, int baseIndex, int count,
            ref NativeArray<EntityRemapUtility.EntityRemapInfo> entityRemapping)
        {
            Assert.AreEqual(chunk->Archetype->Offsets[0], 0);
            Assert.AreEqual(chunk->Archetype->SizeOfs[0], sizeof(Entity));

            var entityInChunkStart = (Entity*) (chunk->Buffer) + baseIndex;

            for (var i = 0; i != count; i++)
            {
                var entityInChunk = entityInChunkStart + i;
                var target = EntityRemapUtility.RemapEntity(ref entityRemapping, *entityInChunk);
                var entityVersion = m_Entities.Version[target.Index];

                Assert.AreEqual(entityVersion, target.Version);

                entityInChunk->Index = target.Index;
                entityInChunk->Version = entityVersion;
                m_Entities.ChunkData[target.Index].IndexInChunk = baseIndex + i;
                m_Entities.Archetype[target.Index] = arch;
                m_Entities.ChunkData[target.Index].Chunk = chunk;
            }

            if (chunk->metaChunkEntity != Entity.Null)
            {
                chunk->metaChunkEntity = EntityRemapUtility.RemapEntity(ref entityRemapping, chunk->metaChunkEntity);
            }
        }

        public void FreeAllEntities()
        {
            m_Entities.FreeAllEntities();
        }

        public void FreeEntities(Chunk* chunk)
        {
            m_Entities.FreeEntities(chunk);
        }

#if UNITY_EDITOR
        public string GetName(Entity entity)
        {
            return m_Entities.Name[entity.Index].ToString();
        }

        public void SetName(Entity entity, string name)
        {
            m_Entities.Name[entity.Index].SetString(name);
        }
#endif

        public bool HasComponent(Entity entity, int type)
        {
            return m_Entities.HasComponent(entity, type);
        }

        public bool HasComponent(Entity entity, ComponentType type)
        {
            return m_Entities.HasComponent(entity, type);
        }

        public int GetSizeInChunk(Entity entity, int typeIndex, ref int typeLookupCache)
        {
            return m_Entities.GetSizeInChunk(entity, typeIndex, ref typeLookupCache);
        }

        public byte* GetComponentDataWithTypeRO(Entity entity, int typeIndex)
        {
            return m_Entities.GetComponentDataWithTypeRO(entity, typeIndex);
        }

        public byte* GetComponentDataWithTypeRW(Entity entity, int typeIndex, uint globalVersion)
        {
            return m_Entities.GetComponentDataWithTypeRW(entity, typeIndex, globalVersion);
        }

        public byte* GetComponentDataWithTypeRO(Entity entity, int typeIndex, ref int typeLookupCache)
        {
            return m_Entities.GetComponentDataWithTypeRO(entity, typeIndex, ref typeLookupCache);
        }

        public byte* GetComponentDataWithTypeRW(Entity entity, int typeIndex, uint globalVersion,
            ref int typeLookupCache)
        {
            return m_Entities.GetComponentDataWithTypeRW(entity, typeIndex, globalVersion, ref typeLookupCache);
        }

        public void GetComponentChunk(Entity entity, out Chunk* chunk, out int chunkIndex)
        {
            m_Entities.GetComponentChunk(entity, out chunk, out chunkIndex);
        }

        public Chunk* GetComponentChunk(Entity entity)
        {
            return m_Entities.GetComponentChunk(entity);
        }

        public Archetype* GetArchetype(Entity entity)
        {
            return m_Entities.Archetype[entity.Index];
        }

        public void CreateMetaEntityForChunk(ArchetypeManager archetypeManager, Chunk* chunk)
        {
            m_Entities.CreateMetaEntityForChunk(archetypeManager, chunk);
        }

        public void AllocateEntities(Archetype* arch, Chunk* chunk, int baseIndex, int count, Entity* outputEntities)
        {
            m_Entities.AllocateEntities(arch, chunk, baseIndex, count, outputEntities);
        }

        public void AddComponents(Entity entity, ComponentTypes types, ArchetypeManager archetypeManager,
            SharedComponentDataManager sharedComponentDataManager,
            EntityGroupManager groupManager)
        {
            m_Entities.AddComponents(entity, types, archetypeManager, sharedComponentDataManager, groupManager);
        }

        public void AddComponent(Entity entity, ComponentType type, ArchetypeManager archetypeManager,
            SharedComponentDataManager sharedComponentDataManager,
            EntityGroupManager groupManager)
        {
            m_Entities.AddComponent(entity, type, archetypeManager, sharedComponentDataManager, groupManager);
        }

        public void AddComponent(NativeArray<Entity> entities, ComponentType componentType,
            ArchetypeManager archetypeManager,
            SharedComponentDataManager sharedComponentDataManager,
            EntityGroupManager groupManager)
        {
            var entityBatchList = EntityBatch.Create(entities, m_Entities);
            m_Entities.AddComponent(entityBatchList, componentType, 0, archetypeManager, sharedComponentDataManager,
                groupManager);
            entityBatchList.Dispose();
        }

        public void AddChunkComponent<T>(NativeArray<ArchetypeChunk> chunkArray, T componentData,
            ArchetypeManager archetypeManager,
            EntityGroupManager groupManager, SharedComponentDataManager sharedComponentDataManager)
            where T : struct, IComponentData
        {
            var type = ComponentType.ReadWrite<T>();
            var chunkType = ComponentType.FromTypeIndex(TypeManager.MakeChunkComponentTypeIndex(type.TypeIndex));

            var entityBatchList = EntityBatch.Create(chunkArray, m_Entities);
            m_Entities.AddComponent(entityBatchList, chunkType, 0, archetypeManager, sharedComponentDataManager,
                groupManager);
            m_Entities.SetChunkComponent<T>(entityBatchList, componentData);
            entityBatchList.Dispose();
        }

        public void AddSharedComponent(NativeArray<ArchetypeChunk> chunkArray, ComponentType componentType,
            ArchetypeManager archetypeManager, EntityGroupManager groupManager,
            SharedComponentDataManager sharedComponentDataManager, int sharedComponentIndex)
        {
            var entityBatchList = EntityBatch.Create(chunkArray, m_Entities);
            m_Entities.AddComponent(entityBatchList, componentType, sharedComponentIndex, archetypeManager,
                sharedComponentDataManager, groupManager);
            entityBatchList.Dispose();
        }

        public void AddComponent(NativeArray<ArchetypeChunk> chunkArray, ComponentType componentType,
            ArchetypeManager archetypeManager, EntityGroupManager groupManager,
            SharedComponentDataManager sharedComponentDataManager)
        {
            var entityBatchList = EntityBatch.Create(chunkArray, m_Entities);
            m_Entities.AddComponent(entityBatchList, componentType, 0, archetypeManager, sharedComponentDataManager,
                groupManager);
            entityBatchList.Dispose();
        }

        public void RemoveComponent(NativeArray<ArchetypeChunk> chunkArray, ComponentType type,
            ArchetypeManager archetypeManager, EntityGroupManager groupManager,
            SharedComponentDataManager sharedComponentDataManager)
        {
            m_Entities.RemoveComponent(chunkArray, type, archetypeManager, groupManager, sharedComponentDataManager);
        }

        public void DeleteChunks(NativeArray<ArchetypeChunk> chunkArray,
            ArchetypeManager archetypeManager, SharedComponentDataManager sharedComponentDataManager)
        {
            m_Entities.DestroyEntities(chunkArray, archetypeManager, sharedComponentDataManager);
        }

        public void TryRemoveEntityId(Entity* entities, int count,
            ArchetypeManager archetypeManager, SharedComponentDataManager sharedComponentDataManager)
        {
            m_Entities.DestroyEntities(entities, count, archetypeManager, sharedComponentDataManager);
        }

        public void RemoveComponent(Entity entity, ComponentType type,
            ArchetypeManager archetypeManager, SharedComponentDataManager sharedComponentDataManager,
            EntityGroupManager groupManager)
        {
            m_Entities.RemoveComponent(entity, type, archetypeManager, sharedComponentDataManager, groupManager);
        }

        public void CreateEntities(ArchetypeManager archetypeManager, Archetype* archetype, Entity* entities, int count)
        {
            m_Entities.CreateEntities(archetypeManager, archetype, entities, count);
        }

        public void LockChunks(Chunk** chunks, int count, ChunkFlags flags)
        {
            for (int i = 0; i < count; i++)
            {
                var chunk = chunks[i];

                Assert.IsFalse(chunk->Locked);

                chunk->Flags |= (uint) flags;
                if (chunk->Count < chunk->Capacity && (flags & ChunkFlags.Locked) != 0)
                    ArchetypeManager.EmptySlotTrackingRemoveChunk(chunk);
            }
        }

        public void UnlockChunks(Chunk** chunks, int count, ChunkFlags flags)
        {
            for (int i = 0; i < count; i++)
            {
                var chunk = chunks[i];

                Assert.IsTrue(chunk->Locked);

                chunk->Flags &= ~(uint) flags;
                if (chunk->Count < chunk->Capacity && (flags & ChunkFlags.Locked) != 0)
                    ArchetypeManager.EmptySlotTrackingAddChunk(chunk);
            }
        }

        public void CreateChunks(ArchetypeManager archetypeManager, Archetype* archetype, ArchetypeChunk* chunks,
            int count)
        {
            int* sharedComponentValues = stackalloc int[archetype->NumSharedComponents];
            UnsafeUtility.MemClear(sharedComponentValues, archetype->NumSharedComponents * sizeof(int));

            Chunk* lastChunk = null;
            int chunkIndex = 0;
            while (count != 0)
            {
                var chunk = archetypeManager.GetCleanChunk(archetype, sharedComponentValues);
                int allocatedIndex;
                var allocatedCount = archetypeManager.AllocateIntoChunk(chunk, count, out allocatedIndex);
                m_Entities.AllocateEntities(archetype, chunk, allocatedIndex, allocatedCount, null);
                ChunkDataUtility.InitializeComponents(chunk, allocatedIndex, allocatedCount);
                chunk->SetAllChangeVersions(GlobalSystemVersion);
                chunks[chunkIndex] = new ArchetypeChunk {m_Chunk = chunk};
                lastChunk = chunk;

                count -= allocatedCount;
                chunkIndex++;
            }

            IncrementComponentTypeOrderVersion(archetype);
        }

        struct InstantiateRemapChunk
        {
            public Chunk* Chunk;
            public int IndexInChunk;
            public int AllocatedCount;
            public int InstanceBeginIndex;
        }

        public void InstantiateEntities(ArchetypeManager archetypeManager,
            SharedComponentDataManager sharedComponentDataManager, Entity srcEntity, Entity* outputEntities,
            int instanceCount)
        {
            var linkedType = TypeManager.GetTypeIndex<LinkedEntityGroup>();

            if (HasComponent(srcEntity, linkedType))
            {
                var header = (BufferHeader*) GetComponentDataWithTypeRO(srcEntity, linkedType);
                var entityPtr = (Entity*) BufferHeader.GetElementPointer(header);
                var entityCount = header->Length;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (entityCount == 0 || entityPtr[0] != srcEntity)
                    throw new ArgumentException("LinkedEntityGroup[0] must always be the Entity itself.");
                for (int i = 0; i < entityCount; i++)
                {
                    if (!Exists(entityPtr[i]))
                        throw new ArgumentException(
                            "The srcEntity's LinkedEntityGroup references an entity that is invalid. (Entity at index {i} on the LinkedEntityGroup.)");

                    if (GetArchetype(entityPtr[i])->InstantiableArchetype == null)
                        throw new ArgumentException(
                            "The srcEntity's LinkedEntityGroup references an entity that has already been destroyed. (Entity at index {i} on the LinkedEntityGroup. Only system state components are left on the entity)");
                }
#endif

                InstantiateEntitiesGroup(archetypeManager, sharedComponentDataManager, entityPtr, entityCount,
                    outputEntities, instanceCount);
            }
            else
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (!Exists(srcEntity))
                    throw new ArgumentException("srcEntity is not a valid entity");

                if (GetArchetype(srcEntity)->InstantiableArchetype == null)
                    throw new ArgumentException(
                        "srcEntity is not instantiable because it has already been destroyed. (Only system state components are left on it)");
#endif

                InstantiateEntitiesOne(archetypeManager, sharedComponentDataManager, srcEntity, outputEntities,
                    instanceCount, null, 0);
            }
        }

        int InstantiateEntitiesOne(ArchetypeManager archetypeManager,
            SharedComponentDataManager sharedComponentDataManager, Entity srcEntity, Entity* outputEntities,
            int instanceCount, InstantiateRemapChunk* remapChunks, int remapChunksCount)
        {
            var src = m_Entities.ChunkData[srcEntity.Index];
            var srcArchetype = src.Chunk->Archetype;
            var dstArchetype = srcArchetype->InstantiableArchetype;

            var temp = stackalloc int[dstArchetype->NumSharedComponents];
            if (EntityData.RequiresBuildingResidueSharedComponentIndices(srcArchetype, dstArchetype))
            {
                EntityData.BuildResidueSharedComponentIndices(srcArchetype, dstArchetype,
                    src.Chunk->SharedComponentValues, temp);
            }
            else
            {
                // Always copy shared component indices since GetChunkWithEmptySlots might reallocate the storage of SharedComponentValues
                src.Chunk->SharedComponentValues.CopyTo(temp, 0, dstArchetype->NumSharedComponents);
            }

            SharedComponentValues sharedComponentValues = temp;

            Chunk* chunk = null;

            int instanceBeginIndex = 0;
            while (instanceBeginIndex != instanceCount)
            {
                chunk = archetypeManager.GetChunkWithEmptySlots(dstArchetype, sharedComponentValues);
                int indexInChunk;
                var allocatedCount =
                    archetypeManager.AllocateIntoChunk(chunk, instanceCount - instanceBeginIndex, out indexInChunk);
                ChunkDataUtility.ReplicateComponents(src.Chunk, src.IndexInChunk, chunk, indexInChunk, allocatedCount);
                m_Entities.AllocateEntities(dstArchetype, chunk, indexInChunk, allocatedCount,
                    outputEntities + instanceBeginIndex);
                chunk->SetAllChangeVersions(GlobalSystemVersion);

#if UNITY_EDITOR
                for (var i = 0; i < allocatedCount; ++i)
                    m_Entities.Name[outputEntities[i + instanceBeginIndex].Index] = m_Entities.Name[srcEntity.Index];
#endif

                if (remapChunks != null)
                {
                    remapChunks[remapChunksCount].Chunk = chunk;
                    remapChunks[remapChunksCount].IndexInChunk = indexInChunk;
                    remapChunks[remapChunksCount].AllocatedCount = allocatedCount;
                    remapChunks[remapChunksCount].InstanceBeginIndex = instanceBeginIndex;
                    remapChunksCount++;
                }


                instanceBeginIndex += allocatedCount;
            }

            if (chunk != null)
                IncrementComponentOrderVersion(dstArchetype, chunk, sharedComponentDataManager);

            return remapChunksCount;
        }

        void InstantiateEntitiesGroup(ArchetypeManager archetypeManager,
            SharedComponentDataManager sharedComponentDataManager, Entity* srcEntities, int srcEntityCount,
            Entity* outputRootEntities, int instanceCount)
        {
            int totalCount = srcEntityCount * instanceCount;

            var tempAllocSize = sizeof(EntityRemapUtility.SparseEntityRemapInfo) * totalCount +
                                sizeof(InstantiateRemapChunk) * totalCount + sizeof(Entity) * instanceCount;
            byte* allocation;
            const int kMaxStackAllocSize = 16 * 1024;

            if (tempAllocSize > kMaxStackAllocSize)
            {
                allocation = (byte*) UnsafeUtility.Malloc(tempAllocSize, 16, Allocator.Temp);
            }
            else
            {
                var temp = stackalloc byte[tempAllocSize];
                allocation = temp;
            }


            var entityRemap = (EntityRemapUtility.SparseEntityRemapInfo*) allocation;
            var remapChunks = (InstantiateRemapChunk*) (entityRemap + totalCount);
            var outputEntities = (Entity*) (remapChunks + totalCount);

            var remapChunksCount = 0;

            for (int i = 0; i != srcEntityCount; i++)
            {
                var srcEntity = srcEntities[i];
                remapChunksCount = InstantiateEntitiesOne(archetypeManager, sharedComponentDataManager, srcEntity,
                    outputEntities, instanceCount, remapChunks, remapChunksCount);

                for (int r = 0; r != instanceCount; r++)
                {
                    var ptr = entityRemap + (r * srcEntityCount + i);
                    ptr->Src = srcEntity;
                    ptr->Target = outputEntities[r];
                }

                if (i == 0)
                {
                    for (int r = 0; r != instanceCount; r++)
                        outputRootEntities[r] = outputEntities[r];
                }
            }

            for (int i = 0; i != remapChunksCount; i++)
            {
                var chunk = remapChunks[i].Chunk;
                var dstArchetype = chunk->Archetype;
                var allocatedCount = remapChunks[i].AllocatedCount;
                var indexInChunk = remapChunks[i].IndexInChunk;
                var instanceBeginIndex = remapChunks[i].InstanceBeginIndex;

                var localRemap = entityRemap + instanceBeginIndex * srcEntityCount;
                EntityRemapUtility.PatchEntitiesForPrefab(dstArchetype->ScalarEntityPatches + 1,
                    dstArchetype->ScalarEntityPatchCount - 1, dstArchetype->BufferEntityPatches,
                    dstArchetype->BufferEntityPatchCount, chunk->Buffer, indexInChunk, allocatedCount, localRemap,
                    srcEntityCount);
            }

            if (tempAllocSize > kMaxStackAllocSize)
                UnsafeUtility.Free(allocation, Allocator.Temp);
        }


        public int GetSharedComponentDataIndex(Entity entity, int typeIndex)
        {
            return m_Entities.GetSharedComponentDataIndex(entity, typeIndex);
        }

        public void SetSharedComponentDataIndex(ArchetypeManager archetypeManager,
            SharedComponentDataManager sharedComponentDataManager, Entity entity, int typeIndex,
            int newSharedComponentDataIndex)
        {
            m_Entities.SetSharedComponentDataIndex(archetypeManager, sharedComponentDataManager, entity, typeIndex,
                newSharedComponentDataIndex);
        }

        public void IncrementComponentOrderVersion(Archetype* archetype, Chunk* chunk,
            SharedComponentDataManager sharedComponentDataManager)
        {
            m_Entities.IncrementComponentOrderVersion(archetype, chunk, sharedComponentDataManager);
        }

        public void IncrementComponentTypeOrderVersion(Archetype* archetype)
        {
            m_Entities.IncrementComponentTypeOrderVersion(archetype);
        }

        public int GetComponentTypeOrderVersion(int typeIndex)
        {
            return m_Entities.GetComponentTypeOrderVersion(typeIndex);
        }
    }
}
