using System;
using System.Collections.Generic;

namespace NetScannerDesktop.Services;

/// <summary>
/// Comparers for the sortable result tables. Blank cells always sort
/// last, whichever way the column is sorted: a host without a name is
/// never the first thing you see when sorting by name.
/// </summary>
public static class TableSort
{
    /// <summary>Order by a text column (case-insensitive); ties fall back to <paramref name="then"/>.</summary>
    public static IComparer<T> ByText<T>(Func<T, string?> key, bool descending, IComparer<T>? then = null) =>
        Comparer<T>.Create((a, b) =>
        {
            string? x = key(a), y = key(b);
            bool xBlank = string.IsNullOrEmpty(x), yBlank = string.IsNullOrEmpty(y);
            if (xBlank != yBlank)
            {
                return xBlank ? 1 : -1;
            }

            int c = xBlank ? 0 : StringComparer.CurrentCultureIgnoreCase.Compare(x, y);
            return Directed(c, descending, a, b, then);
        });

    /// <summary>Order by a value column; null sorts last. Ties fall back to <paramref name="then"/>.</summary>
    public static IComparer<T> ByValue<T, TKey>(Func<T, TKey?> key, bool descending, IComparer<T>? then = null)
        where TKey : struct, IComparable<TKey> =>
        Comparer<T>.Create((a, b) =>
        {
            TKey? x = key(a), y = key(b);
            if (x.HasValue != y.HasValue)
            {
                return x.HasValue ? -1 : 1;
            }

            int c = x.HasValue ? x.Value.CompareTo(y!.Value) : 0;
            return Directed(c, descending, a, b, then);
        });

    private static int Directed<T>(int c, bool descending, T a, T b, IComparer<T>? then)
    {
        if (c != 0)
        {
            return descending ? -c : c;
        }

        return then?.Compare(a, b) ?? 0;
    }

    /// <summary>Header arrow for a column: up when sorted ascending, down when descending, blank otherwise.</summary>
    public static string Glyph(string column, string sortColumn, bool descending) =>
        column != sortColumn ? string.Empty : descending ? "" : "";
}
