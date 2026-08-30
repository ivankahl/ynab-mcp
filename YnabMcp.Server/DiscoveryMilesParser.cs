using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace YnabMcp.Server;

public sealed record DiscoveryStatementRow(DateOnly Date, decimal Miles, string? Payee, string? Memo);

public sealed record DiscoveryStatementParseResult(
    List<DiscoveryStatementRow> Rows,
    List<string> SkippedRows,
    string Source,
    string? DateColumn,
    string? MilesColumn,
    string? DescriptionColumn,
    string? AdditionalInfoColumn);

public static class DiscoveryMilesParser
{
    private static readonly DateOnly ExcelEpoch = new(1899, 12, 30);

    private static readonly string[] DateHeaderNames =
        ["valuedate", "date", "transactiondate", "postingdate", "postdate", "posteddate", "txndate", "datetime"];

    private static readonly string[] MilesHeaderNames =
        ["miles", "milesamount", "amountmiles", "milesvalue", "milevalue", "mile"];

    private static readonly string[] AmountHeaderNames =
        ["amount", "transactionamount", "txamount", "netamount", "value"];

    private static readonly string[] DebitHeaderNames =
        ["debit", "outflow", "spent", "withdrawal", "milesspent", "milesdebit", "debitmiles"];

    private static readonly string[] CreditHeaderNames =
        ["credit", "inflow", "earned", "deposit", "milesearned", "milescredit", "creditmiles"];

    private static readonly string[] DescriptionHeaderNames =
        ["description", "transactiondescription", "txndescription", "details", "transactiondetails", "merchant", "merchantdetails", "narrative", "payee"];

    private static readonly string[] AdditionalInfoHeaderNames =
        ["additionalinformation", "additionalinfo", "additionaldetails", "moreinformation", "moreinfo"];

    private static readonly string[] TypeHeaderNames =
        ["type", "transactiontype", "txntype", "rewardtype"];

    private static readonly string[] BalanceHeaderNames =
        ["balance", "runningbalance", "availablebalance", "closingbalance", "openingbalance", "accountbalance"];

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy/MM/dd", "yyyy.MM.dd", "yyyyMMdd",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy/MM/dd HH:mm:ss",
        "d/M/yyyy", "dd/MM/yyyy", "d/M/yyyy HH:mm", "dd/MM/yyyy HH:mm:ss",
        "d-M-yyyy", "dd-MM-yyyy", "d-M-yyyy HH:mm", "dd-MM-yyyy HH:mm:ss",
        "d.M.yyyy", "dd.MM.yyyy",
        "M/d/yyyy", "MM/dd/yyyy",
        "d MMM yyyy", "dd MMM yyyy", "d MMMM yyyy", "dd MMMM yyyy",
        "MMM d yyyy", "MMM d, yyyy", "MMMM d, yyyy",
        "ddd, d MMM yyyy",
    ];

    public static DiscoveryStatementParseResult Parse(string? filePath, string? csvContent, string? fileContentBase64, bool includeZeroMiles)
    {
        var (grid, source) = LoadGrid(filePath, csvContent, fileContentBase64);
        return ParseGrid(grid, includeZeroMiles) with { Source = source };
    }

    private static (List<string[]> Grid, string Source) LoadGrid(string? filePath, string? csvContent, string? fileContentBase64)
    {
        byte[] bytes;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            if (!File.Exists(filePath))
            {
                throw new YnabException($"File not found: '{filePath}'.");
            }

            bytes = File.ReadAllBytes(filePath);
        }
        else if (csvContent is not null)
        {
            bytes = Encoding.UTF8.GetBytes(csvContent);
        }
        else if (!string.IsNullOrWhiteSpace(fileContentBase64))
        {
            try
            {
                bytes = Convert.FromBase64String(fileContentBase64);
            }
            catch (FormatException)
            {
                throw new YnabException("fileContentBase64 is not valid base64.");
            }
        }
        else
        {
            throw new YnabException("Provide the statement via filePath, csvContent or fileContentBase64.");
        }

        if (IsZip(bytes))
        {
            return (ReadXlsxGrid(bytes), "xlsx");
        }

        return (ReadCsvGrid(Encoding.UTF8.GetString(bytes)), "csv");
    }

    private static bool IsZip(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] is 3 or 5 or 7;

    public static DiscoveryStatementParseResult ParseGrid(List<string[]> grid, bool includeZeroMiles)
    {
        if (grid.Count == 0)
        {
            throw new YnabException("The statement file is empty.");
        }

        var columns = DetectColumns(grid);

        var rows = new List<DiscoveryStatementRow>();
        var skipped = new List<string>();

        for (var i = columns.HeaderRowIndex + 1; i < grid.Count; i++)
        {
            var cells = grid[i];
            var label = $"Row {i + 1}";

            var dateText = CellAt(cells, columns.DateCol);
            if (string.IsNullOrWhiteSpace(dateText))
            {
                skipped.Add($"{label}: no date.");
                continue;
            }

            if (!TryParseDate(dateText, out var date))
            {
                skipped.Add($"{label}: unrecognized date '{Truncate(dateText)}'.");
                continue;
            }

            decimal miles;
            if (columns.AmountCol >= 0)
            {
                var amountText = CellAt(cells, columns.AmountCol);
                if (string.IsNullOrWhiteSpace(amountText))
                {
                    skipped.Add($"{label}: no miles amount.");
                    continue;
                }

                if (!TryParseAmount(amountText, out miles))
                {
                    skipped.Add($"{label}: unrecognized miles amount '{Truncate(amountText)}'.");
                    continue;
                }
            }
            else
            {
                var hasDebit = TryParseAmount(CellAt(cells, columns.DebitCol), out var debit);
                var hasCredit = TryParseAmount(CellAt(cells, columns.CreditCol), out var credit);
                if (!hasDebit && !hasCredit)
                {
                    skipped.Add($"{label}: no miles amount.");
                    continue;
                }

                miles = (hasCredit ? credit : 0) - (hasDebit ? debit : 0);
            }

            if (miles == 0 && !includeZeroMiles)
            {
                skipped.Add($"{label}: 0 miles, nothing to import.");
                continue;
            }

            var description = NullIfEmpty(CellAt(cells, columns.DescriptionCol));
            var additionalInfo = NullIfEmpty(CellAt(cells, columns.AdditionalInfoCol));
            var type = NullIfEmpty(CellAt(cells, columns.TypeCol));

            var payee = description ?? type;
            var memo = JoinNonEmpty(" - ", description, additionalInfo);

            rows.Add(new DiscoveryStatementRow(date, miles, payee, memo));
        }

        return new DiscoveryStatementParseResult(
            rows,
            skipped,
            "unknown",
            columns.DateColumnName,
            columns.AmountColumnName,
            columns.DescriptionColumnName,
            columns.AdditionalInfoColumnName);
    }

    private sealed record ColumnMapping(
        int DateCol,
        int AmountCol,
        int DebitCol,
        int CreditCol,
        int DescriptionCol,
        int AdditionalInfoCol,
        int TypeCol,
        int HeaderRowIndex,
        string? DateColumnName,
        string? AmountColumnName,
        string? DescriptionColumnName,
        string? AdditionalInfoColumnName);

    private static ColumnMapping DetectColumns(List<string[]> grid)
    {
        var scanLimit = Math.Min(grid.Count, 10);
        for (var r = 0; r < scanLimit; r++)
        {
            var mapping = MatchHeaderRow(grid[r]);
            if (mapping is not null)
            {
                return mapping with { HeaderRowIndex = r };
            }
        }

        return InferColumns(grid);
    }

    private static ColumnMapping? MatchHeaderRow(string[] cells)
    {
        int dateCol = -1, milesCol = -1, amountCol = -1, debitCol = -1, creditCol = -1;
        int descriptionCol = -1, additionalInfoCol = -1, typeCol = -1;
        string? dateName = null, amountName = null, descriptionName = null, additionalInfoName = null;

        for (var c = 0; c < cells.Length; c++)
        {
            var normalized = Normalize(cells[c]);
            if (normalized.Length == 0 || IsBalanceHeader(normalized))
            {
                continue;
            }

            if (dateCol < 0 && IsDateHeader(normalized))
            {
                dateCol = c;
                dateName = cells[c].Trim();
            }
            else if (debitCol < 0 && IsDebitHeader(normalized))
            {
                debitCol = c;
            }
            else if (creditCol < 0 && IsCreditHeader(normalized))
            {
                creditCol = c;
            }
            else if (milesCol < 0 && IsMilesHeader(normalized))
            {
                milesCol = c;
                amountName = cells[c].Trim();
            }
            else if (amountCol < 0 && IsAmountHeader(normalized))
            {
                amountCol = c;
                amountName ??= cells[c].Trim();
            }
            else if (additionalInfoCol < 0 && IsAdditionalInfoHeader(normalized))
            {
                additionalInfoCol = c;
                additionalInfoName = cells[c].Trim();
            }
            else if (descriptionCol < 0 && IsDescriptionHeader(normalized))
            {
                descriptionCol = c;
                descriptionName = cells[c].Trim();
            }
            else if (typeCol < 0 && IsTypeHeader(normalized))
            {
                typeCol = c;
            }
        }

        var amount = milesCol >= 0 ? milesCol : amountCol;
        if (dateCol < 0 || (amount < 0 && (debitCol < 0 || creditCol < 0)))
        {
            return null;
        }

        return new ColumnMapping(
            dateCol,
            amount,
            debitCol,
            creditCol,
            descriptionCol,
            additionalInfoCol,
            typeCol,
            -1,
            dateName,
            amountName,
            descriptionName,
            additionalInfoName);
    }

    private static ColumnMapping InferColumns(List<string[]> grid)
    {
        var sample = grid.Take(25).ToList();
        var width = sample.Max(cells => cells.Length);

        int dateCol = -1, amountCol = -1, descriptionCol = -1;
        double bestDateScore = 0, bestAmountScore = 0, bestTextScore = 0;

        for (var c = 0; c < width; c++)
        {
            var nonEmpty = 0;
            var dateCount = 0;
            var amountCount = 0;
            var textCount = 0;
            foreach (var cells in sample)
            {
                var cell = CellAt(cells, c);
                if (string.IsNullOrWhiteSpace(cell))
                {
                    continue;
                }

                nonEmpty++;
                if (TryParseDate(cell, out _))
                {
                    dateCount++;
                }

                if (TryParseAmount(cell, out _))
                {
                    amountCount++;
                }
                else
                {
                    textCount++;
                }
            }

            if (nonEmpty < 3)
            {
                continue;
            }

            var dateScore = (double)dateCount / nonEmpty;
            var amountScore = (double)amountCount / nonEmpty;
            var textScore = (double)textCount / nonEmpty;

            if (dateScore >= 0.5 && dateScore > bestDateScore)
            {
                bestDateScore = dateScore;
                dateCol = c;
            }
        }

        for (var c = 0; c < width; c++)
        {
            if (c == dateCol)
            {
                continue;
            }

            var nonEmpty = 0;
            var amountCount = 0;
            foreach (var cells in sample)
            {
                var cell = CellAt(cells, c);
                if (string.IsNullOrWhiteSpace(cell))
                {
                    continue;
                }

                nonEmpty++;
                if (TryParseAmount(cell, out _))
                {
                    amountCount++;
                }
            }

            if (nonEmpty < 3)
            {
                continue;
            }

            var amountScore = (double)amountCount / nonEmpty;
            if (amountScore >= 0.5 && amountScore > bestAmountScore)
            {
                bestAmountScore = amountScore;
                amountCol = c;
            }
        }

        if (dateCol < 0 || amountCol < 0)
        {
            var headers = string.Join(" | ", grid[0].Select(c => $"'{Truncate(c)}'"));
            throw new YnabException(
                $"Could not find Value Date and Miles columns in the statement. Header row: {headers}. " +
                "Expected a Discovery Miles export with 'Value Date' and 'Miles' columns.");
        }

        for (var c = 0; c < width; c++)
        {
            if (c == dateCol || c == amountCol)
            {
                continue;
            }

            var nonEmpty = 0;
            var textCount = 0;
            foreach (var cells in sample)
            {
                var cell = CellAt(cells, c);
                if (string.IsNullOrWhiteSpace(cell))
                {
                    continue;
                }

                nonEmpty++;
                if (!TryParseAmount(cell, out _) && !TryParseDate(cell, out _))
                {
                    textCount++;
                }
            }

            if (nonEmpty < 3)
            {
                continue;
            }

            var textScore = (double)textCount / nonEmpty;
            if (textScore >= 0.5 && textScore > bestTextScore)
            {
                bestTextScore = textScore;
                descriptionCol = c;
            }
        }

        return new ColumnMapping(
            dateCol,
            amountCol,
            -1,
            -1,
            descriptionCol,
            -1,
            -1,
            -1,
            ColumnLabel(dateCol),
            ColumnLabel(amountCol),
            descriptionCol >= 0 ? ColumnLabel(descriptionCol) : null,
            null);
    }

    private static bool IsDateHeader(string value) => DateHeaderNames.Contains(value);
    private static bool IsMilesHeader(string value) => MilesHeaderNames.Contains(value);
    private static bool IsAmountHeader(string value) => AmountHeaderNames.Contains(value);
    private static bool IsDebitHeader(string value) => DebitHeaderNames.Contains(value);
    private static bool IsCreditHeader(string value) => CreditHeaderNames.Contains(value);
    private static bool IsDescriptionHeader(string value) => DescriptionHeaderNames.Contains(value);
    private static bool IsAdditionalInfoHeader(string value) => AdditionalInfoHeaderNames.Contains(value);
    private static bool IsTypeHeader(string value) => TypeHeaderNames.Contains(value);
    private static bool IsBalanceHeader(string value) => BalanceHeaderNames.Contains(value);

    private static string Normalize(string? value) =>
        Regex.Replace(value ?? string.Empty, @"[^\p{L}\p{N}]", string.Empty).ToLowerInvariant();

    private static string? CellAt(string[] cells, int index) =>
        index >= 0 && index < cells.Length ? cells[index] : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

    private static string? JoinNonEmpty(string separator, params string?[] parts)
    {
        var joined = string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
        return joined.Length > 0 ? joined : null;
    }

    private static string Truncate(string value, int maxLength = 40) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";

    private static string ColumnLabel(int index) => $"column {index + 1} ({ColumnLetters(index)})";

    private static string ColumnLetters(int index)
    {
        var letters = string.Empty;
        index++;
        while (index > 0)
        {
            var remainder = (index - 1) % 26;
            letters = (char)('A' + remainder) + letters;
            index = (index - 1) / 26;
        }

        return letters;
    }

    public static bool TryParseDate(string? raw, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim();
        foreach (var format in DateFormats)
        {
            if (DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                date = DateOnly.FromDateTime(parsed);
                return true;
            }
        }

        if (Regex.IsMatch(text, @"^\d{5}(\.\d+)?$") &&
            decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) &&
            serial >= 20000m && serial < 100000m)
        {
            date = ExcelEpoch.AddDays((int)Math.Floor(serial));
            return true;
        }

        return false;
    }

    public static bool TryParseAmount(string? raw, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim().Replace("\u00A0", " ");
        var negative = false;

        if (text.Length > 2 && text.StartsWith('(') && text.EndsWith(')'))
        {
            negative = true;
            text = text[1..^1].Trim();
        }

        if (text.Length >= 2)
        {
            var suffix = text[^2..].ToLowerInvariant();
            if (suffix == "cr")
            {
                text = text[..^2].Trim();
            }
            else if (suffix == "dr")
            {
                negative = true;
                text = text[..^2].Trim();
            }
        }

        text = Regex.Replace(text, "[A-Za-z]", string.Empty).Replace(" ", string.Empty);
        if (text.Length == 0)
        {
            return false;
        }

        if (text.StartsWith('+'))
        {
            text = text[1..];
        }

        if (text.StartsWith('-'))
        {
            negative = !negative;
            text = text[1..];
        }

        if (text.Length == 0)
        {
            return false;
        }

        var lastComma = text.LastIndexOf(',');
        var lastDot = text.LastIndexOf('.');
        if (lastComma >= 0 && lastDot >= 0)
        {
            if (lastComma > lastDot)
            {
                text = text.Replace(".", string.Empty).Replace(',', '.');
            }
            else
            {
                text = text.Replace(",", string.Empty);
            }
        }
        else if (lastComma >= 0)
        {
            var parts = text.Split(',');
            text = parts.Length == 2 && parts[1].Length is 1 or 2
                ? parts[0].Replace(",", string.Empty) + "." + parts[1]
                : text.Replace(",", string.Empty);
        }
        else if (lastDot >= 0 && text.Count(c => c == '.') > 1)
        {
            text = text.Replace(".", string.Empty);
        }

        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        if (negative)
        {
            value = -value;
        }

        return true;
    }

    private static List<string[]> ReadCsvGrid(string content)
    {
        content = content.TrimStart('\uFEFF');
        var delimiter = DetectDelimiter(content);

        var rows = new List<string[]>();
        var cell = new StringBuilder();
        var cells = new List<string>();
        var inQuotes = false;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == delimiter)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n')
                {
                    i++;
                }

                cells.Add(cell.ToString());
                cell.Clear();
                rows.Add([.. cells]);
                cells.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        if (cell.Length > 0 || cells.Count > 0)
        {
            cells.Add(cell.ToString());
            rows.Add([.. cells]);
        }

        return rows.Where(row => row.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
    }

    private static char DetectDelimiter(string content)
    {
        var line = content.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? string.Empty;
        char best = ',';
        var bestCount = -1;
        foreach (var delimiter in new[] { ',', ';', '\t', '|' })
        {
            var count = line.Count(c => c == delimiter);
            if (count > bestCount)
            {
                best = delimiter;
                bestCount = count;
            }
        }

        return best;
    }

    private static List<string[]> ReadXlsxGrid(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var sharedStrings = LoadSharedStrings(zip);
        var dateStyles = LoadDateStyles(zip);
        var sheetPath = FindFirstSheetPath(zip);
        var entry = FindEntry(zip, sheetPath)
            ?? throw new YnabException($"Not a valid XLSX file: worksheet '{sheetPath}' is missing.");

        var document = XDocument.Load(entry.Open());
        var rows = new List<string[]>();

        foreach (var row in document.Descendants().Where(e => e.Name.LocalName == "row"))
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements().Where(e => e.Name.LocalName == "c"))
            {
                var column = (string?)cell.Attribute("r") is { } cellRef ? ColumnIndexFromRef(cellRef) : cells.Count;
                var type = (string?)cell.Attribute("t");
                var style = (string?)cell.Attribute("s");

                string? value = null;
                string? inline = null;
                foreach (var child in cell.Elements())
                {
                    if (child.Name.LocalName == "v")
                    {
                        value = child.Value;
                    }
                    else if (child.Name.LocalName == "is")
                    {
                        inline = string.Concat(child.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value));
                    }
                }

                var text = ConvertCell(type, style, value, inline, sharedStrings, dateStyles);
                while (cells.Count <= column)
                {
                    cells.Add(string.Empty);
                }

                cells[column] = text ?? string.Empty;
            }

            if (cells.Any(c => !string.IsNullOrWhiteSpace(c)))
            {
                rows.Add([.. cells]);
            }
        }

        return rows;
    }

    private static string? ConvertCell(string? type, string? style, string? value, string? inline, List<string> sharedStrings, HashSet<int> dateStyles)
    {
        switch (type)
        {
            case "s":
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
                       index >= 0 && index < sharedStrings.Count
                    ? sharedStrings[index]
                    : value;
            case "inlineStr":
                return inline ?? value;
            case "b":
                return value == "1" ? "TRUE" : "FALSE";
            case "str":
            case "e":
                return value;
            default:
                if (value is null)
                {
                    return null;
                }

                if (dateStyles.Count > 0 &&
                    int.TryParse(style, NumberStyles.Integer, CultureInfo.InvariantCulture, out var styleIndex) &&
                    dateStyles.Contains(styleIndex) &&
                    decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) &&
                    serial >= 1m && serial < 100000m)
                {
                    return ExcelEpoch.AddDays((int)Math.Floor(serial)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }

                return value;
        }
    }

    private static List<string> LoadSharedStrings(ZipArchive zip)
    {
        var entry = FindEntry(zip, "xl/sharedStrings.xml");
        if (entry is null)
        {
            return [];
        }

        var document = XDocument.Load(entry.Open());
        return document.Descendants()
            .Where(e => e.Name.LocalName == "si")
            .Select(si => string.Concat(si.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value)))
            .ToList();
    }

    private static HashSet<int> LoadDateStyles(ZipArchive zip)
    {
        var entry = FindEntry(zip, "xl/styles.xml");
        if (entry is null)
        {
            return [];
        }

        var document = XDocument.Load(entry.Open());
        var customFormats = document.Descendants()
            .Where(e => e.Name.LocalName == "numFmt")
            .Select(e => (Id: (int?)e.Attribute("numFmtId"), Code: (string?)e.Attribute("formatCode")))
            .Where(x => x.Id.HasValue && !string.IsNullOrWhiteSpace(x.Code))
            .ToDictionary(x => x.Id!.Value, x => x.Code!.Trim());

        var cellXfs = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "cellXfs");
        if (cellXfs is null)
        {
            return [];
        }

        var dateStyles = new HashSet<int>();
        var index = 0;
        foreach (var xf in cellXfs.Elements().Where(e => e.Name.LocalName == "xf"))
        {
            if (IsDateFormat((int?)xf.Attribute("numFmtId") ?? 0, customFormats))
            {
                dateStyles.Add(index);
            }

            index++;
        }

        return dateStyles;
    }

    private static bool IsDateFormat(int numFmtId, Dictionary<int, string> customFormats)
    {
        if ((numFmtId >= 14 && numFmtId <= 22) ||
            (numFmtId >= 27 && numFmtId <= 36) ||
            (numFmtId >= 45 && numFmtId <= 47) ||
            (numFmtId >= 50 && numFmtId <= 58))
        {
            return true;
        }

        return customFormats.TryGetValue(numFmtId, out var code) && Regex.IsMatch(code, "[yYd]");
    }

    private static string FindFirstSheetPath(ZipArchive zip)
    {
        var workbookEntry = FindEntry(zip, "xl/workbook.xml")
            ?? throw new YnabException("Not a valid XLSX file: workbook.xml is missing.");

        var workbook = XDocument.Load(workbookEntry.Open());
        var sheet = workbook.Descendants().FirstOrDefault(e => e.Name.LocalName == "sheet");
        var relationshipId = sheet?.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;

        var relsEntry = FindEntry(zip, "xl/_rels/workbook.xml.rels");
        if (relationshipId is not null && relsEntry is not null)
        {
            var relationships = XDocument.Load(relsEntry.Open());
            var target = relationships.Descendants()
                .Where(e => e.Name.LocalName == "Relationship")
                .FirstOrDefault(e => string.Equals((string?)e.Attribute("Id"), relationshipId, StringComparison.Ordinal))
                ?.Attribute("Target")?.Value;

            if (!string.IsNullOrWhiteSpace(target))
            {
                return ResolveSheetPath(target);
            }
        }

        return "xl/worksheets/sheet1.xml";
    }

    private static string ResolveSheetPath(string target)
    {
        var path = target.Replace('\\', '/');
        if (path.StartsWith('/'))
        {
            return path.TrimStart('/');
        }

        var parts = new List<string>();
        foreach (var segment in ("xl/" + path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (segment != ".")
            {
                parts.Add(segment);
            }
        }

        return string.Join('/', parts);
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string path) =>
        zip.Entries.FirstOrDefault(entry =>
            string.Equals(entry.FullName, path, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.FullName, "/" + path, StringComparison.OrdinalIgnoreCase));

    private static int ColumnIndexFromRef(string cellRef)
    {
        var index = 0;
        foreach (var c in cellRef)
        {
            if (!char.IsAsciiLetter(c))
            {
                break;
            }

            index = index * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }

        return index - 1;
    }
}
