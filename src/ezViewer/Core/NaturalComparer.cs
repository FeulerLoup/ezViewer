using System;
using System.Collections.Generic;
using EzViewer.Interop;

namespace EzViewer.Core;

/// <summary>与资源管理器一致的“自然排序”（第2话 排在 第10话 之前）。</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        int r = NativeMethods.StrCmpLogicalW(x, y);
        return r != 0 ? r : string.CompareOrdinal(x, y);
    }
}
