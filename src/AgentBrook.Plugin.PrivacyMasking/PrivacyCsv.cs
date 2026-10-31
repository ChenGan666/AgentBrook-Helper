using System.Text;

namespace AgentBrook.Privacy;

/// <summary>
/// 词典 CSV（附录 B：category,term,aliases,note[,enabled]）。
/// 解析为字符级状态机，支持引号字段内含逗号 / 引号 / 换行；导出按需加引号。
/// 读取方传入 StreamReader（默认探测 BOM）即可容忍 UTF-8 BOM。
/// </summary>
internal static class PrivacyCsv
{
    public static IReadOnlyList<IReadOnlyList<string>> Parse(TextReader reader)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;

        int c = reader.Peek();
        if (c == '\uFEFF')
        {
            reader.Read();   // 容忍调用方传入未剥 BOM 的 TextReader
        }
        while ((c = reader.Read()) >= 0)
        {
            if (inQuotes)
            {
                if (c == '"')
                {
                    var next = reader.Peek();
                    if (next == '"')
                    {
                        field.Append('"');
                        reader.Read();
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append((char)c);
                }
                continue;
            }
            switch (c)
            {
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    fieldStarted = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    EndRow(rows, row, field, fieldStarted);
                    fieldStarted = false;
                    break;
                case '"':
                    inQuotes = true;
                    fieldStarted = true;
                    break;
                default:
                    field.Append((char)c);
                    fieldStarted = true;
                    break;
            }
        }
        EndRow(rows, row, field, fieldStarted);
        return rows;
    }

    private static void EndRow(List<IReadOnlyList<string>> rows, List<string> row,
        StringBuilder field, bool fieldStarted)
    {
        var last = field.ToString();
        field.Clear();
        if (fieldStarted || row.Count > 0)
        {
            row.Add(last);
            rows.Add([.. row]);
        }
        row.Clear();
    }

    public static string Escape(string value)
    {
        var needsQuote = value.Contains(',') || value.Contains('"')
            || value.Contains('\n') || value.Contains('\r');
        return needsQuote ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
