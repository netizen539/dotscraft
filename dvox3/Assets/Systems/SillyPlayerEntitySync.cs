using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.XR.WSA;

public class SillyPlayerEntitySync : MonoBehaviour
{
  //  BeginInitializationEntityCommandBufferSystem ecbSystem;
    Entity entity;
    
    // Start is called before the first frame update
    void Start()
    {
        

    }

    // Update is called once per frame
    void Update()
    {
        /*
        GameObjectEntity goe = GetComponent<GameObjectEntity>();
        if (goe == null)
            return;

        entity = goe.Entity;

      //  ecbSystem = World.Active.GetOrCreateSystem<BeginInitializationEntityCommandBufferSystem>();
        float3 pos = new float3(transform.position.x, transform.position.y, transform.position.z);
        var translation = new Translation() { Value = pos };
        goe.EntityManager.SetComponentData(entity, translation);
        */
    }
}
