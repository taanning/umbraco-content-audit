import { css, html, customElement, property, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { UMB_MODAL_MANAGER_CONTEXT, UMB_CONFIRM_MODAL } from "@umbraco-cms/backoffice/modal";
import type { AuditCatalogResponse, AuditColumnDefinition, AuditDefinition } from "../api/index.js";
import {
  availableColumns, BASE_COLUMNS, columnFilterValid, OPERATORS, REQUIRED_BASE_COLUMNS,
  sanitizeHiddenBaseColumns, selectedTypes,
  type PanelRequest, type PanelSubmit,
} from "./audit-table-state.js";

@customElement("property-audit-config-panel")
export class AuditConfigPanelElement extends UmbLitElement {
  #modalManager?: typeof UMB_MODAL_MANAGER_CONTEXT.TYPE;

  constructor() {
    super();
    this.consumeContext(UMB_MODAL_MANAGER_CONTEXT, (context) => {
      this.#modalManager = context;
    });
  }

  @property({ attribute: false }) request!: PanelRequest;
  @property({ attribute: false }) catalog!: AuditCatalogResponse;
  @property({ type: Boolean }) busy = false;
  @property({ type: Boolean }) embedded = false;
  @state() private _draft!: AuditDefinition;
  @state() private _search: Record<string, string> = {};
  @state() private _title = "";
  @state() private _description = "";
  @state() private _shared = false;
  @state() private _icon = "";
  @state() private _colour = "";
  @state() private _expressionMode = false;

  willUpdate(changes: Map<string, unknown>): void {
    if (changes.has("request")) {
      this._draft = JSON.parse(JSON.stringify(this.request.definition));
      this._title = this.request.title;
      this._description = this.request.description;
      this._shared = this.request.isShared;
      this._icon = this.request.icon;
      this._colour = this.request.colour.replace(/^theme-/, "@");
      this._search = {};
      this._expressionMode = this.#filterColumn?.filter?.expression != null;
      if (this.request.kind === "filter" && !this._expressionMode && !this.#filterColumn?.filter) {
        this.#setFilter({ filter: { operator: "hasText", value: null, expression: null } });
      }
    }
  }

  #term(key: string, ...args: unknown[]): string { return this.localize.term(`propertyAudit_${key}`, ...args); }
  #matches(key: string, text: string): boolean {
    return text.toLocaleLowerCase().includes((this._search[key] ?? "").toLocaleLowerCase().trim());
  }
  #search(key: string) {
    return html`<uui-input class="search" type="search" label=${this.#term("searchOptions")}
      placeholder=${this.#term("searchOptions")} .value=${this._search[key] ?? ""}
      @click=${(event: Event) => void (event.currentTarget as HTMLElement & { focus(): void }).focus()}
      @input=${(event: InputEvent) => {
        this._search = { ...this._search, [key]: (event.target as HTMLInputElement).value };
      }}>
      <uui-button slot="append" compact look="default" label=${this.#term("searchOptions")}
        @click=${(event: Event) => {
          event.stopPropagation();
          void ((event.currentTarget as HTMLElement).parentElement as HTMLElement & { focus(): void }).focus();
        }}>
        <uui-icon name="icon-search"></uui-icon>
      </uui-button>
    </uui-input>`;
  }
  #allButtons(select: (all: boolean) => void) {
    return html`<div class="selection-actions">
      <uui-button compact look="secondary" label=${this.#term("selectAll")}
        @click=${() => select(true)}>${this.#term("selectAll")}</uui-button>
      <uui-button compact look="secondary" label=${this.#term("deselectAll")}
        @click=${() => select(false)}>${this.#term("deselectAll")}</uui-button>
    </div>`;
  }
  #setTypes(kind: string, aliases: string[], forceAll?: boolean): void {
    const count = this.catalog.types.filter((type) => type.kind === kind).length;
    const all = forceAll ?? (count > 0 && count === aliases.length);
    this._draft = {
      ...this._draft,
      ...(kind === "document" ? { allDocumentTypes: all } : { allElementTypes: all }),
      types: [
        ...this._draft.types.filter((type) => type.kind !== kind),
        ...(all ? [] : aliases.map((alias) => ({ kind, alias }))),
      ],
    };
  }
  #types() {
    const selected = selectedTypes(this.catalog, this._draft);
    return html`<div class="type-lists">${["document", "element"].map((kind) => {
      const types = this.catalog.types.filter((type) => type.kind === kind);
      const aliases = selected.filter((type) => type.kind === kind).map((type) => type.alias);
      return html`<section>
        <h3>${this.#term(kind === "document" ? "documentTypes" : "elementTypes")}
          <small>${aliases.length} / ${types.length}</small></h3>
        ${this.#search(kind)}
        ${this.#allButtons((all) => this.#setTypes(kind, all ? types.map((type) => type.alias) : [], all))}
        <div class="choices">${types.filter((type) => this.#matches(kind, `${type.name} ${type.alias}`))
          .map((type) => html`<uui-checkbox .value=${type.alias}
            label=${`${type.name} (${type.alias})`} ?checked=${aliases.includes(type.alias)}
            @change=${(event: Event) => this.#setTypes(kind, (event.target as HTMLInputElement).checked
              ? [...aliases, type.alias] : aliases.filter((alias) => alias !== type.alias))}
          ></uui-checkbox>`)}</div>
      </section>`;
    })}</div>`;
  }
  #cultures() {
    const choices = [{ value: "", name: this.#term("invariantCulture") },
      ...this.catalog.languages.map((language) => ({
        value: language.isoCode, name: `${language.name} (${language.isoCode})`,
      }))];
    const selected = this._draft.allCultures ? choices.map((item) => item.value) : this._draft.cultures ?? [];
    const set = (values: string[]) => {
      this._draft = { ...this._draft, allCultures: values.length === choices.length,
        cultures: values.length === choices.length ? null : values };
    };
    return html`${this.#search("culture")}
      ${this.#allButtons((all) => set(all ? choices.map((item) => item.value) : []))}
      <div class="choices">${choices.filter((item) => this.#matches("culture", item.name))
        .map((item) => html`<uui-checkbox label=${item.name} .value=${item.value}
          ?checked=${selected.includes(item.value)}
          @change=${(event: Event) => set((event.target as HTMLInputElement).checked
            ? [...selected, item.value] : selected.filter((value) => value !== item.value))}
        ></uui-checkbox>`)}</div>`;
  }
  #columns() {
    const properties = availableColumns(this.catalog, this._draft);
    const required = new Set<string>(REQUIRED_BASE_COLUMNS);
    const set = (columns: AuditColumnDefinition[], hidden: string[]) => {
      this._draft = { ...this._draft, columns, hiddenBaseColumns: sanitizeHiddenBaseColumns(hidden) };
    };
    return html`${this.#search("column")}
      ${this.#allButtons((all) => set(all ? properties.filter((column) => !column.segment)
        .map((column) => this._draft.columns.find((item) => item.alias === column.alias)
          ?? { alias: column.alias, valueType: column.hint, filter: null }) : [],
        all ? [] : BASE_COLUMNS.filter((column) => !required.has(column.key)).map((column) => column.key)))}
      <div class="choices columns">
        ${BASE_COLUMNS.filter((column) => this.#matches("column", this.#term(column.label)))
          .map((column) => html`<uui-checkbox label=${this.#term(column.label)}
            .value=${column.key} ?checked=${required.has(column.key) || !this._draft.hiddenBaseColumns?.includes(column.key)}
            ?disabled=${required.has(column.key)}
            @change=${(event: Event) => set(this._draft.columns,
              (event.target as HTMLInputElement).checked
                ? (this._draft.hiddenBaseColumns ?? []).filter((key) => key !== column.key)
                : [...this._draft.hiddenBaseColumns ?? [], column.key])}
          ></uui-checkbox>`)}
        ${properties.filter((column) => this.#matches("column", `${column.name} ${column.alias}`))
          .map((column) => html`<uui-checkbox .value=${column.alias}
            label=${`${column.name} (${column.typeCount})${column.segment ? ` - ${this.#term("segmentUnsupported")}` : ""}`}
            title=${column.alias}
            ?disabled=${column.segment && !this._draft.columns.some((item) => item.alias === column.alias)}
            ?checked=${this._draft.columns.some((item) => item.alias === column.alias)}
            @change=${(event: Event) => set((event.target as HTMLInputElement).checked
              ? [...this._draft.columns, { alias: column.alias, valueType: column.hint, filter: null }]
              : this._draft.columns.filter((item) => item.alias !== column.alias),
              this._draft.hiddenBaseColumns ?? [])}
          ></uui-checkbox>`)}
      </div>`;
  }

  get #filterColumn(): AuditColumnDefinition | undefined {
    return (this.request.base ? this._draft?.rowFilters : this._draft?.columns)
      ?.find((column) => column.alias === this.request.alias);
  }
  #setFilter(patch: Partial<AuditColumnDefinition>): void {
    const key = this.request.base ? "rowFilters" : "columns";
    const column = this.#filterColumn ?? { alias: this.request.alias!, valueType: "text", filter: null };
    const columns = this._draft[key] ?? [];
    this._draft = { ...this._draft, [key]: columns.some((item) => item.alias === column.alias)
      ? columns.map((item) => item.alias === column.alias ? { ...column, ...patch } : item)
      : [...columns, { ...column, ...patch }] };
  }
  #filter() {
    const column = this.#filterColumn ?? { alias: this.request.alias!, valueType: "text", filter: null };
    const filter = column.filter;
    return html`<div class="filter-fields">
      <uui-select label=${this.#term("filterMode")} .value=${this._expressionMode ? "expression" : "simple"}
        .options=${["simple", "expression"].map((value) => ({
          name: this.#term(`filter_${value}`), value,
          selected: value === (this._expressionMode ? "expression" : "simple"),
        }))}
        @change=${(event: Event) => {
          const mode = (event.target as HTMLSelectElement).value;
          this._expressionMode = mode === "expression";
          this.#setFilter({ filter: {
            operator: "hasText", value: null, expression: mode === "expression" ? "" : null,
          } });
        }}></uui-select>
      ${this._expressionMode
        ? html`<uui-input label=${this.#term("expression")} placeholder=${this.#term("expressionExamples")}
            .value=${filter?.expression ?? ""} @input=${(event: InputEvent) =>
              this.#setFilter({ filter: { operator: "equals", expression: (event.target as HTMLInputElement).value } })}
          ></uui-input><div class="expression-notes">
          <small>${this.#term("expressionNote")}</small>
          <small>${this.#term("expressionOps")}</small>
          <ul>
            <li><small>${this.#term("expressionHelpQuoted")} <code>= 'Kim'</code>, <code>&lt; '2025-06-01'</code></small></li>
            <li><small>${this.#term("expressionHelpNumber")} <code>&gt;= 10</code>, <code>!= true</code></small></li>
            <li><small>${this.#term("expressionHelpLike")} <code>LIKE 'coffee%'</code>, <code>LIKE '_en'</code></small></li>
          </ul>
        </div>`
        : filter ? html`<uui-select label=${this.#term("operator")} .value=${filter.operator}
            .options=${(OPERATORS[column.valueType] ?? OPERATORS.text).map((value) => ({
              name: this.#term(`op_${value}`), value, selected: value === filter.operator,
            }))}
            @change=${(event: Event) => this.#setFilter({ filter: {
              ...filter, operator: (event.target as HTMLSelectElement).value,
            } })}></uui-select>
          ${!["isEmpty", "hasText"].includes(filter.operator) ? html`<uui-input
            label=${this.#term("searchValue")} .value=${filter.value ?? ""}
            @input=${(event: InputEvent) => this.#setFilter({ filter: {
              ...filter, value: (event.target as HTMLInputElement).value,
            } })}></uui-input>` : ""}` : ""}
      <div class="filter-actions">
        <uui-button class="apply" look="primary" color="positive" label=${this.#term("applyFilters")}
          ?disabled=${!this.#valid || this.busy} .state=${this.busy ? "waiting" : undefined}
          @click=${this.#apply}>${this.#term("applyFilters")}</uui-button>
        <uui-button look="secondary" label=${this.#term("cancel")} ?disabled=${this.busy}
          @click=${() => this.dispatchEvent(new CustomEvent("panel-cancel", { bubbles: true, composed: true }))}
        >${this.#term("cancel")}</uui-button>
        <uui-button look="primary" color="danger" label=${this.#term("removeFilter")} ?disabled=${this.busy}
          @click=${this.#removeFilter}
        >${this.#term("removeFilter")}</uui-button>
      </div>
    </div>`;
  }
  #requestName(): string {
    if (this.request.kind === "types") return this.#term("types");
    if (this.request.kind === "cultures") return this.#term("culture");
    if (this.request.kind === "columns") return this.#term("columns");
    if (this.request.base) {
      return this.#term(BASE_COLUMNS.find((column) => column.key === this.request.alias)?.label ?? "property");
    }
    return availableColumns(this.catalog, this._draft)
      .find((column) => column.alias === this.request.alias)?.name ?? this.request.alias ?? "";
  }
  #metadata() {
    return html`<div class="metadata">
      <umb-property-layout orientation="vertical" .label=${this.#term("auditTitle")} .mandatory=${true}>
        <uui-input slot="editor" id="audit-title" label=${this.#term("auditTitle")}
          required maxlength="120" .value=${this._title}
          @input=${(event: InputEvent) => { this._title = (event.target as HTMLInputElement).value; }}
        ></uui-input>
      </umb-property-layout>
      <umb-property-layout orientation="vertical" .label=${this.#term("subtitle")}>
        <uui-textarea slot="editor" id="audit-subtitle" label=${this.#term("subtitle")}
          maxlength="2000" .value=${this._description}
          @input=${(event: InputEvent) => { this._description = (event.target as HTMLInputElement).value; }}
        ></uui-textarea>
      </umb-property-layout>
    </div>`;
  }
  #dashboard() {
    return html`<div class="filter-fields">
      <uui-select label=${this.#term("icon")} .value=${this._icon}
        .options=${["icon-search", "icon-alert", "icon-document", "icon-check", "icon-chart"].map((value) => ({
          name: value, value, selected: value === this._icon,
        }))}
        @change=${(event: Event) => { this._icon = (event.target as HTMLSelectElement).value; }}
      ></uui-select>
      <uui-select label=${this.#term("colour")}
        .options=${["", "@grey", "@green", "@blue"].map((value) => ({
          name: this.#term(value ? value.replace("@", "theme-") : "automaticColour"), value, selected: value === this._colour,
        }))}
        @change=${(event: Event) => { this._colour = (event.target as HTMLSelectElement).value; }}
      ></uui-select>
      <uui-input label=${this.#term("customColour")} placeholder="#RRGGBB"
        .value=${this._colour.startsWith("#") ? this._colour : ""}
        @input=${(event: InputEvent) => { this._colour = (event.target as HTMLInputElement).value.trim(); }}
      ></uui-input>
    </div>`;
  }
  get #valid(): boolean {
    if (this.request.kind === "save")
      return Boolean(this._title.trim()) && this._title.trim().length <= 120 && this._description.length <= 2000
        && (!this.request.isSaved || !this.request.isOwner
          || this._title.trim() !== this.request.title || this._description.trim() !== this.request.description);
    if (this.request.kind === "dashboard")
      return /^#[0-9a-fA-F]{6}$/.test(this._colour)
        || ["", "@grey", "@green", "@blue"].includes(this._colour);
    return this._draft.columns.length <= 512
      && (!this.#filterColumn || columnFilterValid(this.#filterColumn));
  }
  /** Confirm, then clear the filter and apply in one step - no second Apply needed. */
  async #removeFilter(): Promise<void> {
    if (this.busy) return;
    if (this.#modalManager) {
      const modal = this.#modalManager.open(this, UMB_CONFIRM_MODAL, {
        data: {
          headline: this.#term("removeFilterTitle"),
          content: this.#term("removeFilterConfirm", this.#requestName()),
          color: "danger",
          confirmLabel: this.#term("removeFilter"),
        },
      });
      try {
        await modal.onSubmit();
      } catch {
        return;
      }
    }
    this._expressionMode = false;
    this.#setFilter({ filter: null });
    this.#apply();
  }
  #apply(): void {
    if (!this.#valid || this.busy) return;
    const detail: PanelSubmit = {
      definition: this._draft, title: this._title.trim(), description: this._description.trim(),
      isShared: this._shared, icon: this._icon, colour: this._colour,
    };
    this.dispatchEvent(new CustomEvent<PanelSubmit>("panel-apply", { detail, bubbles: true, composed: true }));
  }
  render() {
    if (!this._draft) return "";
    const kind = this.request.kind;
    const headline = kind === "save"
      ? this.#term("saveAuditTitle")
      : kind === "dashboard"
        ? this.#term("dashboardAppearance")
        : this.#term("filterPanelTitle", this.#requestName());
    const action = kind === "save"
      ? this.embedded ? "save" : "saveAuditTitle"
      : kind === "columns" ? "applyColumns" : kind === "dashboard" ? "applyAppearance" : "applyFilters";
    const contents = html`
      <div ?inert=${this.busy}>
        ${kind === "types" ? this.#types() : kind === "cultures" ? this.#cultures()
          : kind === "columns" ? this.#columns() : kind === "filter" ? this.#filter()
          : kind === "save" ? this.#metadata() : this.#dashboard()}
      </div>
      ${kind === "filter" ? "" : html`<div class="actions">
        <span></span>
        <uui-button look="secondary" label=${this.#term("cancel")} ?disabled=${this.busy}
          @click=${() => this.dispatchEvent(new CustomEvent("panel-cancel", { bubbles: true, composed: true }))}
        >${this.#term("cancel")}</uui-button>
        <uui-button class="apply" look="primary" color="positive" label=${this.#term(action)}
          ?disabled=${!this.#valid || this.busy} .state=${this.busy ? "waiting" : undefined}
          @click=${this.#apply}>${this.#term(action)}</uui-button>
      </div>`}
    `;
    return this.embedded ? contents : html`<uui-box .headline=${headline}>${contents}</uui-box>`;
  }
  static styles = css`
    :host { display: block; margin-top: var(--uui-size-space-5); }
    :host([embedded]) { margin: 0; }
    .type-lists { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: var(--uui-size-space-6); }
    h3 { margin-top: 0; display: flex; justify-content: space-between; gap: var(--uui-size-space-3); }
    small { color: var(--uui-color-text-alt); font-weight: normal; }
    .search { display: block; width: 100%; }
    .selection-actions { display: flex; gap: var(--uui-size-space-3); margin-block: var(--uui-size-space-3); }
    .choices { display: flex; flex-direction: column; gap: var(--uui-size-space-3); max-height: 35vh; overflow: auto; }
    .columns { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 20em), 1fr)); }
    .actions { display: flex; gap: var(--uui-size-space-3); margin-top: var(--uui-size-space-5); flex-wrap: wrap; }
    .actions > span { flex: 1; }
    .filter-fields { display: flex; align-items: center; flex-wrap: wrap; gap: var(--uui-size-space-4); }
    .filter-fields uui-input { flex: 1; min-width: min(100%, 18em); }
    .expression-notes { display: flex; flex-direction: column; gap: var(--uui-size-space-1); flex-basis: 100%; }
    .expression-notes ul { margin: 0; padding-left: var(--uui-size-space-5); }
    .expression-notes code {
      background: var(--uui-color-surface-alt);
      padding: 0 var(--uui-size-space-1);
      border-radius: var(--uui-border-radius);
      font-size: inherit;
    }
    .filter-actions { display: flex; align-items: center; gap: var(--uui-size-space-3); margin-left: auto; flex-wrap: wrap; }
    .metadata uui-input, .metadata uui-textarea { width: 100%; }
    .metadata umb-property-layout { padding-top: 0; }
    @media (max-width: 700px) { .type-lists { grid-template-columns: minmax(0, 1fr); } }
  `;
}
