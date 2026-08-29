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
| `import_discovery_miles_statement` | Imports a Discovery Miles XLSX/CSV statement into an account, converting Miles to Rand (÷ 10 by default), matching against uncleared transactions and creating the rest as new unapproved transactions |

## Setup

Create a YNAB personal access token at https://app.ynab.com/settings/developer. The token is **not** stored on the server — the client sends it with every request as an `Authorization: Bearer <token>` header.

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
dotnet run --urls http://127.0.0.1:3001
```

### Docker

```bash
docker build -t ynab-mcp .
docker run --rm -p 8080:8080 ynab-mcp
```

The MCP endpoint inside the container is `http://localhost:8080/mcp`. Send your YNAB token as the `Authorization: Bearer <token>` header on each request.

## Client configuration

Point any Streamable HTTP MCP client at the `/mcp` endpoint and send your YNAB token as the bearer header. Example for clients that accept JSON config with headers:

```json
{
  "mcpServers": {
    "ynab": {
      "type": "http",
      "url": "http://localhost:5198/mcp",
      "headers": {
        "Authorization": "Bearer your-ynab-token"
      }
    }
  }
}
```

The server is stateless: each request is authenticated with the token in that request's header, so multiple API keys can use the same server instance without sharing state. `last-used` / `default` budget references are resolved by YNAB per token, so they're never shared across keys.

### Authentication

Authentication is enforced at the HTTP boundary on `/mcp`:
- Missing or malformed `Authorization: Bearer` header → **401**
- Invalid token (rejected by YNAB) → **401**
- Valid token → **200** (MCP initialization and tools/list work)

Tokens are validated against the YNAB API; results are cached briefly by a SHA-256 hash of the token. The raw token is never stored on the server — it exists only for the duration of the request.

## Notes

- Budgets can be referenced by id, `last-used`, or `default`.
- Amounts returned by tools are in currency units. `create_transaction` also takes currency units (e.g. `-25.50` for a $25.50 expense); conversion to YNAB milliunits happens automatically.
- Months are accepted as `current`, `YYYY-MM`, or a full date (normalized to the first of the month).
- Search tools match items whose names contain all whitespace-separated words of the query, case-insensitively.
- `search_transactions` filters are combined with AND; each filter is optional. Payee/category/account accept an id or a name fragment; amount is exact in currency units (negative = outflow).
- `import_ofx_statement` accepts raw OFX 1.x (SGML) or OFX 2.x (XML) content. OFX entries are matched to uncleared transactions in the account with the same amount and a date within `matchDateToleranceDays` (default 5); unmatched entries are created as uncleared, unapproved transactions with `YNAB:amount:date:occurrence` import ids so re-importing the same file won't duplicate. Use `dryRun=true` to preview.
- `import_discovery_miles_statement` accepts a Discovery Miles statement as a file path (`filePath`, .xlsx or .csv on the server machine), raw CSV text (`csvContent`) or base64 content (`fileContentBase64`). Statement amounts are Miles and are divided by `milesPerRand` (default 10, i.e. 100 miles = R10.00) to get Rand values; negative miles (redemptions) become outflows. Columns are detected from the headers (`Value Date`, `Type`, `Description`, `Additional Information`, `Miles`); the payee comes from Description (falling back to Type) and the memo joins Description and Additional Information. Rows are matched to uncleared transactions in the account with the same Rand amount within `matchDateToleranceDays` (default 5); unmatched rows are created uncleared/unapproved with `YNAB:amount:date:occurrence` import ids. Rows with 0 miles are skipped unless `includeZeroMiles=true`. Use `dryRun=true` to preview.
