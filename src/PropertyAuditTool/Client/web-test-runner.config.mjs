import { esbuildPlugin } from '@web/dev-server-esbuild';
import { importMapsPlugin } from '@web/dev-server-import-maps';
import { playwrightLauncher } from '@web/test-runner-playwright';

const backoffice = '/node_modules/@umbraco-cms/backoffice/dist-cms';

export default {
  rootDir: '.',
  files: ['./src/**/*.test.ts', '!**/node_modules/**'],
  nodeResolve: {
    exportConditions: ['development'],
  },
  browsers: [playwrightLauncher({ product: 'chromium' })],
  plugins: [
    importMapsPlugin({
      inject: {
        importMap: {
          imports: {
            '@umbraco-cms/backoffice/external/lit': `${backoffice}/external/lit/index.js`,
            '@umbraco-cms/backoffice/external/uui': `${backoffice}/external/uui/index.js`,
            '@umbraco-cms/backoffice/lit-element': `${backoffice}/packages/core/lit-element/index.js`,
            '@umbraco-cms/backoffice/element-api': `${backoffice}/libs/element-api/index.js`,
            '@umbraco-cms/backoffice/observable-api': `${backoffice}/libs/observable-api/index.js`,
            '@umbraco-cms/backoffice/context-api': `${backoffice}/libs/context-api/index.js`,
            '@umbraco-cms/backoffice/controller-api': `${backoffice}/libs/controller-api/index.js`,
            '@umbraco-cms/backoffice/class-api': `${backoffice}/packages/core/class-api/index.js`,
            '@umbraco-cms/backoffice/localization-api': `${backoffice}/libs/localization-api/index.js`,
            '@umbraco-cms/backoffice/extension-api': `${backoffice}/libs/extension-api/index.js`,
            '@umbraco-cms/backoffice/notification': `${backoffice}/packages/core/notification/index.js`,
            '@umbraco-cms/backoffice/http-client': `${backoffice}/packages/core/http-client/index.js`,
            '@umbraco-cms/backoffice/localization': `${backoffice}/packages/core/localization/index.js`,
            '@umbraco-cms/backoffice/document': `${backoffice}/packages/documents/documents/index.js`,
          },
        },
      },
    }),
    esbuildPlugin({
      ts: true,
      tsconfig: './tsconfig.json',
      // Tests always run in modern Playwright Chromium; 'auto' falls back to
      // chrome64-class targets when the UA isn't recognized and esbuild then
      // fails transforming destructuring.
      target: 'esnext',
      json: true,
    }),
  ],
  testRunnerHtml: (testFramework) =>
    `<html lang="en-us">
      <head>
        <meta charset="UTF-8" />
      </head>
      <body>
        <script type="module" src="${testFramework}"></script>
      </body>
    </html>`,
};
