# Contributing Guidelines

Contributions are welcome.

## Development setup

- Use .NET 10 and a recent Node.js LTS release.
- Run `dotnet test src/PropertyAuditTool.slnx` for backend and client build verification.
- Run `npm ci` in `src/PropertyAuditTool/Client` before client-only work.
- `src/PropertyAuditTool.TestSite` is a local Umbraco test site with uSync fixture content.
- `appsettings.Development.json` is ignored. Copy `appsettings.Development.example.json` and set local-only values before running the site.

## Rules

- Keep audit execution read-only against CMS content.
- Always authorize results as the current viewer.
- Do not execute user-supplied SQL or JavaScript.
- Keep persisted audit definitions and dashboard preferences in package-owned tables.
- Do not commit generated build output, local databases, agent tooling or secrets.
