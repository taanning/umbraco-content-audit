import { expect, fixture, html } from '@open-wc/testing';
import '@umbraco-cms/backoffice/external/uui';
import './audit-config-panel.element.js';
import './audit-results.element.js';
import './audit-overview.element.js';
import type { AuditConfigPanelElement } from './audit-config-panel.element.js';
import type { AuditCatalogResponse, AuditResponse } from '../api/index.js';
import { availableColumns, newTableDefinition, normalizedFilterChain, querySignature, reconcileColumns, tableDefinition, visibleSortKey, type PanelRequest } from './audit-table-state.js';

const property = (alias: string) => ({
  alias, name: alias, editorAlias: 'Umbraco.TextBox', valueTypeHint: 'text',
  variesByCulture: false, variesBySegment: false,
});
const catalog: AuditCatalogResponse = {
  types: [
    { kind: 'document', alias: 'article', name: 'Article', unique: 'a', variesByCulture: false,
      properties: [property('title'), property('articleNotes')] },
    { kind: 'element', alias: 'hero', name: 'Hero', unique: 'h', variesByCulture: false,
      properties: [property('title'), property('heroNotes')] },
  ],
  languages: [{ isoCode: 'en-US', name: 'English', isDefault: true }],
};
const request = (kind: PanelRequest['kind']): PanelRequest => ({
  kind, definition: newTableDefinition(catalog), title: '', description: '',
  isShared: false, isOwner: false, isSaved: false, icon: 'icon-search', colour: '',
});
async function panel(kind: PanelRequest['kind']) {
  return fixture<AuditConfigPanelElement>(html`<property-audit-config-panel
    .catalog=${catalog} .request=${request(kind)}></property-audit-config-panel>`);
}
const click = (element: Element) => (element as HTMLElement).click();

describe('table-first state and inline panels', () => {
  it('defaults to all document/element types, cultures and the property union', () => {
    const definition = newTableDefinition(catalog);
    expect(definition.allDocumentTypes).to.be.true;
    expect(definition.allElementTypes).to.be.true;
    expect(definition.allCultures).to.be.true;
    expect(definition.columns.map(column => column.alias)).to.have.members(['title', 'articleNotes', 'heroNotes']);
  });

  it('does not broaden legacy scope when editing a saved definition', () => {
    const legacy = { ...newTableDefinition(catalog), schemaVersion: 2, allDocumentTypes: false,
      allElementTypes: false, allCultures: false, cultures: undefined, culture: 'en-US',
      types: [{ kind: 'document', alias: 'article' }] };
    const upgraded = tableDefinition(legacy);
    expect(upgraded.types).to.deep.equal(legacy.types);
    expect(upgraded.allElementTypes).to.be.false;
    expect(upgraded.cultures).to.deep.equal(['', 'en-US']);
  });

  it('renders document and element groups beside one another, with all choices checked', async () => {
    const element = await panel('types');
    expect(element.shadowRoot!.querySelectorAll('.type-lists > section')).to.have.length(2);
    expect([...element.shadowRoot!.querySelectorAll('uui-checkbox')].every(box => (box as any).checked)).to.be.true;
  });

  it('searches one type group without discarding hidden selections', async () => {
    const element = await panel('types');
    const input = element.shadowRoot!.querySelector('uui-input') as HTMLInputElement;
    input.value = 'not present';
    input.dispatchEvent(new InputEvent('input', { bubbles: true, composed: true }));
    await element.updateComplete;
    expect(element.shadowRoot!.querySelectorAll('uui-checkbox')).to.have.length(1);
    expect((element as any)._draft.allDocumentTypes).to.be.true;
  });

  it('deselecting all types does not silently restore all selection', async () => {
    const element = await panel('types');
    const buttons = element.shadowRoot!.querySelectorAll('.selection-actions uui-button');
    click(buttons[1]);
    click(buttons[3]);
    await element.updateComplete;
    expect((element as any)._draft.allDocumentTypes).to.be.false;
    expect((element as any)._draft.allElementTypes).to.be.false;
    expect((element as any)._draft.types).to.deep.equal([]);
  });

  it('edits a panel draft without mutating the applied definition; Cancel emits no apply', async () => {
    const element = await panel('types');
    let applied = false;
    let cancelled = false;
    element.addEventListener('panel-apply', () => { applied = true; });
    element.addEventListener('panel-cancel', () => { cancelled = true; });
    click(element.shadowRoot!.querySelectorAll('.selection-actions uui-button')[1]);
    const cancel = [...element.shadowRoot!.querySelectorAll('uui-button')]
      .find(button => button.getAttribute('label') === 'propertyAudit_cancel')!;
    click(cancel);
    await new Promise(resolve => setTimeout(resolve, 0));
    expect(element.request.definition.allDocumentTypes).to.be.true;
    expect(applied).to.be.false;
    expect(cancelled).to.be.true;
  });

  it('requires only a nonblank title, not a subtitle, before saving', async () => {
    const element = await panel('save');
    const apply = element.shadowRoot!.querySelector('.apply') as any;
    expect(apply.disabled).to.be.true;
    const input = element.shadowRoot!.querySelector('#audit-title') as HTMLInputElement;
    input.value = 'A title';
    input.dispatchEvent(new InputEvent('input', { bubbles: true, composed: true }));
    await element.updateComplete;
    expect(apply.disabled).to.be.false;
    let detail: any;
    element.addEventListener('panel-apply', event => { detail = (event as CustomEvent).detail; });
    click(apply);
    await new Promise(resolve => setTimeout(resolve, 0));
    expect(detail.title).to.equal('A title');
    expect(detail.description).to.equal('');
  });

  it('lists invariant and configured cultures, all checked by default', async () => {
    const element = await panel('cultures');
    expect(element.shadowRoot!.querySelectorAll('uui-checkbox')).to.have.length(2);
    expect([...element.shadowRoot!.querySelectorAll('uui-checkbox')].every(box => (box as any).checked)).to.be.true;
  });

  it('does not treat element-only properties as missing on the document type', () => {
    const definition = newTableDefinition(catalog);
    definition.allElementTypes = false;
    expect(availableColumns(catalog, definition).map(column => column.alias)).not.to.include('heroNotes');
  });

  it('renders filterable headers before the first run, including culture', async () => {
    const element = await fixture(html`<property-audit-results></property-audit-results>`);
    expect(element.shadowRoot!.querySelectorAll('.filter')).to.have.length(5);
    expect(element.shadowRoot!.textContent).to.include('propertyAudit_culture');
    expect(element.shadowRoot!.querySelectorAll('.sort.active')).to.have.length(1);
  });

  it('falls back to a visible sort when the sorted property or base column is hidden', () => {
    const definition = newTableDefinition(catalog);
    definition.columns = [];
    expect(visibleSortKey(definition, 'property:title')).to.equal('kind');
    definition.hiddenBaseColumns = ['kind'];
    expect(visibleSortKey(definition, 'kind')).to.equal('typeName');
  });

  it('keeps declared chain order, defaults missing joiners to and, clears the last one', () => {
    const columns = [
      { alias: 'seoTitle', filter: { operator: 'isEmpty' } },
      { alias: 'seoDescription', filter: { operator: 'isEmpty' } },
      { alias: 'seoKeywords', filter: { operator: 'isEmpty' } },
    ];
    const chain = normalizedFilterChain(columns, [
      { column: 'seoDescription', joiner: 'or' },
      { column: 'seoTitle', joiner: 'and' },
      { column: 'seoKeywords' },
    ]);
    expect(chain.map(link => `${link.column}:${link.joiner ?? ''}`))
      .to.deep.equal(['seoDescription:or', 'seoTitle:and', 'seoKeywords:']);
  });

  it('derives a plain AND chain for legacy definitions without filterChain', () => {
    const columns = [
      { alias: 'seoTitle', filter: { operator: 'isEmpty' } },
      { alias: 'seoDescription', filter: { operator: 'isEmpty' } },
      { alias: 'plain', filter: null },
    ];
    const chain = normalizedFilterChain(columns, null);
    expect(chain.map(link => `${link.column}:${link.joiner ?? ''}`))
      .to.deep.equal(['seoTitle:and', 'seoDescription:']);
    expect(tableDefinition({
      ...newTableDefinition(catalog), columns, filterChain: undefined,
    }).filterChain).to.deep.equal(chain);
  });

  it('drops chain entries whose filter was removed, skips unknown and duplicate aliases', () => {
    const columns = [
      { alias: 'seoTitle', filter: { operator: 'isEmpty' } },
      { alias: 'seoDescription', filter: null },
    ];
    const chain = normalizedFilterChain(columns, [
      { column: 'seoTitle', joiner: 'or' },
      { column: 'seoDescription', joiner: 'or' },
      { column: '  ', joiner: 'or' },
      { column: 'seoTitle', joiner: 'and' },
      { column: 'missing' },
    ]);
    expect(chain).to.deep.equal([{ column: 'seoTitle', joiner: null }]);
  });

  it('treats unrecognised joiner values as and', () => {
    const columns = [
      { alias: 'a', filter: { operator: 'isEmpty' } },
      { alias: 'b', filter: { operator: 'isEmpty' } },
    ];
    const chain = normalizedFilterChain(columns, [
      { column: 'a', joiner: 'OR' },
      { column: 'b', joiner: 'xor' },
    ]);
    expect(chain.map(link => link.joiner)).to.deep.equal(['or', null]);
  });

  it('changes the query signature when chain order or joiners change', () => {
    const base = newTableDefinition(catalog);
    base.columns = [
      { alias: 'title', valueType: 'text', filter: { operator: 'isEmpty' } },
      { alias: 'articleNotes', valueType: 'text', filter: { operator: 'isEmpty' } },
    ];
    base.filterChain = [
      { column: 'title', joiner: 'and' },
      { column: 'articleNotes', joiner: null },
    ];
    const reordered = { ...base, filterChain: [
      { column: 'articleNotes', joiner: 'and' },
      { column: 'title', joiner: null },
    ] };
    const orChain = { ...base, filterChain: [
      { column: 'title', joiner: 'or' },
      { column: 'articleNotes', joiner: null },
    ] };
    const signature = querySignature(base, false);
    expect(querySignature(reordered, false)).not.to.equal(signature);
    expect(querySignature(orChain, false)).not.to.equal(signature);
    expect(querySignature({ ...base, filterChain: null }, false)).to.equal(signature);
  });

  it('preserves the chosen property-column order when changing type scope', () => {
    const previous = newTableDefinition(catalog);
    previous.columns = [
      { alias: 'title', valueType: 'text' },
      { alias: 'articleNotes', valueType: 'text' },
      { alias: 'heroNotes', valueType: 'text' },
    ];
    const next = { ...previous, allElementTypes: false };
    expect(reconcileColumns(catalog, previous, next).columns.map(column => column.alias))
      .to.deep.equal(['title', 'articleNotes']);
  });

  it('sizes the actual tile icon to the title when the optional subtitle is absent', async () => {
    const audit: AuditResponse = {
      id: 'a', title: 'Short title', description: null, isOwner: true, isShared: false,
      ownerName: 'Alice', version: 1, definition: newTableDefinition(catalog),
      createdDate: '2026-10-20T00:00:00Z', updatedDate: '2026-10-20T00:00:00Z',
      dashboard: { isPinned: true, icon: 'icon-search', colour: null,
        lastCount: 1, lastCompletedUtc: null, isStale: false },
    };
    const element = await fixture(html`<property-audit-overview .audits=${[audit]}></property-audit-overview>`);
    const icon = element.shadowRoot!.querySelector('.tile-symbol uui-icon') as any;
    icon.fallback = icon.svg = '<svg viewBox="0 0 16 16"><rect width="16" height="16"/></svg>';
    await icon.updateComplete;
    const iconBox = icon.getBoundingClientRect();
    const symbol = element.shadowRoot!.querySelector('.tile-symbol')!.getBoundingClientRect();
    expect(Math.abs(iconBox.height - symbol.height)).to.be.lessThan(1);
  });
});
