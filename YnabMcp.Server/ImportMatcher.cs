namespace YnabMcp.Server;

/// <summary>An existing transaction in the target account, used only as a matching candidate.</summary>
public sealed record ImportMatchTarget(
    string Id,
    DateOnly Date,
    long AmountMilli,
    string? ImportId,
    string? PayeeName,
    string? Memo);

/// <summary>
/// A normalized statement row (shared by the OFX and Discovery Miles importers).
/// <paramref name="SourceValue"/> carries any source-specific value for reporting
/// (e.g. Discovery Miles); it is not used for matching.
/// </summary>
public sealed record ImportRow(
    DateOnly Date,
    decimal Amount,
    string? Payee,
    string? Memo,
    string? SourceType,
    string? SourceId,
    decimal? SourceValue);

/// <summary>Details of how one statement row was resolved against the account.</summary>
public sealed record ImportMatchReport(
    string Reason,                     // "already_imported" | "matched"
    string Date,
    decimal Amount,
    string? Payee,
    string? Memo,
    string? SourceType,
    string? SourceId,
    string TransactionId,              // the existing transaction it resolved to
    string? TransactionDate,
    string? TransactionPayeeName,
    string? TransactionMemo);

/// <summary>A statement row that should be created as a new transaction.</summary>
public sealed record ImportDraft(
    long AmountMilli,
    DateOnly Date,
    decimal Amount,
    string? Payee,
    string? Memo,
    decimal? SourceValue,
    string ImportId);

/// <summary>The import plan for one statement.</summary>
public sealed class ImportPlan
{
    /// <summary>Rows that already exist in the account (deduped by YNAB import id). Not imported.</summary>
    public List<ImportMatchReport> AlreadyImported { get; } = [];

    /// <summary>Rows matched to a manually-entered transaction (exact amount + date within tolerance). Not imported.</summary>
    public List<ImportMatchReport> Matched { get; } = [];

    /// <summary>Rows that will be created as new transactions.</summary>
    public List<ImportDraft> ToCreate { get; } = [];
}

/// <summary>
/// Mirrors YNAB's own file-based import matching so imported transactions line up
/// with what YNAB would do, regardless of whether they come from an OFX file or a
/// Discovery Miles CSV/XLSX statement:
///  1. Deduplicate by YNAB import id (YNAB:{amountMilli}:{date}:{occurrence}) against
///     ANY existing transaction in the account, regardless of cleared/approved state.
///     This is how YNAB avoids re-creating transactions that already exist.
///  2. Match against manually-entered transactions (those with no import id) using the
///     same amount and a date within the tolerance window (YNAB uses 10 days), one-to-one.
///  3. Everything left is a genuinely new transaction and is created.
/// </summary>
public static class ImportMatcher
{
    public static ImportPlan Plan<T>(
        IReadOnlyList<T> source,
        IReadOnlyList<ImportMatchTarget> existing,
        int toleranceDays,
        Func<T, ImportRow> project)
    {
        var plan = new ImportPlan();
        var rows = source.Select(project).ToList();
        var indexed = AssignImportIds(rows);

        // Layer 1: import-id dedup against any existing transaction (any cleared/approved state).
        var existingByImportId = existing
            .Where(e => !string.IsNullOrWhiteSpace(e.ImportId))
            .GroupBy(e => e.ImportId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var claimedTxnIds = new HashSet<string>(StringComparer.Ordinal);
        var handled = new bool[indexed.Count];

        for (var i = 0; i < indexed.Count; i++)
        {
            var (row, importId) = indexed[i];
            // YNAB writes import ids in the same format, so a prior native import or a
            // prior MCP import of the same statement is recognised here.
            if (existingByImportId.TryGetValue(importId, out var dup) && claimedTxnIds.Add(dup.Id))
            {
                handled[i] = true;
                plan.AlreadyImported.Add(Report("already_imported", row, importId, dup));
            }
        }

        // Layer 2: match manually-entered transactions (no import id) by exact amount and
        // a date within tolerance days; one-to-one, nearest date first.
        var manualPool = existing
            .Where(e => !claimedTxnIds.Contains(e.Id) && string.IsNullOrWhiteSpace(e.ImportId))
            .ToList();

        for (var i = 0; i < indexed.Count; i++)
        {
            if (handled[i]) continue;
            var (row, importId) = indexed[i];
            var milli = YnabClient.ToMilliunits(row.Amount);

            var candidate = manualPool
                .Where(t => t.AmountMilli == milli &&
                            Math.Abs(t.Date.DayNumber - row.Date.DayNumber) <= toleranceDays)
                .OrderBy(t => Math.Abs(t.Date.DayNumber - row.Date.DayNumber))
                .ThenBy(t => t.Date)
                .ThenBy(t => t.Id)
                .FirstOrDefault();

            if (candidate is null)
            {
                plan.ToCreate.Add(new ImportDraft(
                    milli, row.Date, row.Amount, row.Payee, row.Memo, row.SourceValue, importId));
                continue;
            }

            var consumedIdx = manualPool.FindIndex(t => t.Id == candidate.Id);
            if (consumedIdx >= 0) manualPool.RemoveAt(consumedIdx);
            claimedTxnIds.Add(candidate.Id);
            plan.Matched.Add(Report("matched", row, importId, candidate));
        }

        return plan;
    }

    /// <summary>
    /// Assigns YNAB-compatible import ids in FILE ORDER (never reordered). YNAB uses
    /// YNAB:{amountMilli}:{yyyy-MM-dd}:{n} where n counts the occurrence of identical
    /// (amount, date) pairs in the file, so re-importing the same file yields the same
    /// ids and earlier imports are recognised as duplicates.
    /// </summary>
    private static List<(ImportRow Row, string ImportId)> AssignImportIds(IReadOnlyList<ImportRow> rows)
    {
        var counters = new Dictionary<(long AmountMilli, DateOnly Date), int>();
        var result = new List<(ImportRow, string)>(rows.Count);
        foreach (var row in rows)
        {
            var milli = YnabClient.ToMilliunits(row.Amount);
            var key = (milli, row.Date);
            counters.TryGetValue(key, out var n);
            n += 1;
            counters[key] = n;
            result.Add((row, $"YNAB:{milli}:{row.Date:yyyy-MM-dd}:{n}"));
        }
        return result;
    }

    private static ImportMatchReport Report(string reason, ImportRow row, string importId, ImportMatchTarget target) => new(
        reason,
        row.Date.ToString("yyyy-MM-dd"),
        row.Amount,
        row.Payee,
        row.Memo,
        row.SourceType,
        row.SourceId,
        target.Id,
        target.Date.ToString("yyyy-MM-dd"),
        target.PayeeName,
        target.Memo);
}
