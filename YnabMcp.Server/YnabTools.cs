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
        [Description("Free text search query; empty returns all payees")] string? searchText,
        CancellationToken cancellationToken)
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
        [Description("Free text search query; empty returns all accounts")] string? searchText,
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
        [Description("Free text search query; empty returns all categories")] string? searchText,
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
    [Description("Creates a transaction in a budget. Amount is in currency units: positive for inflow, negative for outflow (e.g. -25.50 for a $25.50 expense). Provide either payeeId or payeeName. CategoryId is required for on-budget expenses and can be found with search_categories. To create a split transaction, pass splits; the parent categoryId is then ignored and the parent amount must equal the sum of the split allocations.")]
    public static string CreateTransaction(
        YnabClient ynab,
        [Description("The budget id (from list_budgets), 'last-used' or 'default'")] string budgetId,
        [Description("The account id to create the transaction in (from search_accounts)")] string accountId,
        [Description("Transaction date in YYYY-MM-DD format")] DateTime date,
        [Description("Amount in currency units; positive for inflow, negative for outflow")] decimal amount,
        [Description("Payee id (from search_payees). Optional if payeeName is given")] string? payeeId = null,
        [Description("Payee name. Used when there is no payee id")] string? payeeName = null,
        [Description("Category id (from search_categories). Optional for inflows and transfers; ignored when splits are provided")] string? categoryId = null,
        [Description("Transaction memo. Optional")] string? memo = null,
        [Description("Whether the transaction is approved (default true)")] bool approved = true,
        [Description("Cleared status: 'cleared', 'uncleared' or 'reconciled' (default 'uncleared')")] string cleared = "uncleared",
        [Description("Split allocations: each entry needs categoryId and amount (currency units). When provided the transaction becomes a split and categoryId on the parent is ignored.")] IReadOnlyList<SplitAllocation>? splits = null,
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
    [Description("Updates an existing transaction in a budget. Only the provided fields are changed. Amounts are in currency units. To convert a non-split transaction into a split, pass splits; this sets categoryId to null on the parent and creates subtransactions. Updating subtransactions on an already-split transaction is not supported by YNAB and will return an error. YNAB ignores attempts to change the parent date or amount on a split transaction.")]
    public static string UpdateTransaction(
        YnabClient ynab,
        [Description("The budget id (from list_budgets), 'last-used' or 'default'")] string budgetId,
        [Description("The transaction id to update")] string transactionId,
        [Description("The account id to move the transaction to (optional)")] string? accountId = null,
        [Description("New transaction date (YYYY-MM-DD). Ignored by YNAB if the transaction is a split.")] DateTime? date = null,
        [Description("New amount in currency units. Ignored by YNAB if the transaction is a split.")] decimal? amount = null,
        [Description("New payee id (optional)")] string? payeeId = null,
        [Description("New payee name (optional)")] string? payeeName = null,
        [Description("New category id (optional). Ignored when splits are provided.")] string? categoryId = null,
        [Description("New memo (optional)")] string? memo = null,
        [Description("New approved state (optional)")] bool? approved = null,
        [Description("New cleared status: 'cleared', 'uncleared' or 'reconciled' (optional)")] string? cleared = null,
        [Description("Split allocations to convert this transaction into a split. Not allowed if the transaction is already a split.")] IReadOnlyList<SplitAllocation>? splits = null,
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
                amount = built.Sum(s => YnabClient.ToMilliunits(s.Amount) / 1000m); // currency units sum
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
    [Description("Imports bank statement transactions from an OFX file (OFX 1.x SGML or OFX 2.x XML) into a YNAB account. Each OFX transaction is matched against existing uncleared transactions in the account with the same amount and a date within the tolerance window; matches are reported and not duplicated. All remaining transactions are created as new uncleared, unapproved transactions with YNAB import ids so future imports deduplicate. Use dryRun=true to preview without writing.")]
    public static string ImportOfxStatement(
        YnabClient ynab,
        [Description("The budget id (from list_budgets), 'last-used' or 'default'")] string budgetId,
        [Description("The account id to import into (from search_accounts)")] string accountId,
        [Description("The raw OFX file content")] string ofxContent,
        [Description("Days of date tolerance when matching against uncleared transactions (default 5)")] int matchDateToleranceDays = 5,
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
                .Where(t => string.Equals(t.Cleared, "uncleared", StringComparison.OrdinalIgnoreCase))
                .Select(t => (Transaction: t, Date: DateOnly.Parse(t.Date)))
                .ToList();

            var available = new List<(Transaction Transaction, DateOnly Date)>(existing);
            var matched = new List<object>();
            var toCreate = new List<OfxTransaction>();

            foreach (var ofx in ofxTransactions.OrderBy(t => t.Date).ThenBy(t => t.Amount))
            {
                var milli = YnabClient.ToMilliunits(ofx.Amount);
                var candidate = available
                    .Where(x => x.Transaction.Amount == milli && Math.Abs(x.Date.DayNumber - ofx.Date.DayNumber) <= matchDateToleranceDays)
                    .OrderBy(x => Math.Abs(x.Date.DayNumber - ofx.Date.DayNumber))
                    .ThenBy(x => x.Date)
                    .FirstOrDefault();

                if (candidate.Transaction is not null)
                {
                    available.Remove(candidate);
                    matched.Add(new
                    {
                        ofxDate = ofx.Date.ToString("yyyy-MM-dd"),
                        ofxAmount = ofx.Amount,
                        ofxPayee = ofx.Payee,
                        ofxMemo = ofx.Memo,
                        matchedTransactionId = candidate.Transaction.Id,
                        matchedDate = candidate.Transaction.Date,
                        matchedPayeeName = candidate.Transaction.PayeeName,
                        matchedMemo = candidate.Transaction.Memo,
                    });
                }
                else
                {
                    toCreate.Add(ofx);
                }
            }

            var drafts = toCreate
                .GroupBy(t => (t.Amount, t.Date))
                .SelectMany(g => g.Select((t, i) => (Txn: t, ImportId: $"YNAB:{YnabClient.ToMilliunits(t.Amount)}:{t.Date:yyyy-MM-dd}:{i + 1}")))
                .OrderBy(x => x.Txn.Date)
                .ToList();

            List<Transaction>? created = null;
            if (!dryRun && drafts.Count > 0)
            {
                created = ynab.CreateTransactionsAsync(budgetId, drafts.Select(x => new TransactionDraft
                {
                    AccountId = account.Id,
                    Date = x.Txn.Date.ToString("yyyy-MM-dd"),
                    Amount = YnabClient.ToMilliunits(x.Txn.Amount),
                    PayeeName = x.Txn.Payee,
                    Memo = x.Txn.Memo,
                    Approved = false,
                    Cleared = "uncleared",
                    ImportId = x.ImportId,
                }).ToList(), cancellationToken).GetAwaiter().GetResult();
            }

            return JsonSerializer.Serialize(new
            {
                account = new { id = account.Id, name = account.Name },
                dryRun = dryRun,
                totalInFile = ofxTransactions.Count,
                matchedCount = matched.Count,
                createdCount = created?.Count ?? drafts.Count,
                matchedTransactions = matched,
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
                    ? drafts.Select(x => new
                    {
                        date = x.Txn.Date.ToString("yyyy-MM-dd"),
                        amount = x.Txn.Amount,
                        payee = x.Txn.Payee,
                        memo = x.Txn.Memo,
                        importId = x.ImportId,
                    })
                    : null,
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

        foreach (var split in splits)
        {
            if (split.Amount == 0m)
            {
                return ("Each split allocation must have a non-zero amount.", []);
            }

            if (string.IsNullOrWhiteSpace(split.CategoryId))
            {
                return ("Each split allocation must have a categoryId.", []);
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
            return ($"Split allocations sum to {sum} but the transaction amount is {expectedAmount.Value}. Pass the correct total or omit the amount.", []);
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
