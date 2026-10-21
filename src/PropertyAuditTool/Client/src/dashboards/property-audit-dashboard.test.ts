import { expect, fixture, html } from '@open-wc/testing';
import '@umbraco-cms/backoffice/external/uui';
import './property-audit-dashboard.element.js';
import type { PropertyAuditDashboardElement } from './property-audit-dashboard.element.js';
import type { AuditOverviewElement } from './audit-overview.element.js';
import { PropertyAuditRepository } from '../repository/property-audit.repository.js';
import type { AuditResponse } from '../api/index.js';

const audit = (over: Partial<AuditResponse> = {}): AuditResponse => ({
  id: 'a3f2c1d4-1111-4222-8333-444455556666',
  title: 'Article audit',
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
  version: 1,
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
  ...over,
});

describe('property-audit-dashboard', () => {
  const proto = PropertyAuditRepository.prototype;
  let originalListAudits: typeof proto.listAudits;
  let originalGetAuditCatalog: typeof proto.getAuditCatalog;
  let originalCreateRun: typeof proto.createRun;
  let originalDeleteRun: typeof proto.deleteRun;
  let runCalls: unknown[];
  let audits: AuditResponse[];

  beforeEach(() => {
    originalListAudits = proto.listAudits;
    originalGetAuditCatalog = proto.getAuditCatalog;
    originalCreateRun = proto.createRun;
    originalDeleteRun = proto.deleteRun;
    runCalls = [];
    proto.createRun = ((body: unknown) => {
      runCalls.push(body);
      return Promise.resolve({ runId: 'home-run', status: 'completed', scannedCount: 0,
        matchedCount: 0, totalCount: 0, warnings: [], startedUtc: '' });
    }) as typeof proto.createRun;
    proto.deleteRun = (() => Promise.resolve()) as typeof proto.deleteRun;
    audits = [];
    // The builder writes ?audit=<id> into the URL; clear leftovers so each
    // test starts on the home view.
    window.history.replaceState(null, '', window.location.pathname);
    proto.listAudits = (() =>
      Promise.resolve([...audits])) as typeof proto.listAudits;
    proto.getAuditCatalog = (() =>
      Promise.resolve({
        types: [],
        languages: [{ isoCode: 'en-US', name: 'English (US)', isDefault: true }],
      })) as typeof proto.getAuditCatalog;
  });

  afterEach(() => {
    proto.listAudits = originalListAudits;
    proto.getAuditCatalog = originalGetAuditCatalog;
    proto.createRun = originalCreateRun;
    proto.deleteRun = originalDeleteRun;
  });

  async function createElement(): Promise<PropertyAuditDashboardElement> {
    const el = await fixture<PropertyAuditDashboardElement>(
      html`<property-audit-dashboard></property-audit-dashboard>`,
    );
    await el.updateComplete;
    await Promise.resolve();
    await el.updateComplete;
    const overview = el.shadowRoot!.querySelector(
      'property-audit-overview',
    ) as AuditOverviewElement | null;
    if (overview) await overview.updateComplete;
    return el;
  }

  function overview(el: PropertyAuditDashboardElement): AuditOverviewElement {
    const o = el.shadowRoot!.querySelector('property-audit-overview');
    expect(o).to.exist;
    return o as AuditOverviewElement;
  }

  function search(overviewEl: AuditOverviewElement, term: string) {
    const input = overviewEl.shadowRoot!.querySelector(
      'uui-input',
    ) as HTMLInputElement;
    input.value = term;
    input.dispatchEvent(
      new InputEvent('input', { bubbles: true, composed: true }),
    );
  }

  it('shows the first-audit empty state when there are no audits', async () => {
    const el = await createElement();
    const o = overview(el);
    expect(o.shadowRoot!.querySelector('.empty')).to.exist;
    expect(o.shadowRoot!.textContent).to.include('propertyAudit_emptyAudits');
    expect(o.shadowRoot!.querySelector('.tile')).to.not.exist;
    expect(o.shadowRoot!.querySelector('uui-table')).to.not.exist;
  });

  it('shows the no-match empty state for an unsuccessful search', async () => {
    audits = [audit()];
    const el = await createElement();
    const o = overview(el);
    search(o, 'zzz');
    await o.updateComplete;
    expect(o.shadowRoot!.querySelector('.empty')).to.exist;
    expect(o.shadowRoot!.textContent).to.include(
      'propertyAudit_noMatchingAudits',
    );
  });

  it('filters list by title/description/creator case-insensitively', async () => {
    audits = [
      audit({ title: 'Article audit' }),
      audit({
        id: 'b3f2c1d4-1111-4222-8333-444455556666',
        title: 'Product audit',
        description: 'checks sku emptiness',
      }),
      audit({
        id: 'c3f2c1d4-1111-4222-8333-444455556666',
        title: 'Bob audit',
        ownerName: 'Carol',
      }),
    ];
    const el = await createElement();
    const o = overview(el);

    search(o, '  SKU ');
    await o.updateComplete;
    expect(o.shadowRoot!.querySelectorAll('uui-table-row').length).to.equal(1);

    search(o, 'carol');
    await o.updateComplete;
    expect(o.shadowRoot!.querySelectorAll('uui-table-row').length).to.equal(1);
  });

  it('keeps pinned tiles visible while search hides list rows', async () => {
    audits = [
      audit({
        title: 'Pinned audit',
        dashboard: {
          isPinned: true,
          icon: 'icon-search',
          colour: null,
          lastCount: 42,
          lastCompletedUtc: '2026-01-02T00:00:00Z',
          isStale: false,
        },
      }),
      audit({
        id: 'd3f2c1d4-1111-4222-8333-444455556666',
        title: 'Other audit',
      }),
    ];
    const el = await createElement();
    const o = overview(el);
    expect(o.shadowRoot!.querySelectorAll('.tile').length).to.equal(1);
    expect(o.shadowRoot!.querySelector('.tile-count')!.textContent).to.include('42');

    search(o, 'nothing matches this');
    await o.updateComplete;
    expect(o.shadowRoot!.querySelectorAll('.tile').length).to.equal(1);
    expect(o.shadowRoot!.querySelectorAll('uui-table-row').length).to.equal(0);
  });

  it('opens a clean builder for New Audit', async () => {
    const el = await createElement();
    const o = overview(el);
    const btn = o.shadowRoot!.querySelector(
      'uui-button[look="primary"]',
    ) as HTMLElement;
    btn.dispatchEvent(new MouseEvent('click', { bubbles: true, composed: true }));
    await el.updateComplete;
    const builder = el.shadowRoot!.querySelector('property-audit-builder');
    expect(builder).to.exist;
    expect(el.shadowRoot!.querySelector('property-audit-overview')).to.not.exist;
    expect((builder as any).audit).to.be.null;
    expect((builder as any)._columns.length).to.equal(0);
  });

  it('returns home on Back to audits', async () => {
    const el = await createElement();
    const o = overview(el);
    (
      o.shadowRoot!.querySelector('uui-button[look="primary"]') as HTMLElement
    ).dispatchEvent(new MouseEvent('click', { bubbles: true, composed: true }));
    await el.updateComplete;
    const builder = el.shadowRoot!.querySelector('property-audit-builder')!;
    (
      builder.shadowRoot!.querySelector('uui-button') as HTMLElement
    ).dispatchEvent(new MouseEvent('click', { bubbles: true, composed: true }));
    await el.updateComplete;
    await Promise.resolve();
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('property-audit-builder')).to.not.exist;
    expect(el.shadowRoot!.querySelector('property-audit-overview')).to.exist;
  });

  it('keeps the same builder instance mounted after run-finished', async () => {
    audits = [audit()];
    const el = await createElement();
    const o = overview(el);
    (
      o.shadowRoot!.querySelector('uui-button[look="primary"]') as HTMLElement
    ).dispatchEvent(new MouseEvent('click', { bubbles: true, composed: true }));
    await el.updateComplete;
    const builder = el.shadowRoot!.querySelector('property-audit-builder')!;
    builder.dispatchEvent(
      new CustomEvent('run-finished', { bubbles: true, composed: true }),
    );
    await el.updateComplete;
    await Promise.resolve();
    await el.updateComplete;
    const after = el.shadowRoot!.querySelector('property-audit-builder');
    expect(after).to.exist;
    expect(after).to.equal(builder);
    expect(el.shadowRoot!.querySelector('uui-loader-bar')).to.not.exist;
  });

  it('does not call deleteAudit when the confirm modal is cancelled', async () => {
    audits = [audit()];
    let deleteCalls = 0;
    const originalDelete = proto.deleteAudit;
    proto.deleteAudit = (() => {
      deleteCalls++;
      return Promise.resolve();
    }) as typeof proto.deleteAudit;
    try {
      const el = await createElement();
      const o = overview(el);
      // No modal manager in tests: overview dispatch -> orchestrator guard
      // must not delete without confirmation.
      o.dispatchEvent(
        new CustomEvent('audit-delete', {
          detail: audits[0],
          bubbles: true,
          composed: true,
        }),
      );
      await Promise.resolve();
      expect(deleteCalls).to.equal(0);
    } finally {
      proto.deleteAudit = originalDelete;
    }
  });

  it('opens the builder when a tile is clicked', async () => {
    const saved = audit({
      dashboard: {
        isPinned: true,
        icon: 'icon-search',
        colour: '#ff00ff',
        lastCount: null,
        lastCompletedUtc: null,
        isStale: false,
      },
    });
    audits = [saved];
    const el = await createElement();
    const o = overview(el);
    (
      o.shadowRoot!.querySelector('.tile-open') as HTMLElement
    ).dispatchEvent(new MouseEvent('click', { bubbles: true, composed: true }));
    await el.updateComplete;
    const builder = el.shadowRoot!.querySelector(
      'property-audit-builder',
    ) as any;
    expect(builder).to.exist;
    expect(builder.audit.id).to.equal(saved.id);
  });

  it('auto-runs pinned audits on initial home load', async () => {
    audits = [audit({ dashboard: { ...audit().dashboard, isPinned: true } })];
    const el = await createElement();
    for (let i = 0; i < 6; i++) await new Promise(resolve => setTimeout(resolve, 0));
    // Tiles refresh their persisted counts by running pinned audits on load.
    expect(runCalls).to.deep.equal([{ auditId: audits[0].id }]);
    const o = overview(el);
    expect(o.shadowRoot!.querySelectorAll('.tile').length).to.equal(1);
  });

  it('discards a pending initial load when the element disconnects', async () => {
    let release!: (value: AuditResponse[]) => void;
    let initialSignal: AbortSignal | undefined;
    const pending = new Promise<AuditResponse[]>(resolve => { release = resolve; });
    proto.listAudits = ((signal: AbortSignal) => {
      initialSignal ??= signal;
      return pending;
    }) as typeof proto.listAudits;
    const element = await fixture<PropertyAuditDashboardElement>(
      html`<property-audit-dashboard></property-audit-dashboard>`);
    element.remove();
    release([audit()]);
    await new Promise(resolve => setTimeout(resolve, 0));
    expect(initialSignal?.aborted, 'disconnecting aborts the pending list request').to.be.true;
    expect(element.shadowRoot!.querySelector('property-audit-overview')).to.not.exist;
  });

  it('runs an unpinned audit from its row without opening the editor', async () => {
    audits = [audit()];
    const el = await createElement();
    expect(runCalls).to.have.length(0);
    (overview(el).shadowRoot!.querySelector('.run-audit') as HTMLElement).click();
    await new Promise(resolve => setTimeout(resolve, 0));
    expect(runCalls).to.deep.equal([{ auditId: audits[0].id }]);
    expect(el.shadowRoot!.querySelector('property-audit-builder')).to.not.exist;
  });

  it('shows the later updated timestamp rather than the creation timestamp', async () => {
    audits = [audit({ updatedDate: '2026-10-20T12:30:00Z' })];
    const el = await createElement();
    const text = overview(el).shadowRoot!.textContent!;
    expect(text).to.include(new Date(audits[0].updatedDate).toLocaleString());
    expect(text).to.include('propertyAudit_updated');
    expect(text).not.to.include('propertyAudit_colCreated\n');
  });
});
