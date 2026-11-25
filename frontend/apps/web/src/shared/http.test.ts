import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, requestJson } from './http';

function respond(body: string, init: ResponseInit) {
  const fetchMock = vi.fn(() => Promise.resolve(new Response(body, init)));
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

const token = () => Promise.resolve('token-1');

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('requestJson', () => {
  it('Request_WithABody_SendsTheBearerTokenAndJsonWithoutCookies', async () => {
    const fetchMock = respond('{"id":"1"}', { status: 202 });

    const result = await requestJson<{ id: string }>('/api/v1/organizations', token, {
      method: 'POST',
      body: { name: 'Acme' },
    });

    expect(result).toEqual({ id: '1' });
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/v1/organizations',
      expect.objectContaining({
        method: 'POST',
        body: '{"name":"Acme"}',
        credentials: 'omit',
        referrerPolicy: 'no-referrer',
        headers: expect.objectContaining({
          Authorization: 'Bearer token-1',
          'Content-Type': 'application/json',
        }) as unknown,
      }),
    );
  });

  it('Request_WhenAProblemComesBack_ThrowsItsCodeAndRetryAfter', async () => {
    respond('{"code":"directory.organization_limit","detail":"x"}', {
      status: 429,
      headers: { 'Retry-After': '120', 'Content-Type': 'application/problem+json' },
    });

    const error = await requestJson('/api', token).catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({
      status: 429,
      code: 'directory.organization_limit',
      retryAfterSeconds: 120,
    });
  });

  it('Request_WhenTheErrorIsNotAProblem_UsesTheHttpStatusAsCode', async () => {
    respond('<html>Bad gateway</html>', { status: 502 });

    await expect(requestJson('/api', token)).rejects.toMatchObject({
      code: 'http.502',
      retryAfterSeconds: undefined,
    });
  });

  it('Request_WhenTheResponseIsTooLarge_RefusesIt', async () => {
    respond(`"${'a'.repeat(1_000_001)}"`, { status: 200 });

    await expect(requestJson('/api', token)).rejects.toMatchObject({ code: 'client.response_too_large' });
  });

  it('Request_WhenTheDeclaredSizeIsTooLarge_RefusesItBeforeReading', async () => {
    respond('{}', { status: 200, headers: { 'Content-Length': '2000000' } });

    await expect(requestJson('/api', token)).rejects.toMatchObject({ code: 'client.response_too_large' });
  });

  it('Write_WhileTheCellsEpochIsUnconfirmed_IsRetriedAfterRetryAfterWithoutAnError', async () => {
    vi.useFakeTimers();
    try {
      const fetchMock = vi
        .fn()
        .mockResolvedValueOnce(
          new Response('{"code":"cell.epoch_unconfirmed"}', { status: 503, headers: { 'Retry-After': '5' } }),
        )
        .mockResolvedValueOnce(new Response('{"id":"1"}', { status: 201 }));
      vi.stubGlobal('fetch', fetchMock);

      const result = requestJson<{ id: string }>('/api', token, { method: 'POST', body: {} });
      await vi.advanceTimersByTimeAsync(5000);

      await expect(result).resolves.toEqual({ id: '1' });
      expect(fetchMock).toHaveBeenCalledTimes(2);
    } finally {
      vi.useRealTimers();
    }
  });

  it('Write_WhenTheCellStaysFenced_ThrowsAfterAFewRetries', async () => {
    vi.useFakeTimers();
    try {
      const fetchMock = vi.fn(() =>
        Promise.resolve(
          new Response('{"code":"cell.epoch_unconfirmed"}', { status: 503, headers: { 'Retry-After': '5' } }),
        ),
      );
      vi.stubGlobal('fetch', fetchMock);

      const result = requestJson('/api', token, { method: 'POST', body: {} }).catch(
        (caught: unknown) => caught,
      );
      await vi.advanceTimersByTimeAsync(20_000);

      expect(await result).toMatchObject({ status: 503, code: 'cell.epoch_unconfirmed' });
      expect(fetchMock).toHaveBeenCalledTimes(4);
    } finally {
      vi.useRealTimers();
    }
  });

  it('Request_WithAnotherServiceUnavailable_IsNotRetried', async () => {
    const fetchMock = respond('{"code":"http.503"}', { status: 503, headers: { 'Retry-After': '1' } });

    await expect(requestJson('/api', token, { method: 'POST', body: {} })).rejects.toMatchObject({
      code: 'http.503',
    });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});

describe('requestJson reason', () => {
  it('Request_WhenAProblemNamesAReason_ThrowsItOnlyIfItIsAFixedToken', async () => {
    respond('{"code":"messaging.message_invalid","reason":"links.limit"}', { status: 400 });
    await expect(requestJson('/api', token)).rejects.toMatchObject({ reason: 'links.limit' });

    respond('{"code":"messaging.message_invalid","reason":"<script>alert(1)</script>"}', { status: 400 });
    await expect(requestJson('/api', token)).rejects.toMatchObject({ reason: undefined });
  });
});
