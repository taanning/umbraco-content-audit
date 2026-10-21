import {
  css,
  html,
  customElement,
  property,
  state,
} from "@umbraco-cms/backoffice/external/lit";
import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { UMB_NOTIFICATION_CONTEXT } from "@umbraco-cms/backoffice/notification";
import { UMB_MODAL_MANAGER_CONTEXT, UMB_CONFIRM_MODAL } from "@umbraco-cms/backoffice/modal";
import type {
  AuditCatalogResponse,
  AuditDefinition,
  AuditResponse,
  AuditRunProgress,
  AuditRunResults,
  PropertyAuditTypeModel as AuditTypeModel,
  AuditTypeSelection,
  AuditColumnDefinition,
  AuditFilterLink,
} from "../api/index.js";
import {
  normalizeError,
  PropertyAuditRepository,
} from "../repository/property-audit.repository.js";
import "./audit-results.element.js";
import "./audit-config-panel.element.js";
import {
  activeFilterKeys, BASE_COLUMNS, newTableDefinition, normalizedFilterChain,
  querySignature, reconcileColumns, sanitizeHiddenBaseColumns, tableDefinition,
  visibleSortKey,
  type PanelRequest, type PanelSubmit,
} from "./audit-table-state.js";

const MAX_COLUMNS = 512;
const PAGE_TAKE = 100;

/** Column edit state; filter absent = no filter. */
interface ColumnState {
  alias: string;
  valueType: string;
  filterMode: "none" | "simple" | "expression";
  operator: string;
  value: string;
  expression: string;
}

@customElement("property-audit-builder")
export class AuditBuilderElement extends UmbLitElement {
  #repository = new PropertyAuditRepository();
  #notificationContext?: typeof UMB_NOTIFICATION_CONTEXT.TYPE;
  #modalManager?: typeof UMB_MODAL_MANAGER_CONTEXT.TYPE;

  /** The audit being edited, or null for a new audit. */
  @property({ attribute: false })
  audit: AuditResponse | null = null;

  /** v2 catalog with document + element types and languages. */
  @property({ attribute: false })
  catalog?: AuditCatalogResponse;

  @state() private _allDocumentTypes = false;
  @state() private _allElementTypes = false;
  @state() private _selectedTypes: AuditTypeSelection[] = [];
  @state() private _columns: ColumnState[] = [];
  @state() private _culture = "";
  @state() private _allCultures = false;
  @state() private _cultures: string[] | null = null;
  @state() private _rowFilters: AuditColumnDefinition[] = [];
  @state() private _hiddenBaseColumns: string[] = [];
  @state() private _filterChain: AuditFilterLink[] = [];
  @state() private _chainExpanded = false;
  #chainDragIndex: number | null = null;
  @state() private _panel?: PanelRequest;
  @state() private _runError = "";
  #newInitialized = false;

  // Saved-audit bookkeeping.
  @state() private _isShared = false;
  @state() private _isPinned = false;
  @state() private _icon = "icon-search";
  @state() private _colour = "";
  /** Latched version; never advanced by list refreshes. */
  #editVersion: number | null = null;
  /** Incremented on every definition/metadata edit; stale saves never apply. */
  #draftRevision = 0;
  /** Incremented on every preference edit; stale pref responses are dropped. */
  #prefRevision = 0;
  @state() private _dirty = false;
  @state() private _saving = false;

  // Run lifecycle.
  @state() private _running = false;
  @state() private _progress?: AuditRunProgress;
  @state() private _results?: AuditRunResults;
  @state() private _resultsLoading = false;
  @state() private _contentSearch = "";
  @state() private _sortBy = "kind";
  @state() private _direction: "asc" | "desc" = "asc";
  @state() private _skip = 0;
  @state() private _runCooldown = false;
  #runGeneration = 0;
  #abortController?: AbortController;
  #runId?: string;
  #resultsSeq = 0;
  #resultsAbort?: AbortController;
  #runQueue: Promise<void> = Promise.resolve();
  #releaseQueue: Promise<void> = Promise.resolve();
  #prefQueue: Promise<void | boolean> = Promise.resolve();

  constructor() {
    super();
    this.consumeContext(UMB_NOTIFICATION_CONTEXT, (context) => {
      this.#notificationContext = context;
    });
    this.consumeContext(UMB_MODAL_MANAGER_CONTEXT, (context) => {
      this.#modalManager = context;
    });
  }

  connectedCallback(): void {
    super.connectedCallback();
    if (this.audit && this.#editVersion === null) {
      this.#applyAudit(this.audit);
    }
    this.#freshnessTimer = window.setInterval(() => this.requestUpdate(), 30000);
  }

  willUpdate(): void {
    // The host may bind `.audit` after connection (lit property timing); apply
    // it only on the null→audit transition so later response swaps don't reset
    // the draft.
    if (this.audit && this.#editVersion === null) {
      this.#applyAudit(this.audit);
    } else if (!this.audit && this.catalog && !this.#newInitialized) {
      this.#loadDefinition(newTableDefinition(this.catalog));
      this.#newInitialized = true;
    }
  }

  updated(): void {
    if (!this.#autoRan && this.catalog && this.#definition && !this.#runDisabled) {
      this.#autoRan = true;
      void this.#onRun(Boolean(this.audit));
    }
  }

  #autoRan = false;

  /** CompletedUtc of this session's run, else the saved audit's last count run. */
  get #lastRunUtc(): string | undefined {
    return this._progress?.completedUtc ?? this.audit?.dashboard.lastCompletedUtc ?? undefined;
  }

  /** Relative age like "45s", "3m 12s", "1h 22m", "2d 4h". */
  #formatAgo(iso: string): string {
    let rest = Math.max(0, Math.round((Date.now() - Date.parse(iso)) / 1000));
    const parts: string[] = [];
    for (const [label, size] of [["d", 86400], ["h", 3600], ["m", 60], ["s", 1]] as const) {
      const count = Math.floor(rest / size);
      if (count > 0 || (parts.length === 0 && label === "s")) {
        parts.push(`${count}${label}`);
        rest -= count * size;
      }
      if (parts.length === 2) break;
    }
    return parts.join(" ");
  }

  disconnectedCallback(): void {
    window.clearInterval(this.#freshnessTimer);
    void this.#cancelRun();
    super.disconnectedCallback();
  }

  #freshnessTimer?: number;

  #applyAudit(audit: AuditResponse): void {
    this.#editVersion = audit.version;
    this.#loadDefinition(tableDefinition(audit.definition));
    this._isShared = audit.isShared;
    this._isPinned = audit.dashboard.isPinned;
    this._icon = audit.dashboard.icon;
    this._colour = audit.dashboard.colour ?? "";
    this._dirty = false;
    void this.#cancelRun();
  }

  #loadDefinition(definition: AuditDefinition): void {
    this._allDocumentTypes = definition.allDocumentTypes;
    this._allElementTypes = definition.allElementTypes ?? false;
    this._selectedTypes = definition.types.map((type) => ({ ...type }));
    this._allCultures = definition.allCultures ?? false;
    this._cultures = definition.cultures ?? null;
    this._rowFilters = definition.rowFilters ?? [];
    this._hiddenBaseColumns = sanitizeHiddenBaseColumns(definition.hiddenBaseColumns);
    this._columns = definition.columns.map((column) => ({
      alias: column.alias,
      valueType: column.valueType,
      filterMode: column.filter?.expression != null
        ? "expression"
        : column.filter
          ? "simple"
          : "none",
      operator: column.filter?.operator ?? "hasText",
      value: column.filter?.value ?? "",
      expression: column.filter?.expression ?? "",
    }));
    this._culture = definition.culture ?? "";
    this._filterChain = normalizedFilterChain(definition.columns, definition.filterChain);
    const sort = visibleSortKey(definition, this._sortBy);
    if (sort !== this._sortBy) {
      this._sortBy = sort;
      this._direction = "asc";
    }
  }

  // ---- derived metadata --------------------------------------------------------

  get #selectedTypeModels(): AuditTypeModel[] {
    if (!this.catalog) return [];
    const selected = new Map(
      this._selectedTypes.map((t) => [`${t.kind}:${t.alias}`, true] as const),
    );
    return this.catalog.types.filter((type) => {
      const kind = type.kind === "element" ? "element" : "document";
      return selected.has(`${kind}:${type.alias}`)
        || (this._allDocumentTypes && kind === "document")
        || (this._allElementTypes && kind === "element");
    });
  }

  /**
   * Property union across selected row types, deduplicated by alias. Mixed
   * value-type hints collapse to text; segment variance is an OR across types.
   */
  get #availableColumns() {
    const seen = new Map<
      string,
      { alias: string; name: string; hint: string; segment: boolean }
    >();
    for (const type of this.#selectedTypeModels) {
      for (const property of type.properties) {
        const existing = seen.get(property.alias);
        if (!existing) {
          seen.set(property.alias, {
            alias: property.alias,
            name: property.name,
            hint: property.valueTypeHint,
            segment: property.variesBySegment,
          });
        } else {
          if (existing.hint !== property.valueTypeHint) {
            existing.hint = "text";
          }
          existing.segment = existing.segment || property.variesBySegment;
        }
      }
    }
    return [...seen.values()];
  }

  get #segmentUnsupported(): boolean {
    return this._columns.some((column) => {
      const meta = this.#availableColumns.find((p) => p.alias === column.alias);
      return meta?.segment === true;
    });
  }

  /** Current definition, or null while locally invalid. */
  get #definition(): AuditDefinition | null {
    if (this._columns.length > MAX_COLUMNS) {
      return null;
    }
    if (this.#segmentUnsupported) return null;
    for (const column of this._columns) {
      if (column.filterMode === "simple") {
        const needsValue = !["isEmpty", "hasText"].includes(column.operator);
        if (needsValue && column.value.trim() === "") return null;
        if (column.value.length > 2000) return null;
      }
      if (column.filterMode === "expression") {
        const trimmed = column.expression.trim();
        if (trimmed === "" || column.expression.length > 2000) return null;
      }
    }
    return this.#definitionSnapshot();
  }

  #definitionSnapshot(): AuditDefinition {
    const columns: AuditColumnDefinition[] = this._columns.map((column) => ({
      alias: column.alias,
      valueType: column.valueType,
      filter:
        column.filterMode === "none"
          ? null
          : column.filterMode === "expression"
            ? { operator: "equals", value: null, expression: column.expression }
            : {
                operator: column.operator,
                value: ["isEmpty", "hasText"].includes(column.operator)
                  ? null
                  : column.value,
                expression: null,
              },
    }));
    return {
      schemaVersion: 3,
      allDocumentTypes: this._allDocumentTypes,
      allElementTypes: this._allElementTypes,
      types: this._selectedTypes.map((t) => ({ kind: t.kind, alias: t.alias })),
      columns,
      culture: this._culture === "" ? null : this._culture,
      allCultures: this._allCultures,
      cultures: this._cultures,
      rowFilters: this._rowFilters,
      filterChain: normalizedFilterChain(columns, this._filterChain),
      hiddenBaseColumns: this._hiddenBaseColumns,
    };
  }

  get #runDisabled(): boolean {
    return this._running || this._saving || !this.catalog || this.#definition === null;
  }

  get #saveDisabled(): boolean {
    return this._running || this._saving || !this.catalog || this.#definition === null
      || Boolean(this.audit?.isOwner && !this._dirty);
  }

  #syncDirty(): void {
    this._dirty = !this.audit || querySignature(this.#definitionSnapshot(), this._isShared)
      !== querySignature(this.audit.definition, this.audit.isShared);
  }

  #markDirty(): void {
    this.#syncDirty();
    this.#draftRevision++;
    void this.#cancelRun();
    // Definition changed: stale retained results must not keep rendering.
    this._progress = undefined;
    this._results = undefined;
    this._resultsLoading = false;
    this._skip = 0;
  }

  #linkDetail(column: AuditColumnDefinition | undefined): string {
    const filter = column?.filter;
    if (!filter) return "";
    return filter.expression != null
      ? filter.expression
      : ["isEmpty", "hasText"].includes(filter.operator)
        ? this.localize.term(`propertyAudit_op_${filter.operator}`)
        : `${this.localize.term(`propertyAudit_op_${filter.operator}`)} ${JSON.stringify(filter.value ?? "")}`;
  }

  #describeFilter(column: AuditColumnDefinition, name: string): string {
    const detail = this.#linkDetail(column);
    return detail ? `${name}: ${detail}` : name;
  }

  #propertyName(alias: string): string {
    return this.#availableColumns.find((column) => column.alias === alias)?.name ?? alias;
  }

  /** Left-to-right sentence of the chain, e.g. "Score Greater than "80" OR Title Is empty". */
  #chainSentence(): string {
    const snapshot = this.#definitionSnapshot();
    const chain = this.#chain;
    return chain.map((link, index) => {
      const column = snapshot.columns.find((item) => item.alias === link.column);
      const name = this.#propertyName(link.column);
      const detail = column ? this.#linkDetail(column) : "";
      const describe = detail ? `${name} ${detail}` : name;
      if (index === 0) return describe;
      return `${this.#joinerTerm(chain, index)} ${describe}`;
    }).join(" ");
  }

  #joinerTerm(chain: AuditFilterLink[], index: number): string {
    return this.localize.term(chain[index - 1].joiner === "or"
      ? "propertyAudit_joinerOr" : "propertyAudit_joinerAnd");
  }

  /** Operator part of a link's filter, rendered green; value stays normal text. */
  #linkDetailHtml(column: AuditColumnDefinition | undefined) {
    const filter = column?.filter;
    if (!filter) return "";
    if (filter.expression != null) {
      return html`<span class="chain-good">${filter.expression}</span>`;
    }
    const op = html`<span class="chain-good">${this.localize.term(`propertyAudit_op_${filter.operator}`)}</span>`;
    return ["isEmpty", "hasText"].includes(filter.operator)
      ? op
      : html`${op} ${JSON.stringify(filter.value ?? "")}`;
  }

  /** Chain sentence as lit parts so the operator renders green. */
  #chainSentenceHtml() {
    const snapshot = this.#definitionSnapshot();
    const chain = this.#chain;
    return chain.map((link, index) => {
      const column = snapshot.columns.find((item) => item.alias === link.column);
      const name = this.#propertyName(link.column);
      const detail = this.#linkDetailHtml(column);
      const describe = detail === "" ? html`${name}` : html`${name} ${detail}`;
      if (index === 0) return describe;
      return html` ${this.#joinerTerm(chain, index)} ${describe}`;
    });
  }

  #filterSummary(): string[] {
    const typeNames = this.#selectedTypeModels.map((type) => type.name);
    const typeText = this._allDocumentTypes && this._allElementTypes
      ? this.localize.term("propertyAudit_all")
      : typeNames.length === 0
        ? this.localize.term("propertyAudit_none")
        : typeNames.slice(0, 3).join(", ")
          + (typeNames.length > 3 ? ` +${typeNames.length - 3}` : "");

    const cultureName = (value: string) => value === ""
      ? this.localize.term("propertyAudit_invariantCulture")
      : this.catalog?.languages.find((language) => language.isoCode === value)?.name ?? value;
    const cultures = this._allCultures ? [this.localize.term("propertyAudit_all")]
      : (this._cultures ?? []).map(cultureName);
    const cultureText = cultures.length === 0
      ? this.localize.term("propertyAudit_none")
      : cultures.slice(0, 3).join(", ")
        + (cultures.length > 3 ? ` +${cultures.length - 3}` : "");

    const baseLabel = (alias: string) => {
      const column = BASE_COLUMNS.find((item) => item.key === alias);
      return column ? this.localize.term(`propertyAudit_${column.label}`) : alias;
    };

    const chainSentence = this.#chainSentence();
    return [
      `${this.localize.term("propertyAudit_type")}: ${typeText}`,
      `${this.localize.term("propertyAudit_culture")}: ${cultureText}`,
      ...this._rowFilters.filter((column) => column.filter)
        .map((column) => this.#describeFilter(column, baseLabel(column.alias))),
      ...(chainSentence === "" ? []
        : [this.#chain.length > 1 ? `(${chainSentence})` : chainSentence]),
    ];
  }

  // ---- save -------------------------------------------------------------------

  async #onSave(result: PanelSubmit, saveAsCopy = false, metadataOnly = false): Promise<boolean> {
    const definition = metadataOnly && this.audit ? this.audit.definition : this.#definition;
    if (!definition || this._saving || !result.title.trim()) return false;

    // Latch identity/ownership before awaiting anything.
    const source = this.audit;
    if (metadataOnly && !source?.isOwner) return false;
    const sourceId = source?.id;
    const sourceIsOwner = source?.isOwner ?? false;
    const editVersion = this.#editVersion;

    // Copies and non-owner saves are always private by default.
    const isShared =
      saveAsCopy || (source && !sourceIsOwner)
        ? false
        : metadataOnly ? source!.isShared : result.isShared;
    const snapshot = {
      title: result.title,
      description: result.description,
      isShared,
      definition,
      id: sourceId,
      version: saveAsCopy ? null : editVersion,
      revision: this.#draftRevision,
    };

    this._saving = true;
    try {
      const saved =
        !source || saveAsCopy || !sourceIsOwner
          ? await this.#repository.createAudit({
              title: snapshot.title,
              description: snapshot.description,
              isShared: snapshot.isShared,
              definition: snapshot.definition,
            })
          : await this.#repository.updateAudit(snapshot.id!, {
              title: snapshot.title,
              description: snapshot.description,
              isShared: snapshot.isShared,
              definition: snapshot.definition,
              version: snapshot.version,
            });

      if (!this.isConnected) return false; // unmounted while saving
      // The saved record is persisted — propagate identity/version so a later
      // update/run targets the server row, but only apply the response to the
      // draft fields when no edit happened while the request was in flight.
      this.#editVersion = saved.version;
      this.audit = saved;
      if (this.#draftRevision === snapshot.revision) {
        if (!metadataOnly) this._isShared = saved.isShared;
        this._isPinned = saved.dashboard.isPinned;
        this._icon = saved.dashboard.icon;
        this._colour = saved.dashboard.colour ?? "";
        if (metadataOnly) this.#syncDirty();
        else this._dirty = false;
        this._panel = undefined;
      } else {
        // Newer edits exist: keep the current draft and mark it dirty.
        this._dirty = true;
      }
      this.dispatchEvent(
        new CustomEvent("audit-saved", {
          detail: saved,
          bubbles: true,
          composed: true,
        }),
      );
      this.#notificationContext?.peek("positive", {
        data: {
          headline: this.localize.term("propertyAudit_auditSaved"),
          message: "",
        },
      });
      return true;
    } catch (error) {
      const apiError = normalizeError(error);
      this.#notificationContext?.peek("danger", {
        data: {
          headline: this.localize.term("propertyAudit_saveFailed"),
          message:
            apiError.status === 409
              ? this.localize.term("propertyAudit_savedStale")
              : apiError.message,
        },
      });
      return false;
    } finally {
      this._saving = false;
    }
  }

  #saveQuery(): void {
    if (!this.audit?.isOwner || !this._dirty || this._panel || this.#saveDisabled) return;
    const queryChanged = querySignature(this.#definitionSnapshot(), false)
      !== querySignature(this.audit.definition, false);
    void this.#onSave({
      definition: this.#definitionSnapshot(), title: this.audit.title,
      description: this.audit.description ?? "", isShared: this._isShared,
      icon: this._icon, colour: this._colour,
    }).then((saved) => {
      if (saved && queryChanged) void this.#onRun(true);
    });
  }

  #cancelChanges(): void {
    if (!this.audit || !this._dirty || this._panel || this._saving || this._running) return;
    this.#applyAudit(this.audit);
    void this.#onRun(true);
  }

  #openPanel(kind: PanelRequest["kind"], alias?: string, base = false): void {
    if (this._panel || this._saving || this._running) return;
    this._panel = {
      kind, alias, base, definition: this.#definitionSnapshot(),
      title: this.audit?.title ?? "", description: this.audit?.description ?? "",
      isShared: this._isShared, isOwner: this.audit?.isOwner ?? false,
      isSaved: Boolean(this.audit), icon: this._icon, colour: this._colour,
    };
    void this.updateComplete.then(() =>
      this.shadowRoot?.querySelector("property-audit-config-panel")?.scrollIntoView({ block: "nearest" }));
  }

  async #applyPanel(event: CustomEvent<PanelSubmit>): Promise<void> {
    const panel = this._panel;
    if (!panel || this._saving || this._running) return;
    if (panel.kind === "save") {
      await this.#onSave(event.detail, false, Boolean(this.audit?.isOwner));
      return;
    }
    if (panel.kind === "dashboard") {
      this._icon = event.detail.icon;
      this._colour = event.detail.colour;
      this._saving = true;
      const saved = await this.#persistPreference();
      this._saving = false;
      if (saved) this._panel = undefined;
      return;
    }
    const previous = panel.definition;
    const dirty = this._dirty;
    const previousSort = this._sortBy;
    const previousDirection = this._direction;
    const next = panel.kind === "types" && this.catalog
      ? reconcileColumns(this.catalog, previous, event.detail.definition) : event.detail.definition;
    this.#loadDefinition(next);
    this.#markDirty();
    const succeeded = await this.#onRun();
    if (succeeded) {
      this._panel = undefined;
    } else {
      this.#loadDefinition(previous);
      this._sortBy = previousSort;
      this._direction = previousDirection;
      this._dirty = dirty;
    }
  }

  async #clearFilters(): Promise<void> {
    if (this._panel || !this.#modalManager) return;
    const modal = this.#modalManager.open(this, UMB_CONFIRM_MODAL, {
      data: {
        headline: this.localize.term("propertyAudit_clearFiltersTitle"),
        content: this.localize.term("propertyAudit_clearFiltersConfirm"),
        color: "danger",
        confirmLabel: this.localize.term("propertyAudit_clearFilters"),
      },
    });
    try {
      await modal.onSubmit();
    } catch {
      return;
    }

    this._columns = this._columns.map((column) => ({ ...column, filterMode: "none" }));
    this._rowFilters = [];
    this._allDocumentTypes = true;
    this._allElementTypes = true;
    this._selectedTypes = [];
    this._allCultures = true;
    this._cultures = null;
    this.#markDirty();
    void this.#onRun();
  }

  // ---- filter chain -----------------------------------------------------------

  /** Chain in display order, synced against the columns that still have filters. */
  get #chain(): AuditFilterLink[] {
    return normalizedFilterChain(this.#definitionSnapshot().columns, this._filterChain);
  }

  get #chainBusy(): boolean {
    return Boolean(this._panel) || this._running || this._resultsLoading || this._saving;
  }

  #applyChainEdit(): void {
    this.#markDirty();
    void this.#onRun();
  }

  #setJoiner(alias: string, joiner: string): void {
    if (this.#chainBusy) return;
    this._filterChain = this.#chain.map((link) =>
      link.column === alias ? { ...link, joiner } : link);
    this.#applyChainEdit();
  }

  #moveLink(from: number, to: number): void {
    if (this.#chainBusy || from === to) return;
    const columns = this.#definitionSnapshot().columns;
    const reordered = [...this.#chain];
    if (from < 0 || from >= reordered.length) return;
    const [moved] = reordered.splice(from, 1);
    reordered.splice(to, 0, moved);
    this._filterChain = normalizedFilterChain(columns, reordered);
    this.#applyChainEdit();
  }

  async #removeChainLink(alias: string): Promise<void> {
    if (this.#chainBusy || !this.#modalManager) return;
    const modal = this.#modalManager.open(this, UMB_CONFIRM_MODAL, {
      data: {
        headline: this.localize.term("propertyAudit_removeFilterTitle"),
        content: this.localize.term("propertyAudit_removeFilterConfirm",
          this.#propertyName(alias)),
        color: "danger",
        confirmLabel: this.localize.term("propertyAudit_removeFilter"),
      },
    });
    try {
      await modal.onSubmit();
    } catch {
      return;
    }
    this._filterChain = this.#chain.filter((link) => link.column !== alias);
    this._columns = this._columns.map((column) =>
      column.alias === alias ? { ...column, filterMode: "none" } : column);
    this.#applyChainEdit();
  }

  async #onDelete(): Promise<void> {
    if (!this.audit?.isOwner || this.#editVersion === null || this._saving) {
      return;
    }
    if (!this.#modalManager) {
      this.#notificationContext?.peek("danger", {
        data: {
          headline: this.localize.term("propertyAudit_deleteFailed"),
          message: this.localize.term("propertyAudit_deleteAuditTitle"),
        },
      });
      return;
    }
    const { id } = this.audit;
    const version = this.#editVersion;
    const modal = this.#modalManager.open(this, UMB_CONFIRM_MODAL, {
      data: {
        headline: this.localize.term("propertyAudit_deleteAuditTitle"),
        content: this.localize.term(
          "propertyAudit_deleteAuditConfirm",
          this.audit.title,
        ),
        color: "danger",
        confirmLabel: this.localize.term("propertyAudit_delete"),
      },
    });
    try {
      await modal.onSubmit();
    } catch {
      return; // cancelled: nothing is deleted
    }
    try {
      await this.#repository.deleteAudit(id, version);
      this.dispatchEvent(
        new CustomEvent("audit-deleted", { bubbles: true, composed: true }),
      );
    } catch (error) {
      this.#notificationContext?.peek("danger", {
        data: {
          headline: this.localize.term("propertyAudit_deleteFailed"),
          message: normalizeError(error).message,
        },
      });
    }
  }

  /**
   * Serializes preference writes so rapid icon/colour/pin changes don't race.
   * Each edit bumps #prefRevision; a response only touches the controls when
   * it still describes the latest edit on the same audit.
   */
  #persistPreference(): Promise<void | boolean> {
    const audit = this.audit;
    if (!audit) return Promise.resolve(false);
    const auditId = audit.id;
    const revision = ++this.#prefRevision;
    const isPinned = this._isPinned;
    const icon = this._icon || "icon-search";
    const colour = this._colour;
    this.#prefQueue = this.#prefQueue.then(async () => {
      try {
        const updated = await this.#repository.setAuditDashboardPreference(
          auditId,
          { isPinned, icon, colour: colour === "" ? null : colour },
        );
        if (!this.isConnected) return;
        // Parent sees the preference update but never the draft identity.
        this.dispatchEvent(
          new CustomEvent("audit-preference-changed", {
            detail: updated,
            bubbles: true,
            composed: true,
          }),
        );
        if (revision === this.#prefRevision && this.audit?.id === auditId) {
          this._isPinned = updated.dashboard.isPinned;
          this._icon = updated.dashboard.icon;
          this._colour = updated.dashboard.colour ?? "";
        }
        return true;
      } catch (error) {
        if (!this.isConnected) return;
        this.#notificationContext?.peek("danger", {
          data: {
            headline: this.localize.term("propertyAudit_saveFailed"),
            message: normalizeError(error).message,
          },
        });
        return false;
      }
    });
    return this.#prefQueue;
  }

  #onPinToggle(checked: boolean): void {
    if (!this.audit) return;
    this._isPinned = checked;
    if (!checked && this._panel?.kind === "dashboard") this._panel = undefined;
    this.#persistPreference();
  }

  // ---- run lifecycle ------------------------------------------------------------

  async #cancelRun(): Promise<void> {
    this.#runGeneration++;
    this.#resultsSeq++;
    this.#abortController?.abort();
    this.#abortController = undefined;
    this.#resultsAbort?.abort();
    this.#resultsAbort = undefined;
    const runId = this.#runId;
    this.#runId = undefined;
    this._running = false;
    const release = this.#releaseQueue.then(() => runId
      ? this.#repository.deleteRun(runId).catch(() => undefined)
      : undefined);
    this.#releaseQueue = release;
    await release;
  }

  async #onRun(updateSavedSummary = false): Promise<boolean> {
    const run = this.#runQueue.then(() => this.#performRun(updateSavedSummary));
    this.#runQueue = run.then(() => undefined, () => undefined);
    return run;
  }

  async #performRun(updateSavedSummary: boolean): Promise<boolean> {
    const definition = this.#definition;
    if (!definition || this._running || this._saving) return false;

    // Clean up any previously allocated run before allocating a new one.
    await this.#cancelRun();
    const generation = this.#runGeneration;
    this.#abortController = new AbortController();
    const signal = this.#abortController.signal;
    this._running = true;
    this._runError = "";
    this._progress = undefined;
    this._results = undefined;
    this._skip = 0;

    try {
      // Only an explicit Run refreshes a saved summary; filter previews
      // always post a draft definition.
      const useSaved =
        updateSavedSummary && this.audit && !this._dirty && this.#editVersion !== null;
      let progress = await this.#repository.createRun(
        useSaved
          ? { auditId: this.audit!.id }
          : { definition },
        signal,
      );

      // Track the allocated run immediately so cancel/back/disconnect can
      // release it even while it is still running.
      if (generation !== this.#runGeneration) {
        void this.#repository.deleteRun(progress.runId).catch(() => undefined);
        return false;
      }
      this.#runId = progress.runId;

      while (progress.status === "running") {
        this._progress = progress;
        progress = await this.#repository.advanceRun(progress.runId, signal);
        if (generation !== this.#runGeneration) return false;
      }

      this._progress = progress;
      await this.#loadResults();
      if (generation !== this.#runGeneration) return false;
      this.dispatchEvent(
        new CustomEvent("run-finished", {
          detail: progress,
          bubbles: true,
          composed: true,
        }),
      );
      return !this._runError;
    } catch (error) {
      if (generation !== this.#runGeneration) return false;
      const apiError = normalizeError(error);
      if (apiError.aborted) return false;
      this._runError = apiError.message;
      // A failed run is not pageable — release the retained server-side run.
      const failedRunId = this.#runId;
      this.#runId = undefined;
      if (failedRunId) {
        void this.#repository.deleteRun(failedRunId).catch(() => undefined);
      }
      this.#notificationContext?.peek("danger", {
        data: {
          headline: this.localize.term("propertyAudit_runFailed"),
          message: apiError.message,
        },
      });
      return false;
    } finally {
      if (generation === this.#runGeneration) {
        this._running = false;
        // Brief lockout so a fast repeat click cannot spam a new scan.
        this._runCooldown = true;
        window.setTimeout(() => { this._runCooldown = false; }, 2000);
      }
    }
  }

  async #loadResults(): Promise<void> {
    const runId = this.#runId;
    if (!runId) return;
    const generation = this.#runGeneration;
    // Each results request supersedes any in-flight one; out-of-order
    // responses are dropped by the sequence check.
    const seq = ++this.#resultsSeq;
    this.#resultsAbort?.abort();
    this.#resultsAbort = new AbortController();
    const signal = this.#resultsAbort.signal;
    const params = {
      skip: this._skip,
      take: PAGE_TAKE,
      sortBy: this._sortBy,
      direction: this._direction,
    };
    this._resultsLoading = true;
    try {
      const results = await this.#repository.getRunResults(runId, params, signal);
      if (generation !== this.#runGeneration || seq !== this.#resultsSeq) {
        return;
      }
      this._results = results;
      if (results.progress.status !== "running") {
        this._progress = results.progress;
      }
    } catch (error) {
      if (generation !== this.#runGeneration) return;
      const apiError = normalizeError(error);
      if (apiError.aborted) return;
      this._runError = apiError.message;
      this.#notificationContext?.peek("danger", {
        data: {
          headline: this.localize.term("propertyAudit_runFailed"),
          message: apiError.message,
        },
      });
    } finally {
      if (generation === this.#runGeneration && seq === this.#resultsSeq) {
        this._resultsLoading = false;
      }
    }
  }

  async #onSort(event: CustomEvent<{ sortBy: string; direction?: "asc" | "desc" }>): Promise<void> {
    if (this._running || this._resultsLoading) return;
    const sortBy = event.detail.sortBy;
    this._direction = event.detail.direction ??
      (this._sortBy === sortBy && this._direction === "asc" ? "desc" : "asc");
    this._sortBy = sortBy;
    this._skip = 0;
    if (this.#runId) {
      // The retained run holds the same rows; re-sorting it is far cheaper
      // than a fresh scan and spends no run slot. A dead retained run falls
      // back to a full rerun.
      await this.#loadResults();
      if (!this._runError) return;
    }
    void this.#onRun();
  }

  #onPage(event: CustomEvent<{ skip: number }>): void {
    if (this._resultsLoading) return;
    this._skip = event.detail.skip;
    void this.#loadResults();
  }

  #onBack(): void {
    void this.#cancelRun();
    this.dispatchEvent(
      new CustomEvent("builder-back", { bubbles: true, composed: true }),
    );
  }

  // ---- rendering ---------------------------------------------------------------

  #linkChip(alias: string) {
    const column = this.#definitionSnapshot().columns.find((item) => item.alias === alias);
    const name = this.#propertyName(alias);
    const detail = this.#linkDetailHtml(column);
    return detail === "" ? html`${name}` : html`${name} ${detail}`;
  }

  #renderFilterChain() {
    const chain = this.#chain;
    if (chain.length === 0) return "";
    const busy = this.#chainBusy;
    const expanded = this._chainExpanded;
    return html`<section class="filter-chain" ?inert=${Boolean(this._panel)}>
      <button type="button" class="chain-toggle" aria-expanded=${expanded ? "true" : "false"}
        ?disabled=${busy}
        @click=${() => { this._chainExpanded = !expanded; }}
      ><uui-icon name=${expanded ? "icon-navigation-down" : "icon-navigation-right"}></uui-icon>
        <strong>${this.localize.term("propertyAudit_activeFilters")} (${chain.length}):</strong>
        <span class="chain-sentence">${this.#chainSentenceHtml()}</span></button>
      ${expanded ? html`<ol class="chain-rows">
            ${chain.map((link, index) => html`
              <li class="chain-row" draggable=${busy ? "false" : "true"}
                @dragstart=${(event: DragEvent) => {
                  this.#chainDragIndex = index;
                  if (event.dataTransfer) event.dataTransfer.effectAllowed = "move";
                }}
                @dragover=${(event: DragEvent) => {
                  event.preventDefault();
                  if (event.dataTransfer) event.dataTransfer.dropEffect = "move";
                }}
                @drop=${(event: DragEvent) => {
                  event.preventDefault();
                  if (this.#chainDragIndex !== null) {
                    this.#moveLink(this.#chainDragIndex, index);
                  }
                }}
                @dragend=${() => { this.#chainDragIndex = null; }}
              >
                <uui-icon class="chain-grip" name="icon-navigation"
                  title=${this.localize.term("propertyAudit_reorderFilter")}></uui-icon>
                <span class="chain-chip">${this.#linkChip(link.column)}</span>
                <uui-button class="chain-edit" look="default" compact
                  label=${this.localize.term("propertyAudit_edit")}
                  title=${this.localize.term("propertyAudit_edit")}
                  ?disabled=${busy}
                  @click=${() => this.#openPanel("filter", link.column)}
                ><uui-icon name="icon-edit"></uui-icon></uui-button>
                ${index < chain.length - 1
                  ? html`<uui-select class="chain-joiner"
                      label=${this.localize.term("propertyAudit_filterJoiner")}
                      .value=${link.joiner ?? "and"}
                      .options=${["or", "and"].map((value) => ({
                        name: this.localize.term(value === "or"
                          ? "propertyAudit_joinerOr" : "propertyAudit_joinerAnd"),
                        value,
                        selected: link.joiner === value,
                      }))}
                      ?disabled=${busy}
                      @change=${(event: Event) => this.#setJoiner(
                        link.column, (event.target as HTMLSelectElement).value)}
                    ></uui-select>`
                  : ""}
                <uui-button class="chain-remove" look="default" compact
                  label=${this.localize.term("propertyAudit_removeFilter")}
                  title=${this.localize.term("propertyAudit_removeFilter")}
                  ?disabled=${busy}
                  @click=${() => void this.#removeChainLink(link.column)}
                ><uui-icon name="icon-wrong"></uui-icon></uui-button>
              </li>`)}
          </ol>
          <p class="chain-hint">${this.localize.term("propertyAudit_chainOrderHint")}</p>` : ""}
    </section>`;
  }

  #renderConfigPanel(embedded = false) {
    if (!this._panel || !this.catalog) return "";
    return html`<property-audit-config-panel
      .request=${this._panel} .catalog=${this.catalog} .busy=${this._saving || this._running}
      ?embedded=${embedded}
      @panel-cancel=${() => {
        if (this._panel?.kind === "dashboard") {
          this._icon = this._panel.icon;
          this._colour = this._panel.colour;
        }
        this._panel = undefined;
        this._runError = "";
      }}
      @panel-apply=${this.#applyPanel}
    ></property-audit-config-panel>`;
  }

  render() {
    return html`
      <div class="toolbar" ?inert=${Boolean(this._panel)}>
        <uui-button
          look="primary"
          label=${this.localize.term("propertyAudit_backToAudits")}
          ?disabled=${Boolean(this._panel) || this._saving}
          @click=${this.#onBack}
          ><uui-icon name="icon-arrow-left"></uui-icon>${this.localize.term("propertyAudit_backToAudits")}</uui-button
        >
        <uui-button class="run-audit" look="primary" color="positive"
          label=${this.localize.term(this._running ? "propertyAudit_running" : "propertyAudit_runAudit")}
          ?disabled=${this.#runDisabled || Boolean(this._panel) || this._runCooldown}
          .state=${this._running ? "waiting" : undefined} @click=${() => void this.#onRun(true)}
        ><uui-icon name="icon-play"></uui-icon>${this.localize.term(this._running ? "propertyAudit_running" : "propertyAudit_runAudit")}</uui-button>
        <uui-button look="primary" color="positive" class="save-audit"
          label=${this.localize.term(this.audit && !this.audit.isOwner ? "propertyAudit_makeCopy" : "propertyAudit_save")}
          ?disabled=${this.#saveDisabled || Boolean(this._panel)}
          @click=${() => this.audit?.isOwner ? this.#saveQuery() : this.#openPanel("save")}
        ><umb-icon name="icon-save"></umb-icon>
          ${this.localize.term(this.audit && !this.audit.isOwner ? "propertyAudit_makeCopy" : "propertyAudit_save")}
        </uui-button>
        ${this.audit ? html`<uui-button look="outline"
          label=${this.localize.term("propertyAudit_cancel")}
          ?disabled=${!this._dirty || this._saving || this._running || Boolean(this._panel)}
          @click=${this.#cancelChanges}
        ><uui-icon name="icon-undo"></uui-icon>${this.localize.term("propertyAudit_cancel")}</uui-button>
        ${this._dirty ? html`<uui-tag look="secondary" class="dirty-tag">${this.localize.term("propertyAudit_unsavedChanges")}</uui-tag>` : ""}` : ""}
        <div class="toolbar-actions">
          <uui-checkbox label-position="left" label=${this.localize.term("propertyAudit_public")}
            ?checked=${this._isShared}
            ?disabled=${Boolean(this.audit && !this.audit.isOwner) || this._saving || Boolean(this._panel)}
            @change=${(event: Event) => {
              this._isShared = (event.target as HTMLInputElement).checked;
              this.#draftRevision++;
              this.#syncDirty();
            }}
          ></uui-checkbox>
          <uui-checkbox label-position="left" label=${this.localize.term("propertyAudit_showOnDashboard")}
            ?checked=${this._isPinned} ?disabled=${!this.audit || this._saving}
            @change=${(event: Event) => this.#onPinToggle((event.target as HTMLInputElement).checked)}
          ></uui-checkbox>
          ${this.audit && this._isPinned ? html`<uui-button
            look="outline"
            label=${this.localize.term("propertyAudit_dashboardAppearance")}
            ?disabled=${Boolean(this._panel) || this._saving || this._running}
            @click=${() => this.#openPanel("dashboard")}
          >${this.localize.term("propertyAudit_dashboardAppearance")}</uui-button>` : ""}
          ${this.audit?.isOwner
            ? html`<uui-button
                look="primary"
                color="danger"
                label=${this.localize.term("propertyAudit_delete")}
                ?disabled=${this._saving}
                @click=${this.#onDelete}
                ><uui-icon name="icon-wrong"></uui-icon>${this.localize.term("propertyAudit_delete")}</uui-button
              >`
            : ""}
        </div>
      </div>

      ${this._runError ? html`<p role="alert" class="warning">${this._runError}</p>` : ""}
      ${this._columns.length > MAX_COLUMNS ? html`<p role="alert" class="warning">
        ${this.localize.term("propertyAudit_columnLimit", MAX_COLUMNS)}</p>` : ""}
      ${this.#segmentUnsupported ? html`<p role="alert" class="warning">
        ${this.localize.term("propertyAudit_segmentUnsupported")}</p>` : ""}
      ${this.audit || this._panel?.kind === "save" ? html`<div class="audit-summary ${this._panel?.kind === "save" ? "editing" : ""} ${!this.audit ? "required" : ""}"
        ?inert=${Boolean(this._panel && this._panel.kind !== "save")}>
        ${this._panel?.kind === "save" ? this.#renderConfigPanel(true)
          : html`<div class="summary-heading">
              <div class="summary-text">
                <div class="title-row">
                  <h2 class="title">${this.audit ? this.audit.title : this.localize.term("propertyAudit_newAudit")}</h2>
                  ${!this.audit || this.audit.isOwner ? html`<uui-button
                    class="edit-summary" look="default" compact
                    label=${this.localize.term("propertyAudit_editMetadata")}
                    title=${this.localize.term("propertyAudit_editMetadata")}
                    ?disabled=${Boolean(this._panel) || this._saving || this._running || !this.catalog}
                    @click=${() => this.#openPanel("save")}
                  ><uui-icon name="icon-edit"></uui-icon></uui-button>` : ""}
                  ${this._running
                    ? html`<span class="last-run">${this.localize.term("propertyAudit_running")}</span>`
                    : this.#lastRunUtc ? html`<span class="last-run">
                        ${this.localize.term("propertyAudit_lastRunAgo", this.#formatAgo(this.#lastRunUtc))}
                      </span>` : ""}
                </div>
                ${this.audit?.description ? html`<p>${this.audit.description}</p>` : ""}
              </div>
            </div>`}
        ${this.audit && this.#editVersion !== null && this.audit.version !== this.#editVersion
          ? html`<p class="warning" role="alert">
              ${this.localize.term("propertyAudit_savedStale")}
            </p>`
          : ""}
      </div>` : ""}

      ${this.#renderFilterChain()}

      ${this._panel?.kind !== "save" ? this.#renderConfigPanel() : ""}

      <uui-box class="results-box" ?inert=${Boolean(this._panel)}>
            <section>
              <div class="results-heading">
              <h3>
                ${this.localize.term("propertyAudit_results")}${this._results
                  ? html`<span class="results-summary">
                      (${this.localize.term(
                        "propertyAudit_resultsSummary",
                        this._results.progress.scannedCount,
                        this._results.progress.totalCount ??
                          this._results.totalItems,
                      )})
                    </span>`
                  : ""}
              </h3>
              <div class="results-actions">
                <uui-input class="results-search" type="search"
                  label=${this.localize.term("propertyAudit_filterResults")}
                  placeholder=${this.localize.term("propertyAudit_filterResults")}
                  .value=${this._contentSearch}
                  @input=${(event: InputEvent) => {
                    this._contentSearch = (event.target as HTMLInputElement).value;
                  }}
                ></uui-input>
                <uui-button look="secondary" label=${this.localize.term("propertyAudit_doctype")}
                  ?disabled=${Boolean(this._panel) || this._running || this._resultsLoading}
                  @click=${() => this.#openPanel("types")}
                >${this.localize.term("propertyAudit_doctype")}</uui-button>
                <uui-button look="secondary" label=${this.localize.term("propertyAudit_columns")}
                  ?disabled=${Boolean(this._panel) || this._running || this._resultsLoading}
                  @click=${() => this.#openPanel("columns")}
                >${this.localize.term("propertyAudit_columns")}</uui-button>
                <uui-button look="primary" color="danger" label=${this.localize.term("propertyAudit_clearFilters")}
                  ?disabled=${Boolean(this._panel) || this._running || this._resultsLoading
                    || activeFilterKeys(this.#definitionSnapshot()).length === 0}
                  @click=${this.#clearFilters}
                >${this.localize.term("propertyAudit_clearFilters")}</uui-button>
              </div>
              </div>
              <p class="filter-summary">${this.#filterSummary().join(" · ")}</p>
              ${this._running ? html`<uui-loader-bar></uui-loader-bar>
                <p aria-live="polite">${this.localize.term("propertyAudit_running")}
                  ${this._progress ? this.localize.term("propertyAudit_resultsSummary",
                    this._progress.scannedCount, this._progress.matchedCount) : ""}</p>` : ""}
              <property-audit-results
                .results=${this._results}
                .contentSearch=${this._contentSearch}
                .columns=${this._columns.map((column) => column.alias)}
                .columnLabels=${Object.fromEntries(this.#availableColumns.map((column) => [column.alias, column.name]))}
                .hiddenBaseColumns=${this._hiddenBaseColumns}
                .activeFilters=${activeFilterKeys(this.#definitionSnapshot())}
                .typeKeys=${Object.fromEntries((this.catalog?.types ?? [])
                  .map((type) => [`${type.kind}:${type.alias}`, type.unique]))}
                .configurationOpen=${Boolean(this._panel)}
                .sortBy=${this._sortBy}
                .direction=${this._direction}
                .loading=${this._running || this._resultsLoading}
                @results-sort=${this.#onSort}
                @results-page=${this.#onPage}
                @results-filter=${(event: CustomEvent<{ key: string }>) => {
                  const key = event.detail.key;
                  this.#openPanel(key === "culture" ? "cultures" : "filter",
                    key.startsWith("property:") ? key.slice(9) : key, !key.startsWith("property:"));
                }}
                @results-columns=${() => this.#openPanel("columns")}
                @results-clear=${this.#clearFilters}
              ></property-audit-results>
            </section>
          </uui-box>
    `;
  }

  static styles = css`
    :host {
      display: block;
      max-width: 100%;
      min-width: 0;
    }
    .toolbar {
      display: flex;
      align-items: center;
      gap: var(--uui-size-space-3);
      margin-bottom: var(--uui-size-space-5);
      flex-wrap: wrap;
    }
    [inert] {
      opacity: 0.45;
      transition: opacity 0.15s ease-in-out;
    }
    .toolbar-actions {
      margin-left: auto;
      display: flex;
      align-items: center;
      gap: var(--uui-size-space-3);
      position: relative;
    }
    .toolbar uui-button :is(uui-icon, umb-icon) { margin-inline-end: var(--uui-size-space-2); }
    .toolbar .dirty-tag {
      align-self: stretch;
      display: inline-flex;
      align-items: center;
      box-sizing: border-box;
    }
    
    .results-box {
      margin-top: var(--uui-size-space-5);
    }

    .filter-chain {
      margin-top: var(--uui-size-space-5);
      background: var(--uui-color-surface);
      border: var(--uui-size-border-width, 1px) solid var(--uui-color-border);
      border-radius: var(--uui-border-radius);
    }
    .chain-toggle {
      display: flex;
      align-items: baseline;
      gap: var(--uui-size-space-3);
      width: 100%;
      padding: var(--uui-size-space-3) var(--uui-size-space-4);
      border: none;
      border-radius: var(--uui-border-radius);
      background: transparent;
      font: inherit;
      color: inherit;
      text-align: left;
      cursor: pointer;
    }
    .chain-toggle:hover:not(:disabled) {
      color: var(--uui-color-interactive);
    }
    .chain-toggle:disabled {
      cursor: default;
    }
    .chain-toggle uui-icon {
      align-self: center;
    }
    .chain-sentence {
      font-weight: normal;
    }
    .chain-rows {
      list-style: none;
      margin: 0;
      padding: 0 var(--uui-size-space-4) var(--uui-size-space-3);
      display: flex;
      flex-direction: column;
      gap: var(--uui-size-space-2);
      max-width: 46em;
    }
    .chain-row {
      display: flex;
      align-items: center;
      gap: var(--uui-size-space-3);
      padding: var(--uui-size-space-2) var(--uui-size-space-3);
    }
    .chain-grip {
      cursor: grab;
      color: var(--uui-color-text-alt);
    }
    .chain-chip {
      min-width: 0;
    }
    .chain-joiner {
      width: 6em;
      flex-shrink: 0;
    }
    .chain-good {
      color: #0b8152;
    }
    .chain-hint {
      margin: 0;
      padding: 0 var(--uui-size-space-4) var(--uui-size-space-3);
      font-size: var(--uui-type-small-size);
      color: var(--uui-color-text-alt);
    }
    .audit-summary {
      margin-bottom: var(--uui-size-space-4);
    }
    .audit-summary.editing {
      background: var(--uui-color-surface);
      border: var(--uui-size-border-width, 1px) solid var(--uui-color-border);
      border-radius: var(--uui-border-radius);
      padding: var(--uui-size-space-4);
    }
    .audit-summary.required {
      border: var(--uui-size-border-width, 1px) solid var(--uui-color-danger);
      border-radius: var(--uui-border-radius);
      padding: var(--uui-size-space-4);
    }
    .results-heading { display: flex; align-items: center; justify-content: space-between; gap: var(--uui-size-space-4); flex-wrap: wrap; margin-bottom: var(--uui-size-space-4); }
    .results-heading h3 { margin: 0; }
    .results-actions { display: flex; align-items: center; gap: var(--uui-size-space-3); margin-left: auto; flex-wrap: wrap; }
    .results-search { width: 200px; }
    .results-summary {
      font-weight: 400;
      font-size: 0.85em;
      opacity: 0.8;
    }
    .filter-summary {
      margin: calc(var(--uui-size-space-3) * -1) 0 var(--uui-size-space-4);
      color: var(--uui-color-text-alt);
      font-size: var(--uui-type-small-size);
    }
    .title {
      margin: 0;
    }
    .summary-heading { display: flex; align-items: flex-start; gap: var(--uui-size-space-4); }
    .edit-summary { opacity: 0.55; }
    .edit-summary:hover:not([disabled]) { opacity: 1; }
    .summary-text { min-width: 0; }
    .title-row { display: flex; align-items: center; gap: var(--uui-size-space-3); flex-wrap: wrap; }
    .last-run {
      margin-left: auto;
      font-size: var(--uui-type-small-size);
      font-weight: normal;
      color: var(--uui-color-text-alt);
      white-space: nowrap;
    }
    .summary-text p { margin: var(--uui-size-space-3) 0 0; }
    .warning {
      color: var(--uui-color-warning-alt);
    }
  `;
}

export default AuditBuilderElement;

declare global {
  interface HTMLElementTagNameMap {
    "property-audit-builder": AuditBuilderElement;
  }
}
