# YNAB MCP Server

A C# MCP (Model Context Protocol) server for [YNAB](https://www.ynab.com), served over Streamable HTTP using the official [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk).

## Tools

| Tool | Description |
|---|---|
| `list_budgets` | Lists all budgets with their ids and names |
| `search_payees` | Free text search over payees in a budget |
| `search_accounts` | Free text search over accounts (balances included) |
| `search_categories` | Free text search over categories (envelopes) |
| `create_transaction` | Creates a transaction (amount in currency units, negative = outflow) |
| `get_budget_month` | Budget + envelope amounts for any month (budgeted / activity / balance per category, income, to be budgeted) |
| `search_transactions` | Search transactions by payee, envelope, account, amount, memo, cleared status and approved state — any combination |
| `import_ofx_statement` | Imports OFX bank statement content into an account, matching against uncleared transactions and creating the rest as new unapproved transactions |

## Setup

1. Create a YNAB personal access token at https://app.ynab.com/settings/developer
2. Set it via the `YNAB_ACCESS_TOKEN` environment variable (or the `YNAB_ACCESS_TOKEN` key in `appsettings.json`)

## Run

```bash
cd YnabMcp.Server
dotnet run
```

By default (from `Properties/launchSettings.json`) it listens on `http://localhost:5198`. The MCP endpoint is:

```
http://localhost:5198/mcp
```

You can override the URL:

```bash
YNAB_ACCESS_TOKEN=your-token dotnet run --urls http://127.0.0.1:3001
```

### Docker

```bash
docker build -t ynab-mcp .
docker run --rm -p 8080:8080 -e YNAB_ACCESS_TOKEN=your-token ynab-mcp
```

The MCP endpoint inside the container is `http://localhost:8080/mcp`.

## Client configuration

Point any Streamable HTTP MCP client at the `/mcp` endpoint. Example for clients that accept JSON config:

```json
{
  "mcpServers": {
    "ynab": {
      "type": "http",
      "url": "http://localhost:5198/mcp"
    }
  }
}
```

## Notes

- Budgets can be referenced by id, `last-used`, or `default`.
- Amounts returned by tools are in currency units. `create_transaction` also takes currency units (e.g. `-25.50` for a $25.50 expense); conversion to YNAB milliunits happens automatically.
- Months are accepted as `current`, `YYYY-MM`, or a full date (normalized to the first of the month).
- Search tools match items whose names contain all whitespace-separated words of the query, case-insensitively.
- `search_transactions` filters are combined with AND; each filter is optional. Payee/category/account accept an id or a name fragment; amount is exact in currency units (negative = outflow).
- `import_ofx_statement` accepts raw OFX 1.x (SGML) or OFX 2.x (XML) content. OFX entries are matched to uncleared transactions in the account with the same amount and a date within `matchDateToleranceDays` (default 5); unmatched entries are created as uncleared, unapproved transactions with `YNAB:amount:date:occurrence` import ids so re-importing the same file won't duplicate. Use `dryRun=true` to preview.
