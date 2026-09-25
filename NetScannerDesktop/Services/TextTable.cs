using System;
using System.Collections.Generic;
using System.Linq;

namespace NetScannerDesktop.Services;

/// <summary>
/// Plain-text table for "Copy > Everything (as a table)": a header row and
/// space-padded columns, like the engine's own terminal tables. Reads well
/// pasted into a note or a monospaced message; Export is there for
/// spreadsheets.
/// </summary>
public static class TextTable
{
    private const string Gap = "  ";

    public static string Format(string[] header, IEnumerable<string[]> rows)
    {
        List<string[]> lines = [header, .. rows];
        int[] widths = Enumerable.Range(0, header.Length)
            .Select(column => lines.Max(line => line[column].Length))
            .ToArray();

        // The last column is not padded, and rows keep no trailing spaces
        // when their last cells are empty.
        return string.Join(Environment.NewLine, lines.Select(line =>
            string.Join(Gap, line.Select((cell, column) =>
                column == line.Length - 1 ? cell : cell.PadRight(widths[column]))).TrimEnd()));
    }
}
