using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace NetScannerDesktop.Services;

/// <summary>
/// A filtered, sorted, observable view over a growing result list.
/// <para>
/// Streamed results are inserted at their sorted position (binary search),
/// so the bound ListView sees one insert per result. The previous approach
/// rebuilt and re-assigned the whole list on every hit, which lost
/// selection and scroll position and made large scans quadratic. Only
/// filter or sort changes rebuild (<see cref="Refresh"/>).
/// </para>
/// </summary>
public sealed class FilteredSortedView<T>
{
    private readonly List<T> source = new();
    private Func<T, bool> filter = _ => true;
    private IComparer<T> comparer;

    public FilteredSortedView(IComparer<T> comparer)
    {
        this.comparer = comparer;
    }

    /// <summary>Bind this to the list control.</summary>
    public ObservableCollection<T> View { get; } = new();

    /// <summary>All items, including those hidden by the filter.</summary>
    public IReadOnlyList<T> Source => source;

    public void Add(T item)
    {
        source.Add(item);
        if (filter(item))
        {
            View.Insert(InsertionIndex(item), item);
        }
    }

    public void Clear()
    {
        source.Clear();
        View.Clear();
    }

    public void SetFilter(Func<T, bool> newFilter)
    {
        filter = newFilter;
        Refresh();
    }

    public void SetComparer(IComparer<T> newComparer)
    {
        comparer = newComparer;
        Refresh();
    }

    /// <summary>
    /// Re-applies filter and sort to everything, e.g. after items changed
    /// in ways that affect either (a hostname arriving).
    /// </summary>
    public void Refresh()
    {
        var visible = new List<T>(source.Count);
        foreach (T item in source)
        {
            if (filter(item))
            {
                visible.Add(item);
            }
        }

        // Stable sort: equal items keep arrival order.
        var indexed = new List<(T Item, int Order)>(visible.Count);
        for (int i = 0; i < visible.Count; i++)
        {
            indexed.Add((visible[i], i));
        }

        indexed.Sort((a, b) =>
        {
            int c = comparer.Compare(a.Item, b.Item);
            return c != 0 ? c : a.Order.CompareTo(b.Order);
        });

        View.Clear();
        foreach ((T item, _) in indexed)
        {
            View.Add(item);
        }
    }

    /// <summary>After the last equal item, so equal items keep arrival order.</summary>
    private int InsertionIndex(T item)
    {
        int lo = 0, hi = View.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (comparer.Compare(View[mid], item) <= 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
