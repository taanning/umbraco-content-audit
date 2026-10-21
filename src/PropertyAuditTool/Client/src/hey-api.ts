import type { CreateClientConfig } from './api/client.gen';

/**
 * Base config for the generated client. A relative base URL keeps requests on
 * the same origin as the backoffice; the entrypoint applies the resolved
 * UmbAuthContext OpenAPI configuration (base URL, credentials, token) via
 * client.setConfig once the auth context is available.
 */
export const createClientConfig: CreateClientConfig = (config) =>
	({
		...config,
		baseUrl: '',
	});
