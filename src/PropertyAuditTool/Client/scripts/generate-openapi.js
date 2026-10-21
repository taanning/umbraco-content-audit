// Generates the TypeScript API client for the Property Audit endpoints.
//
// Fetch mode (default): downloads the built-in Umbraco Management OpenAPI
// document from a running host, extracts only the property-audit paths plus
// their transitively referenced components, saves the filtered spec to
// openapi/property-audit.json, then runs @hey-api/openapi-ts on that file.
//
//   node scripts/generate-openapi.js [--server http://localhost:5571]
//
// Offline mode regenerates the SDK from the saved filtered spec without a
// running server:
//
//   node scripts/generate-openapi.js --offline
//
// The Management document route differs by Umbraco major:
//   17: /umbraco/swagger/management/swagger.json
//   18: /umbraco/openapi/management.json
// Use --spec-path to override when probing a host.

import { createClient } from '@hey-api/openapi-ts';
import { fileURLToPath } from 'node:url';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';

const SCRIPT_DIR = path.dirname(fileURLToPath(import.meta.url));
const CLIENT_ROOT = path.resolve(SCRIPT_DIR, '..');
const OUT_DIR = path.join(CLIENT_ROOT, 'src', 'api');
const SPEC_PATH = path.join(CLIENT_ROOT, 'openapi', 'property-audit.json');
const PATH_PATTERN = /^\/umbraco\/management\/api\/v1\/property-audit(?:\/|$)/;

function argValue(args, name) {
	const index = args.indexOf(name);
	return index !== -1 && index + 1 < args.length ? args[index + 1] : undefined;
}

/**
 * Extracts the property-audit paths from a full Management OpenAPI document
 * and recursively copies every locally referenced component.
 * @param {object} spec Full Management OpenAPI document.
 * @returns {object} Filtered document.
 */
export function filterManagementSpec(spec) {
	const paths = {};
	for (const [key, value] of Object.entries(spec.paths ?? {})) {
		if (PATH_PATTERN.test(key)) {
			const segments = key.replace('/umbraco/management/api/v1/', '').split('/');
			const suffix = segments.map((segment) => {
				const parameter = /^\{([^}:]+)(?::[^}]+)?\}$/.exec(segment);
				if (parameter) return `By${parameter[1][0].toUpperCase()}${parameter[1].slice(1)}`;
				return segment.split('-').map((part) => part[0].toUpperCase() + part.slice(1)).join('');
			}).join('');
			// Umbraco majors differ in operationId conventions; keep our SDK stable.
			paths[key] = Object.fromEntries(Object.entries(value).map(([method, operation]) =>
				['get', 'post', 'put', 'delete', 'patch'].includes(method)
					? [method, { ...operation, operationId: `${method}${suffix}` }]
					: [method, operation]));
		}
	}
	if (Object.keys(paths).length === 0) {
		throw new Error('No property-audit paths found in the OpenAPI document.');
	}

	const components = {};
	const queue = [];
	const seen = new Set();
	const enqueueRef = (ref) => {
		const match = /^#\/components\/([^/]+)\/([^/]+)$/.exec(ref);
		if (!match) return;
		const [, category, name] = match;
		if (seen.has(`${category}/${name}`)) return;
		seen.add(`${category}/${name}`);
		queue.push([category, name]);
	};
	const walk = (node) => {
		if (!node || typeof node !== 'object') return;
		if (typeof node.$ref === 'string') enqueueRef(node.$ref);
		for (const value of Object.values(node)) {
			if (value && typeof value === 'object') walk(value);
		}
	};
	walk(paths);

	while (queue.length > 0) {
		const [category, name] = queue.shift();
		const source = spec.components?.[category]?.[name];
		if (source === undefined) {
			throw new Error(`Missing local reference #/components/${category}/${name} in OpenAPI document.`);
		}
		components[category] ??= {};
		components[category][name] = source;
		walk(source);
	}

	// Security schemes referenced by operation security requirements are
	// components too but are not traversed via $ref; copy them all.
	if (spec.components?.securitySchemes) {
		components.securitySchemes = spec.components.securitySchemes;
	}

	return {
		openapi: spec.openapi,
		info: spec.info,
		servers: spec.servers,
		// Preserve global security requirements (some hosts secure globally
		// rather than per-operation).
		security: spec.security,
		paths,
		components,
	};
}

async function generate(input) {
	await createClient({
		input,
		output: {
			path: OUT_DIR,
			format: 'prettier',
		},
		plugins: [
			{ name: '@hey-api/client-fetch', runtimeConfigPath: '../hey-api.ts' },
			'@hey-api/sdk',
		],
	});
	console.log(`Generated client into ${OUT_DIR}`);
}

const args = process.argv.slice(2);
if (args.includes('--offline')) {
	const spec = JSON.parse(await readFile(SPEC_PATH, 'utf8'));
	await generate(spec);
} else {
	const server = argValue(args, '--server') ?? 'http://localhost:5571';
	const specPath = argValue(args, '--spec-path') ?? '/umbraco/swagger/management/swagger.json';
	const url = new URL(specPath, server).toString();
	console.log(`Fetching Management OpenAPI document from ${url}`);
	const response = await fetch(url);
	if (!response.ok) {
		throw new Error(`Failed to fetch ${url}: ${response.status} ${response.statusText}`);
	}
	const spec = await response.json();
	const filtered = filterManagementSpec(spec);
	await mkdir(path.dirname(SPEC_PATH), { recursive: true });
	await writeFile(SPEC_PATH, JSON.stringify(filtered, null, 2) + '\n');
	console.log(`Saved filtered spec (${Object.keys(filtered.paths).length} paths) to ${SPEC_PATH}`);
	await generate(filtered);
}
