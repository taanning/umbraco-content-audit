import type { AuditCatalogResponse, AuditColumnDefinition, AuditDefinition, AuditFilterLink, PropertyAuditTypeModel as AuditTypeModel } from "../api/index.js";

export const BASE_COLUMNS = [
  { key: "kind", label: "colType" },
  { key: "typeName", label: "colTypeName" },
  { key: "content", label: "colContent" },
  { key: "culture", label: "culture" },
  { key: "location", label: "colLocation" },
] as const;

export const REQUIRED_BASE_COLUMNS: readonly string[] = ["kind", "typeName", "content"];

export function sanitizeHiddenBaseColumns(hidden: readonly string[] | null | undefined): string[] {
  return [...new Set(hidden ?? [])].filter((key) => !REQUIRED_BASE_COLUMNS.includes(key));
}

export const OPERATORS: Record<string, string[]> = {
  text: ["isEmpty", "hasText", "equals", "notEquals", "contains", "notContains", "startsWith", "endsWith", "like"],
  number: ["isEmpty", "hasText", "equals", "notEquals", "greaterThan", "greaterOrEqual", "lessThan", "lessOrEqual"],
  date: ["isEmpty", "hasText", "equals", "notEquals", "greaterThan", "greaterOrEqual", "lessThan", "lessOrEqual"],
  boolean: ["isEmpty", "hasText", "equals", "notEquals"],
};

export interface ColumnMetadata {
  alias: string;
  name: string;
  hint: string;
  segment: boolean;
  typeCount: number;
}

export function selectedTypes(catalog: AuditCatalogResponse | undefined, definition: AuditDefinition): AuditTypeModel[] {
  return (catalog?.types ?? []).filter((type) =>
    (type.kind === "element" ? definition.allElementTypes : definition.allDocumentTypes)
    || definition.types.some((selection) => selection.kind === type.kind && selection.alias === type.alias));
}

export function availableColumns(catalog: AuditCatalogResponse | undefined, definition: AuditDefinition): ColumnMetadata[] {
  const union = new Map<string, ColumnMetadata>();
  for (const type of selectedTypes(catalog, definition)) {
    for (const property of type.properties) {
      const existing = union.get(property.alias);
      if (existing) {
        existing.typeCount++;
        if (existing.hint !== property.valueTypeHint) existing.hint = "text";
        existing.segment ||= property.variesBySegment;
      } else {
        union.set(property.alias, {
          alias: property.alias, name: property.name,
          hint: property.valueTypeHint, segment: property.variesBySegment,
          typeCount: 1,
        });
      }
    }
  }
  return [...union.values()].sort((a, b) => a.name.localeCompare(b.name));
}

export function newTableDefinition(catalog: AuditCatalogResponse): AuditDefinition {
  const definition: AuditDefinition = {
    schemaVersion: 3, allDocumentTypes: true, allElementTypes: true,
    allCultures: true, cultures: null, types: [], columns: [], culture: null,
    rowFilters: [], hiddenBaseColumns: [],
  };
  definition.columns = availableColumns(catalog, definition).filter((column) => !column.segment)
    .map((column) => ({ alias: column.alias, valueType: column.hint, filter: null }));
  return definition;
}

/** Upgrade the editor's draft only; never broaden a stored legacy definition. */
export function tableDefinition(definition: AuditDefinition): AuditDefinition {
  const value = {
    ...JSON.parse(JSON.stringify(definition)),
    schemaVersion: 3,
    allElementTypes: definition.allElementTypes ?? false,
    allCultures: definition.allCultures ?? false,
    cultures: definition.cultures ?? (definition.allCultures ? null
      : definition.culture ? ["", definition.culture] : [""]),
    rowFilters: definition.rowFilters ?? [],
    hiddenBaseColumns: sanitizeHiddenBaseColumns(definition.hiddenBaseColumns),
    filterChain: definition.filterChain ?? null,
  } as AuditDefinition;
  value.filterChain = normalizedFilterChain(value.columns ?? [], value.filterChain);
  return value;
}

/**
 * Ordered filter chain in persisted shape: one entry per filtered column,
 * declared order first, newly filtered columns appended, last entry has no
 * joiner. Evaluation folds left to right, so order and joiners are the whole
 * predicate; an absent or stale declared chain degrades to plain AND.
 */
export function normalizedFilterChain(
  columns: ReadonlyArray<Pick<AuditColumnDefinition, "alias" | "filter">>,
  declared?: ReadonlyArray<AuditFilterLink | null> | null,
): AuditFilterLink[] {
  const filtered = new Set(columns.filter((column) => column.filter).map((column) => column.alias));
  const chain: AuditFilterLink[] = [];
  for (const link of declared ?? []) {
    const alias = (link?.column ?? "").trim();
    if (!filtered.has(alias) || chain.some((item) => item.column === alias)) continue;
    chain.push({ column: alias, joiner: (link!.joiner ?? "").toLowerCase() === "or" ? "or" : "and" });
  }
  for (const column of columns) {
    if (column.filter && !chain.some((item) => item.column === column.alias)) {
      chain.push({ column: column.alias });
    }
  }
  chain.forEach((link, index) => {
    link.joiner = index === chain.length - 1 ? null : link.joiner ?? "and";
  });
  return chain;
}

export function reconcileColumns(
  catalog: AuditCatalogResponse, previous: AuditDefinition, next: AuditDefinition,
): AuditDefinition {
  const before = availableColumns(catalog, previous).filter((column) => !column.segment);
  const after = availableColumns(catalog, next).filter((column) => !column.segment);
  const wasAll = before.every((column) => previous.columns.some((selected) => selected.alias === column.alias));
  const kept = previous.columns.filter((column) => after.some((available) => available.alias === column.alias));
  return {
    ...next,
    columns: [...kept, ...(wasAll ? after.filter((column) => !kept.some((existing) => existing.alias === column.alias))
      .map((column) => ({ alias: column.alias, valueType: column.hint, filter: null })) : [])],
  };
}

export function activeFilterKeys(definition: AuditDefinition): string[] {
  return [
    ...(!definition.allDocumentTypes || !definition.allElementTypes ? ["kind"] : []),
    ...(!definition.allCultures ? ["culture"] : []),
    ...(definition.rowFilters ?? []).filter((column) => column.filter).map((column) => column.alias),
    ...definition.columns.filter((column) => column.filter).map((column) => `property:${column.alias}`),
  ];
}

export function visibleSortKey(definition: AuditDefinition, current: string): string {
  const base = BASE_COLUMNS.filter((column) => !definition.hiddenBaseColumns?.includes(column.key));
  if (base.some((column) => column.key === current)
    || definition.columns.some((column) => `property:${column.alias}` === current)) return current;
  return base[0]?.key ?? (definition.columns[0] ? `property:${definition.columns[0].alias}` : "kind");
}

export function querySignature(definition: AuditDefinition, isShared: boolean): string {
  const value = tableDefinition(definition);
  const column = (item: AuditColumnDefinition) => ({
    alias: item.alias, valueType: item.valueType,
    filter: item.filter ? {
      operator: item.filter.expression != null ? "expression" : item.filter.operator,
      expression: item.filter.expression ?? null,
      value: item.filter.expression != null || ["isEmpty", "hasText"].includes(item.filter.operator)
        ? null : item.filter.value ?? null,
    } : null,
  });
  return JSON.stringify({
    isShared, allDocuments: value.allDocumentTypes, allElements: value.allElementTypes,
    types: value.types.map(type => `${type.kind}:${type.alias}`).sort(),
    columns: value.columns.map(column), allCultures: value.allCultures,
    cultures: value.allCultures ? null : [...value.cultures ?? []].map(culture => culture.toLowerCase()).sort(),
    rowFilters: (value.rowFilters ?? []).filter(item => item.filter).map(column)
      .sort((a, b) => a.alias.localeCompare(b.alias)),
    filterChain: (value.filterChain ?? []).map((link) => `${link.column}:${link.joiner ?? ""}`),
    hidden: [...value.hiddenBaseColumns ?? []].sort(),
  });
}

export interface PanelRequest {
  kind: "types" | "cultures" | "columns" | "filter" | "save" | "dashboard";
  definition: AuditDefinition;
  alias?: string;
  base?: boolean;
  title: string;
  description: string;
  isShared: boolean;
  isOwner: boolean;
  isSaved: boolean;
  icon: string;
  colour: string;
}

export interface PanelSubmit {
  definition: AuditDefinition;
  title: string;
  description: string;
  isShared: boolean;
  icon: string;
  colour: string;
}

export function columnFilterValid(column: AuditColumnDefinition): boolean {
  const filter = column.filter;
  if (!filter) return true;
  if (filter.expression != null) return filter.expression.trim().length > 0 && filter.expression.length <= 2000;
  return ["isEmpty", "hasText"].includes(filter.operator)
    || Boolean(filter.value?.trim() && filter.value.length <= 2000);
}
