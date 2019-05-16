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

public class BlockVisibleJobSystem
{
    BeginPresentationEntityCommandBufferSystem m_EndFrameBarrier;

    protected void OnCreate()
    {
        //m_EndFrameBarrier = World.GetOrCreateSystem<BeginPresentationEntityCommandBufferSystem>();
    }

    struct HideUnseenBlocksJob : IJobForEachWithEntity<Translation, BlockTagComponent>
    {
      //  [ReadOnly]
        public EntityCommandBuffer.Concurrent commandBuffer;
        
        public void Execute(Entity entity, int index, [ReadOnly] ref Translation c0, [ReadOnly]ref BlockTagComponent c1)
        {
            int xFloor = (int)c0.Value.x;
            int zFloor = (int)c0.Value.z;
            if ((xFloor % 2) == 0 && (zFloor % 2) == 0)
            {
             //   Debug.Log("RJ idx:"+index+"entity:"+entity.Index+"xFloor:"+xFloor+" zFloor:"+zFloor);
               // commandBuffer.AddComponent(index, entity, new Disabled());
            }
        }
    }
    
   // protected override JobHandle OnUpdate(JobHandle inputDeps)
   // {
       /* 
        var desc = new EntityQueryDesc()
        {
            All = new ComponentType[] { typeof(BlockInvisible) },
            Options = EntityQueryOptions.IncludeDisabled
        };
        var query = GetEntityQuery(desc);
        
        var job = new HideUnseenBlocksJob();
        job.commandBuffer = m_EndFrameBarrier.CreateCommandBuffer().ToConcurrent();
        var handle = job.ScheduleSingle(query, inputDeps);
        return handle;
        */
                
        
        
        /*
        var job = new HideUnseenBlocksJob();
        job.commandBuffer = m_EndFrameBarrier.CreateCommandBuffer().ToConcurrent();
        var handle = job.Schedule(this, inputDeps);
        m_EndFrameBarrier.AddJobHandleForProducer(handle);
        return handle;
        */
        
   // }
}
