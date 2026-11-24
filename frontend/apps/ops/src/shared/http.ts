// A small JSON client for the control plane.

/** An error response (RFC 9457 problem details) or a failed status. */
export class ApiError extends Error {
  constructor(readonly status: number) {
    super(`HTTP ${String(status)}`);
    this.name = 'ApiError';
  }
}

export type AccessTokenSource = () => Promise<string>;

/** Largest response body accepted; a declared larger body is refused before it is read. */
const MAX_RESPONSE_CHARS = 4_000_000;

export async function getJson<T>(
  url: string,
  getAccessToken: AccessTokenSource,
  signal?: AbortSignal,
): Promise<T> {
  const response = await fetch(url, {
    headers: { Accept: 'application/json', Authorization: `Bearer ${await getAccessToken()}` },
    // Bearer tokens only: no cookies go to the API, and no referrer leaves the page.
    credentials: 'omit',
    referrerPolicy: 'no-referrer',
    signal: signal ?? null,
  });

  if (!response.ok) {
    await response.body?.cancel();
    throw new ApiError(response.status);
  }

  if (Number(response.headers.get('Content-Length')) > MAX_RESPONSE_CHARS) {
    await response.body?.cancel();
    throw new ApiError(response.status);
  }

  const text = await response.text();
  if (text.length > MAX_RESPONSE_CHARS) {
    throw new ApiError(response.status);
  }

  return JSON.parse(text) as T;
}
