using System.Globalization;
using CsvHelper;

namespace WowCompanion.Data;

public sealed class CsvRow(Dictionary<string, int> index, string[] fields)
{
    public bool Has(string column) => index.ContainsKey(column);

    public string Get(string column) =>
        index.TryGetValue(column, out var i) && i < fields.Length ? fields[i] : "";

    public int GetInt(string column)
    {
        var text = Get(column);
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
        // Bit masks above int.MaxValue arrive as unsigned text; keep their bits.
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wide)) return unchecked((int)wide);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) ? (int)real : 0;
    }

    public long GetLong(string column) =>
        long.TryParse(Get(column), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    public double GetDouble(string column) =>
        double.TryParse(Get(column), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
}

public static class CsvTable
{
    public static string[] ReadHeader(string path)
    {
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        if (!csv.Read() || !csv.ReadHeader()) return [];
        return csv.HeaderRecord ?? [];
    }

    public static IEnumerable<CsvRow> Read(string path)
    {
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        if (!csv.Read() || !csv.ReadHeader()) yield break;

        var header = csv.HeaderRecord!;
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++) index.TryAdd(header[i], i);

        while (csv.Read())
        {
            yield return new CsvRow(index, (string[])csv.Parser.Record!.Clone());
        }
    }

    /// <summary>First header name that contains every fragment (case-insensitive), or null.</summary>
    public static string? FindColumn(string path, params string[] fragments) =>
        ReadHeader(path).FirstOrDefault(h => fragments.All(f => h.Contains(f, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Fails with the real column list, so a renamed column is a 10-second fix.</summary>
    public static void Require(string path, params string[] columns)
    {
        var header = ReadHeader(path);
        var missing = columns.Where(c => !header.Contains(c, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (missing.Length == 0) return;
        throw new InvalidDataException(
            $"{Path.GetFileName(path)} has no column {string.Join(", ", missing)}. Columns found: {string.Join(", ", header)}");
    }
}
