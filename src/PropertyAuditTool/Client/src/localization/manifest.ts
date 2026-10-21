export const manifests: Array<UmbExtensionManifest> = [
  {
    name: "Property Audit English Localization",
    alias: "PropertyAuditTool.Localization.En",
    type: "localization",
    js: () => import("./en.js"),
    meta: {
      culture: "en",
    },
  },
  {
    name: "Property Audit Danish Localization",
    alias: "PropertyAuditTool.Localization.Da",
    type: "localization",
    js: () => import("./da.js"),
    meta: {
      culture: "da",
    },
  },
];
