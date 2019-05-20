using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public struct ChunkPosition : IComponentData
{
    public float3 pos;
}

[InternalBufferCapacity(128)]
public struct ChunkChildBlock : IBufferElementData
{
    public Entity Value;
}