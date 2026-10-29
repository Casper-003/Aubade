using System.Text.RegularExpressions;

namespace Vesper.Core;

public static class LyricsDisplay
{
    private static readonly Regex Prefix = new(
        @"^(?:\s*\[(?:(?:id|hash|sign|total|ar|al|ti|by|offset|length|ver|version|re|ve|tool|language|encoding):[^\]]*|\d{1,3}:\d{2}(?:[.:]\d+)?)\])+\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Clean(string text)
    {
        var lines = new List<string>();
        foreach (string original in text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = Prefix.Replace(original.Trim(), "");
            if (original.Trim().Length > 0 && line.Length == 0) continue;
            if (line.Length == 0 && (lines.Count == 0 || lines[^1].Length == 0)) continue;
            lines.Add(line);
        }
        return string.Join('\n', lines).Trim();
    }
}
