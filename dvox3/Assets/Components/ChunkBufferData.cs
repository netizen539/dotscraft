using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

[InternalBufferCapacity(4096)]
public struct ChunkBufferData : IBufferElementData
{
    public short blockId;
}
