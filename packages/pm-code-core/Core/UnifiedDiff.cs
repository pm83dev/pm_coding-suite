using System.Text;

namespace LocalCodeAgent.Core;

/// <summary>
/// Generatore di diff unified minimale (LCS su righe) usato per mostrare in chat cosa cambierebbe
/// un write_file/edit_file prima di chiederne l'approvazione.
/// </summary>
public static class UnifiedDiff
{
    /// <summary>
    /// Costruisce un diff unified tra <paramref name="oldText"/> e <paramref name="newText"/> con
    /// intestazione <c>--- a/label</c> / <c>+++ b/label</c>. Contenuti identici → stringa vuota.
    /// </summary>
    public static string Build(string oldText, string newText, string label)
    {
        if (string.Equals(oldText, newText, StringComparison.Ordinal)) return string.Empty;

        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        var ops = DiffLines(oldLines, newLines);

        var sb = new StringBuilder();
        sb.AppendLine($"--- a/{label}");
        sb.AppendLine($"+++ b/{label}");

        const int context = 3;
        int i = 0;
        while (i < ops.Count)
        {
            if (ops[i].op == ' ') { i++; continue; }
            int blockStart = i;
            while (i < ops.Count && ops[i].op != ' ') i++;
            int blockEnd = i;

            int cs = blockStart;
            while (cs > 0 && ops[cs - 1].op == ' ' && (blockStart - cs) < context) cs--;
            int ce = blockEnd;
            while (ce < ops.Count && ops[ce].op == ' ' && (ce - blockEnd) < context) ce++;

            int oldStart = 0, newStart = 0;
            for (int k = 0; k < cs; k++)
            {
                if (ops[k].op == ' ' || ops[k].op == '-') oldStart++;
                if (ops[k].op == ' ' || ops[k].op == '+') newStart++;
            }
            int oldCount = 0, newCount = 0;
            for (int k = cs; k < ce; k++)
            {
                if (ops[k].op == ' ' || ops[k].op == '-') oldCount++;
                if (ops[k].op == ' ' || ops[k].op == '+') newCount++;
            }

            sb.AppendLine($"@@ -{oldStart + 1},{oldCount} +{newStart + 1},{newCount} @@");
            for (int k = cs; k < ce; k++)
            {
                var (op, oldIdx, newIdx) = ops[k];
                if (op == ' ') sb.Append(' ').AppendLine(oldLines[oldIdx]);
                else if (op == '-') sb.Append('-').AppendLine(oldLines[oldIdx]);
                else sb.Append('+').AppendLine(newLines[newIdx]);
            }
        }

        return sb.ToString();
    }

    private static List<string> SplitLines(string text) =>
        string.IsNullOrEmpty(text) ? new List<string>()
        : text.Replace("\r\n", "\n").Split('\n').ToList();

    /// <summary>Sequenza di operazioni: ' ' (uguale), '-' (rimossa), '+' (aggiunta).</summary>
    private static List<(char op, int oldIdx, int newIdx)> DiffLines(List<string> oldLines, List<string> newLines)
    {
        const int cap = 4000;
        var n = Math.Min(oldLines.Count, cap);
        var m = Math.Min(newLines.Count, cap);

        // LCS (tabella) su righe, limitata per contenere memoria e tempo.
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = oldLines[i] == newLines[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var ops = new List<(char, int, int)>();
        int oi = 0, oj = 0;
        while (oi < oldLines.Count && oj < newLines.Count)
        {
            if (oi < n && oj < m && oldLines[oi] == newLines[oj]) { ops.Add((' ', oi, oj)); oi++; oj++; }
            else if (oj < m && (oi >= n || lcs[oi + 1, oj] >= lcs[oi, oj + 1])) { ops.Add(('+', -1, oj)); oj++; }
            else { ops.Add(('-', oi, -1)); oi++; }
        }
        while (oi < oldLines.Count) { ops.Add(('-', oi, -1)); oi++; }
        while (oj < newLines.Count) { ops.Add(('+', -1, oj)); oj++; }
        return ops;
    }
}