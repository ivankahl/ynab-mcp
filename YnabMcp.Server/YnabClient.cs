using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YnabMcp.Server;

public sealed class YnabException : Exception
{
    public YnabException(string message) : base(message) { }
}

public sealed class YnabClient
{
    private const string BaseUrl = "https://api.ynab.com/v1/";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public YnabClient(HttpClient http, string? accessToken)
    {
        _http = http;
        _http.BaseAddress = new Uri(BaseUrl);
        if (!string.IsNullOrEmpty(accessToken))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
    }

    public async Task<List<BudgetSummary>> GetBudgetsAsync(CancellationToken ct = default)
    {
        var response = await GetAsync<YnabResponse<BudgetList>>("budgets", ct);
        return response.Data.Budgets;
    }

    public async Task<List<Payee>> SearchPayeesAsync(string budgetId, string? searchText, CancellationToken ct = default)
    {
        var response = await GetAsync<YnabResponse<PayeeList>>($"budgets/{budgetId}/payees", ct);
        return Filter(response.Data.Payees.Where(p => !p.Deleted && p.TransferAccountId is null), searchText, p => p.Name);
    }

    public async Task<List<Account>> SearchAccountsAsync(string budgetId, string? searchText, bool includeClosed = false, CancellationToken ct = default)
    {
        var response = await GetAsync<YnabResponse<AccountList>>($"budgets/{budgetId}/accounts", ct);
        return Filter(response.Data.Accounts.Where(a => includeClosed || (!a.Closed && !a.Deleted)), searchText, a => a.Name);
    }

    public async Task<List<Category>> SearchCategoriesAsync(string budgetId, string? searchText, bool includeHidden = false, CancellationToken ct = default)
    {
        var response = await GetAsync<YnabResponse<CategoryList>>($"budgets/{budgetId}/categories", ct);
        var categories = response.Data.CategoryGroups
            .SelectMany(g => g.Categories, (g, c) => { c.CategoryGroupName = g.Name; return c; })
            .Where(c => includeHidden || (!c.Hidden && !c.Deleted));
        return Filter(categories, searchText, c => $"{c.CategoryGroupName} {c.Name}");
    }

    public async Task<List<Transaction>> GetTransactionsAsync(string budgetId, DateTime? sinceDate = null, CancellationToken ct = default)
    {
        var path = $"budgets/{budgetId}/transactions";
        if (sinceDate.HasValue)
        {
            path += $"?since_date={sinceDate.Value:yyyy-MM-dd}";
        }

        var response = await GetAsync<YnabResponse<TransactionList>>(path, ct);
        return response.Data.Transactions ?? [];
    }

    public async Task<Transaction> GetTransactionAsync(string budgetId, string transactionId, CancellationToken ct = default)
    {
        var response = await GetAsync<YnabResponse<TransactionList>>($"budgets/{budgetId}/transactions/{transactionId}", ct);
        return response.Data.Transaction
            ?? throw new YnabException($"Transaction '{transactionId}' was not found.");
    }

    public async Task<Transaction> CreateTransactionAsync(
        string budgetId,
        string accountId,
        DateTime date,
        decimal amount,
        string? payeeId,
        string? payeeName,
        string? categoryId,
        string? memo,
        bool approved,
        string cleared,
        IReadOnlyList<SubTransactionDraft>? subtransactions = null,
        CancellationToken ct = default)
    {
        var draft = new TransactionDraft
        {
            AccountId = accountId,
            Date = date.ToString("yyyy-MM-dd"),
            Amount = ToMilliunits(amount),
            PayeeId = payeeId,
            PayeeName = payeeName,
            CategoryId = categoryId,
            Memo = memo,
            Approved = approved,
            Cleared = cleared,
            SubTransactions = subtransactions?.ToList(),
        };

        var response = await PostAsync<YnabResponse<TransactionList>>(
            $"budgets/{budgetId}/transactions",
            new CreateTransactionRequest { Transaction = draft },
            ct);

        return response.Data.Transaction ?? response.Data.Transactions?.FirstOrDefault()
            ?? throw new YnabException("YNAB did not return the created transaction.");
    }

    public async Task<List<Transaction>> CreateTransactionsAsync(string budgetId, IReadOnlyList<TransactionDraft> drafts, CancellationToken ct = default)
    {
        var created = new List<Transaction>();
        foreach (var chunk in drafts.Chunk(100))
        {
            var response = await PostAsync<YnabResponse<TransactionList>>(
                $"budgets/{budgetId}/transactions",
                new CreateTransactionsRequest { Transactions = chunk.ToList() },
                ct);
            created.AddRange(response.Data.Transactions ?? []);
        }

        return created;
    }

    public async Task<Transaction> UpdateTransactionAsync(string budgetId, string transactionId, TransactionDraft draft, CancellationToken ct = default)
    {
        var response = await PutAsync<YnabResponse<TransactionList>>(
            $"budgets/{budgetId}/transactions/{transactionId}",
            new UpdateTransactionRequest { Transaction = draft },
            ct);

        return response.Data.Transaction
            ?? throw new YnabException("YNAB did not return the updated transaction.");
    }

    public async Task<Transaction> DeleteTransactionAsync(string budgetId, string transactionId, CancellationToken ct = default)
    {
        try
        {
            var response = await DeleteAsync<YnabResponse<TransactionList>>(
                $"budgets/{budgetId}/transactions/{transactionId}",
                ct);

            return response.Data.Transaction
                ?? throw new YnabException($"Transaction '{transactionId}' was not found.");
        }
        catch (YnabException ex) when (ex.Message.Contains("404", StringComparison.OrdinalIgnoreCase))
        {
            throw new YnabException($"Transaction '{transactionId}' was not found.");
        }
    }

    public async Task<Month> GetMonthAsync(string budgetId, string month, CancellationToken ct = default)
    {
        var normalized = NormalizeMonth(month);
        var response = await GetAsync<YnabResponse<MonthWrapper>>($"budgets/{budgetId}/months/{normalized}", ct);
        return response.Data.Month;
    }

    internal static long ToMilliunits(decimal amount) =>
        (long)Math.Round(amount * 1000m, MidpointRounding.AwayFromZero);

    internal static string NormalizeMonth(string month)
    {
        if (string.Equals(month, "current", StringComparison.OrdinalIgnoreCase))
        {
            return "current";
        }

        if (DateOnly.TryParseExact(month, "yyyy-MM", out var parsed))
        {
            return parsed.ToString("yyyy-MM-01");
        }

        if (DateOnly.TryParse(month, out parsed))
        {
            return parsed.ToString("yyyy-MM-01");
        }

        throw new YnabException($"'{month}' is not a valid month. Use 'current', 'YYYY-MM' or a date.");
    }

    private static List<T> Filter<T>(IEnumerable<T> source, string? searchText, Func<T, string> textSelector)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return source.ToList();
        }

        var tokens = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return source
            .Where(item =>
            {
                var text = textSelector(item);
                return tokens.All(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(path, body, JsonOptions, ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task<T> PutAsync<T>(string path, object body, CancellationToken ct)
    {
        using var response = await _http.PutAsJsonAsync(path, body, JsonOptions, ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task<T> DeleteAsync<T>(string path, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync(path, ct);
        return await ReadAsync<T>(response, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new YnabException($"YNAB API request failed with {(int)response.StatusCode} {response.StatusCode}: {body}");
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct)
            ?? throw new YnabException("YNAB API returned an empty response.");
    }
}
