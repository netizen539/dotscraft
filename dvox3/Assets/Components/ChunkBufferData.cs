using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

[InternalBufferCapacity(4096)]
public struct ChunkBufferData : IBufferElementData
{
    public short blockId;
}

public struct ChunkBufferBlockToCreateData : IBufferElementData
{
    public short blockId;
    public float3 position;
}

