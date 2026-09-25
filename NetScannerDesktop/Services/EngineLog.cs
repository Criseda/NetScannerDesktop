using System.Collections.Generic;
using System.Linq;

namespace NetScannerDesktop.Services;

/// <summary>
/// Bounded, throttled engine-output buffer.
/// Keeps the last <see cref="MaxLines"/> lines so a /16 scan (65k lines)
/// cannot freeze the UI with O(n^2) TextBox rebuilds. Callers append every
/// engine line but only push <see cref="Text"/> to the UI when
/// <see cref="ShouldRefresh"/> is true, plus a final <see cref="Flush"/>.
/// </summary>
public sealed class EngineLog
{
    public const int MaxLines = 800;
    public const int RefreshEvery = 20;

    private readonly Queue<string> lines = new();
    private int sinceRefresh;

    public string Text { get; private set; } = string.Empty;

    public int LineCount => lines.Count;

    public string LineCountText => LineCount == 1 ? "1 line" : $"{LineCount:N0} lines";

    public void Clear()
    {
        lines.Clear();
        sinceRefresh = 0;
        Text = string.Empty;
    }

    /// <summary>
    /// Append one engine line. Returns true when the UI should refresh.
    /// </summary>
    public bool Append(string line)
    {
        lines.Enqueue(line);
        while (lines.Count > MaxLines)
        {
            lines.Dequeue();
        }

        sinceRefresh++;
        if (sinceRefresh >= RefreshEvery)
        {
            Flush();
            return true;
        }

        return false;
    }

    public string Flush()
    {
        sinceRefresh = 0;
        Text = string.Join(System.Environment.NewLine, lines);
        if (lines.Count >= MaxLines)
        {
            Text += System.Environment.NewLine + $"… showing last {MaxLines:N0} lines";
        }

        return Text;
    }
}
