using System.Diagnostics;

namespace Unity.Entities
{
    [DebuggerTypeProxy(typeof(ChunkListDebugView))]
    internal unsafe struct ChunkList
    {
        public Chunk** p;
        public int Count;
        public int Capacity;
    }
}
