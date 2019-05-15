using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

public class ChunkGeneratorProxy : MonoBehaviour, IConvertGameObjectToEntity
{
    public void Convert(Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
    {
        //var data = new RotationSpeed_ForEach { RadiansPerSecond = math.radians(DegreesPerSecond) };
        dstManager.AddComponent(entity, typeof(ChunkGenerator));
    }
}
