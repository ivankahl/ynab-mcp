using System.Text.Json.Serialization;

namespace YnabMcp.Server;

public sealed class YnabResponse<T>
{
    [JsonPropertyName("data")]
    public T Data { get; set; } = default!;
}

public sealed class BudgetList
{
    [JsonPropertyName("budgets")]
    public List<BudgetSummary> Budgets { get; set; } = [];
}

public sealed class BudgetSummary
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("last_modified_on")]
    public DateTime? LastModifiedOn { get; set; }

    [JsonPropertyName("date_format")]
    public DateFormat? DateFormat { get; set; }

    [JsonPropertyName("currency_format")]
    public CurrencyFormat? CurrencyFormat { get; set; }
}

public sealed class DateFormat
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;
}

public sealed class CurrencyFormat
{
    [JsonPropertyName("iso_code")]
    public string IsoCode { get; set; } = string.Empty;

    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = string.Empty;
}

public sealed class PayeeList
{
    [JsonPropertyName("payees")]
    public List<Payee> Payees { get; set; } = [];
}

public sealed class Payee
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("transfer_account_id")]
    public string? TransferAccountId { get; set; }

    [JsonPropertyName("deleted")]
    public bool Deleted { get; set; }
}

public sealed class AccountList
{
    [JsonPropertyName("accounts")]
    public List<Account> Accounts { get; set; } = [];
}

public sealed class Account
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("on_budget")]
    public bool OnBudget { get; set; }

    [JsonPropertyName("closed")]
    public bool Closed { get; set; }

    [JsonPropertyName("deleted")]
    public bool Deleted { get; set; }

    [JsonPropertyName("balance")]
    public long Balance { get; set; }

    [JsonPropertyName("cleared_balance")]
    public long ClearedBalance { get; set; }

    [JsonPropertyName("uncleared_balance")]
    public long UnclearedBalance { get; set; }
}

public sealed class CategoryList
{
    [JsonPropertyName("category_groups")]
    public List<CategoryGroup> CategoryGroups { get; set; } = [];
}

public sealed class CategoryGroup
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }

    [JsonPropertyName("deleted")]
    public bool Deleted { get; set; }

    [JsonPropertyName("categories")]
    public List<Category> Categories { get; set; } = [];
}

public sealed class Category
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("category_group_id")]
    public string CategoryGroupId { get; set; } = string.Empty;

    [JsonPropertyName("category_group_name")]
    public string? CategoryGroupName { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }

    [JsonPropertyName("deleted")]
    public bool Deleted { get; set; }

    [JsonPropertyName("budgeted")]
    public long Budgeted { get; set; }

    [JsonPropertyName("activity")]
    public long Activity { get; set; }

    [JsonPropertyName("balance")]
    public long Balance { get; set; }

    [JsonPropertyName("goal_type")]
    public string? GoalType { get; set; }
}

public sealed class TransactionList
{
    [JsonPropertyName("transaction_ids")]
    public List<string>? TransactionIds { get; set; }

    [JsonPropertyName("transaction")]
    public Transaction? Transaction { get; set; }

    [JsonPropertyName("transactions")]
    public List<Transaction>? Transactions { get; set; }

    [JsonPropertyName("duplicate_import_ids")]
    public List<string>? DuplicateImportIds { get; set; }
}

public sealed class Transaction
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public long Amount { get; set; }

    [JsonPropertyName("memo")]
    public string? Memo { get; set; }

    [JsonPropertyName("cleared")]
    public string? Cleared { get; set; }

    [JsonPropertyName("approved")]
    public bool Approved { get; set; }

    [JsonPropertyName("account_id")]
    public string AccountId { get; set; } = string.Empty;

    [JsonPropertyName("account_name")]
    public string? AccountName { get; set; }

    [JsonPropertyName("payee_id")]
    public string? PayeeId { get; set; }

    [JsonPropertyName("payee_name")]
    public string? PayeeName { get; set; }

    [JsonPropertyName("category_id")]
    public string? CategoryId { get; set; }

    [JsonPropertyName("category_name")]
    public string? CategoryName { get; set; }

    [JsonPropertyName("import_id")]
    public string? ImportId { get; set; }

    [JsonPropertyName("deleted")]
    public bool Deleted { get; set; }
}

public sealed class MonthWrapper
{
    [JsonPropertyName("month")]
    public Month Month { get; set; } = new();
}

public sealed class Month
{
    [JsonPropertyName("month")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("income")]
    public long Income { get; set; }

    [JsonPropertyName("budgeted")]
    public long Budgeted { get; set; }

    [JsonPropertyName("activity")]
    public long Activity { get; set; }

    [JsonPropertyName("to_be_budgeted")]
    public long ToBeBudgeted { get; set; }

    [JsonPropertyName("age_of_money")]
    public int? AgeOfMoney { get; set; }

    [JsonPropertyName("categories")]
    public List<Category> Categories { get; set; } = [];
}

public sealed class TransactionDraft
{
    [JsonPropertyName("account_id")]
    public string AccountId { get; set; } = string.Empty;

    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public long Amount { get; set; }

    [JsonPropertyName("payee_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PayeeId { get; set; }

    [JsonPropertyName("payee_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PayeeName { get; set; }

    [JsonPropertyName("category_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CategoryId { get; set; }

    [JsonPropertyName("memo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Memo { get; set; }

    [JsonPropertyName("cleared")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cleared { get; set; }

    [JsonPropertyName("approved")]
    public bool Approved { get; set; }

    [JsonPropertyName("import_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImportId { get; set; }
}

public sealed class CreateTransactionRequest
{
    [JsonPropertyName("transaction")]
    public TransactionDraft Transaction { get; set; } = new();
}

public sealed class CreateTransactionsRequest
{
    [JsonPropertyName("transactions")]
    public List<TransactionDraft> Transactions { get; set; } = [];
}
