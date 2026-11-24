// A small JSON client for our APIs until the generated client (@backroom/api-client) arrives.

/** An error response (RFC 9457 problem details): `code` is stable and translated by the client. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    /** Seconds from the Retry-After header (a 429, or a 503 of a fenced write). */
    readonly retryAfterSeconds?: number,
  ) {
    super(`HTTP ${String(status)} ${code}`);
    this.name = 'ApiError';
  }
}

export type AccessTokenSource = () => Promise<string>;

/** Largest response body accepted; a declared larger body is refused before it is read. */
const MAX_RESPONSE_CHARS = 1_000_000;

/**
 * A cell that has not confirmed its epoch yet (a cold start, or right after a restore) refuses writes with this code
 * before handling them, so the request is safe to send again. It is retried briefly, after Retry-After, without
 * showing an error; only when it lasts longer does the caller see it.
 */
const EPOCH_UNCONFIRMED = 'cell.epoch_unconfirmed';

/** Retries of a write the cell fenced, and the longest wait between them. */
const MAX_FENCED_RETRIES = 3;
const MAX_FENCED_WAIT_SECONDS = 5;

type RequestOptions = {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE';
  body?: unknown;
  signal?: AbortSignal | undefined;
};

export async function requestJson<T>(
  url: string,
  getAccessToken: AccessTokenSource,
  init: RequestOptions = {},
): Promise<T> {
  for (let attempt = 0; ; attempt++) {
    try {
      return await requestOnce<T>(url, getAccessToken, init);
    } catch (error) {
      if (
        !(error instanceof ApiError) ||
        error.status !== 503 ||
        error.code !== EPOCH_UNCONFIRMED ||
        attempt >= MAX_FENCED_RETRIES
      ) {
        throw error;
      }

      await wait(Math.min(error.retryAfterSeconds ?? 1, MAX_FENCED_WAIT_SECONDS) * 1000, init.signal);
    }
  }
}

function wait(milliseconds: number, signal: AbortSignal | undefined): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(signal.reason as Error);
      return;
    }

    const timer = setTimeout(() => {
      signal?.removeEventListener('abort', onAbort);
      resolve();
    }, milliseconds);
    function onAbort() {
      clearTimeout(timer);
      reject(signal?.reason as Error);
    }

    signal?.addEventListener('abort', onAbort, { once: true });
  });
}

async function requestOnce<T>(
  url: string,
  getAccessToken: AccessTokenSource,
  init: RequestOptions,
): Promise<T> {
  const headers: Record<string, string> = {
    Accept: 'application/json',
    Authorization: `Bearer ${await getAccessToken()}`,
  };
  if (init.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  const response = await fetch(url, {
    method: init.method ?? 'GET',
    headers,
    body: init.body === undefined ? null : JSON.stringify(init.body),
    // Bearer tokens only: no cookies go to the API, and no referrer leaves the page.
    credentials: 'omit',
    referrerPolicy: 'no-referrer',
    signal: init.signal ?? null,
  });

  if (Number(response.headers.get('Content-Length')) > MAX_RESPONSE_CHARS) {
    await response.body?.cancel();
    throw new ApiError(response.status, 'client.response_too_large');
  }

  const text = await response.text();
  if (text.length > MAX_RESPONSE_CHARS) {
    throw new ApiError(response.status, 'client.response_too_large');
  }

  if (!response.ok) {
    throw new ApiError(response.status, problemCode(text, response.status), retryAfter(response.headers));
  }

  // A 204 has no body.
  return (text === '' ? undefined : JSON.parse(text)) as T;
}

function problemCode(text: string, status: number): string {
  try {
    const problem: unknown = JSON.parse(text);
    if (
      typeof problem === 'object' &&
      problem !== null &&
      'code' in problem &&
      typeof problem.code === 'string'
    ) {
      return problem.code;
    }
  } catch {
    // Not a problem document (a proxy's error page, for example).
  }

  return `http.${String(status)}`;
}

function retryAfter(headers: Headers): number | undefined {
  const seconds = Number(headers.get('Retry-After'));
  return Number.isFinite(seconds) && seconds > 0 ? seconds : undefined;
}
