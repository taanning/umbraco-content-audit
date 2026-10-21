import { expect, fixture, html } from '@open-wc/testing';
import '@umbraco-cms/backoffice/external/uui';
import { customElement, html as litHtml } from '@umbraco-cms/backoffice/external/lit';
import { UmbLitElement } from '@umbraco-cms/backoffice/lit-element';
import { UMB_MODAL_MANAGER_CONTEXT } from '@umbraco-cms/backoffice/modal';
import './audit-builder.element.js';
import type { AuditBuilderElement } from './audit-builder.element.js';
import { PropertyAuditRepository } from '../repository/property-audit.repository.js';
import type { AuditCatalogResponse, AuditResponse } from '../api/index.js';

const catalog: AuditCatalogResponse = {
  types: [
    {
      kind: 'document',
      alias: 'article',
      name: 'Article',
      unique: '11111111-1111-4111-8111-111111111111',
      variesByCulture: false,
      properties: [
        {
          alias: 'title',
          name: 'Title',
          editorAlias: 'Umbraco.TextBox',
          valueTypeHint: 'text',
          variesByCulture: false,
          variesBySegment: false,
        },
        {
          alias: 'author',
          name: 'Author',
          editorAlias: 'Umbraco.TextBox',
          valueTypeHint: 'text',
          variesByCulture: false,
          variesBySegment: false,
        },
      ],
    },
  ],
  languages: [{ isoCode: 'en-US', name: 'English (US)', isDefault: true }],
};

const ownerAudit: AuditResponse = {
  id: 'a3f2c1d4-1111-4222-8333-444455556666',
  title: 'Existing audit',
  description: null,
  isShared: false,
  isOwner: true,
  ownerName: 'Alice',
  definition: {
    schemaVersion: 2,
    allDocumentTypes: false,
    types: [{ kind: 'document', alias: 'article' }],
    columns: [{ alias: 'title', valueType: 'text', filter: null }],
    culture: null,
  },
  version: 3,
  createdDate: '2026-01-01T00:00:00Z',
  updatedDate: '2026-01-01T00:00:00Z',
  dashboard: {
    isPinned: false,
    icon: 'icon-search',
    colour: null,
    lastCount: null,
    lastCompletedUtc: null,
    isStale: false,
  },
};

function deferred<T>() {
  let resolve!: (v: T) => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

/** Modal manager stub whose onSubmit resolves to a controllable value. */
function modalManager(result: Promise<{ title: string; description: string | null } | undefined>) {
  // UmbContextConsumer calls getHostElement on provided instances.
  return {
    getHostElement: () => document.body,
    open: () => ({ onSubmit: () => result }),
  };
}

@customElement('test-builder-host')
class TestBuilderHost extends UmbLitElement {
  static properties = {
    manager: { attribute: false },
    audit: { attribute: false },
  };

  manager: unknown;
  audit: AuditResponse | null = null;

  constructor() {
    super();
    this.manager = modalManager(Promise.resolve({ title: 'Saved', description: null }));
  }

  connectedCallback() {
    super.connectedCallback();
    // UmbElementMixin provideContext (LitElement doesn't expose it, use the
    // context consumer helper on the host element).
    this.provideContext(UMB_MODAL_MANAGER_CONTEXT, this.manager);
  }

  render() {
    return litHtml`<property-audit-builder
      .audit=${this.audit}
      .catalog=${catalog}
    ></property-audit-builder>`;
  }
}

describe('property-audit-builder', () => {
  const proto = PropertyAuditRepository.prototype;
  let originals: Record<string, unknown> = {};
  const restore = ['updateAudit', 'createAudit', 'createRun', 'advanceRun', 'getRunResults', 'setAuditDashboardPreference', 'deleteRun'];

  beforeEach(() => {
    originals = {};
    for (const key of restore) {
      originals[key] = (proto as any)[key];
    }
    (proto as any).createRun = () => Promise.resolve({
      runId: 'r-default', status: 'completed', scannedCount: 0, matchedCount: 0, totalCount: 0, warnings: [],
    });
    (proto as any).getRunResults = () => Promise.resolve({
      items: [], skip: 0, take: 100, totalItems: 0, columns: ['title'],
      progress: { runId: 'r-default', status: 'completed', scannedCount: 0, matchedCount: 0, totalCount: 0, warnings: [] },
    });
    (proto as any).deleteRun = () => Promise.resolve();
  });
  afterEach(() => {
    for (const key of restore) {
      (proto as any)[key] = originals[key];
    }
  });

  async function createBuilder(
    audit: AuditResponse | null,
    manager?: unknown,
  ): Promise<AuditBuilderElement> {
    const host = await fixture<TestBuilderHost>(
      html`<test-builder-host
        .audit=${audit}
        .manager=${manager ?? modalManager(Promise.resolve({ title: 'Saved', description: null }))}
      ></test-builder-host>`,
    );
    await host.updateComplete;
    const el = host.shadowRoot!.querySelector(
      'property-audit-builder',
    ) as AuditBuilderElement;
    await el.updateComplete;
    await Promise.resolve();
    await el.updateComplete;
    return el;
  }

  async function flush(el: AuditBuilderElement) {
    await new Promise(resolve => setTimeout(resolve, 0));
    await el.updateComplete;
  }

  async function toggleColumn(el: AuditBuilderElement, alias: string, check = true) {
    el.shadowRoot!.querySelector('property-audit-results')!.dispatchEvent(
      new CustomEvent('results-columns', { bubbles: true, composed: true }));
    await flush(el);
    const panel = el.shadowRoot!.querySelector('property-audit-config-panel')!;
    const boxes = [...panel.shadowRoot!.querySelectorAll('uui-checkbox')].filter(
      (b) => (b as any).value === alias,
    );
    const box = boxes[0] as HTMLInputElement & { checked: boolean };
    expect(box, `checkbox for ${alias}`).to.exist;
    box.checked = check;
    box.dispatchEvent(new Event('change', { bubbles: true, composed: true }));
    await (panel as any).updateComplete;
    (panel.shadowRoot!.querySelector('.apply') as HTMLElement).click();
    await flush(el);
  }

  async function save(el: AuditBuilderElement) {
    (el.shadowRoot!.querySelector('.save-audit') as HTMLElement).click();
    await flush(el);
    // Owner audits save directly; new/shared audits go through the save panel.
    const panel = el.shadowRoot!.querySelector('property-audit-config-panel');
    if (panel) {
      (panel.shadowRoot!.querySelector('.apply') as HTMLElement).click();
      await flush(el);
    }
  }

  it('starts new audits with all types, cultures and available columns enabled', async () => {
    const el = await createBuilder(null);
    const run = el.shadowRoot!.querySelector(
      '.run-audit',
    ) as HTMLButtonElement & { disabled: boolean };
    expect(run.disabled).to.be.false;
    expect((el as any)._allDocumentTypes).to.be.true;
    expect((el as any)._allElementTypes).to.be.true;
    expect((el as any)._allCultures).to.be.true;
    expect((el as any)._columns.map((column: any) => column.alias)).to.have.members(['title', 'author']);
    expect(el.shadowRoot!.querySelector('property-audit-config-panel')).to.not.exist;
  });

  it('does not unexpectedly auto-run after saving a new audit', async () => {
    let runs = 0;
    (proto as any).createRun = () => {
      runs++;
      return Promise.resolve({ runId: 'new-run', status: 'completed', warnings: [] });
    };
    (proto as any).createAudit = (body: any) => Promise.resolve({ ...ownerAudit, ...body, version: 1 });
    const el = await createBuilder(null);
    // Opening a new audit already runs it once; saving must not add another.
    const baselineRuns = runs;
    (el.shadowRoot!.querySelector('.save-audit') as HTMLElement).click();
    await flush(el);
    const panel = el.shadowRoot!.querySelector('property-audit-config-panel')!;
    const title = panel.shadowRoot!.querySelector('#audit-title') as HTMLInputElement;
    title.value = 'Newly saved';
    title.dispatchEvent(new InputEvent('input', { bubbles: true, composed: true }));
    await (panel as any).updateComplete;
    (panel.shadowRoot!.querySelector('.apply') as HTMLElement).click();
    await flush(el);
    expect(runs).to.equal(baselineRuns);
    expect(el.shadowRoot!.querySelector('property-audit-config-panel')).not.to.exist;
    expect((el.shadowRoot!.querySelector('.save-audit') as any).label).to.equal('propertyAudit_save');
  });

  it('guards a pending save snapshot; later applied edits run the new draft', async () => {
    const update = deferred<AuditResponse>();
    let updateBody: any;
    let runBody: any;
    (proto as any).updateAudit = (id: string, body: unknown) => {
      updateBody = body;
      return update.promise;
    };
    (proto as any).createRun = (body: unknown) => {
      runBody = body;
      return Promise.resolve({
        runId: 'r1',
        status: 'completed',
        scannedCount: 0,
        warnings: [],
      });
    };
    (proto as any).getRunResults = () =>
      Promise.resolve({
        items: [],
        skip: 0,
        take: 50,
        totalItems: 0,
        columns: ['title'],
        progress: { runId: 'r1', status: 'completed', scannedCount: 0, warnings: [] },
      });

    const el = await createBuilder(ownerAudit);
    const flush = async () => {
      for (let i = 0; i < 5; i++) await Promise.resolve();
      await el.updateComplete;
    };
    // Dirty the draft so owner Save is enabled, then save directly.
    await new Promise(resolve => setTimeout(resolve, 0));
    await toggleColumn(el, 'author');
    await save(el);
    expect(updateBody, 'save payload captured').to.exist;

    expect((el.shadowRoot!.querySelector('.run-audit') as any).disabled).to.be.true;
    expect((el as any)._saving).to.be.true;

    update.resolve({ ...ownerAudit, version: 4, definition: updateBody.definition });
    await flush();

    // Payload contains the snapshot definition, not any later edit.
    expect(updateBody.definition.columns.map((c: any) => c.alias)).to.deep.equal(['title', 'author']);
    expect((el as any)._dirty).to.be.false;
    expect((el as any).audit.version).to.equal(4);
    await toggleColumn(el, 'author', false);
    expect((el as any)._dirty).to.be.true;
    expect((el as any)._columns.map((c: any) => c.alias)).to.deep.equal(['title']);

    // Dirty draft runs with the posted definition, not the saved ID.
    const run = el.shadowRoot!.querySelector(
      '.run-audit',
    ) as HTMLElement;
    run.dispatchEvent(new MouseEvent('click', { bubbles: true, composed: true }));
    await flush();
    expect(runBody.definition.columns.map((c: any) => c.alias)).to.deep.equal([
      'title',
    ]);
  });

  it('a 409 save keeps draft edits and the latched version', async () => {
    (proto as any).updateAudit = () =>
      Promise.reject({ status: 409, title: 'Conflict' });
    const el = await createBuilder(ownerAudit);
    await toggleColumn(el, 'author');
    await save(el);
    for (let i = 0; i < 5; i++) await Promise.resolve();
    await el.updateComplete;
    expect((el as any)._dirty).to.be.true;
    expect((el as any)._columns.map((c: any) => c.alias)).to.deep.equal(['title', 'author']);
    expect((el as any).audit.version).to.equal(3);
  });

  it('a stale preference response cannot erase a newer colour edit', async () => {
    const pref = deferred<AuditResponse>();
    (proto as any).setAuditDashboardPreference = () => pref.promise;
    const el = await createBuilder(ownerAudit);
    // Trigger a pin write; response is deferred.
    const pinToggle = [...el.shadowRoot!.querySelectorAll('uui-checkbox')].find(
      (t) => (t as any).label === 'propertyAudit_showOnDashboard',
    ) as HTMLInputElement & { checked: boolean };
    expect(pinToggle).to.exist;
    pinToggle.checked = true;
    pinToggle.dispatchEvent(new Event('change', { bubbles: true, composed: true }));
    await el.updateComplete;
    // While it is in flight, type a new colour.
    (el.shadowRoot!.querySelector('uui-button[label="propertyAudit_dashboardAppearance"]') as HTMLElement).click();
    await flush(el);
    const panel = el.shadowRoot!.querySelector('property-audit-config-panel')!;
    const colour = panel.shadowRoot!.querySelector('uui-input[label="propertyAudit_customColour"]') as HTMLInputElement;
    colour.value = '#112233';
    colour.dispatchEvent(new InputEvent('input', { bubbles: true, composed: true }));
    // The stale pin response arrives with the old (null) colour.
    pref.resolve({
      ...ownerAudit,
      dashboard: { ...ownerAudit.dashboard, isPinned: true, colour: null },
    });
    await Promise.resolve();
    await el.updateComplete;
    expect(colour.value).to.equal('#112233');
    expect((el as any)._isPinned).to.be.true;
  });
});
