# RuleManager

RuleManager is a Blazor-based platform for centrally managing QuickBooks Online bank-feed categorization rules across many client companies.

## Web 1.0 goals

- Client/company management
- Master rule library
- Reusable rule sets and templates
- Shared category management
- Client rule imports and QuickBooks-compatible exports
- Consistency checking, conflicts, and client overrides
- Audit history and multi-user administration

## Solution layout

- **RuleManager.Web** — Blazor web application
- **RuleManager.Core** — domain models and QuickBooks rule engine
- **RuleManager.Data** — Entity Framework Core / PostgreSQL data layer
- **RuleManager.Tests** — automated tests

Development work is currently taking place on `feature/web-foundation`.
