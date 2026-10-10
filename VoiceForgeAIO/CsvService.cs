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
        bool first = true;
        foreach (var (lineNum, fields) in ParseRecords(File.ReadAllText(path)))
        {
            // A header row (EditorID,VoiceType,Text) is skipped rather than treated as a line to voice.
            bool isFirst = first;
            first = false;
            if (isFirst && fields.Count >= 2
                && fields[0].Trim().Equals("EditorID", StringComparison.OrdinalIgnoreCase)
                && fields[1].Trim().Equals("VoiceType", StringComparison.OrdinalIgnoreCase))
                continue;

            if (fields.Count < 3)
            {
                throw new FormatException(
                    $"Line {lineNum}: expected 3 columns (EditorID,VoiceType,Text), got {fields.Count}. " +
                    $"Content: {string.Join(",", fields)}");
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

    /// <summary>
    /// Splits CSV text into records. Unlike splitting on line breaks first, a quoted field
    /// may contain line breaks (multi-line dialogue), which stay part of that field.
    /// Blank lines are skipped. Returns each record with the line it starts on.
    /// </summary>
    public static List<(int Line, List<string> Fields)> ParseRecords(string text)
    {
        var records = new List<(int, List<string>)>();
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false, sawContent = false;
        int line = 1, startLine = 1;

        void EndRecord()
        {
            if (sawContent || fields.Count > 0)
            {
                fields.Add(sb.ToString());
                if (!(fields.Count == 1 && string.IsNullOrWhiteSpace(fields[0])))
                    records.Add((startLine, fields));
            }
            fields = new List<string>();
            sb.Clear();
            sawContent = false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!sawContent && fields.Count == 0 && sb.Length == 0) startLine = line;

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else
                {
                    if (c == '\n') line++;
                    sb.Append(c);
                }
                continue;
            }

            if (c == '"') { inQuotes = true; sawContent = true; }
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); sawContent = true; }
            else if (c == '\r') { /* part of CRLF; the \n ends the record */ if (i + 1 >= text.Length || text[i + 1] != '\n') { EndRecord(); line++; } }
            else if (c == '\n') { EndRecord(); line++; }
            else { sb.Append(c); sawContent = true; }
        }

        if (inQuotes) throw new FormatException($"Line {startLine}: a quoted field is never closed (missing closing \" ).");
        EndRecord();
        return records;
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
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var writer = new StreamWriter(path, false, Encoding.UTF8);
        await writer.WriteLineAsync("EditorID,VoiceType,Error,Text");
        foreach (var (row, error) in failures)
        {
            await writer.WriteLineAsync(
                $"{Escape(row.Id)},{Escape(row.VoiceType)},{Escape(error)},{Escape(row.Text)}");
        }
    }
}
