import type {
  UmbEntryPointOnInit,
  UmbEntryPointOnUnload,
} from "@umbraco-cms/backoffice/extension-api";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import { client } from "../api/client.gen.js";

export const onInit: UmbEntryPointOnInit = async (host, _extensionRegistry) => {
  // Configure the generated client from the backoffice auth context so requests
  // carry the management API base URL, credentials, and token resolution.
  const authContext = await host.getContext(UMB_AUTH_CONTEXT);
  if (!authContext) {
    return;
  }
  const config = authContext.getOpenApiConfiguration();
  client.setConfig({
    baseUrl: config.base ?? "",
    credentials: config.credentials,
    auth: config.token,
  });
};

export const onUnload: UmbEntryPointOnUnload = (_host, _extensionRegistry) => {
  // Nothing to dispose; the generated client has no persistent resources.
};
