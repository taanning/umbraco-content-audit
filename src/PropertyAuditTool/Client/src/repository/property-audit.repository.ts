import {
  deletePropertyAuditAuditById,
  deletePropertyAuditRunByRunId,
  getPropertyAuditAudit,
  getPropertyAuditAuditById,
  getPropertyAuditAuditCatalog,
  getPropertyAuditRunByRunId,
  getPropertyAuditRunByRunIdResults,
  postPropertyAuditAudit,
  postPropertyAuditRun,
  postPropertyAuditRunByRunIdAdvance,
  putPropertyAuditAuditById,
  putPropertyAuditAuditByIdDashboard,
} from "../api/index.js";
import type { Client } from "../api/client/index.js";
import type {
  AuditCatalogResponse,
  AuditDashboardPreferenceWriteRequest,
  AuditResponse,
  AuditRunCreateRequest,
  AuditRunProgress,
  AuditRunResults,
  AuditWriteRequest,
} from "../api/index.js";

/**
 * Normalized API failure. `status` is the HTTP status code (0 for network
 * failures, -1 for aborted requests); `errors` carries field-level validation
 * messages for 400 responses.
 */
export class PropertyAuditApiError extends Error {
  readonly status: number;
  readonly errors?: Record<string, string[]>;

  constructor(status: number, message: string, errors?: Record<string, string[]>) {
    super(message);
    this.name = "PropertyAuditApiError";
    this.status = status;
    this.errors = errors;
  }

  get aborted(): boolean {
    return this.status === -1;
  }
}

export function normalizeError(error: unknown): PropertyAuditApiError {
  if (error instanceof PropertyAuditApiError) {
    return error;
  }

  if (
    typeof error === "object" &&
    error !== null &&
    "name" in error &&
    (error as Error).name === "AbortError"
  ) {
    return new PropertyAuditApiError(-1, "Request was cancelled.");
  }

  // hey-api errors carry the fetch Response on `response` when configured so;
  // otherwise fall back to a generic message.
  const anyError = error as {
    status?: number;
    message?: string;
    errors?: Record<string, string[]>;
    detail?: string;
    title?: string;
  };

  const message =
    anyError?.detail ?? anyError?.title ?? anyError?.message ?? "Request failed.";

  return new PropertyAuditApiError(anyError?.status ?? 0, message, anyError?.errors);
}

type CallResult<T> = {
  data?: T;
  error?: unknown;
  response: Response;
};

/**
 * The HTTP status is authoritative: core may return empty 401/404/409 bodies
 * (or a proxy an empty 500), so `error` alone can't be trusted.
 */
function unwrap<T>(result: CallResult<T>): T {
  const { data, error, response } = result;
  if (!response.ok) {
    const anyError = (error ?? {}) as {
      message?: string;
      detail?: string;
      title?: string;
      errors?: Record<string, string[]>;
    };
    throw new PropertyAuditApiError(
      response.status,
      anyError.detail ?? anyError.title ?? anyError.message ?? response.statusText ??
        "Request failed.",
      anyError.errors,
    );
  }
  if (data === undefined || data === null) {
    throw new PropertyAuditApiError(
      response.status,
      "The server returned an empty response.",
    );
  }
  return data;
}

/**
 * All generated-SDK calls for the Property Audit API go through this repository.
 * An alternate generated client may be injected (tests); production callers use
 * the default shared client configured by the entrypoint.
 */
export class PropertyAuditRepository {
  readonly #client?: Client;

  constructor(client?: Client) {
    this.#client = client;
  }

  // ---- saved audits ----------------------------------------------------------

  async getAuditCatalog(signal?: AbortSignal): Promise<AuditCatalogResponse> {
    try {
      return unwrap(
        await getPropertyAuditAuditCatalog({ client: this.#client, signal }),
      );
    } catch (error) {
      throw normalizeError(error);
    }
  }

  async listAudits(signal?: AbortSignal): Promise<AuditResponse[]> {
    try {
      return unwrap(
        await getPropertyAuditAudit({ client: this.#client, signal }),
      );
    } catch (error) {
      throw normalizeError(error);
    }
  }

  async getAudit(id: string, signal?: AbortSignal): Promise<AuditResponse> {
    try {
      return unwrap(
        await getPropertyAuditAuditById({
          client: this.#client,
          path: { id },
          signal,
        }),
      );
    } catch (error) {
      throw normalizeError(error);
    }
  }

  async createAudit(request: AuditWriteRequest): Promise<AuditResponse> {
    try {
      return unwrap(
        await postPropertyAuditAudit({ client: this.#client, body: request }),
      );
    } catch (error) {
      throw normalizeError(error);
    }
  }

  async updateAudit(id: string, request: AuditWriteRequest): Promise<AuditResponse> {
    try {
      return unwrap(
        await putPropertyAuditAuditById({
          client: this.#client,
          path: { id },
          body: request,
        }),
      );
    } catch (error) {
      throw normalizeError(error);
    }
  }

  async deleteAudit(id: string, version: number): Promise<void> {
    try {
      const { response, error } = await deletePropertyAuditAuditById({
        client: this.#client,
        path: { id },
        query: { version },
      });
      if (!response.ok) {
        unwrap<never>({ response, error });
      }
    } catch (error) {
      throw normalizeError(error);
    }
  }

  async setAuditDashboardPreference(
    id: string,
    request: AuditDashboardPreferenceWriteRequest,
  ): Promise<AuditResponse> {
    try {
      return unwrap(
        await putPropertyAuditAuditByIdDashboard({
          client: this.#client,
          path: { id },
          body: request,
        }),
      );
    } catch (error) {
      throw normalizeError(error);
    }
  }

  // ---- incremental audit runs -------------------------------------------------

  async createRun(
    request: AuditRunCreateRequest,
    signal?: AbortSignal,
  ): Promise<AuditRunProgress> {
    try {
      return unwrap(
        await postPropertyAuditRun({
          client: this.#client,
          body: request,
          signal,
        }),
      );
    } catch (error) {
      throw normalizeError(error);
    }
  }

  async advanceRun(runId: string, signal?: AbortSignal): Promise<AuditRunProgress> {
    try {
      return unwrap(
        await postPropertyAuditRunByRunIdAdvance({
          client: this.#client,
          path: { runId },
          signal,
        }),
      );
    } catch (error) {
      throw normalizeError(error);
    }
  }

  async getRun(runId: string, signal?: AbortSignal): Promise<AuditRunProgress> {
    try {
      return unwrap(
        await getPropertyAuditRunByRunId({
          client: this.#client,
          path: { runId },
          signal,
        }),
      );
    } catch (error) {
      throw normalizeError(error);
    }
  }

  async getRunResults(
    runId: string,
    query: { skip?: number; take?: number; sortBy?: string; direction?: string },
    signal?: AbortSignal,
  ): Promise<AuditRunResults> {
    try {
      return unwrap(
        await getPropertyAuditRunByRunIdResults({
          client: this.#client,
          path: { runId },
          query,
          signal,
        }),
      );
    } catch (error) {
      throw normalizeError(error);
    }
  }

  async deleteRun(runId: string, signal?: AbortSignal): Promise<void> {
    try {
      const { response, error } = await deletePropertyAuditRunByRunId({
        client: this.#client,
        path: { runId },
        signal,
      });
      if (!response.ok) {
        unwrap<never>({ response, error });
      }
    } catch (error) {
      throw normalizeError(error);
    }
  }
}
