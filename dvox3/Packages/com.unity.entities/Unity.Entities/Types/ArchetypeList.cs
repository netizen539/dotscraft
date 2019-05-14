using System.Diagnostics;

namespace Unity.Entities
{
    [DebuggerTypeProxy(typeof(ArchetypeListDebugView))]
    internal unsafe struct ArchetypeList
    {
        public Archetype** p;
        public int Count;
        public int Capacity;
    }
}
