import {
  css,
  html,
  customElement,
  property,
  state,
} from "@umbraco-cms/backoffice/external/lit";
import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import type {
  AuditResponse,
} from "../api/index.js";

/**
 * Audit home: the current viewer's pinned tiles (when any), a toolbar with
 * New Audit / Search, and the full authorized audit list. Emits
 * `audit-open`, `audit-new`, `audit-delete`, `audit-pin`
 * events for the orchestrator.
 */
@customElement("property-audit-overview")
export class AuditOverviewElement extends UmbLitElement {
  @property({ attribute: false })
  audits: AuditResponse[] = [];

  @property({ attribute: false })
  refreshingTiles = false;

  /** Audits with a run currently in progress from this home view. */
  @property({ attribute: false })
  busyAuditIds: ReadonlyArray<string> = [];
  @property({ attribute: false })
  inexactAuditIds: ReadonlyArray<string> = [];

  @state() private _search = "";
  @state() private _pinnedOnly = false;
  @state() private _sortBy: "title" | "hits" | "props" | "owner" | "updated" = "title";
  @state() private _direction: "asc" | "desc" = "asc";

  /** Personal pinned audits, independent of list/search filtering. */
  get #tiles(): AuditResponse[] {
    return this.audits.filter((audit) => audit.dashboard.isPinned);
  }

  get #filteredAudits(): AuditResponse[] {
    const needle = this._search.trim().toLowerCase();
    const filtered = this.audits.filter((audit) =>
      (!this._pinnedOnly || audit.dashboard.isPinned)
      && (!needle || (audit.title + " " + (audit.description ?? "") + " " + audit.ownerName)
        .toLowerCase()
        .includes(needle)),
    );
    return filtered.sort((a, b) => this.#compareAudits(a, b));
  }

  #sortValue(audit: AuditResponse): string | number | null {
    switch (this._sortBy) {
      case "title": return audit.title.toLocaleLowerCase();
      case "hits": return audit.dashboard.lastCount ?? null;
      case "props": return audit.definition.columns.length;
      case "owner": return audit.ownerName.toLocaleLowerCase();
      case "updated": return Math.max(Date.parse(audit.createdDate), Date.parse(audit.updatedDate));
    }
  }

  #compareAudits(a: AuditResponse, b: AuditResponse): number {
    const aValue = this.#sortValue(a);
    const bValue = this.#sortValue(b);
    if (aValue === bValue) return a.title.localeCompare(b.title);
    if (aValue === null) return 1;
    if (bValue === null) return -1;
    const result = typeof aValue === "string" && typeof bValue === "string"
      ? aValue.localeCompare(bValue)
      : Number(aValue) - Number(bValue);
    return this._direction === "asc" ? result : -result;
  }

  /** Contrasting text colour for a valid 6-digit hex background. */
  #textColour(hex?: string | null): string {
    if (!hex || !/^#[0-9a-fA-F]{6}$/.test(hex)) return "inherit";
    const r = parseInt(hex.slice(1, 3), 16);
    const g = parseInt(hex.slice(3, 5), 16);
    const b = parseInt(hex.slice(5, 7), 16);
    return (r * 299 + g * 587 + b * 114) / 1000 >= 140 ? "#1d1d1f" : "#ffffff";
  }

  #tileStyle(audit: AuditResponse): string {
    const colour = audit.dashboard.colour;
    const theme = {
      "@grey": "default", "@green": "positive", "@blue": "interactive",
      "theme-grey": "default", "theme-green": "positive", "theme-blue": "interactive",
    }[colour ?? ""];
    if (theme) return `background:var(--uui-color-${theme});color:var(--uui-color-${theme}-contrast);`;
    if (!colour || !/^#[0-9a-fA-F]{6}$/.test(colour)) return "";
    return `background:${colour};color:${this.#textColour(colour)};`;
  }

  #onOpen(audit: AuditResponse): void {
    this.dispatchEvent(
      new CustomEvent("audit-open", { detail: audit, bubbles: true, composed: true }),
    );
  }

  #sortHead(label: string, key: "title" | "hits" | "props" | "owner" | "updated") {
    const asc = this._sortBy === key && this._direction === "asc";
    const desc = this._sortBy === key && this._direction === "desc";
    return html`<div class="column-header">
      <span class="column-name">${label}</span>
      <uui-button
        class="sort ${asc || desc ? "active" : ""}"
        look="default"
        compact
        label=${`${this.localize.term("propertyAudit_sortAscending")}/${this.localize.term("propertyAudit_sortDescending")}: ${label}`}
        aria-pressed=${asc || desc}
        @click=${() => {
          this._direction =
            this._sortBy === key && this._direction === "asc" ? "desc" : "asc";
          this._sortBy = key;
        }}
      ><svg class="sort-icon" viewBox="0 0 24 24" aria-hidden="true">
        <path class=${asc ? "on" : ""} d="M6 10 12 3 18 10Z" />
        <path class=${desc ? "on" : ""} d="M6 14 12 21 18 14Z" />
      </svg></uui-button>
    </div>`;
  }

  render() {
    const tiles = this.#tiles;
    const filtered = this.#filteredAudits;
    return html`
      ${tiles.length > 0
        ? html`<div class="tiles" role="list">
            ${tiles.map(
              (audit) => html`
                <div
                  role="listitem"
                  class="tile"
                  style=${this.#tileStyle(audit)}
                >
                  <div class="tile-content">
                    <span class="tile-label">
                      <span class="tile-symbol"><uui-icon name=${audit.dashboard.icon}></uui-icon></span>
                      <span class="tile-body">
                        <span class="tile-title" title=${audit.title}>${audit.title}</span>
                        ${audit.description
                          ? html`<span class="tile-desc"
                              >${audit.description}</span
                            >`
                          : ""}
                        ${audit.dashboard.isStale
                          ? html`<uui-tag color="warning" look="secondary"
                              >${this.localize.term(
                                "propertyAudit_tileStale",
                              )}</uui-tag
                            >`
                          : ""}
                      </span>
                    </span>
                    <span class="tile-count">
                      ${this.busyAuditIds.includes(audit.id)
                        ? html`<uui-loader-circle aria-label=${this.localize.term("propertyAudit_running")}></uui-loader-circle>`
                        : ""}
                      ${audit.dashboard.lastCount == null
                        ? html`<span class="dash">--</span>
                            <small
                              >${this.localize.term(
                                "propertyAudit_neverRun",
                              )}</small
                            >`
                        : html`<span class="num"
                            >${audit.dashboard.lastCount}</span
                          >`}
                      ${this.busyAuditIds.includes(audit.id) || this.inexactAuditIds.includes(audit.id)
                        ? html`<small>${this.localize.term("propertyAudit_previousCount")}</small>` : ""}
                    </span>
                  </div>
                  <uui-button
                    look="default"
                    class="tile-open"
                    @click=${() => this.#onOpen(audit)}
                    label=${`${this.localize.term("propertyAudit_tileOpen")}: ${audit.title}`}
                  ><span></span></uui-button>
                </div>
              `,
            )}
          </div>`
        : ""}

      <div class="toolbar">
        <uui-button
          look="primary"
          color="positive"
          label=${this.localize.term("propertyAudit_newAudit")}
          @click=${() =>
            this.dispatchEvent(
              new CustomEvent("audit-new", { bubbles: true, composed: true }),
            )}
          ><uui-icon name="icon-add"></uui-icon>${this.localize.term("propertyAudit_newAudit")}</uui-button
        >
        <uui-input
          class="search"
          label=${this.localize.term("propertyAudit_searchAudits")}
          placeholder=${this.localize.term("propertyAudit_searchAudits")}
          .value=${this._search}
          @input=${(event: InputEvent) => {
            this._search = (event.target as HTMLInputElement).value;
          }}
        ></uui-input>
      </div>

      ${filtered.length === 0
        ? this.audits.length === 0
          ? html`<uui-box class="empty">
              <p>${this.localize.term("propertyAudit_emptyAudits")}</p>
            </uui-box>`
          : html`<uui-box class="empty">
              <p>${this.localize.term("propertyAudit_noMatchingAudits")}</p>
            </uui-box>`
        : html`<div class="table-scroll"><uui-table>
            <uui-table-head>
              <uui-table-head-cell class="pin-cell">
                <uui-button
                  look="default"
                  compact
                  class="pin-toggle pin-filter ${this._pinnedOnly ? "pinned" : ""}"
                  title=${this.localize.term(this._pinnedOnly ? "propertyAudit_showAllAudits" : "propertyAudit_filterPinned")}
                  aria-label=${this.localize.term(this._pinnedOnly ? "propertyAudit_showAllAudits" : "propertyAudit_filterPinned")}
                  aria-pressed=${this._pinnedOnly}
                  label=${this.localize.term(this._pinnedOnly ? "propertyAudit_showAllAudits" : "propertyAudit_filterPinned")}
                  @click=${() => { this._pinnedOnly = !this._pinnedOnly; }}
                ><umb-icon name="icon-pushpin"></umb-icon></uui-button>
              </uui-table-head-cell>
              <uui-table-head-cell>
                ${this.#sortHead(this.localize.term("propertyAudit_colTitle"), "title")}
              </uui-table-head-cell>
              <uui-table-head-cell>
                ${this.localize.term("propertyAudit_colShared")}
              </uui-table-head-cell>
              <uui-table-head-cell>
                ${this.#sortHead(this.localize.term("propertyAudit_colHits"), "hits")}
              </uui-table-head-cell>
              <uui-table-head-cell>
                ${this.#sortHead(this.localize.term("propertyAudit_colProps"), "props")}
              </uui-table-head-cell>
              <uui-table-head-cell>
                ${this.#sortHead(this.localize.term("propertyAudit_colCreatedBy"), "owner")}
              </uui-table-head-cell>
              <uui-table-head-cell>
                ${this.#sortHead(this.localize.term("propertyAudit_updated"), "updated")}
              </uui-table-head-cell>
              <uui-table-head-cell>
                ${this.localize.term("propertyAudit_colActions")}
              </uui-table-head-cell>
            </uui-table-head>
            ${filtered.map(
              (audit) => html`
                <uui-table-row>
                  <uui-table-cell class="pin-cell">
                    <uui-button
                      look="default"
                      compact
                      class="pin-toggle ${audit.dashboard.isPinned
                        ? "pinned"
                        : ""}"
                      title=${this.localize.term(
                        audit.dashboard.isPinned
                          ? "propertyAudit_unpinAudit"
                          : "propertyAudit_pinAudit",
                      )}
                      aria-label=${this.localize.term(
                        audit.dashboard.isPinned
                          ? "propertyAudit_unpinAudit"
                          : "propertyAudit_pinAudit",
                      )}
                      aria-pressed=${audit.dashboard.isPinned}
                      label=${this.localize.term(audit.dashboard.isPinned ? "propertyAudit_unpinAudit" : "propertyAudit_pinAudit")}
                      @click=${() =>
                        this.dispatchEvent(
                          new CustomEvent("audit-pin", {
                            detail: audit,
                            bubbles: true,
                            composed: true,
                          }),
                        )}
                    >
                      <umb-icon name="icon-pushpin"></umb-icon>
                    </uui-button>
                  </uui-table-cell>
                  <uui-table-cell>
                    <uui-button
                      look="default"
                      class="link"
                      label=${audit.title}
                      @click=${() => this.#onOpen(audit)}
                    >
                      ${audit.title}
                    </uui-button>
                    ${audit.description
                      ? html`<small class="desc"
                          >${audit.description}</small
                        >`
                      : ""}
                  </uui-table-cell>
                  <uui-table-cell>
                    ${audit.isShared
                      ? html`<uui-tag look="secondary"
                          >${this.localize.term(
                            "propertyAudit_tagShared",
                          )}</uui-tag
                        >`
                      : ""}
                  </uui-table-cell>
                  <uui-table-cell>
                    ${audit.dashboard.lastCount ?? "--"}
                    ${audit.dashboard.isStale
                      ? html`<uui-tag
                          look="secondary"
                          color="warning"
                          title=${this.localize.term("propertyAudit_hitsStale")}
                          >${this.localize.term(
                            "propertyAudit_tileStale",
                          )}</uui-tag
                        >`
                      : ""}
                  </uui-table-cell>
                  <uui-table-cell>
                    ${audit.definition.columns.length}
                  </uui-table-cell>
                  <uui-table-cell>${audit.ownerName}</uui-table-cell>
                  <uui-table-cell>
                    ${new Date(Math.max(Date.parse(audit.createdDate), Date.parse(audit.updatedDate))).toLocaleString()}
                  </uui-table-cell>
                  <uui-table-cell class="actions-cell">
                    <div class="row-actions">
                    <uui-button
                      class="run-audit"
                      look="secondary"
                      compact
                      label=${this.localize.term("propertyAudit_runAudit")}
                      ?disabled=${this.busyAuditIds.includes(audit.id)}
                      .state=${this.busyAuditIds.includes(audit.id) ? "waiting" : undefined}
                      @click=${() => this.dispatchEvent(new CustomEvent("audit-run", {
                        detail: audit, bubbles: true, composed: true,
                      }))}
                    ><uui-icon name="icon-play"></uui-icon>${this.localize.term("propertyAudit_runAudit")}</uui-button>
                    <uui-button
                      look="secondary"
                      compact
                      label=${this.localize.term(
                        audit.isOwner
                          ? "propertyAudit_edit"
                          : "propertyAudit_open",
                      )}
                      @click=${() => this.#onOpen(audit)}
                      >${this.localize.term(
                        audit.isOwner
                          ? "propertyAudit_edit"
                          : "propertyAudit_open",
                      )}</uui-button
                    >
                    ${audit.isOwner
                      ? html`<uui-button
                          look="secondary"
                          compact
                          color="danger"
                          label=${this.localize.term("propertyAudit_delete")}
                          @click=${() =>
                            this.dispatchEvent(
                              new CustomEvent("audit-delete", {
                                detail: audit,
                                bubbles: true,
                                composed: true,
                              }),
                            )}
                          ><uui-icon name="icon-wrong"></uui-icon>${this.localize.term("propertyAudit_delete")}</uui-button
                        >`
                      : ""}
                    </div>
                  </uui-table-cell>
                </uui-table-row>
              `,
            )}
          </uui-table></div>`}
    `;
  }

  static styles = css`
    :host {
      display: block;
      max-width: 100%;
      min-width: 0;
    }
    .tiles {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(240px, 100%), 1fr));
      gap: var(--uui-size-space-4);
      margin-bottom: var(--uui-size-space-5);
    }
    .tile {
      position: relative;
      display: flex;
      align-items: stretch;
      border: 1px solid var(--uui-color-border);
      border-radius: var(--uui-border-radius);
      background: var(--uui-color-surface);
      overflow: hidden;
      min-height: 5.5em;
    }
    .tile-open {
      position: absolute;
      inset: 0;
      z-index: 1;
      width: 100%;
      height: 100%;
      border: 0;
      --uui-button-border-width: 0;
      --uui-button-background-color: transparent;
      --uui-button-background-color-hover: transparent;
      --uui-button-padding-left-factor: 0;
      --uui-button-padding-right-factor: 0;
      --uui-button-padding-top-factor: 0;
      --uui-button-padding-bottom-factor: 0;
      --uui-button-height: 100%;
    }
    .tile-content {
      position: relative;
      z-index: 0;
      display: grid;
      grid-template-columns: minmax(0, 1fr) auto;
      align-items: center;
      gap: var(--uui-size-space-4);
      width: 100%;
      min-width: 0;
      min-height: inherit;
      box-sizing: border-box;
      padding: var(--uui-size-space-4);
      text-align: left;
      pointer-events: none;
    }
    .tile-label {
      display: grid;
      grid-template-columns: auto minmax(0, 1fr);
      align-items: center;
      min-width: 0;
      gap: var(--uui-size-space-4);
    }
    .tile-symbol {
      display: grid;
      place-items: center;
      inline-size: var(--uui-size-12);
      block-size: var(--uui-size-12);
      flex: none;
    }
    .tile-symbol uui-icon {
      inline-size: 100%;
      block-size: 100%;
    }
    .tile-body {
      flex: 1;
      min-width: 0;
      align-self: stretch;
      display: flex;
      flex-direction: column;
      justify-content: center;
    }
    .tile-title {
      font-weight: 600;
      display: block;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .tile-desc {
      font-size: 0.85em;
      line-height: 1.3;
      opacity: 0.8;
      display: -webkit-box;
      -webkit-box-orient: vertical;
      -webkit-line-clamp: 2;
      line-height: 1.1;
      min-block-size: 2.2em;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: normal;
    }
    .tile-count {
      text-align: right;
      align-self: center;
    }
    .tile-count .num {
      font-size: 1.6em;
      font-weight: 700;
      display: block;
    }
    .tile-count .dash {
      font-size: 1.6em;
      display: block;
    }
    .tile-count small {
      opacity: 0.75;
    }
    .table-scroll {
      overflow-x: auto;
      max-width: 100%;
    }
    uui-table { width: max-content; min-width: 100%; table-layout: auto; }
    .pin-cell {
      width: var(--uui-size-space-6);
      text-align: center;
    }
    .column-header {
      display: flex;
      align-items: center;
      gap: var(--uui-size-space-1);
      white-space: nowrap;
    }
    .column-name {
      font-weight: 600;
      margin-right: var(--uui-size-space-2);
    }
    .sort {
      --uui-button-height: 22px;
      inline-size: 22px;
    }
    .sort-icon {
      inline-size: 1em;
      block-size: 1em;
      flex: none;
    }
    .sort-icon path {
      fill: var(--uui-color-text-alt);
      opacity: 0.45;
    }
    .sort-icon path.on {
      fill: var(--uui-color-interactive);
      opacity: 1;
    }
    .actions-cell {
      white-space: nowrap;
    }
    .row-actions {
      display: flex;
      align-items: center;
      justify-content: flex-start;
      gap: var(--uui-size-space-2);
      white-space: nowrap;
    }
    .row-actions uui-button {
      flex: none;
    }
    .row-actions uui-icon {
      margin-inline-end: var(--uui-size-space-2);
    }
    .pin-toggle {
      border: 0;
      background: none;
      cursor: pointer;
      padding: 0;
      font: inherit;
      color: var(--uui-color-interactive);
      opacity: 0.35;
      display: inline-flex;
    }
    .pin-toggle.pinned,
    .pin-toggle:hover,
    .pin-toggle:focus-visible {
      opacity: 1;
    }
    uui-table-cell small.desc {
      display: block;
    }
    .toolbar {
      display: flex;
      gap: var(--uui-size-space-3);
      align-items: center;
      margin-bottom: var(--uui-size-space-4);
      flex-wrap: wrap;
    }
    .toolbar uui-button :is(uui-icon, umb-icon) {
      margin-inline-end: var(--uui-size-space-2);
    }
    .toolbar .search {
      width: 260px;
    }
    .link {
      border: 0;
      background: none;
      color: var(--uui-color-interactive);
      cursor: pointer;
      padding: 0;
      font: inherit;
      font-weight: 600;
      text-align: left;
      --uui-button-padding-left-factor: 0;
      --uui-button-padding-right-factor: 0;
      --uui-button-padding-top-factor: 0;
      --uui-button-padding-bottom-factor: 0;
      --uui-button-height: auto;
      --uui-button-content-align: left;
      --uui-button-font-weight: 600;
    }
    .desc {
      opacity: 0.75;
    }
    .empty {
      padding: var(--uui-size-space-5);
    }
  `;
}

export default AuditOverviewElement;

declare global {
  interface HTMLElementTagNameMap {
    "property-audit-overview": AuditOverviewElement;
  }
}
