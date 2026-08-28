using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace YnabMcp.Server;

public sealed record OfxTransaction(
    DateOnly Date,
    decimal Amount,
    string? Payee,
    string? Memo,
    string? TransactionType,
    string? FitId);

public static class OfxParser
{
    private static readonly string[] FieldNames =
        ["TRNTYPE", "DTPOSTED", "TRNAMT", "FITID", "NAME", "PAYEE", "MEMO"];

    public static List<OfxTransaction> Parse(string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        var transactions = new List<OfxTransaction>();
        var blocks = Regex.Split(content, "<STMTTRN\\s*>", RegexOptions.IgnoreCase);
        var remaining = blocks.Skip(1);

        foreach (var block in remaining)
        {
            var end = block.IndexOf("</STMTTRN>", StringComparison.OrdinalIgnoreCase);
            var body = end >= 0 ? block[..end] : block;
            var fields = ExtractFields(body);

            if (!fields.TryGetValue("DTPOSTED", out var rawDate) ||
                !fields.TryGetValue("TRNAMT", out var rawAmount) ||
                TryParseDate(rawDate) is not { } date ||
                !decimal.TryParse(rawAmount, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            {
                continue;
            }

            transactions.Add(new OfxTransaction(
                Date: date,
                Amount: amount,
                Payee: Decode(fields.TryGetValue("PAYEE", out var payee) ? payee : fields.GetValueOrDefault("NAME")),
                Memo: Decode(fields.GetValueOrDefault("MEMO")),
                TransactionType: fields.GetValueOrDefault("TRNTYPE"),
                FitId: fields.GetValueOrDefault("FITID")));
        }

        return transactions;
    }

    private static Dictionary<string, string> ExtractFields(string block)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in FieldNames)
        {
            var match = Regex.Match(block, $"<{name}\\s*>([^<]*)", RegexOptions.IgnoreCase);
            if (match.Success && !fields.ContainsKey(name))
            {
                fields[name] = match.Groups[1].Value.Trim();
            }
        }

        return fields;
    }

    private static DateOnly? TryParseDate(string value)
    {
        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length < 8 ||
            !DateOnly.TryParseExact(digits[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return null;
        }

        return date;
    }

    private static string? Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&apos;", "'");
    }
}
