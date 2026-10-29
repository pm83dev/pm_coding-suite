using System.Text;

namespace SupervisorLab;

public static class CsvUtil
{
    public static string Escape(string? s)
    {
        s ??= "";
        if (s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    public static string Row(params string?[] fields) => string.Join(",", fields.Select(Escape));

    public static void WriteAllLines(string path, IEnumerable<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllLines(path, lines, Encoding.UTF8);
    }
}
