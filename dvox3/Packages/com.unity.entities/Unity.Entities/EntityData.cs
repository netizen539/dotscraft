using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine.Profiling;
using Unity.Assertions;
using Unity.Mathematics;

// Notes on upcoming changes to EntityData:
//
// Checklist @macton Where is EntityData and the EntityBatch interface going?
// [ ] Replace all internal interfaces to EntityData to work with EntityBatch via EntityData
//   [x] Convert AddComponent NativeArray<Entity> 
//   [x] Convert AddComponent NativeArray<ArchetypeChunk> 
//   [x] Convert AddSharedComponent NativeArray<ArchetypeChunk> 
//   [x] Convert AddChunkComponent NativeArray<ArchetypeChunk> 
//   [x] Move AddComponents(entity)
//   [ ] Need AddComponents for NativeList<EntityBatch>
//   [ ] Convert DestroyEntities
//   [ ] Convert RemoveComponent NativeArray<ArchetypeChunk>
//   [ ] Convert RemoveComponent Entity
// [ ] EntityDataManager just becomes thin shim on top of EntityData
// [ ] Remove EntityDataManager
// [ ] Rework internal storage so that structural changes are blittable (and burst job)
// [ ] Expose EntityBatch interface public via EntityManager
// [ ] Other structural interfaces (e.g. NativeArray<Entity>) are then (optional) utility functions.
//
// 1. Ideally EntityData is the internal interface that EntityCommandBuffer can use (fast).
// 2. That would be the only access point for JobComponentSystem.
// 3. "Easy Mode" can have (the equivalent) of EntityManager as utility functions on EntityData.
// 4. EntityDataManager goes away.
//
// Input data protocol to support for structural changes:
//    1. NativeList<EntityBatch>
//    2. NativeArray<ArchetypeChunk>
//    3. Entity
//
// Expected public (internal) API:
//
// ** Add Component **
//
// IComponentData and ISharedComponentData can be added via:
//    AddComponent NativeList<EntityBatch>
//    AddComponent Entity
//    AddComponents NativeList<EntityBatch>
//    AddComponents Entity
//
// Chunk Components can only be added via;
//    AddChunkComponent NativeArray<ArchetypeChunk>
//
// Alternative to add ISharedComponeentData when changing whole chunks.
//    AddSharedComponent NativeArray<ArchetypeChunk>
//
// ** Remove Component **
//
// Any component type can be removed via:
//    RemoveComponent NativeList<EntityBatch>
//    RemoveComponent Entity
//    RemoveComponent NativeArray<ArchetypeChunk>
//    RemoveComponents NativeList<EntityBatch>
//    RemoveComponents Entity
//    RemoveComponents NativeArray<ArchetypeChunk>


namespace Unity.Entities
{
    internal unsafe struct EntityData
    {
        public int* Version;
        public Archetype** Archetype;
        public EntityChunkData* ChunkData;
#if UNITY_EDITOR
        public NumberedWords* Name;
#endif

        private int m_EntitiesCapacity;
        public int FreeIndex;
        private int* m_ComponentTypeOrderVersion;
        public uint GlobalSystemVersion;

        public int EntityOrderVersion => GetComponentTypeOrderVersion(TypeManager.GetTypeIndex<Entity>());

        public int Capacity
        {
            get { return m_EntitiesCapacity; }
            set
            {
                if (value <= m_EntitiesCapacity)
                    return;

                var versionBytes = (value * sizeof(int) + 63) & ~63;
                var archetypeBytes = (value * sizeof(Archetype*) + 63) & ~63;
                var chunkDataBytes = (value * sizeof(EntityChunkData) + 63) & ~63;
                var bytesToAllocate = versionBytes + archetypeBytes + chunkDataBytes;
#if UNITY_EDITOR
                var nameBytes = (value * sizeof(NumberedWords) + 63) & ~63;
                bytesToAllocate += nameBytes;
#endif

                var bytes = (byte*) UnsafeUtility.Malloc(bytesToAllocate, 64, Allocator.Persistent);

                var version = (int*) (bytes);
                var archetype = (Archetype**) (bytes + versionBytes);
                var chunkData = (EntityChunkData*) (bytes + versionBytes + archetypeBytes);
#if UNITY_EDITOR
                var name = (NumberedWords*) (bytes + versionBytes + archetypeBytes + chunkDataBytes);
#endif

                var startNdx = 0;
                if (m_EntitiesCapacity > 0)
                {
                    UnsafeUtility.MemCpy(version, Version, m_EntitiesCapacity * sizeof(int));
                    UnsafeUtility.MemCpy(archetype, Archetype, m_EntitiesCapacity * sizeof(Archetype*));
                    UnsafeUtility.MemCpy(chunkData, ChunkData, m_EntitiesCapacity * sizeof(EntityChunkData));
#if UNITY_EDITOR
                    UnsafeUtility.MemCpy(name, Name, m_EntitiesCapacity * sizeof(NumberedWords));
#endif
                    UnsafeUtility.Free(Version, Allocator.Persistent);
                    startNdx = m_EntitiesCapacity - 1;
                }

                Version = version;
                Archetype = archetype;
                ChunkData = chunkData;
#if UNITY_EDITOR
                Name = name;
#endif

                m_EntitiesCapacity = value;
                InitializeAdditionalCapacity(startNdx);
            }
        }

        public void IncreaseCapacity()
        {
            Capacity = 2 * Capacity;
        }

        public static EntityData Create(int newCapacity)
        {
            EntityData entities = new EntityData();
            entities.Capacity = newCapacity;

            entities.GlobalSystemVersion = ChangeVersionUtility.InitialGlobalSystemVersion;

            const int componentTypeOrderVersionSize = sizeof(int) * TypeManager.MaximumTypesCount;
            entities.m_ComponentTypeOrderVersion = (int*) UnsafeUtility.Malloc(componentTypeOrderVersionSize,
                UnsafeUtility.AlignOf<int>(), Allocator.Persistent);
            UnsafeUtility.MemClear(entities.m_ComponentTypeOrderVersion, componentTypeOrderVersionSize);

            return entities;
        }

        public void Dispose()
        {
            if (m_EntitiesCapacity > 0)
            {
                UnsafeUtility.Free(Version, Allocator.Persistent);

                Version = null;
                Archetype = null;
                ChunkData = null;
#if UNITY_EDITOR
                Name = null;
#endif

                m_EntitiesCapacity = 0;
            }

            if (m_ComponentTypeOrderVersion != null)
            {
                UnsafeUtility.Free(m_ComponentTypeOrderVersion, Allocator.Persistent);
                m_ComponentTypeOrderVersion = null;
            }
        }

        private void InitializeAdditionalCapacity(int start)
        {
            for (var i = start; i != Capacity; i++)
            {
                ChunkData[i].IndexInChunk = i + 1;
                Version[i] = 1;
                ChunkData[i].Chunk = null;
#if UNITY_EDITOR
                Name[i] = new NumberedWords();
#endif
            }

            // Last entity indexInChunk identifies that we ran out of space...
            ChunkData[Capacity - 1].IndexInChunk = -1;
        }

        public void FreeAllEntities()
        {
            for (var i = 0; i != Capacity; i++)
            {
                ChunkData[i].IndexInChunk = i + 1;
                Version[i] += 1;
                ChunkData[i].Chunk = null;
#if UNITY_EDITOR
                Name[i] = new NumberedWords();
#endif
            }

            // Last entity indexInChunk identifies that we ran out of space...
            ChunkData[Capacity - 1].IndexInChunk = -1;
            FreeIndex = 0;
        }

        public void FreeEntities(Chunk* chunk)
        {
            var count = chunk->Count;
            var entities = (Entity*) chunk->Buffer;
            int freeIndex = FreeIndex;
            for (var i = 0; i != count; i++)
            {
                int index = entities[i].Index;
                Version[index] += 1;
                ChunkData[index].Chunk = null;
                ChunkData[index].IndexInChunk = freeIndex;
#if UNITY_EDITOR
                Name[index] = new NumberedWords();
#endif
                freeIndex = index;
            }

            FreeIndex = freeIndex;
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void ValidateEntity(Entity entity)
        {
            if (entity.Index < 0)
                throw new ArgumentException(
                    $"All entities created using EntityCommandBuffer.CreateEntity must be realized via playback(). One of the entities is still deferred (Index: {entity.Index}).");
            if ((uint) entity.Index >= (uint) Capacity)
                throw new ArgumentException(
                    "All entities passed to EntityManager must exist. One of the entities has already been destroyed or was never created.");
        }

        public bool Exists(Entity entity)
        {
            int index = entity.Index;

            ValidateEntity(entity);

            var versionMatches = Version[index] == entity.Version;
            var hasChunk = ChunkData[index].Chunk != null;

            return versionMatches && hasChunk;
        }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        public int CheckInternalConsistency()
        {
            var aliveEntities = 0;
            var entityType = TypeManager.GetTypeIndex<Entity>();

            for (var i = 0; i != Capacity; i++)
            {
                var chunk = ChunkData[i].Chunk;
                if (chunk == null)
                    continue;

                aliveEntities++;
                var archetype = Archetype[i];
                Assert.AreEqual((IntPtr) archetype, (IntPtr) chunk->Archetype);
                Assert.AreEqual(entityType, archetype->Types[0].TypeIndex);
                var entity =
                    *(Entity*) ChunkDataUtility.GetComponentDataRO(ChunkData[i].Chunk, ChunkData[i].IndexInChunk, 0);
                Assert.AreEqual(i, entity.Index);
                Assert.AreEqual(Version[i], entity.Version);

                Assert.IsTrue(Exists(entity));
            }

            return aliveEntities;
        }
#endif

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertEntitiesExist(Entity* entities, int count)
        {
            for (var i = 0; i != count; i++)
            {
                var entity = entities + i;

                ValidateEntity(*entity);

                int index = entity->Index;
                var exists = Version[index] == entity->Version && ChunkData[index].Chunk != null;
                if (!exists)
                    throw new ArgumentException(
                        "All entities passed to EntityManager must exist. One of the entities has already been destroyed or was never created.");
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanDestroy(Entity* entities, int count)
        {
            for (var i = 0; i != count; i++)
            {
                var entity = entities + i;
                if (!Exists(*entity))
                    continue;

                int index = entity->Index;
                var chunk = ChunkData[index].Chunk;
                if (chunk->Locked || chunk->LockedEntityOrder)
                {
                    throw new InvalidOperationException(
                        "Cannot destroy entities in locked Chunks. Unlock Chunk first.");
                }
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertEntityHasComponent(Entity entity, ComponentType componentType)
        {
            if (HasComponent(entity, componentType))
                return;

            if (!Exists(entity))
                throw new ArgumentException("The entity does not exist");

            throw new ArgumentException($"A component with type:{componentType} has not been added to the entity.");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertEntityHasComponent(Entity entity, int componentType)
        {
            AssertEntityHasComponent(entity, ComponentType.FromTypeIndex(componentType));
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanAddComponent(Entity entity, ComponentType componentType)
        {
            if (!Exists(entity))
                throw new ArgumentException("The entity does not exist");

            if (!componentType.IgnoreDuplicateAdd && HasComponent(entity, componentType))
                throw new ArgumentException(
                    $"The component of type:{componentType} has already been added to the entity.");

            var chunk = GetComponentChunk(entity);
            if (chunk->Locked || chunk->LockedEntityOrder)
                throw new InvalidOperationException("Cannot add components to locked Chunks. Unlock Chunk first.");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanRemoveComponent(Entity entity, ComponentType componentType)
        {
            if (HasComponent(entity, componentType))
            {
                var chunk = GetComponentChunk(entity);
                if (chunk->Locked || chunk->LockedEntityOrder)
                    throw new ArgumentException(
                        $"The component of type:{componentType} has already been added to the entity.");
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanAddComponent(Entity entity, int componentType)
        {
            AssertCanAddComponent(entity, ComponentType.FromTypeIndex(componentType));
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanAddComponents(Entity entity, ComponentTypes types)
        {
            for (int i = 0; i < types.Length; ++i)
                AssertCanAddComponent(entity, ComponentType.FromTypeIndex(types.GetTypeIndex(i)));
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanRemoveComponents(Entity entity, ComponentTypes types)
        {
            for (int i = 0; i < types.Length; ++i)
                AssertCanRemoveComponent(entity, ComponentType.FromTypeIndex(types.GetTypeIndex(i)));
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanAddComponent(NativeArray<ArchetypeChunk> chunkArray, ComponentType componentType)
        {
            var chunks = (ArchetypeChunk*) chunkArray.GetUnsafeReadOnlyPtr();
            for (int i = 0; i < chunkArray.Length; ++i)
            {
                var chunk = chunks[i].m_Chunk;
                if (ChunkDataUtility.GetIndexInTypeArray(chunk->Archetype, componentType.TypeIndex) != -1)
                    throw new ArgumentException(
                        $"A component with type:{componentType} has already been added to the chunk.");
                if (chunk->Locked)
                    throw new InvalidOperationException("Cannot add components to locked Chunks. Unlock Chunk first.");
                if (chunk->LockedEntityOrder && !componentType.IsZeroSized)
                    throw new InvalidOperationException(
                        "Cannot add non-zero sized components to LockedEntityOrder Chunks. Unlock Chunk first.");
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanRemoveComponent(NativeArray<ArchetypeChunk> chunkArray, ComponentType componentType)
        {
            var chunks = (ArchetypeChunk*) chunkArray.GetUnsafeReadOnlyPtr();
            for (int i = 0; i < chunkArray.Length; ++i)
            {
                var chunk = chunks[i].m_Chunk;
                if (ChunkDataUtility.GetIndexInTypeArray(chunk->Archetype, componentType.TypeIndex) != -1)
                {
                    if (chunk->Locked)
                        throw new InvalidOperationException(
                            "Cannot remove components from locked Chunks. Unlock Chunk first.");
                    if (chunk->LockedEntityOrder && !componentType.IsZeroSized)
                        throw new InvalidOperationException(
                            "Cannot remove non-zero sized components to LockedEntityOrder Chunks. Unlock Chunk first.");
                }
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanDestroy(NativeArray<ArchetypeChunk> chunkArray)
        {
            var chunks = (ArchetypeChunk*) chunkArray.GetUnsafeReadOnlyPtr();
            for (int i = 0; i < chunkArray.Length; ++i)
            {
                var chunk = chunks[i].m_Chunk;
                if (chunk->Locked)
                    throw new InvalidOperationException(
                        "Cannot destroy entities from locked Chunks. Unlock Chunk first.");
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanAddChunkComponent(NativeArray<ArchetypeChunk> chunkArray, ComponentType componentType)
        {
            var chunks = (ArchetypeChunk*) chunkArray.GetUnsafeReadOnlyPtr();
            for (int i = 0; i < chunkArray.Length; ++i)
            {
                var chunk = chunks[i].m_Chunk;
                if (ChunkDataUtility.GetIndexInTypeArray(chunk->Archetype, componentType.TypeIndex) != -1)
                    throw new ArgumentException(
                        $"A chunk component with type:{componentType} has already been added to the chunk.");
                if (chunk->Locked)
                    throw new InvalidOperationException(
                        "Cannot add chunk components to locked Chunks. Unlock Chunk first.");
                if ((chunk->metaChunkEntity != Entity.Null) && GetComponentChunk(chunk->metaChunkEntity)->Locked)
                    throw new InvalidOperationException(
                        "Cannot add chunk components if Meta Chunk is locked. Unlock Meta Chunk first.");
                if ((chunk->metaChunkEntity != Entity.Null) &&
                    GetComponentChunk(chunk->metaChunkEntity)->LockedEntityOrder)
                    throw new InvalidOperationException(
                        "Cannot add chunk components if Meta Chunk is LockedEntityOrder. Unlock Meta Chunk first.");
            }
        }

        public void IncrementComponentOrderVersion(Archetype* archetype, Chunk* chunk,
            SharedComponentDataManager sharedComponentDataManager)
        {
            // Increment shared component version
            var sharedComponentValues = chunk->SharedComponentValues;
            IncrementComponentOrderVersion(archetype, sharedComponentDataManager, sharedComponentValues);
        }

        public void IncrementComponentOrderVersion(Archetype* archetype,
            SharedComponentDataManager sharedComponentDataManager,
            SharedComponentValues sharedComponentValues)
        {
            for (var i = 0; i < archetype->NumSharedComponents; i++)
                sharedComponentDataManager.IncrementSharedComponentVersion(sharedComponentValues[i]);

            IncrementComponentTypeOrderVersion(archetype);
        }

        public void IncrementComponentTypeOrderVersion(Archetype* archetype)
        {
            // Increment type component version
            for (var t = 0; t < archetype->TypesCount; ++t)
            {
                var typeIndex = archetype->Types[t].TypeIndex;
                m_ComponentTypeOrderVersion[typeIndex & TypeManager.ClearFlagsMask]++;
            }
        }

        public int GetComponentTypeOrderVersion(int typeIndex)
        {
            return m_ComponentTypeOrderVersion[typeIndex & TypeManager.ClearFlagsMask];
        }

        public void IncrementGlobalSystemVersion()
        {
            ChangeVersionUtility.IncrementGlobalSystemVersion(ref GlobalSystemVersion);
        }

        public bool HasComponent(Entity entity, int type)
        {
            if (!Exists(entity))
                return false;

            var archetype = Archetype[entity.Index];
            return ChunkDataUtility.GetIndexInTypeArray(archetype, type) != -1;
        }

        public bool HasComponent(Entity entity, ComponentType type)
        {
            if (!Exists(entity))
                return false;

            var archetype = Archetype[entity.Index];
            return ChunkDataUtility.GetIndexInTypeArray(archetype, type.TypeIndex) != -1;
        }

        public int GetSizeInChunk(Entity entity, int typeIndex, ref int typeLookupCache)
        {
            var entityChunk = ChunkData[entity.Index].Chunk;
            return ChunkDataUtility.GetSizeInChunk(entityChunk, typeIndex, ref typeLookupCache);
        }

        public void AddComponent(Entity entity, ComponentType type, ArchetypeManager archetypeManager,
            SharedComponentDataManager sharedComponentDataManager,
            EntityGroupManager groupManager)
        {
            var archetype = Archetype[entity.Index];
            int indexInTypeArray = 0;
            var newType =
                archetypeManager.GetArchetypeWithAddedComponentType(archetype, type, groupManager, &indexInTypeArray);
            if (newType == null)
            {
                // This can happen if we are adding a tag component to an entity that already has it.
                return;
            }

            var sharedComponentValues = GetComponentChunk(entity)->SharedComponentValues;
            if (type.IsSharedComponent)
            {
                int* temp = stackalloc int[newType->NumSharedComponents];
                int indexOfNewSharedComponent = indexInTypeArray - newType->FirstSharedComponent;
                BuildSharedComponentIndicesWithAddedComponent(indexOfNewSharedComponent, 0,
                    newType->NumSharedComponents, sharedComponentValues, temp);
                sharedComponentValues = temp;
            }

            SetArchetype(archetypeManager, entity, newType, sharedComponentValues, sharedComponentDataManager);
        }

        public void AddComponent(NativeList<EntityBatch> entityBatchList, ComponentType type,
            int existingSharedComponentIndex,
            ArchetypeManager archetypeManager,
            SharedComponentDataManager sharedComponentDataManager,
            EntityGroupManager groupManager)
        {
            using (var sourceBlittableEntityBatchList = new NativeList<EntityBatch>(Allocator.Persistent))
            using (var destinationBlittableEntityBatchList = new NativeList<EntityBatch>(Allocator.Persistent))
            using (var sourceManagedEntityBatchList = new NativeList<EntityBatch>(Allocator.Persistent))
            using (var destinationManagedEntityBatchList = new NativeList<EntityBatch>(Allocator.Persistent))
            using (var packBlittableEntityBatchList = new NativeList<EntityBatch>(Allocator.Persistent))
            using (var packManagedEntityBatchList = new NativeList<EntityBatch>(Allocator.Persistent))
            using (var sourceCountEntityBatchList = new NativeList<EntityBatch>(Allocator.Persistent))
            using (var moveChunkList = new NativeList<EntityBatch>(Allocator.Persistent))
            {
                AllocateChunksForAddComponent(entityBatchList, type, existingSharedComponentIndex, archetypeManager,
                    groupManager,
                    sourceCountEntityBatchList, packBlittableEntityBatchList, packManagedEntityBatchList,
                    sourceBlittableEntityBatchList, destinationBlittableEntityBatchList, sourceManagedEntityBatchList,
                    destinationManagedEntityBatchList, moveChunkList);

                var copyBlittableChunkDataJobHandle = CopyBlittableChunkDataJob(sourceBlittableEntityBatchList,
                    destinationBlittableEntityBatchList);
                var packBlittableChunkDataJobHandle =
                    PackBlittableChunkDataJob(packBlittableEntityBatchList, copyBlittableChunkDataJobHandle);
                packBlittableChunkDataJobHandle.Complete();

                CopyManagedChunkData(sourceManagedEntityBatchList, destinationManagedEntityBatchList, archetypeManager);
                PackManagedChunkData(archetypeManager, packManagedEntityBatchList);
                UpdateDestinationVersions(sharedComponentDataManager, sourceBlittableEntityBatchList,
                    destinationBlittableEntityBatchList);
                UpdateSourceCountsAndVersions(archetypeManager, sharedComponentDataManager, sourceCountEntityBatchList);
                MoveChunksForAddComponent(moveChunkList, type, existingSharedComponentIndex, archetypeManager,
                    sharedComponentDataManager, groupManager);
            }
        }

        public void AddComponents(Entity entity, ComponentTypes types, ArchetypeManager archetypeManager,
            SharedComponentDataManager sharedComponentDataManager,
            EntityGroupManager groupManager)
        {
            var oldArchetype = Archetype[entity.Index];
            var oldTypes = oldArchetype->Types;

            var newTypesCount = oldArchetype->TypesCount + types.Length;
            ComponentTypeInArchetype* newTypes = stackalloc ComponentTypeInArchetype[newTypesCount];


            var indexOfNewTypeInNewArchetype = stackalloc int[types.Length];

            // zipper the two sorted arrays "type" and "componentTypeInArchetype" into "componentTypeInArchetype"
            // because this is done in-place, it must be done backwards so as not to disturb the existing contents.

            var unusedIndices = 0;
            {
                var oldThings = oldArchetype->TypesCount;
                var newThings = types.Length;
                var mixedThings = oldThings + newThings;
                while (oldThings > 0 && newThings > 0) // while both are still zippering,
                {
                    var oldThing = oldTypes[oldThings - 1];
                    var newThing = types.GetComponentType(newThings - 1);
                    if (oldThing.TypeIndex > newThing.TypeIndex) // put whichever is bigger at the end of the array
                    {
                        newTypes[--mixedThings] = oldThing;
                        --oldThings;
                    }
                    else
                    {
                        if (oldThing.TypeIndex == newThing.TypeIndex && newThing.IgnoreDuplicateAdd)
                            --oldThings;

                        var componentTypeInArchetype = new ComponentTypeInArchetype(newThing);
                        newTypes[--mixedThings] = componentTypeInArchetype;
                        --newThings;
                        indexOfNewTypeInNewArchetype[newThings] = mixedThings; // "this new thing ended up HERE"
                    }
                }

                Assert.AreEqual(0, newThings); // must not be any new things to copy remaining, oldThings contain entity

                while (oldThings > 0) // if there are remaining old things, copy them here
                {
                    newTypes[--mixedThings] = oldTypes[--oldThings];
                }

                unusedIndices = mixedThings; // In case we ignored duplicated types, this will be > 0
            }

            var newArchetype =
                archetypeManager.GetOrCreateArchetype(newTypes + unusedIndices, newTypesCount, groupManager);

            var sharedComponentValues = GetComponentChunk(entity)->SharedComponentValues;
            if (types.m_masks.m_SharedComponentMask != 0)
            {
                int* alloc2 = stackalloc int[newArchetype->NumSharedComponents];
                var oldSharedComponentValues = sharedComponentValues;
                sharedComponentValues = alloc2;
                EntityData.BuildSharedComponentIndicesWithAddedComponents(oldArchetype, newArchetype,
                    oldSharedComponentValues, alloc2);
            }

            SetArchetype(archetypeManager, entity, newArchetype, sharedComponentValues, sharedComponentDataManager);
        }

        public void SetChunkComponent<T>(NativeList<EntityBatch> entityBatchList, T componentData)
            where T : struct, IComponentData
        {
            var type = ComponentType.ReadWrite<T>();
            if (type.IsZeroSized)
                return;

            for (int i = 0; i < entityBatchList.Length; i++)
            {
                var srcEntityBatch = entityBatchList[i];
                var srcChunk = srcEntityBatch.Chunk;
                if (!type.IsZeroSized)
                {
                    var ptr = GetComponentDataWithTypeRW(srcChunk.m_Chunk->metaChunkEntity,
                        TypeManager.GetTypeIndex<T>(),
                        GlobalSystemVersion);
                    UnsafeUtility.CopyStructureToPtr(ref componentData, ptr);
                }
            }
        }

        public void MoveChunksForAddComponent(NativeList<EntityBatch> entityBatchList, ComponentType type,
            int existingSharedComponentIndex,
            ArchetypeManager archetypeManager,
            SharedComponentDataManager sharedComponentDataManager,
            EntityGroupManager groupManager)
        {
            Archetype* prevSrcArchetype = null;
            Archetype* dstArchetype = null;
            int indexInTypeArray = 0;

            for (int i = 0; i < entityBatchList.Length; i++)
            {
                var srcEntityBatch = entityBatchList[i];
                var srcChunk = srcEntityBatch.Chunk;
                var srcArchetype = srcChunk.Archetype.Archetype;
                if (srcArchetype != prevSrcArchetype)
                {
                    dstArchetype =
                        archetypeManager.GetArchetypeWithAddedComponentType(srcArchetype, type, groupManager,
                            &indexInTypeArray);
                    prevSrcArchetype = srcArchetype;
                }

                var sharedComponentValues = srcChunk.m_Chunk->SharedComponentValues;
                if (type.IsSharedComponent)
                {
                    int* temp = stackalloc int[dstArchetype->NumSharedComponents];
                    int indexOfNewSharedComponent = indexInTypeArray - dstArchetype->FirstSharedComponent;
                    BuildSharedComponentIndicesWithAddedComponent(indexOfNewSharedComponent,
                        existingSharedComponentIndex,
                        dstArchetype->NumSharedComponents, sharedComponentValues, temp);
                    sharedComponentValues = temp;
                }

                MoveChunkToNewArchetype(srcChunk.m_Chunk, dstArchetype, GlobalSystemVersion, archetypeManager,
                    sharedComponentValues, sharedComponentDataManager);
            }

            sharedComponentDataManager.AddReference(existingSharedComponentIndex, entityBatchList.Length);
        }

        public void MoveChunkToNewArchetype(Chunk* chunk, Archetype* newArchetype, uint globalVersion,
            ArchetypeManager archetypeManager, SharedComponentValues sharedComponentValues,
            SharedComponentDataManager sharedComponentDataManager)
        {
            var oldArchetype = chunk->Archetype;
            ChunkDataUtility.AssertAreLayoutCompatible(oldArchetype, newArchetype);
            var count = chunk->Count;
            bool hasEmptySlots = count < chunk->Capacity;

            if (hasEmptySlots)
                ArchetypeManager.EmptySlotTrackingRemoveChunk(chunk);

            int chunkIndexInOldArchetype = chunk->ListIndex;

            var newTypes = newArchetype->Types;
            var oldTypes = oldArchetype->Types;

            chunk->Archetype = newArchetype;
            //Change version is overriden below
            newArchetype->AddToChunkList(chunk, sharedComponentValues, 0);
            int chunkIndexInNewArchetype = chunk->ListIndex;

            //Copy change versions from old to new archetype
            for (int iOldType = oldArchetype->TypesCount - 1, iNewType = newArchetype->TypesCount - 1;
                iNewType >= 0;
                --iNewType)
            {
                var newType = newTypes[iNewType];
                while (oldTypes[iOldType] > newType)
                    --iOldType;
                var version = oldTypes[iOldType] == newType
                    ? oldArchetype->Chunks.GetChangeVersion(iOldType, chunkIndexInOldArchetype)
                    : globalVersion;
                newArchetype->Chunks.SetChangeVersion(iNewType, chunkIndexInNewArchetype, version);
            }

            chunk->ListIndex = chunkIndexInOldArchetype;
            oldArchetype->RemoveFromChunkList(chunk);
            chunk->ListIndex = chunkIndexInNewArchetype;

            if (hasEmptySlots)
                ArchetypeManager.EmptySlotTrackingAddChunk(chunk);

            var entities = (Entity*) chunk->Buffer;
            for (int i = 0; i < count; ++i)
            {
                Archetype[entities[i].Index] = newArchetype;
            }

            oldArchetype->EntityCount -= count;
            newArchetype->EntityCount += count;

            if (oldArchetype->MetaChunkArchetype != newArchetype->MetaChunkArchetype)
            {
                if (oldArchetype->MetaChunkArchetype == null)
                {
                    CreateMetaEntityForChunk(archetypeManager, chunk);
                }
                else if (newArchetype->MetaChunkArchetype == null)
                {
                    archetypeManager.DestroyMetaChunkEntity(chunk->metaChunkEntity);
                    chunk->metaChunkEntity = Entity.Null;
                }
                else
                {
                    var metaChunk = GetComponentChunk(chunk->metaChunkEntity);
                    var sharedComponentDataIndices = metaChunk->SharedComponentValues;
                    SetArchetype(archetypeManager, chunk->metaChunkEntity, newArchetype->MetaChunkArchetype,
                        sharedComponentDataIndices, sharedComponentDataManager);
                }
            }
        }

        public void CreateMetaEntityForChunk(ArchetypeManager archetypeManager, Chunk* chunk)
        {
            CreateEntities(archetypeManager, chunk->Archetype->MetaChunkArchetype, &chunk->metaChunkEntity, 1);
            var typeIndex = TypeManager.GetTypeIndex<ChunkHeader>();
            var chunkHeader =
                (ChunkHeader*) GetComponentDataWithTypeRW(chunk->metaChunkEntity, typeIndex, GlobalSystemVersion);
            chunkHeader->chunk = chunk;
        }

        public void CreateEntities(ArchetypeManager archetypeManager, Archetype* archetype, Entity* entities, int count)
        {
            var sharedComponentValues = stackalloc int[archetype->NumSharedComponents];
            UnsafeUtility.MemClear(sharedComponentValues, archetype->NumSharedComponents * sizeof(int));

            while (count != 0)
            {
                var chunk = archetypeManager.GetChunkWithEmptySlots(archetype, sharedComponentValues);
                int allocatedIndex;
                var allocatedCount = archetypeManager.AllocateIntoChunk(chunk, count, out allocatedIndex);
                AllocateEntities(archetype, chunk, allocatedIndex, allocatedCount, entities);
                ChunkDataUtility.InitializeComponents(chunk, allocatedIndex, allocatedCount);
                chunk->SetAllChangeVersions(GlobalSystemVersion);
                entities += allocatedCount;
                count -= allocatedCount;
            }

            IncrementComponentTypeOrderVersion(archetype);
        }

        public void AllocateEntities(Archetype* arch, Chunk* chunk, int baseIndex, int count, Entity* outputEntities)
        {
            Assert.AreEqual(chunk->Archetype->Offsets[0], 0);
            Assert.AreEqual(chunk->Archetype->SizeOfs[0], sizeof(Entity));

            var entityInChunkStart = (Entity*) chunk->Buffer + baseIndex;

            for (var i = 0; i != count; i++)
            {
                var entityIndexInChunk = ChunkData[FreeIndex].IndexInChunk;
                if (entityIndexInChunk == -1)
                {
                    IncreaseCapacity();
                    entityIndexInChunk = ChunkData[FreeIndex].IndexInChunk;
                }

                var entityVersion = Version[FreeIndex];

                if (outputEntities != null)
                {
                    outputEntities[i].Index = FreeIndex;
                    outputEntities[i].Version = entityVersion;
                }

                var entityInChunk = entityInChunkStart + i;

                entityInChunk->Index = FreeIndex;
                entityInChunk->Version = entityVersion;

                ChunkData[FreeIndex].IndexInChunk = baseIndex + i;
                Archetype[FreeIndex] = arch;
                ChunkData[FreeIndex].Chunk = chunk;
#if UNITY_EDITOR
                Name[FreeIndex] = new NumberedWords();
#endif

                FreeIndex = entityIndexInChunk;
            }
        }

        public void GetComponentChunk(Entity entity, out Chunk* chunk, out int chunkIndex)
        {
            var entityChunk = ChunkData[entity.Index].Chunk;
            var entityIndexInChunk = ChunkData[entity.Index].IndexInChunk;

            chunk = entityChunk;
            chunkIndex = entityIndexInChunk;
        }

        public Chunk* GetComponentChunk(Entity entity)
        {
            var entityChunk = ChunkData[entity.Index].Chunk;

            return entityChunk;
        }

        public void SetArchetype(ArchetypeManager typeMan, Entity entity, Archetype* archetype,
            SharedComponentValues sharedComponentValues, SharedComponentDataManager sharedComponentDataManager)
        {
            var chunk = typeMan.GetChunkWithEmptySlots(archetype, sharedComponentValues);
            var chunkIndex = typeMan.AllocateIntoChunk(chunk);

            var oldArchetype = Archetype[entity.Index];
            var oldChunk = ChunkData[entity.Index].Chunk;
            var oldChunkIndex = ChunkData[entity.Index].IndexInChunk;
            ChunkDataUtility.Convert(oldChunk, oldChunkIndex, chunk, chunkIndex);
            if (chunk->ManagedArrayIndex >= 0 && oldChunk->ManagedArrayIndex >= 0)
                ChunkDataUtility.CopyManagedObjects(typeMan, oldChunk, oldChunkIndex, chunk, chunkIndex, 1);

            Archetype[entity.Index] = archetype;
            ChunkData[entity.Index].Chunk = chunk;
            ChunkData[entity.Index].IndexInChunk = chunkIndex;

            var lastIndex = oldChunk->Count - 1;
            // No need to replace with ourselves
            if (lastIndex != oldChunkIndex)
            {
                var lastEntity = (Entity*) ChunkDataUtility.GetComponentDataRO(oldChunk, lastIndex, 0);
                ChunkData[lastEntity->Index].IndexInChunk = oldChunkIndex;

                ChunkDataUtility.Copy(oldChunk, lastIndex, oldChunk, oldChunkIndex, 1);
                if (oldChunk->ManagedArrayIndex >= 0)
                    ChunkDataUtility.CopyManagedObjects(typeMan, oldChunk, lastIndex, oldChunk, oldChunkIndex, 1);
            }

            if (oldChunk->ManagedArrayIndex >= 0)
                ChunkDataUtility.ClearManagedObjects(typeMan, oldChunk, lastIndex, 1);

            --oldArchetype->EntityCount;

            chunk->SetAllChangeVersions(GlobalSystemVersion);
            oldChunk->SetAllChangeVersions(GlobalSystemVersion);

            IncrementComponentOrderVersion(oldArchetype, oldChunk, sharedComponentDataManager);
            typeMan.SetChunkCount(oldChunk, lastIndex);

            IncrementComponentOrderVersion(archetype, chunk, sharedComponentDataManager);
        }

        public void SetArchetype(ArchetypeManager typeMan, Chunk* chunk, Archetype* archetype,
            SharedComponentValues sharedComponentValues, SharedComponentDataManager sharedComponentDataManager)
        {
            var srcChunk = chunk;
            var srcArchetype = srcChunk->Archetype;
            var srcEntities = (Entity*) srcChunk->Buffer;
            var srcEntitiesCount = srcChunk->Count;
            var srcRemainingCount = srcEntitiesCount;
            var srcOffset = 0;

            var dstArchetype = archetype;

            while (srcRemainingCount > 0)
            {
                var dstChunk = typeMan.GetChunkWithEmptySlots(archetype, sharedComponentValues);
                int dstIndexBase;
                var dstCount = typeMan.AllocateIntoChunk(dstChunk, srcRemainingCount, out dstIndexBase);

                ChunkDataUtility.Convert(srcChunk, srcOffset, dstChunk, dstIndexBase, dstCount);
                IncrementComponentOrderVersion(archetype, dstChunk, sharedComponentDataManager);

                for (int i = 0; i < dstCount; i++)
                {
                    var entity = srcEntities[srcOffset + i];
                    Archetype[entity.Index] = dstArchetype;
                    ChunkData[entity.Index].Chunk = dstChunk;
                    ChunkData[entity.Index].IndexInChunk = dstIndexBase + i;
                }

                if (srcChunk->ManagedArrayIndex >= 0 && dstChunk->ManagedArrayIndex >= 0)
                    ChunkDataUtility.CopyManagedObjects(typeMan, srcChunk, srcOffset, dstChunk, dstIndexBase, dstCount);

                srcRemainingCount -= dstCount;
                srcOffset += dstCount;
            }

            srcArchetype->EntityCount -= srcEntitiesCount;

            if (srcChunk->ManagedArrayIndex >= 0)
                ChunkDataUtility.ClearManagedObjects(typeMan, srcChunk, 0, srcEntitiesCount);
            typeMan.SetChunkCount(srcChunk, 0);
        }

        public byte* GetComponentDataWithTypeRO(Entity entity, int typeIndex)
        {
            var entityChunk = ChunkData[entity.Index].Chunk;
            var entityIndexInChunk = ChunkData[entity.Index].IndexInChunk;

            return ChunkDataUtility.GetComponentDataWithTypeRO(entityChunk, entityIndexInChunk, typeIndex);
        }

        public byte* GetComponentDataWithTypeRW(Entity entity, int typeIndex, uint globalVersion)
        {
            var entityChunk = ChunkData[entity.Index].Chunk;
            var entityIndexInChunk = ChunkData[entity.Index].IndexInChunk;

            return ChunkDataUtility.GetComponentDataWithTypeRW(entityChunk, entityIndexInChunk, typeIndex,
                globalVersion);
        }

        public byte* GetComponentDataWithTypeRO(Entity entity, int typeIndex, ref int typeLookupCache)
        {
            var entityChunk = ChunkData[entity.Index].Chunk;
            var entityIndexInChunk = ChunkData[entity.Index].IndexInChunk;

            return ChunkDataUtility.GetComponentDataWithTypeRO(entityChunk, entityIndexInChunk, typeIndex,
                ref typeLookupCache);
        }

        public byte* GetComponentDataWithTypeRW(Entity entity, int typeIndex, uint globalVersion,
            ref int typeLookupCache)
        {
            var entityChunk = ChunkData[entity.Index].Chunk;
            var entityIndexInChunk = ChunkData[entity.Index].IndexInChunk;

            return ChunkDataUtility.GetComponentDataWithTypeRW(entityChunk, entityIndexInChunk, typeIndex,
                globalVersion, ref typeLookupCache);
        }

        private static void AllocateChunksForAddComponent(NativeList<EntityBatch> entityBatchList, ComponentType type,
            int existingSharedComponentIndex,
            ArchetypeManager archetypeManager, EntityGroupManager groupManager,
            NativeList<EntityBatch> sourceCountEntityBatchList,
            NativeList<EntityBatch> packBlittableEntityBatchList,
            NativeList<EntityBatch> packManagedEntityBatchList,
            NativeList<EntityBatch> sourceBlittableEntityBatchList,
            NativeList<EntityBatch> destinationBlittableEntityBatchList,
            NativeList<EntityBatch> sourceManagedEntityBatchList,
            NativeList<EntityBatch> destinationManagedEntityBatchList,
            NativeList<EntityBatch> moveChunkList)
        {
            Profiler.BeginSample("Allocate Chunks");

            var prevSrcArchetype = new EntityArchetype();
            Archetype* dstArchetype = null;
            int indexInTypeArray = 0;
            var layoutCompatible = false;

            for (int i = 0; i < entityBatchList.Length; i++)
            {
                var srcEntityBatch = entityBatchList[i];
                var srcRemainingCount = srcEntityBatch.Count;
                var srcChunk = srcEntityBatch.Chunk;
                var srcArchetype = srcChunk.Archetype;
                var srcStartIndex = srcEntityBatch.StartIndex;
                var srcTail = (srcStartIndex + srcRemainingCount) == srcChunk.Count;
                var srcChunkManagedData = srcChunk.m_Chunk->ManagedArrayIndex >= 0;

                if (prevSrcArchetype != srcArchetype)
                {
                    dstArchetype = archetypeManager.GetArchetypeWithAddedComponentType(srcArchetype.Archetype, type,
                        groupManager, &indexInTypeArray);
                    layoutCompatible = ChunkDataUtility.AreLayoutCompatible(srcArchetype.Archetype, dstArchetype);
                    prevSrcArchetype = srcArchetype;
                }

                if (dstArchetype == null)
                    continue;

                var srcWholeChunk = srcEntityBatch.Count == srcChunk.Count;
                if (srcWholeChunk && layoutCompatible)
                {
                    moveChunkList.Add(srcEntityBatch);
                    continue;
                }

                var sharedComponentValues = srcChunk.m_Chunk->SharedComponentValues;
                if (type.IsSharedComponent)
                {
                    int* temp = stackalloc int[dstArchetype->NumSharedComponents];
                    int indexOfNewSharedComponent = indexInTypeArray - dstArchetype->FirstSharedComponent;
                    BuildSharedComponentIndicesWithAddedComponent(indexOfNewSharedComponent,
                        existingSharedComponentIndex,
                        dstArchetype->NumSharedComponents, sharedComponentValues, temp);

                    sharedComponentValues = temp;
                }

                sourceCountEntityBatchList.Add(srcEntityBatch);
                if (!srcTail)
                {
                    packBlittableEntityBatchList.Add(srcEntityBatch);
                    if (srcChunkManagedData)
                    {
                        packManagedEntityBatchList.Add(srcEntityBatch);
                    }
                }

                var srcOffset = 0;
                while (srcRemainingCount > 0)
                {
                    var dstChunk = archetypeManager.GetChunkWithEmptySlots(dstArchetype, sharedComponentValues);
                    int dstIndexBase;
                    var dstCount =
                        archetypeManager.AllocateIntoChunk(dstChunk, srcRemainingCount, out dstIndexBase);
                    var partialSrcEntityBatch = new EntityBatch
                    {
                        Chunk = srcChunk,
                        Count = dstCount,
                        StartIndex = srcStartIndex + srcOffset
                    };
                    var partialDstEntityBatch = new EntityBatch
                    {
                        Chunk = new ArchetypeChunk {m_Chunk = dstChunk},
                        Count = dstCount,
                        StartIndex = dstIndexBase
                    };

                    sourceBlittableEntityBatchList.Add(partialSrcEntityBatch);
                    destinationBlittableEntityBatchList.Add(partialDstEntityBatch);

                    if (srcChunkManagedData)
                    {
                        sourceManagedEntityBatchList.Add(partialSrcEntityBatch);
                        destinationManagedEntityBatchList.Add(partialDstEntityBatch);
                    }

                    srcOffset += dstCount;
                    srcRemainingCount -= dstCount;
                }
            }

            Profiler.EndSample();
        }

        private void UpdateSourceCountsAndVersions(ArchetypeManager archetypeManager,
            SharedComponentDataManager sharedComponentDataManager, NativeList<EntityBatch> sourceCountEntityBatchList)
        {
            Profiler.BeginSample("Update Source Counts and Versions");
            for (int i = 0; i < sourceCountEntityBatchList.Length; i++)
            {
                var srcEntityBatch = sourceCountEntityBatchList[i];
                var srcChunk = srcEntityBatch.Chunk.m_Chunk;
                var srcCount = srcEntityBatch.Count;
                var srcArchetype = srcChunk->Archetype;
                var srcSharedComponentValues = srcChunk->SharedComponentValues;

                srcArchetype->EntityCount -= srcCount;

                srcChunk->SetAllChangeVersions(GlobalSystemVersion);
                archetypeManager.SetChunkCount(srcChunk, srcChunk->Count - srcCount);
                IncrementComponentOrderVersion(srcArchetype, sharedComponentDataManager, srcSharedComponentValues);
            }

            Profiler.EndSample();
        }

        private void UpdateDestinationVersions(SharedComponentDataManager sharedComponentDataManager,
            NativeList<EntityBatch> sourceBlittableEntityBatchList,
            NativeList<EntityBatch> destinationBlittableEntityBatchList)
        {
            Profiler.BeginSample("Update Destination Versions");
            for (int i = 0; i < destinationBlittableEntityBatchList.Length; i++)
            {
                var srcEntityBatch = sourceBlittableEntityBatchList[i];
                var srcChunk = srcEntityBatch.Chunk.m_Chunk;
                var srcArchetype = srcChunk->Archetype;
                var dstEntityBatch = destinationBlittableEntityBatchList[i];
                var dstChunk = dstEntityBatch.Chunk.m_Chunk;
                var dstSharedComponentValues = dstChunk->SharedComponentValues;
                var dstArchetype = dstChunk->Archetype;

                dstChunk->SetAllChangeVersions(GlobalSystemVersion);
                IncrementComponentOrderVersion(dstArchetype, sharedComponentDataManager, dstSharedComponentValues);
            }

            Profiler.EndSample();
        }

        private static void PackManagedChunkData(ArchetypeManager archetypeManager,
            NativeList<EntityBatch> packManagedEntityBatchList)
        {
            Profiler.BeginSample("Pack Managed Chunk Data");
            // Packing is done in reverse (sorted) so that order is preserved of to-be packed batches in same chunk
            for (int i = packManagedEntityBatchList.Length - 1; i >= 0; i--)
            {
                var srcEntityBatch = packManagedEntityBatchList[i];
                var srcChunk = srcEntityBatch.Chunk.m_Chunk;
                var dstIndexBase = srcEntityBatch.StartIndex;
                var dstCount = srcEntityBatch.Count;
                var srcOffset = dstIndexBase + dstCount;
                var srcCount = srcChunk->Count - srcOffset;

                ChunkDataUtility.CopyManagedObjects(archetypeManager, srcChunk, srcOffset,
                    srcChunk, dstIndexBase, srcCount);
            }

            Profiler.EndSample();
        }

        struct PackBlittableChunkData : IJob
        {
            [ReadOnly] public NativeList<EntityBatch> PackBlittableEntityBatchList;
            [NativeDisableUnsafePtrRestriction] public Archetype** GlobalArchetypeData;
            [NativeDisableUnsafePtrRestriction] public EntityChunkData* GlobalEntityChunkData;

            public void Execute()
            {
                // Packing is done in reverse (sorted) so that order is preserved of to-be packed batches in same chunk
                for (int i = PackBlittableEntityBatchList.Length - 1; i >= 0; i--)
                {
                    var srcEntityBatch = PackBlittableEntityBatchList[i];
                    var srcChunk = srcEntityBatch.Chunk.m_Chunk;
                    var srcArchetype = srcChunk->Archetype;
                    var dstIndexBase = srcEntityBatch.StartIndex;
                    var dstCount = srcEntityBatch.Count;
                    var srcOffset = dstIndexBase + dstCount;
                    var srcCount = srcChunk->Count - srcOffset;
                    var srcEntities = (Entity*) srcChunk->Buffer;

                    ChunkDataUtility.Convert(srcChunk, srcOffset, srcChunk, dstIndexBase, srcCount);
                    for (int entityIndex = 0; entityIndex < srcCount; entityIndex++)
                    {
                        var entity = srcEntities[dstIndexBase + entityIndex];
                        if (entity == Entity.Null)
                            continue;

                        GlobalArchetypeData[entity.Index] = srcArchetype;
                        GlobalEntityChunkData[entity.Index].Chunk = srcChunk;
                        GlobalEntityChunkData[entity.Index].IndexInChunk = dstIndexBase + entityIndex;
                    }
                }
            }
        }

        private JobHandle PackBlittableChunkDataJob(NativeList<EntityBatch> packBlittableEntityBatchList,
            JobHandle inputDeps = new JobHandle())
        {
            var packBlittableChunkDataJob = new PackBlittableChunkData
            {
                PackBlittableEntityBatchList = packBlittableEntityBatchList,
                GlobalArchetypeData = Archetype,
                GlobalEntityChunkData = ChunkData
            };
            var packBlittableChunkDataJobHandle = packBlittableChunkDataJob.Schedule(inputDeps);
            return packBlittableChunkDataJobHandle;
        }

        [BurstCompile]
        struct CopyBlittableChunkData : IJobParallelFor
        {
            [ReadOnly] public NativeList<EntityBatch> DestinationEntityBatchList;
            [ReadOnly] public NativeList<EntityBatch> SourceEntityBatchList;
            [NativeDisableUnsafePtrRestriction] public Archetype** GlobalArchetypeData;
            [NativeDisableUnsafePtrRestriction] public EntityChunkData* GlobalEntityChunkData;

            public void Execute(int i)
            {
                var srcEntityBatch = SourceEntityBatchList[i];
                var dstEntityBatch = DestinationEntityBatchList[i];

                var srcChunk = srcEntityBatch.Chunk.m_Chunk;
                var srcOffset = srcEntityBatch.StartIndex;
                var dstChunk = dstEntityBatch.Chunk.m_Chunk;
                var dstIndexBase = dstEntityBatch.StartIndex;
                var dstCount = dstEntityBatch.Count;
                var srcEntities = (Entity*) srcChunk->Buffer;
                var dstEntities = (Entity*) dstChunk->Buffer;
                var dstArchetype = dstChunk->Archetype;

                ChunkDataUtility.Convert(srcChunk, srcOffset, dstChunk, dstIndexBase, dstCount);
                for (int entityIndex = 0; entityIndex < dstCount; entityIndex++)
                {
                    var entity = dstEntities[dstIndexBase + entityIndex];
                    srcEntities[srcOffset + entityIndex] = Entity.Null;

                    GlobalArchetypeData[entity.Index] = dstArchetype;
                    GlobalEntityChunkData[entity.Index].Chunk = dstChunk;
                    GlobalEntityChunkData[entity.Index].IndexInChunk = dstIndexBase + entityIndex;
                }
            }
        }

        JobHandle CopyBlittableChunkDataJob(NativeList<EntityBatch> sourceEntityBatchList,
            NativeList<EntityBatch> destinationEntityBatchList, JobHandle inputDeps = new JobHandle())
        {
            Profiler.BeginSample("Copy Blittable Chunk Data");
            var copyBlittableChunkDataJob = new CopyBlittableChunkData
            {
                DestinationEntityBatchList = destinationEntityBatchList,
                SourceEntityBatchList = sourceEntityBatchList,
                GlobalArchetypeData = Archetype,
                GlobalEntityChunkData = ChunkData
            };
            var copyBlittableChunkDataJobHandle =
                copyBlittableChunkDataJob.Schedule(sourceEntityBatchList.Length, 64, inputDeps);
            Profiler.EndSample();
            return copyBlittableChunkDataJobHandle;
        }

        void CopyManagedChunkData(NativeList<EntityBatch> sourceEntityBatchList,
            NativeList<EntityBatch> destinationEntityBatchList, ArchetypeManager archetypeManager)
        {
            Profiler.BeginSample("Copy Managed Chunk Data");
            for (int i = 0; i < sourceEntityBatchList.Length; i++)
            {
                var srcEntityBatch = sourceEntityBatchList[i];
                var dstEntityBatch = destinationEntityBatchList[i];

                var srcChunk = srcEntityBatch.Chunk.m_Chunk;
                var srcOffset = srcEntityBatch.StartIndex;
                var dstChunk = dstEntityBatch.Chunk.m_Chunk;
                var dstIndexBase = dstEntityBatch.StartIndex;
                var dstCount = dstEntityBatch.Count;

                if (srcChunk->ManagedArrayIndex >= 0 && dstChunk->ManagedArrayIndex >= 0)
                    ChunkDataUtility.CopyManagedObjects(archetypeManager, srcChunk, srcOffset,
                        dstChunk, dstIndexBase, dstCount);

                if (srcChunk->ManagedArrayIndex >= 0)
                    ChunkDataUtility.ClearManagedObjects(archetypeManager, srcChunk, srcOffset, dstCount);
            }

            Profiler.EndSample();
        }

        static public void BuildResidueSharedComponentIndices(Archetype* srcArchetype, Archetype* dstArchetype,
            SharedComponentValues srcSharedComponentValues, int* dstSharedComponentValues)
        {
            int oldFirstShared = srcArchetype->FirstSharedComponent;
            int newFirstShared = dstArchetype->FirstSharedComponent;
            int newCount = dstArchetype->NumSharedComponents;

            for (int oldIndex = 0, newIndex = 0; newIndex < newCount; ++newIndex, ++oldIndex)
            {
                var t = dstArchetype->Types[newIndex + newFirstShared];
                while (t != srcArchetype->Types[oldIndex + oldFirstShared])
                    ++oldIndex;
                dstSharedComponentValues[newIndex] = srcSharedComponentValues[oldIndex];
            }
        }

        static public void BuildSharedComponentIndicesWithAddedComponents(Archetype* srcArchetype,
            Archetype* dstArchetype, SharedComponentValues srcSharedComponentValues, int* dstSharedComponentValues)
        {
            int oldFirstShared = srcArchetype->FirstSharedComponent;
            int newFirstShared = dstArchetype->FirstSharedComponent;
            int oldCount = srcArchetype->NumSharedComponents;
            int newCount = dstArchetype->NumSharedComponents;

            for (int oldIndex = oldCount - 1, newIndex = newCount - 1; newIndex >= 0; --newIndex)
            {
                // oldIndex might become -1 which is ok since oldFirstShared is always at least 1. The comparison will then always be false
                if (dstArchetype->Types[newIndex + newFirstShared] == srcArchetype->Types[oldIndex + oldFirstShared])
                    dstSharedComponentValues[newIndex] = srcSharedComponentValues[oldIndex--];
                else
                    dstSharedComponentValues[newIndex] = 0;
            }
        }

        static public void BuildSharedComponentIndicesWithAddedComponent(int indexOfNewSharedComponent, int value,
            int newCount, SharedComponentValues srcSharedComponentValues, int* dstSharedComponentValues)
        {
            srcSharedComponentValues.CopyTo(dstSharedComponentValues, 0, indexOfNewSharedComponent);
            dstSharedComponentValues[indexOfNewSharedComponent] = value;
            srcSharedComponentValues.CopyTo(dstSharedComponentValues + indexOfNewSharedComponent + 1,
                indexOfNewSharedComponent, newCount - indexOfNewSharedComponent - 1);
        }

        static public void BuildSharedComponentIndicesWithRemovedComponent(int indexOfRemovedSharedComponent,
            int newCount, SharedComponentValues srcSharedComponentValues, int* dstSharedComponentValues)
        {
            srcSharedComponentValues.CopyTo(dstSharedComponentValues, 0, indexOfRemovedSharedComponent);
            srcSharedComponentValues.CopyTo(dstSharedComponentValues + indexOfRemovedSharedComponent,
                indexOfRemovedSharedComponent + 1, newCount - indexOfRemovedSharedComponent);
        }

        static public bool RequiresBuildingResidueSharedComponentIndices(Archetype* srcArchetype,
            Archetype* dstArchetype)
        {
            return dstArchetype->NumSharedComponents > 0 &&
                   dstArchetype->NumSharedComponents != srcArchetype->NumSharedComponents;
        }

        public void MoveEntityToChunk(ArchetypeManager typeMan, Entity entity, Chunk* newChunk, int newChunkIndex)
        {
            var oldChunk = ChunkData[entity.Index].Chunk;
            Assert.IsTrue(oldChunk->Archetype == newChunk->Archetype);

            var oldChunkIndex = ChunkData[entity.Index].IndexInChunk;

            ChunkDataUtility.Copy(oldChunk, oldChunkIndex, newChunk, newChunkIndex, 1);

            if (oldChunk->ManagedArrayIndex >= 0)
                ChunkDataUtility.CopyManagedObjects(typeMan, oldChunk, oldChunkIndex, newChunk, newChunkIndex, 1);

            ChunkData[entity.Index].Chunk = newChunk;
            ChunkData[entity.Index].IndexInChunk = newChunkIndex;

            var lastIndex = oldChunk->Count - 1;
            // No need to replace with ourselves
            if (lastIndex != oldChunkIndex)
            {
                var lastEntity = (Entity*) ChunkDataUtility.GetComponentDataRO(oldChunk, lastIndex, 0);
                ChunkData[lastEntity->Index].IndexInChunk = oldChunkIndex;

                ChunkDataUtility.Copy(oldChunk, lastIndex, oldChunk, oldChunkIndex, 1);
                if (oldChunk->ManagedArrayIndex >= 0)
                    ChunkDataUtility.CopyManagedObjects(typeMan, oldChunk, lastIndex, oldChunk, oldChunkIndex, 1);
            }

            if (oldChunk->ManagedArrayIndex >= 0)
                ChunkDataUtility.ClearManagedObjects(typeMan, oldChunk, lastIndex, 1);

            newChunk->SetAllChangeVersions(GlobalSystemVersion);
            oldChunk->SetAllChangeVersions(GlobalSystemVersion);

            newChunk->Archetype->EntityCount--;
            typeMan.SetChunkCount(oldChunk, oldChunk->Count - 1);
        }

        public void SetSharedComponentDataIndex(ArchetypeManager archetypeManager,
            SharedComponentDataManager sharedComponentDataManager, Entity entity, int typeIndex,
            int newSharedComponentDataIndex)
        {
            var archetype = Archetype[entity.Index];
            var indexInTypeArray = ChunkDataUtility.GetIndexInTypeArray(archetype, typeIndex);
            var srcChunk = GetComponentChunk(entity);
            var srcSharedComponentValueArray = srcChunk->SharedComponentValues;
            var sharedComponentOffset = indexInTypeArray - archetype->FirstSharedComponent;
            var oldSharedComponentDataIndex = srcSharedComponentValueArray[sharedComponentOffset];

            if (newSharedComponentDataIndex == oldSharedComponentDataIndex)
                return;

            var sharedComponentIndices = stackalloc int[archetype->NumSharedComponents];

            srcSharedComponentValueArray.CopyTo(sharedComponentIndices, 0, archetype->NumSharedComponents);

            sharedComponentIndices[sharedComponentOffset] = newSharedComponentDataIndex;

            var newChunk = archetypeManager.GetChunkWithEmptySlots(archetype, sharedComponentIndices);
            var newChunkIndex = archetypeManager.AllocateIntoChunk(newChunk);

            IncrementComponentOrderVersion(archetype, srcChunk, sharedComponentDataManager);

            MoveEntityToChunk(archetypeManager, entity, newChunk, newChunkIndex);
        }

        public int GetSharedComponentDataIndex(Entity entity, int typeIndex)
        {
            var archetype = Archetype[entity.Index];
            var indexInTypeArray = ChunkDataUtility.GetIndexInTypeArray(archetype, typeIndex);
            var chunk = ChunkData[entity.Index].Chunk;
            var sharedComponentValueArray = chunk->SharedComponentValues;
            var sharedComponentOffset = indexInTypeArray - archetype->FirstSharedComponent;
            return sharedComponentValueArray[sharedComponentOffset];
        }

        public void RemoveComponent(Entity entity, ComponentType type,
            ArchetypeManager archetypeManager, SharedComponentDataManager sharedComponentDataManager,
            EntityGroupManager groupManager)
        {
            if (!HasComponent(entity, type))
                return;

            var archetype = Archetype[entity.Index];
            var chunk = ChunkData[entity.Index].Chunk;

            if (chunk->Locked || chunk->LockedEntityOrder)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new InvalidOperationException(
                    "Cannot remove components in locked Chunks. Unlock Chunk first.");
#else
                    return;
#endif
            }

            int indexInOldTypeArray = -1;
            var newType =
                archetypeManager.GetArchetypeWithRemovedComponentType(archetype, type, groupManager,
                    &indexInOldTypeArray);

            var sharedComponentValues = chunk->SharedComponentValues;

            if (type.IsSharedComponent)
            {
                int* temp = stackalloc int[newType->NumSharedComponents];
                int indexOfRemovedSharedComponent = indexInOldTypeArray - archetype->FirstSharedComponent;
                EntityData.BuildSharedComponentIndicesWithRemovedComponent(indexOfRemovedSharedComponent,
                    newType->NumSharedComponents, sharedComponentValues, temp);
                sharedComponentValues = temp;
            }

            SetArchetype(archetypeManager, entity, newType, sharedComponentValues,
                sharedComponentDataManager);

            // Cleanup residue component
            if (newType->SystemStateCleanupComplete)
                DestroyEntities(&entity, 1, archetypeManager, sharedComponentDataManager);
        }


        public void DestroyEntities(Entity* entities, int count,
            ArchetypeManager archetypeManager, SharedComponentDataManager sharedComponentDataManager)
        {
            var entityIndex = 0;

            var additionalDestroyList = new UnsafeList();
            int minDestroyStride = int.MaxValue;
            int maxDestroyStride = 0;

            while (entityIndex != count)
            {
                int indexInChunk, batchCount;
                var chunk = EntityChunkBatch(entities + entityIndex, count - entityIndex, out indexInChunk,
                    out batchCount);

                if (chunk == null)
                {
                    entityIndex += batchCount;
                    continue;
                }

                AddToDestroyList(chunk, indexInChunk, batchCount, count, ref additionalDestroyList,
                    ref minDestroyStride, ref maxDestroyStride);

                DestroyBatch(entities + entityIndex, archetypeManager, sharedComponentDataManager, chunk,
                    indexInChunk, batchCount);

                entityIndex += batchCount;
            }

            // Apply additional destroys from any LinkedEntityGroup
            if (additionalDestroyList.m_pointer != null)
            {
                var additionalDestroyPtr = (Entity*) additionalDestroyList.m_pointer;
                // Optimal for destruction speed is if entities with same archetype/chunk are followed one after another.
                // So we lay out the to be destroyed objects assuming that the destroyed entities are "similar":
                // Reorder destruction by index in entityGroupArray...

                //@TODO: This is a very specialized fastpath that is likely only going to give benefits in the stress test.
                ///      Figure out how to make this more general purpose.
                if (minDestroyStride == maxDestroyStride)
                {
                    var reordered = (Entity*) UnsafeUtility.Malloc(additionalDestroyList.m_size * sizeof(Entity), 16,
                        Allocator.TempJob);
                    int batchCount = additionalDestroyList.m_size / minDestroyStride;
                    for (int i = 0; i != batchCount; i++)
                    {
                        for (int j = 0; j != minDestroyStride; j++)
                            reordered[j * batchCount + i] = additionalDestroyPtr[i * minDestroyStride + j];
                    }

                    DestroyEntities(reordered, additionalDestroyList.m_size, archetypeManager,
                        sharedComponentDataManager);
                    UnsafeUtility.Free(reordered, Allocator.TempJob);
                }
                else
                {
                    DestroyEntities(additionalDestroyPtr, additionalDestroyList.m_size, archetypeManager,
                        sharedComponentDataManager);
                }

                UnsafeUtility.Free(additionalDestroyPtr, Allocator.TempJob);
            }
        }

        void AddToDestroyList(Chunk* chunk, int indexInChunk, int batchCount, int inputDestroyCount,
            ref UnsafeList entitiesList, ref int minBufferLength, ref int maxBufferLength)
        {
            var linkedGroupType = TypeManager.GetTypeIndex<LinkedEntityGroup>();
            int indexInArchetype = ChunkDataUtility.GetIndexInTypeArray(chunk->Archetype, linkedGroupType);
            if (indexInArchetype != -1)
            {
                var baseHeader = ChunkDataUtility.GetComponentDataWithTypeRO(chunk, indexInChunk, linkedGroupType);
                var stride = chunk->Archetype->SizeOfs[indexInArchetype];
                for (int i = 0; i != batchCount; i++)
                {
                    var header = (BufferHeader*) (baseHeader + stride * i);

                    var entityGroupCount = header->Length - 1;
                    if (entityGroupCount == 0)
                        continue;

                    var entityGroupArray = (Entity*) BufferHeader.GetElementPointer(header) + 1;

                    if (entitiesList.m_capacity == 0)
                        entitiesList.SetCapacity<Entity>(inputDestroyCount * entityGroupCount, Allocator.TempJob);
                    entitiesList.AddRange<Entity>(entityGroupArray, entityGroupCount, Allocator.TempJob);

                    minBufferLength = math.min(minBufferLength, entityGroupCount);
                    maxBufferLength = math.max(maxBufferLength, entityGroupCount);
                }
            }
        }

        void DestroyBatch(Entity* entities, ArchetypeManager archetypeManager,
            SharedComponentDataManager sharedComponentDataManager, Chunk* chunk, int indexInChunk, int batchCount)
        {
            var archetype = chunk->Archetype;
            if (!archetype->SystemStateCleanupNeeded)
            {
                DeallocateDataEntitiesInChunk(entities, chunk, indexInChunk, batchCount);
                IncrementComponentOrderVersion(archetype, chunk, sharedComponentDataManager);

                if (chunk->ManagedArrayIndex >= 0)
                {
                    // We can just chop-off the end, no need to copy anything
                    if (chunk->Count != indexInChunk + batchCount)
                        ChunkDataUtility.CopyManagedObjects(archetypeManager, chunk, chunk->Count - batchCount, chunk,
                            indexInChunk, batchCount);

                    ChunkDataUtility.ClearManagedObjects(archetypeManager, chunk, chunk->Count - batchCount,
                        batchCount);
                }

                chunk->Archetype->EntityCount -= batchCount;
                archetypeManager.SetChunkCount(chunk, chunk->Count - batchCount);
            }
            else
            {
                var newType = archetype->SystemStateResidueArchetype;

                var sharedComponentValues = chunk->SharedComponentValues;

                if (RequiresBuildingResidueSharedComponentIndices(archetype, newType))
                {
                    var tempAlloc = stackalloc int[newType->NumSharedComponents];
                    BuildResidueSharedComponentIndices(archetype, newType, sharedComponentValues, tempAlloc);
                    sharedComponentValues = tempAlloc;
                }

                // See: https://github.com/Unity-Technologies/dots/issues/1387
                // For Locked Order Chunks specfically, need to make sure that structural changes are always done per-chunk.
                // If trying to muutate structure in a way that is not per chunk, will hit an exception in the else clause anyway.
                // This ultimately needs to be replaced by entity batch interface.

                if (batchCount == chunk->Count)
                {
                    IncrementComponentOrderVersion(archetype, chunk, sharedComponentDataManager);
                    SetArchetype(archetypeManager, chunk, newType, sharedComponentValues,
                        sharedComponentDataManager);
                }
                else
                {
                    for (var i = 0; i < batchCount; i++)
                    {
                        var entity = entities[i];
                        IncrementComponentOrderVersion(archetype, GetComponentChunk(entity),
                            sharedComponentDataManager);
                        SetArchetype(archetypeManager, entity, newType, sharedComponentValues,
                            sharedComponentDataManager);
                    }
                }
            }
        }

        void DeallocateDataEntitiesInChunk(Entity* entities, Chunk* chunk,
            int indexInChunk, int batchCount)
        {
            DeallocateBuffers(entities, chunk, batchCount);

            var freeIndex = FreeIndex;

            for (var i = batchCount - 1; i >= 0; --i)
            {
                var entityIndex = entities[i].Index;

                ChunkData[entityIndex].Chunk = null;
                Version[entityIndex]++;
                ChunkData[entityIndex].IndexInChunk = freeIndex;
#if UNITY_EDITOR
                Name[entityIndex] = new NumberedWords();
#endif
                freeIndex = entityIndex;
            }

            FreeIndex = freeIndex;

            // Compute the number of things that need to moved and patched.
            int patchCount = Math.Min(batchCount, chunk->Count - indexInChunk - batchCount);

            if (0 == patchCount)
                return;

            // updates indexInChunk to point to where the components will be moved to
            //Assert.IsTrue(chunk->archetype->sizeOfs[0] == sizeof(Entity) && chunk->archetype->offsets[0] == 0);
            var movedEntities = (Entity*) chunk->Buffer + (chunk->Count - patchCount);
            for (var i = 0; i != patchCount; i++)
                ChunkData[movedEntities[i].Index].IndexInChunk = indexInChunk + i;

            // Move component data from the end to where we deleted components
            ChunkDataUtility.Copy(chunk, chunk->Count - patchCount, chunk, indexInChunk, patchCount);
        }

        public void DeallocateBuffers(Entity* entities, Chunk* chunk, int batchCount)
        {
            var archetype = chunk->Archetype;

            for (var ti = 0; ti < archetype->TypesCount; ++ti)
            {
                var type = archetype->Types[ti];

                if (!type.IsBuffer)
                    continue;

                var basePtr = chunk->Buffer + archetype->Offsets[ti];
                var stride = archetype->SizeOfs[ti];

                for (int i = 0; i < batchCount; ++i)
                {
                    Entity e = entities[i];
                    int indexInChunk = ChunkData[e.Index].IndexInChunk;
                    byte* bufferPtr = basePtr + stride * indexInChunk;
                    BufferHeader.Destroy((BufferHeader*) bufferPtr);
                }
            }
        }

        Chunk* EntityChunkBatch(Entity* entities, int count, out int indexInChunk, out int batchCount)
        {
            // This is optimized for the case where the array of entities are allocated contigously in the chunk
            // Thus the compacting of other elements can be batched

            // Calculate baseEntityIndex & chunk
            var baseEntityIndex = entities[0].Index;

            var versions = Version;
            var chunkData = ChunkData;

            var chunk = versions[baseEntityIndex] == entities[0].Version
                ? ChunkData[baseEntityIndex].Chunk
                : null;
            indexInChunk = chunkData[baseEntityIndex].IndexInChunk;
            batchCount = 0;

            while (batchCount < count)
            {
                var entityIndex = entities[batchCount].Index;
                var curChunk = chunkData[entityIndex].Chunk;
                var curIndexInChunk = chunkData[entityIndex].IndexInChunk;

                if (versions[entityIndex] == entities[batchCount].Version)
                {
                    if (curChunk != chunk || curIndexInChunk != indexInChunk + batchCount)
                        break;
                }
                else
                {
                    if (chunk != null)
                        break;
                }

                batchCount++;
            }

            return chunk;
        }

        public void RemoveComponent(NativeArray<ArchetypeChunk> chunkArray, ComponentType type,
            ArchetypeManager archetypeManager, EntityGroupManager groupManager,
            SharedComponentDataManager sharedComponentDataManager)
        {
            var chunks = (ArchetypeChunk*) chunkArray.GetUnsafeReadOnlyPtr();
            if (type.IsZeroSized)
            {
                Archetype* prevOldArchetype = null;
                Archetype* newArchetype = null;
                int indexInOldTypeArray = 0;
                for (int i = 0; i < chunkArray.Length; ++i)
                {
                    var chunk = chunks[i].m_Chunk;
                    var oldArchetype = chunk->Archetype;
                    if (oldArchetype != prevOldArchetype)
                    {
                        if (ChunkDataUtility.GetIndexInTypeArray(oldArchetype, type.TypeIndex) != -1)
                            newArchetype = archetypeManager.GetArchetypeWithRemovedComponentType(oldArchetype, type,
                                groupManager, &indexInOldTypeArray);
                        else
                            newArchetype = null;
                        prevOldArchetype = oldArchetype;
                    }

                    if (newArchetype == null)
                        continue;

                    if (newArchetype->SystemStateCleanupComplete)
                    {
                        DeleteChunkAfterSystemStateCleanupIsComplete(chunk, archetypeManager, groupManager,
                            sharedComponentDataManager);
                        continue;
                    }

                    var sharedComponentValues = chunk->SharedComponentValues;
                    if (type.IsSharedComponent)
                    {
                        int* temp = stackalloc int[newArchetype->NumSharedComponents];
                        int indexOfRemovedSharedComponent = indexInOldTypeArray - oldArchetype->FirstSharedComponent;
                        var sharedComponentDataIndex = chunk->GetSharedComponentValue(indexOfRemovedSharedComponent);
                        sharedComponentDataManager.RemoveReference(sharedComponentDataIndex);
                        EntityData.BuildSharedComponentIndicesWithRemovedComponent(indexOfRemovedSharedComponent,
                            newArchetype->NumSharedComponents, sharedComponentValues, temp);
                        sharedComponentValues = temp;
                    }

                    MoveChunkToNewArchetype(chunk, newArchetype, GlobalSystemVersion, archetypeManager,
                        sharedComponentValues, sharedComponentDataManager);
                }
            }
            else
            {
                Archetype* prevOldArchetype = null;
                Archetype* newArchetype = null;
                for (int i = 0; i < chunkArray.Length; ++i)
                {
                    var chunk = chunks[i].m_Chunk;
                    var oldArchetype = chunk->Archetype;
                    if (oldArchetype != prevOldArchetype)
                    {
                        if (ChunkDataUtility.GetIndexInTypeArray(oldArchetype, type.TypeIndex) != -1)
                            newArchetype =
                                archetypeManager.GetArchetypeWithRemovedComponentType(oldArchetype, type, groupManager);
                        else
                            newArchetype = null;
                        prevOldArchetype = oldArchetype;
                    }

                    if (newArchetype != null)
                        if (newArchetype->SystemStateCleanupComplete)
                        {
                            DeleteChunkAfterSystemStateCleanupIsComplete(chunk, archetypeManager, groupManager,
                                sharedComponentDataManager);
                        }
                        else
                        {
                            SetArchetype(archetypeManager, chunk, newArchetype, chunk->SharedComponentValues,
                                sharedComponentDataManager);
                        }
                }
            }
        }

        public void DeleteChunkAfterSystemStateCleanupIsComplete(Chunk* chunk, ArchetypeManager archetypeManager,
            EntityGroupManager groupManager, SharedComponentDataManager sharedComponentDataManager)
        {
            var entityCount = chunk->Count;
            DeallocateDataEntitiesInChunk((Entity*) chunk->Buffer, chunk, 0, chunk->Count);
            IncrementComponentOrderVersion(chunk->Archetype, chunk, sharedComponentDataManager);
            chunk->Archetype->EntityCount -= entityCount;
            archetypeManager.SetChunkCount(chunk, 0);
        }

        public void DestroyEntities(NativeArray<ArchetypeChunk> chunkArray,
            ArchetypeManager archetypeManager, SharedComponentDataManager sharedComponentDataManager)
        {
            var chunks = (ArchetypeChunk*) chunkArray.GetUnsafeReadOnlyPtr();
            for (int i = 0; i != chunkArray.Length; i++)
            {
                var chunk = chunks[i].m_Chunk;
                DestroyBatch((Entity*) chunk->Buffer, archetypeManager, sharedComponentDataManager, chunk, 0,
                    chunk->Count);
            }
        }
    }
}
