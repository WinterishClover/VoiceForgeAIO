using System.Text;

namespace VoiceForgeAIO;

/// <summary>
/// Reads/writes the tool's CSV format: EditorID,VoiceType,"Text" (no header row).
/// Handles quoted fields, embedded commas, and doubled-quote ("") escaping,
/// same as a standard RFC 4180 CSV.
/// </summary>
internal static class CsvService
{
    public static List<CsvRow> ReadRows(string path)
    {
        var rows = new List<CsvRow>();
        int lineNum = 0;
        foreach (string rawLine in File.ReadLines(path))
        {
            lineNum++;
            if (string.IsNullOrWhiteSpace(rawLine)) continue;

            var fields = ParseLine(rawLine);
            if (fields.Count < 3)
            {
                throw new FormatException(
                    $"Line {lineNum}: expected 3 columns (EditorID,VoiceType,Text), got {fields.Count}. " +
                    $"Content: {rawLine}");
            }

            string id = fields[0].Trim();
            string voiceType = fields[1].Trim();
            string text = fields[2];

            if (string.IsNullOrWhiteSpace(id))
                throw new FormatException($"Line {lineNum}: empty EditorID column.");
            if (string.IsNullOrWhiteSpace(voiceType))
                throw new FormatException($"Line {lineNum}: empty VoiceType column.");

            rows.Add(new CsvRow(id, voiceType, text));
        }
        return rows;
    }

    public static List<string> ParseLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == '"')
                    inQuotes = true;
                else if (c == ',')
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                }
                else
                    sb.Append(c);
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }

    public static string Escape(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    public static async Task WriteFailuresAsync(string path, IEnumerable<(CsvRow Row, string Error)> failures)
    {
        await using var writer = new StreamWriter(path, false, Encoding.UTF8);
        await writer.WriteLineAsync("EditorID,VoiceType,Error,Text");
        foreach (var (row, error) in failures)
        {
            await writer.WriteLineAsync(
                $"{Escape(row.Id)},{Escape(row.VoiceType)},{Escape(error)},{Escape(row.Text)}");
        }
    }
}
