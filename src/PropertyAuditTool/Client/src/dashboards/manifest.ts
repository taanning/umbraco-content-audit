export const manifests: Array<UmbExtensionManifest> = [
  {
    name: "Content Audit Dashboard",
    alias: "PropertyAuditTool.Dashboard",
    type: "dashboard",
    js: () => import("./property-audit-dashboard.element.js"),
    weight: 100,
    meta: {
      label: "Content Audit",
      pathname: "property-audit",
    },
    conditions: [
      {
        alias: "Umb.Condition.SectionAlias",
        match: "Umb.Section.Content",
      },
    ],
  },
];
