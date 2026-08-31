using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace YnabMcp.Server;

[McpServerToolType]
public static class YnabTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerTool]
    [Description("Lists all YNAB budgets. Returns each budget's id and name. Use the budget id in other tools, or 'last-used' / 'default' where supported.")]
    public static string ListBudgets(YnabClient ynab, CancellationToken cancellationToken)
    {
        return Execute(() =>
        {
            var budgets = ynab.GetBudgetsAsync(cancellationToken).GetAwaiter().GetResult();
            return JsonSerializer.Serialize(new
            {
                count = budgets.Count,
                budgets = budgets.Select(b => new { id = b.Id, name = b.Name, lastModifiedOn = b.LastModifiedOn }),
            }, JsonOptions);
        });
    }

    [McpServerTool]
    [Description("Searches payees in a budget using a free text query. Matches payee names containing all words of the query (case-insensitive). Returns payee id, name, and the id of the source account for transfer payees.")]
    public static string SearchPayees(
        YnabClient ynab,
        [Description("The budget id (from list_budgets), 'last-used' or 'default'")] string budgetId,
        [Description("Free text search query; empty or omitted returns all payees")] string? searchText = null,
        CancellationToken cancellationToken = default)
    {
        return Execute(() =>
        {
            var payees = ynab.SearchPayeesAsync(budgetId, searchText, cancellationToken).GetAwaiter().GetResult();
            return JsonSerializer.Serialize(new
            {
                count = payees.Count,
                payees = payees.Select(p => new { id = p.Id, name = p.Name, transferAccountId = p.TransferAccountId }),
            }, JsonOptions);
        });
    }

    [McpServerTool]
    [Description("Searches accounts in a budget using a free text query. Matches account names containing all words of the query (case-insensitive). Returns account id, name, type, and balance in currency units.")]
    public static string SearchAccounts(
        YnabClient ynab,
        [Description("The budget id (from list_budgets), 'last-used' or 'default'")] string budgetId,
        [Description("Free text search query; empty or omitted returns all accounts")] string? searchText = null,
        [Description("Include closed accounts (default false)")] bool includeClosed = false,
        CancellationToken cancellationToken = default)
    {
        return Execute(() =>
        {
            var accounts = ynab.SearchAccountsAsync(budgetId, searchText, includeClosed, cancellationToken).GetAwaiter().GetResult();
            return JsonSerializer.Serialize(new
            {
                count = accounts.Count,
                accounts = accounts.Select(a => new
                {
                    id = a.Id,
                    name = a.Name,
                    type = a.Type,
                    onBudget = a.OnBudget,
                    closed = a.Closed,
                    balance = FromMilliunits(a.Balance),
                    clearedBalance = FromMilliunits(a.ClearedBalance),
                    unclearedBalance = FromMilliunits(a.UnclearedBalance),
                }),
            }, JsonOptions);
        });
    }

    [McpServerTool]
    [Description("Searches categories (envelopes) in a budget using a free text query. Matches category group and category names containing all words of the query (case-insensitive). Amounts are for the current month in currency units.")]
    public static string SearchCategories(
        YnabClient ynab,
        [Description("The budget id (from list_budgets), 'last-used' or 'default'")] string budgetId,
        [Description("Free text search query; empty or omitted returns all categories")] string? searchText = null,
        [Description("Include hidden categories (default false)")] bool includeHidden = false,
        CancellationToken cancellationToken = default)
    {
        return Execute(() =>
        {
            var categories = ynab.SearchCategoriesAsync(budgetId, searchText, includeHidden, cancellationToken).GetAwaiter().GetResult();
            return JsonSerializer.Serialize(new
            {
                count = categories.Count,
                categories = categories.Select(c => new
                {
                    id = c.Id,
                    name = c.Name,
                    categoryGroup = c.CategoryGroupName,
                    hidden = c.Hidden,
                    budgeted = FromMilliunits(c.Budgeted),
                    activity = FromMilliunits(c.Activity),
                    balance = FromMilliunits(c.Balance),
                    goalType = c.GoalType,
                }),
            }, JsonOptions);
        });
    }

    [McpServerTool]
    [Description("Creates a transaction in a budget. Amounts are in currency units: negative = outflow/expense, positive = inflow (e.g. -25.50 for a $25.50 expense). Provide either payeeId or payeeName. categoryId is required for on-budget expenses (an envelope category id from search_categories); it is optional for inflows and transfers. SPLIT TRANSACTIONS: to divide one transaction across categories, pass splits — an array of objects, one per split line: {\"categoryId\": \"<guid>\", \"amount\": <number>, \"payeeId\"?: \"<guid>\", \"payeeName\"?: \"<string>\", \"memo\"?: \"<string>\"}. Rules: (1) every split line needs a categoryId (usually an envelope category from search_categories; YNAB silently files an unknown id as Uncategorized) and a non-zero amount with the same sign convention as the parent; (2) the parent amount is still required and must equal the exact sum of the split amounts or the call fails; (3) the parent categoryId is ignored/cleared and YNAB shows the transaction as 'Split'.")]
    public static string CreateTransaction(
        YnabClient ynab,
        [Description("The budget id (from list_budgets), 'last-used' or 'default'")] string budgetId,
        [Description("The account id to create the transaction in (from search_accounts)")] string accountId,
        [Description("Transaction date in YYYY-MM-DD format")] DateTime date,
        [Description("Amount in currency units; negative = outflow/expense, positive = inflow. When splits are given, must equal the exact sum of the split amounts")] decimal amount,
        [Description("Payee id (from search_payees). Optional if payeeName is given")] string? payeeId = null,
        [Description("Payee name. Used when there is no payee id")] string? payeeName = null,
        [Description("Category id (from search_categories). Optional for inflows and transfers; ignored when splits are provided")] string? categoryId = null,
        [Description("Transaction memo. Optional")] string? memo = null,
        [Description("Whether the transaction is approved (default true)")] bool approved = true,
        [Description("Cleared status: 'cleared', 'uncleared' or 'reconciled' (default 'uncleared')")] string cleared = "uncleared",
        [Description("Split lines making this a split transaction: an array of objects, each {\"categoryId\": \"<envelope category guid from search_categories>\" (required), \"amount\": <currency units, negative = outflow, positive = inflow; required and non-zero>, \"payeeId\": \"<optional payee guid>\", \"payeeName\": \"<optional payee name>\", \"memo\": \"<optional memo>\"}. The split amounts must sum exactly to the parent amount, and the parent categoryId is ignored when splits are provided.")] IReadOnlyList<SplitAllocation>? splits = null,
        CancellationToken cancellationToken = default)
    {
        return Execute(() =>
        {
            List<SubTransactionDraft>? subtransactions = null;
            if (splits is { Count: > 0 })
            {
                var (error, built) = BuildSubTransactions(splits, amount);
                if (error is not null)
                {
                    return JsonSerializer.Serialize(new { error = error }, JsonOptions);
                }

                subtransactions = built;
                categoryId = null;
            }

            var transaction = ynab.CreateTransactionAsync(
                budgetId, accountId, date, amount, payeeId, payeeName, categoryId, memo, approved, cleared, subtransactions,
                cancellationToken).GetAwaiter().GetResult();

            return JsonSerializer.Serialize(new
            {
                created = true,
                transaction = ProjectTransaction(transaction),
            }, JsonOptions);
        });
    }

    [McpServerTool]
    [Description("Deletes (soft-deletes) a transaction in a budget. Returns the deleted transaction details including its id, date, amount and subtransactions if it was a split.")]
    public static string DeleteTransaction(
        YnabClient ynab,
        [Description("The budget id (from list_budgets), 'last-used' or 'default'")] string budgetId,
        [Description("The transaction id to delete")] string transactionId,
        CancellationToken cancellationToken)
    {
        return Execute(() =>
        {
            var transaction = ynab.DeleteTransactionAsync(budgetId, transactionId, cancellationToken).GetAwaiter().GetResult();
            return JsonSerializer.Serialize(new
            {
                deleted = true,
                transactionId = transaction.Id,
                transaction = ProjectTransaction(transaction),
            }, JsonOptions);
        });
    }

    [McpServerTool]
    [Description("Updates an existing transaction in a budget. Only the provided fields are changed. Amounts are in currency units: negative = outflow/expense, positive = inflow. To convert a non-split transaction into a split, pass splits — an array of {\"categoryId\": \"<guid>\", \"amount\": <number>, \"payeeId\"?: \"<guid>\", \"payeeName\"?: \"<string>\", \"memo\"?: \"<string>\"} objects, same format as create_transaction: every split line needs a categoryId (from search_categories) and a non-zero amount, and the split amounts must sum to the transaction amount (if amount is omitted it is set to the splits sum). The parent categoryId is cleared when converting. Updating the splits of an already-split transaction is not supported by YNAB and returns an error — delete and recreate the transaction to change existing split allocations. YNAB ignores attempts to change the parent date or amount on a split transaction.")]
    public static string UpdateTransaction(
        YnabClient ynab,
        [Description("The budget id (from list_budgets), 'last-used' or 'default'")] string budgetId,
        [Description("The transaction id to update")] string transactionId,
        [Description("The account id to move the transaction to (optional)")] string? accountId = null,
        [Description("New transaction date (YYYY-MM-DD). Ignored by YNAB if the transaction is a split.")] DateTime? date = null,
        [Description("New amount in currency units (negative = outflow, positive = inflow). Ignored by YNAB if the transaction is a split. When converting to a split, must equal the sum of the splits; omit to use the splits sum.")] decimal? amount = null,
        [Description("New payee id (optional)")] string? payeeId = null,
        [Description("New payee name (optional)")] string? payeeName = null,
        [Description("New category id (optional). Ignored when splits are provided.")] string? categoryId = null,
        [Description("New memo (optional)")] string? memo = null,
        [Description("New approved state (optional)")] bool? approved = null,
        [Description("New cleared status: 'cleared', 'uncleared' or 'reconciled' (optional)")] string? cleared = null,
        [Description("Split lines to convert this transaction into a split: an array of objects, each {\"categoryId\": \"<envelope category guid from search_categories>\" (required), \"amount\": <currency units, negative = outflow, positive = inflow; required and non-zero>, \"payeeId\": \"<optional payee guid>\", \"payeeName\": \"<optional payee name>\", \"memo\": \"<optional memo>\"}. Not allowed if the transaction is already a split — delete and recreate it to change split allocations.")] IReadOnlyList<SplitAllocation>? splits = null,
        CancellationToken cancellationToken = default)
    {
        return Execute(() =>
        {
            if (cleared is not null && !new[] { "cleared", "uncleared", "reconciled" }.Contains(cleared, StringComparer.OrdinalIgnoreCase))
            {
                return JsonSerializer.Serialize(new { error = "cleared must be 'cleared', 'uncleared' or 'reconciled'." }, JsonOptions);
            }

            var existing = ynab.GetTransactionAsync(budgetId, transactionId, cancellationToken).GetAwaiter().GetResult();
            var isSplit = existing.SubTransactions is { Count: > 0 };

            if (splits is { Count: > 0 } && isSplit)
            {
                return JsonSerializer.Serialize(new
                {
                    error = "Splits of an existing split transaction cannot be updated by the YNAB API. Update the individual fields, or delete and recreate the transaction to change its split allocations.",
                }, JsonOptions);
            }

            List<SubTransactionDraft>? subtransactions = null;
            if (splits is { Count: > 0 })
            {
                var (error, built) = BuildSubTransactions(splits, amount);
                if (error is not null)
                {
                    return JsonSerializer.Serialize(new { error = error }, JsonOptions);
                }

                subtransactions = built;
                categoryId = null;
                amount = built.Sum(s => s.Amount) / 1000m; // SubTransactionDraft.Amount is already in milliunits; parent amount = exact milliunit sum of the splits
            }

            var draft = new TransactionDraft
            {
                AccountId = accountId,
                Date = date?.ToString("yyyy-MM-dd"),
                Amount = amount.HasValue ? YnabClient.ToMilliunits(amount.Value) : null,
                PayeeId = payeeId,
                PayeeName = payeeName,
                CategoryId = categoryId,
                Memo = memo,
                Approved = approved,
                Cleared = cleared,
                SubTransactions = subtransactions,
            };

            var transaction = ynab.UpdateTransactionAsync(budgetId, transactionId, draft, cancellationToken).GetAwaiter().GetResult();
            return JsonSerializer.Serialize(new
            {
                updated = true,
                transaction = ProjectTransaction(transaction),
            }, JsonOptions);
        });
    }

    [McpServerTool]
    [Description("Gets budget and envelope (category) amounts for a specific month: total budgeted, total activity, income, 'to be budgeted', and every category's budgeted, activity and balance for that month in currency units.")]
    public static string GetBudgetMonth(
        YnabClient ynab,
        [Description("The budget id (from list_budgets), 'last-used' or 'default'")] string budgetId,
        [Description("The month: 'current', 'YYYY-MM' or a date (e.g. '2026-03-15')")] string month,
        CancellationToken cancellationToken)
    {
        return Execute(() =>
        {
            var monthData = ynab.GetMonthAsync(budgetId, month, cancellationToken).GetAwaiter().GetResult();
            return JsonSerializer.Serialize(new
            {
                month = monthData.Name,
                note = monthData.Note,
                ageOfMoney = monthData.AgeOfMoney,
                income = FromMilliunits(monthData.Income),
                budgeted = FromMilliunits(monthData.Budgeted),
                activity = FromMilliunits(monthData.Activity),
                toBeBudgeted = FromMilliunits(monthData.ToBeBudgeted),
                categories = monthData.Categories
                    .Where(c => !c.Deleted && !c.Hidden)
                    .Select(c => new
                    {
                        id = c.Id,
                        name = c.Name,
                        categoryGroup = c.CategoryGroupName,
                        budgeted = FromMilliunits(c.Budgeted),
                        activity = FromMilliunits(c.Activity),
                        balance = FromMilliunits(c.Balance),
                    }),
            }, JsonOptions);
        });
    }

    [McpServerTool]
    [Description("Searches transactions in a budget. All filters are optional and combined with AND (omit a filter to ignore it). Payee, category (envelope) and account match by id or by name containing the text (case-insensitive). Amount is in currency units, negative for outflow. Memo matches as free text. Cleared must be 'cleared', 'uncleared' or 'reconciled'.")]
    public static string SearchTransactions(
        YnabClient ynab,
        [Description("The budget id (from list_budgets), 'last-used' or 'default'")] string budgetId,
        [Description("Payee id or name text")] string? payee = null,
        [Description("Category (envelope) id or name text")] string? category = null,
        [Description("Account id or name text")] string? account = null,
        [Description("Exact amount in currency units, negative for outflow")] decimal? amount = null,
        [Description("Memo free text (case-insensitive contains)")] string? memo = null,
        [Description("'cleared', 'uncleared' or 'reconciled'")] string? cleared = null,
        [Description("Filter by approved state")] bool? approved = null,
        [Description("Only include transactions on or after this date (YYYY-MM-DD)")] DateTime? sinceDate = null,
        [Description("Maximum number of results, newest first (default 100)")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        return Execute(() =>
        {
            if (cleared is not null && !new[] { "cleared", "uncleared", "reconciled" }.Contains(cleared, StringComparer.OrdinalIgnoreCase))
            {
                return JsonSerializer.Serialize(new { error = "cleared must be 'cleared', 'uncleared' or 'reconciled'." }, JsonOptions);
            }

            if (limit is < 1 or > 500)
            {
                return JsonSerializer.Serialize(new { error = "limit must be between 1 and 500." }, JsonOptions);
            }

            var transactions = ynab.GetTransactionsAsync(budgetId, sinceDate, cancellationToken).GetAwaiter().GetResult();

            long? amountMilli = amount.HasValue ? YnabClient.ToMilliunits(amount.Value) : null;
            var results = transactions
                .Where(t => !t.Deleted)
                .Where(t => MatchesIdOrName(payee, t.PayeeId, t.PayeeName))
                .Where(t => MatchesIdOrName(category, t.CategoryId, t.CategoryName))
                .Where(t => MatchesIdOrName(account, t.AccountId, t.AccountName))
                .Where(t => !amountMilli.HasValue || t.Amount == amountMilli.Value)
                .Where(t => memo is null || (t.Memo?.Contains(memo, StringComparison.OrdinalIgnoreCase) ?? false))
                .Where(t => cleared is null || string.Equals(t.Cleared, cleared, StringComparison.OrdinalIgnoreCase))
                .Where(t => !approved.HasValue || t.Approved == approved.Value)
                .OrderByDescending(t => t.Date)
                .ThenByDescending(t => t.Amount)
                .Take(limit)
                .ToList();

            return JsonSerializer.Serialize(new
            {
                count = results.Count,
                transactions = results.Select(t => new
                {
                    id = t.Id,
                    date = t.Date,
                    amount = FromMilliunits(t.Amount),
                    accountName = t.AccountName,
                    payeeName = t.PayeeName,
                    categoryName = t.CategoryName,
                    cleared = t.Cleared,
                    approved = t.Approved,
                    memo = t.Memo,
                    importId = t.ImportId,
                }),
            }, JsonOptions);
        });
    }

    [McpServerTool]
    [Description("Imports bank statement transactions from an OFX file (OFX 1.x SGML or OFX 2.x XML) into a YNAB account, mirroring YNAB's own file-based import. Already-present transactions are never duplicated: they are skipped by YNAB import id (YNAB:{amount}:{date}:{n}) regardless of cleared/approved state, and otherwise matched to a manually-entered transaction with the exact same amount and a date within matchDateToleranceDays (YNAB uses 10, default 10). Only genuinely new transactions are created, as uncleared, unapproved entries with YNAB import ids so re-importing the same file won't duplicate. Use dryRun=true to preview without writing.")]
    public static string ImportOfxStatement(
        YnabClient ynab,
        [Description("The budget id (from list_budgets), 'last-used' or 'default'")] string budgetId,
        [Description("The account id to import into (from search_accounts)")] string accountId,
        [Description("The raw OFX file content")] string ofxContent,
        [Description("Days of date tolerance when matching manually-entered transactions (YNAB uses 10; default 10)")] int matchDateToleranceDays = 10,
        [Description("Preview the import without creating any transactions")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        return Execute(() =>
        {
            var ofxTransactions = OfxParser.Parse(ofxContent);
            if (ofxTransactions.Count == 0)
            {
                return JsonSerializer.Serialize(new { error = "No STMTTRN (statement transaction) entries found in the OFX content." }, JsonOptions);
            }

            if (matchDateToleranceDays is < 0 or > 30)
            {
                return JsonSerializer.Serialize(new { error = "matchDateToleranceDays must be between 0 and 30." }, JsonOptions);
            }

            var accounts = ynab.SearchAccountsAsync(budgetId, null, includeClosed: true, cancellationToken).GetAwaiter().GetResult();
            var account = accounts.FirstOrDefault(a => string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
            if (account is null)
            {
                return JsonSerializer.Serialize(new { error = $"Account '{accountId}' was not found in this budget. Use search_accounts to find valid account ids." }, JsonOptions);
            }

            var earliestOfxDate = ofxTransactions.Min(t => t.Date);
            var sinceDate = earliestOfxDate.AddDays(-matchDateToleranceDays - 1).ToDateTime(TimeOnly.MinValue);
            var existing = ynab.GetTransactionsAsync(budgetId, sinceDate, cancellationToken).GetAwaiter().GetResult()
                .Where(t => !t.Deleted)
                .Where(t => string.Equals(t.AccountId, account.Id, StringComparison.OrdinalIgnoreCase))
                .Select(t => new ImportMatchTarget(
                    Id: t.Id,
                    Date: DateOnly.Parse(t.Date),
                    AmountMilli: t.Amount,
                    ImportId: t.ImportId,
                    PayeeName: t.PayeeName,
                    Memo: t.Memo))
                .ToList();

            var plan = ImportMatcher.Plan(ofxTransactions, existing, matchDateToleranceDays,
                t => new ImportRow(t.Date, t.Amount, t.Payee, t.Memo, t.TransactionType, t.FitId, null));

            List<Transaction>? created = null;
            if (!dryRun && plan.ToCreate.Count > 0)
            {
                var drafts = plan.ToCreate.Select(x => new TransactionDraft
                {
                    AccountId = account.Id,
                    Date = x.Date.ToString("yyyy-MM-dd"),
                    Amount = x.AmountMilli,
                    PayeeName = x.Payee,
                    Memo = x.Memo,
                    Approved = false,
                    Cleared = "uncleared",
                    ImportId = x.ImportId,
                }).ToList();
                created = ynab.CreateTransactionsAsync(budgetId, drafts, cancellationToken).GetAwaiter().GetResult();
            }

            return JsonSerializer.Serialize(new
            {
                account = new { id = account.Id, name = account.Name },
                dryRun = dryRun,
                totalInFile = ofxTransactions.Count,
                alreadyImportedCount = plan.AlreadyImported.Count,
                matchedCount = plan.Matched.Count,
                createdCount = created?.Count ?? plan.ToCreate.Count,
                alreadyImported = plan.AlreadyImported,
                matchedTransactions = plan.Matched,
                createdTransactions = (created ?? []).Select(t => new
                {
                    id = t.Id,
                    date = t.Date,
                    amount = FromMilliunits(t.Amount),
                    payeeName = t.PayeeName,
                    memo = t.Memo,
                    importId = t.ImportId,
                }),
                wouldCreateTransactions = created is null
                    ? plan.ToCreate.Select(x => new
                    {
                        date = x.Date.ToString("yyyy-MM-dd"),
                        amount = x.Amount,
                        payee = x.Payee,
                        memo = x.Memo,
                        importId = x.ImportId,
                    })
                    : null,
            }, JsonOptions);
        });
    }

    [McpServerTool]
    [Description("Imports a Discovery Miles statement (XLSX or CSV) into a YNAB account, mirroring YNAB's own file-based import. Statement amounts are Discovery Miles and are converted to Rand by dividing by milesPerRand (default 10, i.e. 100 miles = R10.00); negative miles become outflows. Already-present rows are never duplicated: they are skipped by YNAB import id (YNAB:{amount}:{date}:{n}) regardless of cleared/approved state, and otherwise matched to a manually-entered transaction with the exact same Rand amount and a date within matchDateToleranceDays (YNAB uses 10, default 10). Only genuinely new rows are created as uncleared, unapproved transactions with YNAB import ids so re-importing the same statement won't duplicate. Rows with 0 miles are skipped unless includeZeroMiles=true. Payee comes from the Description column (falling back to Type) and the memo joins Description and Additional Information. Provide the statement via filePath (xlsx/csv on the server machine), csvContent (raw CSV text) or fileContentBase64 (base64-encoded xlsx/csv). Use dryRun=true to preview without writing.")]
    public static string ImportDiscoveryMilesStatement(
        YnabClient ynab,
        [Description("The budget id (from list_budgets), 'last-used' or 'default'")] string budgetId,
        [Description("The account id to import into (from search_accounts)")] string accountId,
        [Description("Path to the .xlsx or .csv statement file on the server machine")] string? filePath = null,
        [Description("Raw CSV statement content")] string? csvContent = null,
        [Description("Base64-encoded .xlsx or .csv statement content")] string? fileContentBase64 = null,
        [Description("Miles per Rand used to convert statement amounts (default 10: 100 miles = R10.00)")] decimal milesPerRand = 10m,
        [Description("Days of date tolerance when matching manually-entered transactions (YNAB uses 10; default 10)")] int matchDateToleranceDays = 10,
        [Description("Also import rows with 0 miles (default false)")] bool includeZeroMiles = false,
        [Description("Preview the import without creating any transactions")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        return Execute(() =>
        {
            if (milesPerRand <= 0)
            {
                return JsonSerializer.Serialize(new { error = "milesPerRand must be greater than 0." }, JsonOptions);
            }

            if (matchDateToleranceDays is < 0 or > 30)
            {
                return JsonSerializer.Serialize(new { error = "matchDateToleranceDays must be between 0 and 30." }, JsonOptions);
            }

            DiscoveryStatementParseResult parsed;
            try
            {
                parsed = DiscoveryMilesParser.Parse(filePath, csvContent, fileContentBase64, includeZeroMiles);
            }
            catch (Exception ex) when (ex is not YnabException)
            {
                return JsonSerializer.Serialize(new { error = $"Could not read the statement file: {ex.Message}" }, JsonOptions);
            }

            if (parsed.Rows.Count == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    error = "No transaction rows could be parsed from the statement. Expected a Discovery Miles export with 'Value Date' and 'Miles' columns.",
                    detectedColumns = new
                    {
                        date = parsed.DateColumn,
                        miles = parsed.MilesColumn,
                        description = parsed.DescriptionColumn,
                        additionalInformation = parsed.AdditionalInfoColumn,
                    },
                    skippedRows = parsed.SkippedRows.Take(10).ToList(),
                }, JsonOptions);
            }

            var accounts = ynab.SearchAccountsAsync(budgetId, null, includeClosed: true, cancellationToken).GetAwaiter().GetResult();
            var account = accounts.FirstOrDefault(a => string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
            if (account is null)
            {
                return JsonSerializer.Serialize(new { error = $"Account '{accountId}' was not found in this budget. Use search_accounts to find valid account ids." }, JsonOptions);
            }

            decimal RandAmount(DiscoveryStatementRow row) => Math.Round(row.Miles / milesPerRand, 2, MidpointRounding.AwayFromZero);

            var earliestStatementDate = parsed.Rows.Min(r => r.Date);
            var sinceDate = earliestStatementDate.AddDays(-matchDateToleranceDays - 1).ToDateTime(TimeOnly.MinValue);
            var existing = ynab.GetTransactionsAsync(budgetId, sinceDate, cancellationToken).GetAwaiter().GetResult()
                .Where(t => !t.Deleted)
                .Where(t => string.Equals(t.AccountId, account.Id, StringComparison.OrdinalIgnoreCase))
                .Select(t => new ImportMatchTarget(
                    Id: t.Id,
                    Date: DateOnly.Parse(t.Date),
                    AmountMilli: t.Amount,
                    ImportId: t.ImportId,
                    PayeeName: t.PayeeName,
                    Memo: t.Memo))
                .ToList();

            var plan = ImportMatcher.Plan(parsed.Rows, existing, matchDateToleranceDays,
                r => new ImportRow(r.Date, RandAmount(r), r.Payee, r.Memo, null, null, r.Miles));

            List<Transaction>? created = null;
            if (!dryRun && plan.ToCreate.Count > 0)
            {
                var drafts = plan.ToCreate.Select(x => new TransactionDraft
                {
                    AccountId = account.Id,
                    Date = x.Date.ToString("yyyy-MM-dd"),
                    Amount = x.AmountMilli,
                    PayeeName = x.Payee,
                    Memo = x.Memo,
                    Approved = false,
                    Cleared = "uncleared",
                    ImportId = x.ImportId,
                }).ToList();
                created = ynab.CreateTransactionsAsync(budgetId, drafts, cancellationToken).GetAwaiter().GetResult();
            }

            return JsonSerializer.Serialize(new
            {
                account = new { id = account.Id, name = account.Name },
                source = parsed.Source,
                dryRun,
                milesPerRand,
                totalInFile = parsed.Rows.Count,
                skippedCount = parsed.SkippedRows.Count,
                alreadyImportedCount = plan.AlreadyImported.Count,
                matchedCount = plan.Matched.Count,
                createdCount = created?.Count ?? plan.ToCreate.Count,
                detectedColumns = new
                {
                    date = parsed.DateColumn,
                    miles = parsed.MilesColumn,
                    description = parsed.DescriptionColumn,
                    additionalInformation = parsed.AdditionalInfoColumn,
                },
                alreadyImported = plan.AlreadyImported,
                matchedTransactions = plan.Matched,
                createdTransactions = (created ?? []).Select(t => new
                {
                    id = t.Id,
                    date = t.Date,
                    amount = FromMilliunits(t.Amount),
                    payeeName = t.PayeeName,
                    memo = t.Memo,
                    importId = t.ImportId,
                }),
                wouldCreateTransactions = created is null
                    ? plan.ToCreate.Select(x => new
                    {
                        date = x.Date.ToString("yyyy-MM-dd"),
                        miles = x.SourceValue,
                        amount = x.Amount,
                        payee = x.Payee,
                        memo = x.Memo,
                        importId = x.ImportId,
                    })
                    : null,
                skippedRows = parsed.SkippedRows.Take(20).ToList(),
            }, JsonOptions);
        });
    }

    private static bool MatchesIdOrName(string? filter, string? id, string? name) =>
        string.IsNullOrWhiteSpace(filter) ||
        (id is not null && string.Equals(filter, id, StringComparison.OrdinalIgnoreCase)) ||
        (name is not null && name.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private static (string? Error, List<SubTransactionDraft> SubTransactions) BuildSubTransactions(IReadOnlyList<SplitAllocation> splits, decimal? expectedAmount = null)
    {
        var subtransactions = new List<SubTransactionDraft>(splits.Count);
        var sum = 0m;

        for (var i = 0; i < splits.Count; i++)
        {
            var split = splits[i];
            if (split is null)
            {
                return ($"splits[{i}] is null; each split line must be an object like {{\"categoryId\": \"<guid from search_categories>\", \"amount\": -12.34}} with a categoryId and a non-zero amount.", []);
            }

            if (split.Amount == 0m)
            {
                return ($"splits[{i}] has amount 0; every split line needs a non-zero amount in currency units (negative = outflow, positive = inflow).", []);
            }

            if (string.IsNullOrWhiteSpace(split.CategoryId))
            {
                return ($"splits[{i}] is missing a categoryId; each split line needs a categoryId (an envelope category id from search_categories).", []);
            }

            sum += split.Amount;
            subtransactions.Add(new SubTransactionDraft
            {
                Amount = YnabClient.ToMilliunits(split.Amount),
                CategoryId = split.CategoryId,
                PayeeId = split.PayeeId,
                PayeeName = split.PayeeName,
                Memo = split.Memo,
            });
        }

        if (expectedAmount.HasValue && sum != expectedAmount.Value)
        {
            return ($"The splits sum to {sum:0.00#} but the transaction amount is {expectedAmount.Value:0.00#}; the parent amount must equal the exact sum of the split amounts. Fix the amount or the splits (on update_transaction you can omit amount to use the splits sum).", []);
        }

        return (null, subtransactions);
    }

    private static object ProjectTransaction(Transaction transaction) => new
    {
        id = transaction.Id,
        date = transaction.Date,
        amount = FromMilliunits(transaction.Amount),
        accountName = transaction.AccountName,
        payeeName = transaction.PayeeName,
        categoryName = transaction.CategoryName,
        cleared = transaction.Cleared,
        approved = transaction.Approved,
        memo = transaction.Memo,
        subtransactions = transaction.SubTransactions?.Select(s => new
        {
            id = s.Id,
            amount = FromMilliunits(s.Amount),
            categoryName = s.CategoryName,
            memo = s.Memo,
        }),
    };

    private static string Execute(Func<string> action)
    {
        try
        {
            return action();
        }
        catch (YnabException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    private static decimal FromMilliunits(long milliunits) => milliunits / 1000m;
}
