using System;
using System.Collections.Generic;
using System.Linq;

namespace NetScannerDesktop.Services;

/// <summary>RFC 4180 CSV for the Export buttons.</summary>
public static class Csv
{
    public static string Format(IEnumerable<string[]> rows, string[] header) =>
        string.Concat(new[] { header }.Concat(rows).Select(fields => FormatLine(fields) + "\r\n"));

    private static string FormatLine(string[] fields) => string.Join(",", fields.Select(Escape));

    /// <summary>
    /// Quotes fields with separators or quotes, and neutralises a leading
    /// = + - @ so hostnames or vendor strings from the network cannot run
    /// as formulas when the file is opened in a spreadsheet.
    /// </summary>
    private static string Escape(string field)
    {
        if (field.Length > 0 && field[0] is '=' or '+' or '-' or '@')
        {
            field = "'" + field;
        }

        return field.IndexOfAny(['"', ',', '\n', '\r']) >= 0
            ? "\"" + field.Replace("\"", "\"\"") + "\""
            : field;
    }
}
