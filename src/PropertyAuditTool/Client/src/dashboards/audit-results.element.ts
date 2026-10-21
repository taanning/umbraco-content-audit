import {
  css,
  html,
  customElement,
  property,
  state,
} from "@umbraco-cms/backoffice/external/lit";
import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { UMB_EDIT_DOCUMENT_WORKSPACE_PATH_PATTERN } from "@umbraco-cms/backoffice/document";
import { UMB_EDIT_DOCUMENT_TYPE_WORKSPACE_PATH_PATTERN } from "@umbraco-cms/backoffice/document-type";
import type { AuditRunCell, AuditRunResults, AuditRunRow } from "../api/index.js";
import { BASE_COLUMNS } from "./audit-table-state.js";

/**
 * Read-only results table for a finished run. All sorting/paging is
 * server-side: emits `results-sort` {sortBy} and `results-page` {skip}.
 */
@customElement("property-audit-results")
export class AuditResultsElement extends UmbLitElement {
  @property({ attribute: false })
  results?: AuditRunResults;

  @property({ attribute: false })
  sortBy = "kind";

  @property({ attribute: false }) columns: string[] = [];
  @property({ attribute: false }) columnLabels: Record<string, string> = {};
  @property({ attribute: false }) hiddenBaseColumns: string[] = [];
  @property({ attribute: false }) activeFilters: string[] = [];
  @property({ attribute: false }) typeKeys: Record<string, string> = {};
  @property({ type: Boolean }) configurationOpen = false;

  @property({ attribute: false })
  direction: "asc" | "desc" = "asc";

  @property({ attribute: false })
  loading = false;

  @property({ attribute: false })
  contentSearch = "";

  @state() private _copiedCell = "";
  #copyTimer?: ReturnType<typeof setTimeout>;

  #emitSort(sortBy: string, direction: "asc" | "desc"): void {
    this.dispatchEvent(
      new CustomEvent("results-sort", {
        detail: { sortBy, direction },
        bubbles: true,
        composed: true,
      }),
    );
  }

  #emitPage(skip: number): void {
    this.dispatchEvent(
      new CustomEvent("results-page", {
        detail: { skip },
        bubbles: true,
        composed: true,
      }),
    );
  }

  #sortHeader(label: string, key: string) {
    return html`<div class="column-header">
      <span class="column-name">${label}</span>
      <uui-button class="filter ${this.activeFilters.includes(key) ? "active" : ""}" compact
        look="default" label=${`${this.localize.term("propertyAudit_filterColumn")}: ${label}`}
        ?disabled=${this.loading || this.configurationOpen}
        @click=${() => this.dispatchEvent(new CustomEvent("results-filter", {
          detail: { key }, bubbles: true, composed: true,
        }))}
      ><svg class="filter-icon" viewBox="0 0 24 24" aria-hidden="true">
        <path class="filter-fill" d="M10 20a1 1 0 0 0 .553.895l2 1A1 1 0 0 0 14 21v-7a2 2 0 0 1 .517-1.341L21.74 4.67A1 1 0 0 0 21 3H3a1 1 0 0 0-.742 1.67l7.225 7.989A2 2 0 0 1 10 14z" />
      </svg></uui-button>
      <uui-button compact look="default" class="sort ${this.sortBy === key ? "active" : ""}"
        label=${`${this.localize.term("propertyAudit_sortAscending")}/${this.localize.term("propertyAudit_sortDescending")}: ${label}`}
        aria-pressed=${String(this.sortBy === key)} ?disabled=${this.loading}
        @click=${() => this.#emitSort(key, this.sortBy === key && this.direction === "asc" ? "desc" : "asc")}
      ><svg class="sort-icon" viewBox="0 0 24 24" aria-hidden="true">
        <path class=${this.sortBy === key && this.direction === "asc" ? "on" : ""} d="M6 10 12 3 18 10Z" />
        <path class=${this.sortBy === key && this.direction === "desc" ? "on" : ""} d="M6 14 12 21 18 14Z" />
      </svg></uui-button>
    </div>`;
  }

  #editUrl(ownerDocumentKey: string, fallback: string): string {
    return (
      UMB_EDIT_DOCUMENT_WORKSPACE_PATH_PATTERN.generateAbsolute({
        unique: ownerDocumentKey,
      }) ?? fallback
    );
  }

  #typeUrl(row: AuditRunRow): string | undefined {
    const unique = this.typeKeys[`${row.kind}:${row.typeAlias}`];
    return unique
      ? UMB_EDIT_DOCUMENT_TYPE_WORKSPACE_PATH_PATTERN.generateAbsolute({ unique })
      : undefined;
  }

  #cellText(value: unknown): string {
    if (value == null) return "";
    return typeof value === "string" ? value : JSON.stringify(value);
  }

  #copyCell(key: string, text: string): void {
    void navigator.clipboard?.writeText(text).catch(() => undefined);
    this._copiedCell = key;
    clearTimeout(this.#copyTimer);
    this.#copyTimer = setTimeout(() => {
      if (this._copiedCell === key) this._copiedCell = "";
    }, 1500);
  }

  #cellDisplay(cell: AuditRunCell, key: string) {
    switch (cell.status) {
      case "notApplicable":
        return html`<span class="badge na">${this.localize.term("propertyAudit_na")}</span>`;
      case "restricted":
        return html`<span class="badge restricted"
          >${this.localize.term("propertyAudit_restricted")}</span
        >`;
      case "empty":
        return html`<uui-tag look="secondary"
          >${this.localize.term("propertyAudit_empty")}</uui-tag
        >`;
      default: {
        const text = this.#cellText(cell.value);
        return html`<span
            class="cell-clamp"
            role="button"
            tabindex="0"
            title=${this.localize.term("propertyAudit_copyFullValue")}
            @click=${() => this.#copyCell(key, text)}
            @keydown=${(event: KeyboardEvent) => {
              if (event.key === "Enter" || event.key === " ") {
                event.preventDefault();
                this.#copyCell(key, text);
              }
            }}
          >${text}</span
          >${cell.truncated
            ? html`<uui-tag color="warning" look="secondary"
                >${this.localize.term("propertyAudit_truncated")}</uui-tag
              >`
            : ""}${this._copiedCell === key
            ? html`<uui-tag look="secondary" color="positive"
                >${this.localize.term("propertyAudit_copied")}</uui-tag
              >`
            : ""}`;
      }
    }
  }

  #baseCell(row: AuditRunRow, key: string) {
    switch (key) {
      case "kind": return this.localize.term(row.kind === "element" ? "propertyAudit_kindElement" : "propertyAudit_kindDocument");
      case "typeName": {
        const href = this.#typeUrl(row);
        return href
          ? html`<a href=${href} title=${this.localize.term("propertyAudit_openType")}>${row.typeName}</a>`
          : row.typeName;
      }
      case "content": return html`<a href=${this.#editUrl(row.ownerDocumentKey, row.editUrl)}>${row.ownerDocumentName}</a>`;
      case "culture": return row.culture ?? this.localize.term("propertyAudit_invariantCulture");
      default: return row.location;
    }
  }

  /** Client-side filter over the currently loaded page of rows. */
  #rowText(row: AuditRunRow): string {
    return [
      row.kind,
      row.typeName,
      row.typeAlias,
      row.ownerDocumentName,
      row.location,
      row.culture ?? "",
      ...row.cells.map((cell) => `${cell.status} ${this.#cellText(cell.value)}`),
    ]
      .join("\n")
      .toLocaleLowerCase();
  }

  render() {
    const results = this.results;
    const columns = results?.columns ?? this.columns;
    const baseColumns = BASE_COLUMNS.filter((column) => !this.hiddenBaseColumns.includes(column.key));
    const take = results?.take ?? 100;
    const skip = results?.skip ?? 0;
    const total = results?.totalItems ?? 0;
    const canPrev = skip > 0;
    const canNext = skip + take < total;
    const query = this.contentSearch.trim().toLocaleLowerCase();
    const visibleRows = (results?.items ?? []).filter(
      (row) => !query || this.#rowText(row).includes(query),
    );

    return html`
      ${(results?.progress.warnings.length ?? 0) > 0
        ? html`<div class="warnings" role="alert">
            ${results!.progress.warnings.map(
              (warning) => html`<p class="warning">${warning}</p>`,
            )}
          </div>`
        : ""}

      ${this.loading ? html`<uui-loader-bar></uui-loader-bar>` : ""}

      <div class="table-scroll" tabindex="0" role="region"
        aria-label=${this.localize.term("propertyAudit_results")}>
      <uui-table>
        <uui-table-head>
          ${baseColumns.map((column) => html`<uui-table-head-cell>
            ${this.#sortHeader(this.localize.term(`propertyAudit_${column.label}`), column.key)}
          </uui-table-head-cell>`)}
          ${columns.map(
            (alias) => html`<uui-table-head-cell>
              ${this.#sortHeader(this.columnLabels[alias] ?? alias, `property:${alias}`)}
            </uui-table-head-cell>`,
          )}
        </uui-table-head>
        ${visibleRows.map(
          (row, rowIndex) => html`
            <uui-table-row>
              ${baseColumns.map((column) => html`<uui-table-cell>${this.#baseCell(row, column.key)}</uui-table-cell>`)}
              ${row.cells.map(
                (cell, cellIndex) => html`<uui-table-cell>${this.#cellDisplay(cell, `${rowIndex}:${cellIndex}`)}</uui-table-cell>`,
              )}
            </uui-table-row>
          `,
        )}
      </uui-table>
      </div>

      ${!results ? html`<p>${this.localize.term("propertyAudit_runToSeeResults")}</p>`
        : visibleRows.length === 0 ? html`<p>${this.localize.term("propertyAudit_noMatchingRows")}</p>` : ""}
      ${total > take ? html`<div class="pager">
            <uui-button
              look="secondary"
              compact
              label=${this.localize.term("propertyAudit_previous")}
              ?disabled=${!canPrev || this.loading}
              @click=${() => this.#emitPage(Math.max(0, skip - take))}
              >${this.localize.term("propertyAudit_previous")}</uui-button
            >
            <span class="pager-info">
              ${this.localize.term(
                "propertyAudit_pageIndicator",
                Math.floor(skip / take) + 1,
                Math.ceil(total / take),
              )}
            </span>
            <uui-button
              look="secondary"
              compact
              label=${this.localize.term("propertyAudit_next")}
              ?disabled=${!canNext || this.loading}
              @click=${() => this.#emitPage(skip + take)}
              >${this.localize.term("propertyAudit_next")}</uui-button
            >
      </div>` : ""}
      ${results && results.progress.status !== "completed"
        ? html`<p class="footer">
            <small>${this.localize.term("propertyAudit_incompleteNote")}</small>
          </p>`
        : ""}
    `;
  }

  static styles = css`
    :host {
      display: block;
      max-width: 100%;
      min-width: 0;
    }
    .table-scroll {
      overflow: auto;
      max-width: 100%;
      max-height: 65vh;
    }
    uui-table { width: max-content; min-width: 100%; table-layout: auto; }
    uui-table-head { position: sticky; top: 0; z-index: 1; background: var(--uui-color-surface); }
    uui-table-cell {
      max-width: 24em;
      overflow-wrap: anywhere;
    }
    .cell-clamp {
      display: -webkit-box;
      -webkit-box-orient: vertical;
      -webkit-line-clamp: 2;
      overflow: hidden;
      cursor: copy;
      overflow-wrap: anywhere;
      white-space: pre-wrap;
    }
    .cell-clamp:hover,
    .cell-clamp:focus-visible {
      text-decoration: underline dotted;
      outline: none;
    }
    .column-header { display: flex; align-items: center; gap: var(--uui-size-space-1); white-space: nowrap; }
    .column-name { font-weight: 600; margin-right: var(--uui-size-space-2); }
    .sort, .filter { color: var(--uui-color-text-alt); --uui-button-contrast: currentColor; }
    .filter { opacity: 0.5; }
    .sort { --uui-button-height: 22px; inline-size: 22px; }
    .sort-icon, .filter-icon { inline-size: 1em; block-size: 1em; flex: none; }
    .sort-icon path { fill: var(--uui-color-text-alt); opacity: 0.45; }
    .sort-icon path.on { fill: var(--uui-color-interactive); opacity: 1; }
    .sort:hover .sort-icon path { opacity: 0.8; }
    .sort:hover .sort-icon path.on { opacity: 1; }
    .filter-fill {
      fill: transparent;
      stroke: currentColor;
      stroke-width: 1.75;
      stroke-linecap: round;
      stroke-linejoin: round;
    }
    .filter.active .filter-fill { fill: var(--uui-color-positive-emphasis); }
    .active, .sort:hover, .filter:hover { color: var(--uui-color-interactive); opacity: 1; }
    .filter.active { color: var(--uui-color-positive-emphasis); }
    .badge {
      font-size: 0.8em;
      padding: 1px 6px;
      border-radius: 6px;
      background: var(--uui-color-disabled);
    }
    .badge.restricted {
      background: var(--uui-color-warning);
    }
    .warnings .warning {
      color: var(--uui-color-warning-alt);
      margin: 2px 0;
    }
    .pager {
      display: flex;
      gap: var(--uui-size-space-3);
      align-items: center;
      margin-top: var(--uui-size-space-4);
      flex-wrap: wrap;
    }
    .spacer { flex: 1; }
    .footer {
      opacity: 0.8;
    }
  `;
}

export default AuditResultsElement;

declare global {
  interface HTMLElementTagNameMap {
    "property-audit-results": AuditResultsElement;
  }
}
