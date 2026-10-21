import { expect } from '@open-wc/testing';
import {
  PropertyAuditApiError,
  PropertyAuditRepository,
} from './property-audit.repository.js';

/** Minimal generated-client stub: every method returns the canned result. */
function fakeClient(result: { status: number; statusText?: string; data?: unknown; error?: unknown }) {
  const call = () =>
    Promise.resolve({
      data: result.data,
      error: result.error,
      response: {
        ok: result.status >= 200 && result.status < 300,
        status: result.status,
        statusText: result.statusText ?? '',
      } as Response,
    });
  return { get: call, post: call, put: call, delete: call } as never;
}

describe('PropertyAuditRepository response normalization', () => {
  it('maps an empty 409 body to status 409', async () => {
    const repo = new PropertyAuditRepository(fakeClient({ status: 409 }));
    try {
      await repo.updateAudit('id', {
        title: 'x',
        isShared: false,
        definition: { schemaVersion: 3, allDocumentTypes: true, allElementTypes: true, types: [], columns: [] },
        version: 1,
      });
      expect.fail('should have thrown');
    } catch (e) {
      expect(e).to.be.instanceOf(PropertyAuditApiError);
      expect((e as PropertyAuditApiError).status).to.equal(409);
    }
  });

  it('maps an empty 404 body to status 404', async () => {
    const repo = new PropertyAuditRepository(fakeClient({ status: 404 }));
    try {
      await repo.getAudit('missing');
      expect.fail('should have thrown');
    } catch (e) {
      expect((e as PropertyAuditApiError).status).to.equal(404);
    }
  });

  it('treats an empty 204 delete as success', async () => {
    const repo = new PropertyAuditRepository(fakeClient({ status: 204 }));
    await repo.deleteRun('id'); // resolves without throwing
  });

  it('merges problem-details fields while keeping the HTTP status', async () => {
    const repo = new PropertyAuditRepository(
      fakeClient({
        status: 400,
        error: {
          title: 'Validation failed',
          errors: { title: ['Title is required.'] },
        },
      }),
    );
    try {
      await repo.createAudit({
        title: '',
        isShared: false,
        definition: { schemaVersion: 3, allDocumentTypes: true, allElementTypes: true, types: [], columns: [] },
      });
      expect.fail('should have thrown');
    } catch (e) {
      const apiError = e as PropertyAuditApiError;
      expect(apiError.status).to.equal(400);
      expect(apiError.message).to.equal('Validation failed');
      expect(apiError.errors?.title).to.deep.equal(['Title is required.']);
    }
  });

  it('rejects an unexpected empty 200 body', async () => {
    const repo = new PropertyAuditRepository(
      fakeClient({ status: 200, data: undefined }),
    );
    try {
      await repo.listAudits();
      expect.fail('should have thrown');
    } catch (e) {
      expect((e as PropertyAuditApiError).status).to.equal(200);
    }
  });
});
