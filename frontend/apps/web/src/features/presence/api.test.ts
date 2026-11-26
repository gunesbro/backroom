import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { createStatusApi, listPresence } from './api';
import { isSingleEmoji, readCustomStatus, tomorrowMorning } from './status';

const server = setupServer();
beforeAll(() => {
  server.listen({ onUnhandledFrame: 'error' });
});
afterEach(() => {
  server.resetHandlers();
});
afterAll(() => {
  server.close();
});

const CELL = 'https://cell.example.test';
const ORG = '0b6f3c6e-6a49-4c1e-9d0a-3f1f0c2b7a11';

describe('presence API', () => {
  it('ListPresence_AsksTheCellWithCommaSeparatedIdsAndTheCellToken', async () => {
    const seen: { url: string; authorization: string | null }[] = [];
    server.use(
      http.get(`${CELL}/api/v1/orgs/${ORG}/presence`, ({ request }) => {
        seen.push({ url: request.url, authorization: request.headers.get('Authorization') });
        return HttpResponse.json({
          presence: [{ accountId: 'a', state: 'idle', dndUntil: null, customStatus: null }],
        });
      }),
    );

    const presence = await listPresence(CELL, () => Promise.resolve('cell-token'), ORG, ['a', 'b', 'a']);

    expect(presence).toEqual([{ accountId: 'a', state: 'idle', dndUntil: null, customStatus: null }]);
    expect(new URL(seen[0]?.url ?? '').searchParams.get('accountIds')).toBe('a,b');
    expect(seen[0]?.authorization).toBe('Bearer cell-token');
  });

  it('ListPresence_WithoutTheOption_FollowsTheBuildsLoopbackRule', async () => {
    // Tests run as a development build, where the shared rule allows loopback http.
    let seen = '';
    server.use(
      http.get(`http://localhost:5200/api/v1/orgs/${ORG}/presence`, ({ request }) => {
        seen = request.url;
        return HttpResponse.json({ presence: [] });
      }),
    );

    await listPresence('http://localhost:5200', () => Promise.resolve('cell-token'), ORG, ['a']);

    expect(new URL(seen).origin).toBe('http://localhost:5200');
  });

  it('ListPresence_ToAnAddressThatIsNotAllowed_NeverAsksForAToken', async () => {
    const token = vi.fn(() => Promise.resolve('cell-token'));

    await expect(
      listPresence('http://cell.example.test', token, ORG, ['a'], { allowLoopbackHttp: false }),
    ).rejects.toThrow();
    expect(token).not.toHaveBeenCalled();
  });

  it('PutStatus_SendsTheWholeStatusToTheControlPlane', async () => {
    let body: unknown;
    server.use(
      http.put('https://cp.example.test/api/v1/me/status', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json({
          dndUntil: null,
          away: false,
          customStatus: null,
          timeZone: 'UTC',
          version: 2,
        });
      }),
    );
    const input = {
      dndUntil: null,
      away: false,
      customStatus: { emoji: '🎯', text: null, expiresAt: null },
      timeZone: 'UTC',
    };

    await createStatusApi('https://cp.example.test', () => Promise.resolve('t')).put(input);

    expect(body).toEqual(input);
  });
});

describe('status rules', () => {
  it.each(['🎯', '👍🏽', '🇹🇷', '👨‍👩‍👧'])('IsSingleEmoji_%s_IsOne', (value) => {
    expect(isSingleEmoji(value)).toBe(true);
  });

  it.each(['a', '🎯🎯', '', ':)'])('IsSingleEmoji_%s_IsNot', (value) => {
    expect(isSingleEmoji(value)).toBe(false);
  });

  it('ReadCustomStatus_TextWithALineBreakOrBidiControl_IsRefused', () => {
    const now = new Date('2026-11-24T10:00:00Z');
    expect(readCustomStatus('', 'a\nb', null, now)).toEqual({ problem: 'textInvalid' });
    expect(readCustomStatus('', 'a\u202Eb', null, now)).toEqual({ problem: 'textInvalid' });
  });

  it('ReadCustomStatus_TurkishText_CountsCharactersNotUnits', () => {
    const now = new Date('2026-11-24T10:00:00Z');
    expect(readCustomStatus('', 'İ'.repeat(100), null, now)).toEqual({
      status: { emoji: null, text: 'İ'.repeat(100), expiresAt: null },
    });
  });

  it('ReadCustomStatus_ExpiryPastThirtyDays_IsRefused', () => {
    const now = new Date('2026-11-24T10:00:00Z');
    expect(readCustomStatus('🎯', '', new Date('2026-12-25T10:00:00Z'), now)).toEqual({
      problem: 'expiryInvalid',
    });
  });

  it('TomorrowMorning_IsNineOClockOnTheNextLocalDay', () => {
    const value = tomorrowMorning(new Date(2026, 11, 31, 23, 30));
    expect([value.getFullYear(), value.getMonth(), value.getDate(), value.getHours()]).toEqual([
      2027, 0, 1, 9,
    ]);
  });
});
