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
];
