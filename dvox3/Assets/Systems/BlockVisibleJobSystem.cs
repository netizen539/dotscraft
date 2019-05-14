using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Jobs;
using Unity.Rendering;
using Unity.Transforms;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public class BlockVisibleJobSystem : JobComponentSystem
{
    EndSimulationEntityCommandBufferSystem m_EndFrameBarrier;

    protected override void OnCreate()
    {
        m_EndFrameBarrier = World.GetOrCreateSystem<EndSimulationEntityCommandBufferSystem>();
    }

    struct HideUnseenBlocksJob : IJobForEachWithEntity<Translation, BlockTagComponent>
    {
        [ReadOnly]
        public EntityCommandBuffer commandBuffer;
        
        public void Execute(Entity entity, int index, [ReadOnly] ref Translation c0, [ReadOnly]ref BlockTagComponent c1)
        {
            int xFloor = (int)c0.Value.x;
            int zFloor = (int)c0.Value.z;
            if ((xFloor % 2) == 0 && (zFloor % 2) == 0)
            {
                Debug.Log("RJ idx:"+index+"entity:"+entity.Index+"xFloor:"+xFloor+" zFloor:"+zFloor);
                commandBuffer.AddComponent(entity, new Disabled());
            }
        }
    }
    
    protected override JobHandle OnUpdate(JobHandle inputDeps)
    {
        var job = new HideUnseenBlocksJob();
//        var ecbSystem = World.GetOrCreateSystem<EndSimulationEntityCommandBufferSystem>();
        job.commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();
        var handle = job.Schedule(this, inputDeps);
        m_EndFrameBarrier.AddJobHandleForProducer(handle);
        return handle;
    }
}
