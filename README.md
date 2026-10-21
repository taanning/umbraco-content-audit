# Content Audit

A read-only Umbraco backoffice package for finding incomplete or matching content across document types, element types, pages and block instances.

The audit dashboard lets editors and developers:

- Select document types, element types, cultures and property columns.
- Filter results by type, culture, base fields and property values.
- Use validated simple filters or expressions without executing user SQL or JavaScript.
- Page through matching rows, inspect owners and open related content or type editors.
- Save audits as private or shared definitions.
- Pin saved audits to a personal dashboard with an icon and colour.
- Re-run saved audits without automatically refreshing every pinned tile.

All audit results are authorized as the current viewer. Saved definitions and dashboard preferences are stored in package-owned tables; CMS content is never modified.

## Filtering

Every column header in the results table has a filter button. Click it to open that column's filter panel — this works on the base columns (type, type name, content, culture, location) and on every property column.

The panel offers two modes:

- **Simple** - pick an operator and a value: has text, is empty, equals, not equals, contains, not contains, starts with, ends with, like, or the numeric comparisons `>`, `>=`, `<`, `<=`.
- **Expression** - one validated comparison written out, e.g. `= 'travel'`, `!= ''` (has a value), `> 80`, or `LIKE '%promo%'`. Quote text and date values; numbers and booleans stand unquoted.

Edits are staged inside the panel: nothing re-scans until Apply, Cancel discards them, and Clear filters removes every column filter at once. The Filter rows box narrows only the already loaded page, client-side.

## Package

Current package metadata:

- NuGet package ID: `Umbraco.Community.PropertyAuditTool`
- Umbraco package ID: `PropertyAuditTool`
- Display name: `Content Audit`

```bash
dotnet add package Umbraco.Community.PropertyAuditTool
```

## Repository layout

- `src/PropertyAuditTool` - package API, persistence, migrations and backoffice extension source.
- `src/PropertyAuditTool/Client` - Lit, TypeScript and UUI client source.
- `src/PropertyAuditTool.Tests` - backend tests.
- `src/PropertyAuditTool.TestSite` - local Umbraco test site with uSync fixture content.
- `docs/README_nuget.md` - NuGet package readme.
- `.github/workflows/release.yml` - tag-based build, test, pack and NuGet publish workflow.

## Local development

Requirements:

- .NET 10 SDK
- Node.js and npm
- Umbraco 17.x or 18.x

Run the local site:

```bash
cd src/PropertyAuditTool.TestSite
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile --urls http://localhost:5570
```

`appsettings.Development.json` is intentionally ignored because it contains local-only install settings. Copy `appsettings.Development.example.json` and set your own local values before running the test site.

## Build and test

```bash
dotnet test src/PropertyAuditTool.slnx
```

Client only:

```bash
cd src/PropertyAuditTool/Client
npm ci
npm run check
npm run test
npm run build
```

Create a package:

```bash
dotnet pack src/PropertyAuditTool/PropertyAuditTool.csproj -c Release -o artifacts/packages
```

Use `-p:SkipClientBuild=true` only when you intentionally want to skip the client build.

## Compatibility

The package targets .NET 10 and declares Umbraco dependencies in the range `[17.0.0, 19.0.0)`.

Verified behavior is tracked in the repository history and verification notes kept outside this public source set. Treat Umbraco 17.x and 18.x as the intended compatibility range rather than a blanket guarantee for every future minor version.

## Safety model

- Audit execution is read-only against CMS content.
- Shared audits still run with the current viewer's permissions.
- Retained result rows are viewer-owned, bounded and expire after inactivity.
- Property filters use a validated filter language.
- Run limits are surfaced explicitly instead of being presented as site-wide totals.
