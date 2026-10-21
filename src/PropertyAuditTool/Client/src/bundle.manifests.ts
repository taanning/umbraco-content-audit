import { manifests as entrypoints } from "./entrypoints/manifest.js";
import { manifests as dashboards } from "./dashboards/manifest.js";
import { manifests as localization } from "./localization/manifest.js";

// The bundle collates all manifests from the extension and is loaded from
// umbraco-package.json.
export const manifests: Array<UmbExtensionManifest> = [
  ...entrypoints,
  ...dashboards,
  ...localization,
];
