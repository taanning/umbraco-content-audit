export const manifests: Array<UmbExtensionManifest> = [
  {
    name: "Property Audit Tool Entrypoint",
    alias: "PropertyAuditTool.Entrypoint",
    type: "backofficeEntryPoint",
    js: () => import("./entrypoint.js"),
  },
];
