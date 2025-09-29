using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Atlas
{
    /// <summary>
    ///     Struct UiElement.
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
    ///     Struct AtlasNode.
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

        public readonly bool IsAttempted => Flags.HasFlag(AtlasNodeState.Attempted);
        public readonly bool IsPristine => Flags.HasFlag(AtlasNodeState.Pristine);
        public readonly bool IsWatchTower => Flags.HasFlag(AtlasNodeState.WatchTower);
        public readonly bool IsCompleted => IsAttempted && IsPristine;
        public readonly bool IsFailedAttempt => IsAttempted && !IsPristine;

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
        None = 0,
        Attempted = 1 << 0,
        Pristine = 1 << 1,
        WatchTower = 1 << 2
    }
}