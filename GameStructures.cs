using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Atlas
{
    // =========================
    // Core UI/Atlas node structs
    // =========================

    /// <summary>
    ///     Struct UiElement (memory-backed).
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct UiElement
    {
        [FieldOffset(0x038)] public IntPtr FirstChild;
        [FieldOffset(0x040)] public IntPtr LastChild;
        [FieldOffset(0x1B8)] public uint Flags;

        private const int ChildPtrStride = 0x8;

        public readonly int Length => (int)((LastChild.ToInt64() - FirstChild.ToInt64()) / ChildPtrStride);
        public readonly bool IsVisible => (Flags & 0x800) != 0;

        public readonly UiElement GetChild(int index)
        {
            var address = Atlas.Read<IntPtr>(FirstChild + (index * ChildPtrStride));
            return Atlas.Read<UiElement>(address);
        }

        public readonly IntPtr GetChildAddress(int index)
        {
            return Atlas.Read<IntPtr>(FirstChild + (index * ChildPtrStride));
        }

        public readonly AtlasNode GetAtlasNode(int index)
        {
            var address = Atlas.Read<IntPtr>(FirstChild + (index * ChildPtrStride));
            return Atlas.Read<AtlasNode>(address);
        }
    }

    /// <summary>
    ///     Struct AtlasNode (memory-backed).
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct AtlasNode
    {
        [FieldOffset(0x110)] public Vector2 RelativePosition;
        [FieldOffset(0x12C)] public float Zoom;
        [FieldOffset(0x270)] public IntPtr NodeNameAddress;
        [FieldOffset(0x290)] public AtlasNodeState Flags;

        public readonly float Scale => Zoom / 1.5f;
        public readonly Vector2 Position => RelativePosition * Scale;

        public readonly bool IsAccessible => Flags.HasFlag(AtlasNodeState.AccessibleNow);
        public readonly bool IsNotAccessible => !IsAccessible;
        public readonly bool IsCompleted => Flags.HasFlag(AtlasNodeState.CompletedBase);
        public static bool IsFailedAttempt => false;

        public readonly string MapName
        {
            get
            {
                var buffer = Atlas.Read<IntPtr>(NodeNameAddress + 0x8);
                return Atlas.ReadWideString(buffer, 64);
            }
        }
    }

    /// <summary>
    ///     Enum AtlasNodeState.
    /// </summary>
    [Flags]
    public enum AtlasNodeState : ushort
    {
        None = 0x0000,
        AccessibleNow = 0x0001,
        CompletedBase = 0x0002,
    }

    // =======================================
    // Atlas graph reading (grid + connections)
    // =======================================

    /// <summary>
    /// Minimal std::vector layout (MSVC): [begin, end, capacity_end].
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct StdVector
    {
        public IntPtr First; // pointer to first element
        public IntPtr Last;  // pointer one past last element
        public IntPtr Cap;   // pointer one past allocated block

        public int Count<T>() where T : unmanaged
        {
            var sz = Marshal.SizeOf<T>();
            long bytes = Last.ToInt64() - First.ToInt64();
            if (bytes <= 0 || sz <= 0) return 0;
            return (int)(bytes / sz);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct StdTuple2D<T> where T : unmanaged
    {
        public T X;
        public T Y;
    }

    /// <summary>
    /// Placeholder for the UiElement's base region so that AtlasMapOffsets lines up at 0x510.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 0x510)]
    public struct UiElementBaseOffset { }

    /// <summary>
    /// Root atlas struct: contains vectors for nodes and node connections.
    /// Read this at the Atlas panel UiElement's address.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct AtlasMapOffsets
    {
        [FieldOffset(0x000)] public UiElementBaseOffset UiElementBase;
        [FieldOffset(0x510)] public StdVector AtlasNodes;           // AtlasNodeEntry[]
        [FieldOffset(0x528)] public StdVector AtlasNodeConnections; // AtlasNodeConnections[]
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct AtlasNodeEntry
    {
        public StdTuple2D<int> GridPosition;
        public IntPtr UiElementPtr;
        public IntPtr UnknownPtr;
        private long _pad_0x18;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct AtlasNodeConnections
    {
        public StdTuple2D<int> GridPosition;
        public StdTuple2D<int> Connection1;
        public StdTuple2D<int> Connection2;
        public StdTuple2D<int> Connection3;
        public StdTuple2D<int> Connection4;
    }

    /// <summary>
    /// Helpers to read atlas nodes and build an adjacency graph using true grid connections.
    /// </summary>
    public static class AtlasGraphReader
    {
        public static List<T> ReadStdVector<T>(StdVector vec) where T : unmanaged
        {
            var list = new List<T>();
            int count = vec.Count<T>();
            if (count <= 0 || vec.First == IntPtr.Zero) return list;

            int sz = Marshal.SizeOf<T>();
            long baseAddr = vec.First.ToInt64();
            for (int i = 0; i < count; i++)
            {
                var addr = new IntPtr(baseAddr + i * sz);
                list.Add(Atlas.Read<T>(addr));
            }
            return list;
        }

        public static bool TryReadAtlasGraph(
            IntPtr atlasUiAddress,
            out List<AtlasNodeEntry> nodes,
            out List<AtlasNodeConnections> connections)
        {
            nodes = null;
            connections = null;
            if (atlasUiAddress == IntPtr.Zero) return false;

            try
            {
                var offsets = Atlas.Read<AtlasMapOffsets>(atlasUiAddress);
                var nodeList = ReadStdVector<AtlasNodeEntry>(offsets.AtlasNodes);
                var connList = ReadStdVector<AtlasNodeConnections>(offsets.AtlasNodeConnections);

                if (nodeList == null || connList == null || nodeList.Count == 0)
                    return false;

                nodes = nodeList;
                connections = connList;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static List<int>[] BuildGraphFromConnections(
            int atlasCount,
            Func<int, IntPtr> getChildAddressByIndex, // atlasUi.GetChildAddress(i)
            System.Collections.Generic.List<AtlasNodeEntry> nodeEntries,
            System.Collections.Generic.List<AtlasNodeConnections> connEntries)
        {
            // Map UiElement* -> atlas child index
            var uiPtrToIndex = new System.Collections.Generic.Dictionary<IntPtr, int>();
            for (int i = 0; i < atlasCount; i++)
            {
                var addr = getChildAddressByIndex(i);
                if (addr != IntPtr.Zero && !uiPtrToIndex.ContainsKey(addr))
                    uiPtrToIndex[addr] = i;
            }

            // Map grid (x,y) -> atlas child index
            var gridToIndex = new System.Collections.Generic.Dictionary<(int x, int y), int>();
            foreach (var ne in nodeEntries)
            {
                if (ne.UiElementPtr != IntPtr.Zero && uiPtrToIndex.TryGetValue(ne.UiElementPtr, out var idx))
                {
                    var key = (ne.GridPosition.X, ne.GridPosition.Y);
                    gridToIndex[key] = idx;
                }
            }

            var graph = new System.Collections.Generic.List<int>[atlasCount];
            for (int i = 0; i < atlasCount; i++) graph[i] = new System.Collections.Generic.List<int>();

            void AddEdge((int x, int y) aGrid, (int x, int y) bGrid)
            {
                if (!gridToIndex.TryGetValue(aGrid, out var a)) return;
                if (!gridToIndex.TryGetValue(bGrid, out var b)) return;
                if (a < 0 || b < 0 || a >= atlasCount || b >= atlasCount) return;
                if (a == b) return;
                graph[a].Add(b);
                graph[b].Add(a);
            }

            foreach (var conn in connEntries)
            {
                var self = (conn.GridPosition.X, conn.GridPosition.Y);
                var c1 = (conn.Connection1.X, conn.Connection1.Y);
                var c2 = (conn.Connection2.X, conn.Connection2.Y);
                var c3 = (conn.Connection3.X, conn.Connection3.Y);
                var c4 = (conn.Connection4.X, conn.Connection4.Y);

                AddEdge(self, c1);
                AddEdge(self, c2);
                AddEdge(self, c3);
                AddEdge(self, c4);
            }

            // De-dup adjacency
            for (int i = 0; i < atlasCount; i++)
            {
                if (graph[i].Count > 1)
                {
                    var set = new System.Collections.Generic.HashSet<int>(graph[i]);
                    graph[i] = new System.Collections.Generic.List<int>(set);
                }
            }

            return graph;
        }
    }
}
