# Content Audit

[![Downloads](https://img.shields.io/nuget/dt/Umbraco.Community.PropertyAuditTool?color=cc9900)](https://www.nuget.org/packages/Umbraco.Community.PropertyAuditTool/)
[![NuGet](https://img.shields.io/nuget/vpre/Umbraco.Community.PropertyAuditTool?color=0273B3)](https://www.nuget.org/packages/Umbraco.Community.PropertyAuditTool)
[![GitHub license](https://img.shields.io/github/license/taanning/umbraco-content-audit?color=8AB803)](https://github.com/taanning/umbraco-content-audit/blob/main/LICENSE)

A read-only Umbraco backoffice package for finding incomplete or matching content across document types, element types, pages and block instances.

The Content section dashboard supports:

- document type and element type selection,
- culture selection where available,
- selected property columns,
- base-field and property-value filters,
- simple filters and validated expressions,
- paged results,
- private or shared saved audits,
- personal pinned audit tiles,
- direct links to matching content and type editors.

## Filtering

Every column header in the results table carries a filter button —
click it to open that column's filter panel. This works on the base
columns (type, type name, content, culture, location) and on every
property column. A column with an active filter shows a filled icon.

The panel offers two modes:

- **Simple** — pick an operator and value: has text, is empty, equals,
  not equals, contains, not contains, starts with, ends with, like,
  or the numeric comparisons `>`, `>=`, `<`, `<=`.
- **Expression** — one validated comparison written out, e.g.
  `= 'travel'`, `!= ''` (has a value), `> 80`, or `LIKE '%promo%'`.
  Quote text and date values; numbers and booleans stand unquoted.
  Escape an embedded quote by doubling it.

Edits are staged inside the panel: nothing re-scans until you press
Apply, and Cancel discards them. **Clear filters** removes every
column filter at once (after confirmation). The **Filter rows** box
next to the result actions is different — it narrows only the already
loaded page client-side and never re-scans.

## Permissions and safety

- Requires backoffice authentication and Content section access.
- Every audit is authorized as the current viewer, including shared audits.
- Content permissions, language permissions and property read permissions are rechecked when retained results are requested.
- Audit definitions and personal dashboard preferences use package-owned database tables.
- CMS content is never modified.
- Filter input is validated and never executed as SQL or JavaScript.
- Retained run data is viewer-owned, bounded and expires after inactivity.

## Compatibility

The package targets .NET 10 and Umbraco 17.x or 18.x, with Umbraco dependencies declared as `[17.0.0, 19.0.0)`. Treat new Umbraco minor versions as intended support until verified against the current release.

## Source

- Documentation and source: https://github.com/taanning/umbraco-content-audit
- Issues: https://github.com/taanning/umbraco-content-audit/issues
