import {
  css,
  html,
  customElement,
  state,
} from "@umbraco-cms/backoffice/external/lit";
import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { UMB_NOTIFICATION_CONTEXT } from "@umbraco-cms/backoffice/notification";
import { UMB_MODAL_MANAGER_CONTEXT, UMB_CONFIRM_MODAL } from "@umbraco-cms/backoffice/modal";
import type {
  AuditCatalogResponse,
  AuditResponse,
} from "../api/index.js";
import "./audit-overview.element.js";
import "./audit-builder.element.js";
import {
  normalizeError,
  PropertyAuditApiError,
  PropertyAuditRepository,
} from "../repository/property-audit.repository.js";

/**
 * Dashboard orchestrator: the audit overview (home) by default and the audit
 * builder as a local view state. Keeps the existing Content-section dashboard
 * alias/weight; the legacy single-property form was replaced by the v2 flow.
 */
@customElement("property-audit-dashboard")
export class PropertyAuditDashboardElement extends UmbLitElement {
  #repository = new PropertyAuditRepository();
  #notificationContext?: typeof UMB_NOTIFICATION_CONTEXT.TYPE;
  #modalManager?: typeof UMB_MODAL_MANAGER_CONTEXT.TYPE;

  @state() private _view: "home" | "builder" = "home";
  @state() private _audit?: AuditResponse;
  @state() private _audits: AuditResponse[] = [];
  @state() private _catalog?: AuditCatalogResponse;
  @state() private _loading = true;
  @state() private _loadError = false;
  @state() private _busyAuditIds: ReadonlyArray<string> = [];
  @state() private _inexactAuditIds: ReadonlyArray<string> = [];

  #listGeneration = 0;
  #listAbort?: AbortController;
  #runAbort?: AbortController;
  #runIds = new Set<string>();

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
    void this.#initialize();
  }

  async #initialize(): Promise<void> {
    await this.#enterHome(true);
    this.#restoreAuditFromUrl();
  }

  async #enterHome(initial = false): Promise<void> {
    await this.#loadAll(initial);
    void this.#refreshPinnedTiles();
  }

  /** Sequentially refreshes pinned tiles so home always shows fresh counts. */
  async #refreshPinnedTiles(): Promise<void> {
    for (const audit of this._audits.filter((item) => item.dashboard.isPinned)) {
      if (this._view !== "home") return;
      await this.#runFromHome(audit, true);
    }
  }

  disconnectedCallback(): void {
    this.#listGeneration++;
    this.#listAbort?.abort();
    this.#runAbort?.abort();
    // Best-effort cleanup of allocated home run IDs.
    for (const runId of this.#runIds) {
      void this.#repository.deleteRun(runId).catch(() => undefined);
    }
    this.#runIds.clear();
    super.disconnectedCallback();
  }

  /**
   * Loads audits + catalog. `initial` toggles the full-page loader; refreshes
   * triggered while the builder is mounted only update the audit list so an
   * in-progress builder run/draft is never unmounted or overwritten.
   */
  async #loadAll(initial = false): Promise<void> {
    const generation = ++this.#listGeneration;
    this.#listAbort?.abort();
    this.#listAbort = new AbortController();
    if (initial) {
      this._loading = true;
      this._loadError = false;
    }
    try {
      const audits = await this.#repository.listAudits(this.#listAbort.signal);
      let catalog = this._catalog;
      if (catalog === undefined) {
        catalog = await this.#repository.getAuditCatalog(this.#listAbort.signal);
      }
      if (generation !== this.#listGeneration) return;
      this._audits = audits;
      this._catalog = catalog;
      this._loadError = false;
    } catch (error) {
      if (generation !== this.#listGeneration) return;
      if (error instanceof PropertyAuditApiError && error.aborted) return;
      this._loadError = true;
      this.#notificationContext?.peek("danger", {
        data: {
          headline: this.localize.term("propertyAudit_loadFailed"),
          message: normalizeError(error).message,
        },
      });
    } finally {
      if (generation === this.#listGeneration) {
        this._loading = false;
      }
    }
  }

  #syncUrlAudit(auditId?: string): void {
    const url = new URL(window.location.href);
    if (auditId) url.searchParams.set("audit", auditId);
    else url.searchParams.delete("audit");
    window.history.replaceState(window.history.state, "", url);
  }

  #restoreAuditFromUrl(): void {
    const auditId = new URLSearchParams(window.location.search).get("audit");
    if (!auditId) return;
    const audit = this._audits.find((item) => item.id === auditId);
    if (audit) this.#openBuilder(audit);
    else this.#syncUrlAudit();
  }

  #openBuilder(audit?: AuditResponse): void {
    // Leaving home cancels any in-flight list/tile run; the runner deletes
    // its allocated runId in its finally block.
    this.#runAbort?.abort();
    this.#runAbort = undefined;
    this._busyAuditIds = [];
    this._audit = audit;
    this._view = "builder";
    this.#syncUrlAudit(audit?.id);
  }

  #returnHome(): void {
    this._view = "home";
    this._audit = undefined;
    this.#syncUrlAudit();
    void this.#enterHome();
  }

  /** List-only preference update; the builder keeps its own draft state. */
  #onPreferenceChanged(event: CustomEvent<AuditResponse>): void {
    const updated = event.detail;
    this._audits = this._audits.map((audit) =>
      audit.id === updated.id ? updated : audit,
    );
  }

  /**
   * Runs one saved audit from home. Tracks the allocated run so it can be
   * deleted on cancel/disconnect, and only one run per audit at a time.
   * `quiet` suppresses notifications for timer-driven ticks.
   */
  async #runFromHome(audit: AuditResponse, quiet = false): Promise<void> {
    if (this._busyAuditIds.includes(audit.id)) return;
    this._busyAuditIds = [...this._busyAuditIds, audit.id];
    if (!this.#runAbort || this.#runAbort.signal.aborted) this.#runAbort = new AbortController();
    const signal = this.#runAbort.signal;
    let runId: string | undefined;
    try {
      let progress = await this.#repository.createRun(
        { auditId: audit.id },
        signal,
      );
      if (signal.aborted) {
        void this.#repository.deleteRun(progress.runId).catch(() => undefined);
        return;
      }
      runId = progress.runId;
      this.#runIds.add(runId);
      while (progress.status === "running") {
        progress = await this.#repository.advanceRun(runId, signal);
        if (signal.aborted) return;
      }
      this._inexactAuditIds = progress.status === "completed"
        ? this._inexactAuditIds.filter((id) => id !== audit.id)
        : [...new Set([...this._inexactAuditIds, audit.id])];
      if (
        !quiet &&
        (progress.status === "partial" ||
          progress.status === "limited" ||
          progress.status === "cancelled")
      ) {
        this.#notificationContext?.peek("warning", {
          data: {
            headline: audit.title,
            message: this.localize.term("propertyAudit_notExact"),
          },
        });
      }
      await this.#loadAll();
    } catch (error) {
      if (error instanceof PropertyAuditApiError && error.aborted) return;
      this._inexactAuditIds = [...new Set([...this._inexactAuditIds, audit.id])];
      if (quiet) return;
      this.#notificationContext?.peek("danger", {
        data: {
          headline: this.localize.term("propertyAudit_runFailed"),
          message: normalizeError(error).message,
        },
      });
    } finally {
      if (runId) {
        this.#runIds.delete(runId);
        void this.#repository.deleteRun(runId).catch(() => undefined);
      }
      this._busyAuditIds = this._busyAuditIds.filter((id) => id !== audit.id);
    }
  }

  async #onDeleteAudit(audit: AuditResponse): Promise<void> {
    if (!this.#modalManager) {
      this.#notificationContext?.peek("danger", {
        data: {
          headline: this.localize.term("propertyAudit_deleteFailed"),
          message: this.localize.term("propertyAudit_deleteAuditTitle"),
        },
      });
      return;
    }
    const { id, version, title } = audit;
    const modal = this.#modalManager.open(this, UMB_CONFIRM_MODAL, {
      data: {
        headline: this.localize.term("propertyAudit_deleteAuditTitle"),
        content: this.localize.term("propertyAudit_deleteAuditConfirm", title),
        color: "danger",
        confirmLabel: this.localize.term("propertyAudit_delete"),
      },
    });
    try {
      await modal.onSubmit();
    } catch {
      return; // cancelled
    }
    try {
      await this.#repository.deleteAudit(id, version);
      await this.#loadAll();
    } catch (error) {
      if (error instanceof PropertyAuditApiError && error.aborted) return;
      this.#notificationContext?.peek("danger", {
        data: {
          headline: this.localize.term("propertyAudit_deleteFailed"),
          message: normalizeError(error).message,
        },
      });
    }
  }

  /** Toggles a personal dashboard pin straight from the audit list. */
  async #onTogglePin(audit: AuditResponse): Promise<void> {
    try {
      const updated = await this.#repository.setAuditDashboardPreference(
        audit.id,
        {
          isPinned: !audit.dashboard.isPinned,
          icon: audit.dashboard.icon,
          colour: audit.dashboard.colour,
        },
      );
      this._audits = this._audits.map((a) => (a.id === updated.id ? updated : a));
    } catch (error) {
      if (error instanceof PropertyAuditApiError && error.aborted) return;
      this.#notificationContext?.peek("danger", {
        data: {
          headline: this.localize.term("propertyAudit_saveFailed"),
          message: normalizeError(error).message,
        },
      });
    }
  }

  #onAuditSaved(event: CustomEvent<AuditResponse>): void {
    const saved = event.detail;
    this._audits = [
      saved,
      ...this._audits.filter((audit) => audit.id !== saved.id),
    ];
    this._audit = saved;
    this.#syncUrlAudit(saved.id);
  }

  render() {
    if (this._loading) {
      return html`<uui-box
        headline=${this.localize.term("propertyAudit_heading")}
        ><uui-loader-bar></uui-loader-bar
      ></uui-box>`;
    }
    if (this._loadError && this._view === "home") {
      return html`<uui-box
        headline=${this.localize.term("propertyAudit_heading")}
      >
        <p role="alert">${this.localize.term("propertyAudit_loadFailed")}</p>
        <uui-button
          look="primary"
          label=${this.localize.term("propertyAudit_retry")}
          @click=${() => this.#enterHome(true)}
          >${this.localize.term("propertyAudit_retry")}</uui-button
        >
      </uui-box>`;
    }

    return this._view === "builder"
      ? html`<property-audit-builder
          .audit=${this._audit ?? null}
          .catalog=${this._catalog}
          @builder-back=${() => this.#returnHome()}
          @audit-saved=${this.#onAuditSaved}
          @audit-preference-changed=${this.#onPreferenceChanged}
          @audit-deleted=${() => this.#returnHome()}
          @run-finished=${() => void this.#loadAll()}
        ></property-audit-builder>`
      : html`<property-audit-overview
          .audits=${this._audits}
          .busyAuditIds=${this._busyAuditIds}
          .inexactAuditIds=${this._inexactAuditIds}
          @audit-open=${(event: CustomEvent<AuditResponse>) =>
            this.#openBuilder(event.detail)}
          @audit-new=${() => this.#openBuilder()}
          @audit-run=${(event: CustomEvent<AuditResponse>) =>
            this.#runFromHome(event.detail)}
          @audit-pin=${(event: CustomEvent<AuditResponse>) =>
            void this.#onTogglePin(event.detail)}
          @audit-delete=${(event: CustomEvent<AuditResponse>) =>
            this.#onDeleteAudit(event.detail)}
        ></property-audit-overview>`;
  }

  static styles = css`
    :host {
      display: block;
      padding: var(--uui-size-layout-1);
    }
  `;
}

export default PropertyAuditDashboardElement;

declare global {
  interface HTMLElementTagNameMap {
    "property-audit-dashboard": PropertyAuditDashboardElement;
  }
}
