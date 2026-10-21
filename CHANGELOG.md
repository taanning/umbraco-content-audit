# Changelog

All notable changes to Content Audit are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.4.0] - Unreleased

### Added
- NuGet package icon (also shown on the Umbraco Marketplace listing).
- `icon-add` on the New Audit button.
- Filter rows search box next to the Results actions - filters the loaded
  page over base columns and all cell values.
- Two-line clamp on property cells; click to copy the full cell value.

### Changed
- Product name unified to "Content Audit" across NuGet, umbraco-package.json,
  marketplace listing and the backoffice dashboard label.
- Owner save button and save panel now read "Save" instead of "Update".
- Column sort headers are a single toggle button (both-grey / lit-up / lit-down)
  instead of two stacked buttons.
- Sorting a finished run re-queries the retained run instead of starting a
  fresh scan; a dead retained run falls back to a full rerun.

### Removed
- Unused legacy saved-query dashboard element and save modal (the legacy
  API surface remains for stored v1/v2 definitions).
- Unused localization keys.

## [0.3.0] - 2026-10-20

### Added
- Audit-monitoring redesign: overview of saved audits, personal numbered
  dashboard tiles, multiple document/element types, union property columns,
  per-column filters, global result sorting, incremental runs and Save Audit
  metadata.
- Block List and Block Grid element instances included in scan scope.
- Non-destructive upgrade path for existing saved definitions.
