import { afterEach, describe, expect, it, vi } from 'vitest';
import { CellAddressError, cellOrigin, createMessagingApi } from './api';

afterEach(() => {
  vi.restoreAllMocks();
});

describe('cellOrigin', () => {
  it.each([
    ['https://cell.example.test', 'https://cell.example.test'],
    ['https://cell.example.test:8443/some/path?x=1#y', 'https://cell.example.test:8443'],
  ])('Origin_OfAnHttpsAddress_%s_IsItsOriginOnly', (url, expected) => {
    expect(cellOrigin(url, false)).toBe(expected);
  });

  it.each(['http://localhost:5100', 'http://127.0.0.1:5100', 'http://[::1]:5100'])(
    'Origin_OfLoopbackHttp_%s_IsAllowedOnlyInDevelopment',
    (url) => {
      expect(cellOrigin(url, true)).toBe(url);
      expect(cellOrigin(url, false)).toBeUndefined();
    },
  );

  it.each([
    'http://cell.example.test',
    'http://localhost.example.test',
    'javascript:alert(1)',
    'ftp://cell.example.test',
    'https://user:pass@cell.example.test',
    '//cell.example.test',
    'not a url',
    '',
  ])('Origin_OfAnythingElse_%s_IsRefused', (url) => {
    expect(cellOrigin(url, true)).toBeUndefined();
  });
});

describe('createMessagingApi', () => {
  it('Request_ToARefusedAddress_NeverAsksForATokenOrCallsTheNetwork', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    const cellToken = vi.fn(() => Promise.resolve('cell-token'));
    const api = createMessagingApi('http://cell.example.test', cellToken, { allowLoopbackHttp: false });

    await expect(api.listConversations('org', undefined)).rejects.toBeInstanceOf(CellAddressError);
    expect(cellToken).not.toHaveBeenCalled();
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('Request_ToAnAllowedAddress_UsesOnlyItsOrigin', async () => {
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValue(Response.json({ items: [], next: null }));
    const api = createMessagingApi('https://cell.example.test/ignored?x=1', () =>
      Promise.resolve('cell-token'),
    );

    await api.listConversations('org', undefined);

    expect(fetchSpy.mock.calls[0]?.[0]).toBe(
      'https://cell.example.test/api/v1/orgs/org/conversations?limit=50',
    );
  });
});

const message = (seq: number) => ({
  id: `m${String(seq)}`,
  seq,
  clientMessageId: `c${String(seq)}`,
  authorAccountId: 'a',
  text: String(seq),
  createdAt: '2026-11-22T10:00:00Z',
});

function answer(body: unknown) {
  return vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify(body)));
}

describe('createMessagingApi listMessages', () => {
  const api = createMessagingApi('https://cell.example.test', () => Promise.resolve('t'));

  it('ListMessages_WithAfter_ReturnsNewestFirstWithTheCursorOfThePageBelow', async () => {
    const fetchSpy = answer({ items: [message(11), message(12), message(13)], next: 13 });

    const page = await api.listMessages('o', 'c', { after: 10 });

    expect(page.messages.map(({ seq }) => seq)).toEqual([13, 12, 11]);
    expect(page.next).toBe(11);
    expect(fetchSpy.mock.calls[0]?.[0] as string).toBe(
      'https://cell.example.test/api/v1/orgs/o/conversations/c/messages?limit=50&after=10',
    );
  });

  it('ListMessages_WithAfterAndNothingNewer_HasNoCursor', async () => {
    answer({ items: [], next: null });

    const page = await api.listMessages('o', 'c', { after: 99 });

    expect(page).toEqual({ messages: [], next: null });
  });
});

describe('createMessagingApi conversation routes', () => {
  const api = createMessagingApi('https://cell.example.test', () => Promise.resolve('t'));
  const base = 'https://cell.example.test/api/v1/orgs/o';

  it('ListConversations_ReadsItemsAndNext', async () => {
    const fetchSpy = answer({
      items: [{ id: 'c1', kind: 'public_channel', name: 'general', createdAt: '2026-11-22T10:00:00Z' }],
      next: 'c1',
    });

    const page = await api.listConversations('o', 'c0');

    expect(page.conversations.map(({ id }) => id)).toEqual(['c1']);
    expect(page.next).toBe('c1');
    expect(fetchSpy.mock.calls[0]?.[0]).toBe(`${base}/conversations?limit=50&after=c0`);
  });

  it('SendMessage_PostsToTheConversation', async () => {
    const fetchSpy = answer(message(1));

    await api.sendMessage('o', 'c', { clientMessageId: 'x', text: 'hi' });

    expect(fetchSpy.mock.calls[0]?.[0]).toBe(`${base}/conversations/c/messages`);
    expect(fetchSpy.mock.calls[0]?.[1]?.method).toBe('POST');
  });

  it.each([
    ['joinConversation', 'join'],
    ['leaveConversation', 'leave'],
  ] as const)('%s_PostsToTheConversationRoute', async (method, path) => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 204 }));

    await api[method]('o', 'c');

    expect(fetchSpy.mock.calls[0]?.[0]).toBe(`${base}/conversations/c/${path}`);
    expect(fetchSpy.mock.calls[0]?.[1]?.method).toBe('POST');
  });

  const sent = (fetchSpy: ReturnType<typeof answer>) => {
    const [url, init] = fetchSpy.mock.calls[0] ?? [];
    return {
      url,
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? (JSON.parse(init.body) as unknown) : undefined,
    };
  };

  it('CreateChannel_Private_PostsTheDescriptionAndThePeople', async () => {
    const fetchSpy = answer({ id: 'c1', kind: 'private_channel', name: 'plans', description: 'x' });

    await api.createChannel('o', { name: 'plans', private: true, description: 'x', memberAccountIds: ['a'] });

    expect(sent(fetchSpy)).toEqual({
      url: `${base}/channels`,
      method: 'POST',
      body: { name: 'plans', private: true, description: 'x', memberAccountIds: ['a'] },
    });
  });

  it('ListConversationMembers_ReadsItemsAndNext', async () => {
    const fetchSpy = answer({ items: [{ accountId: 'a', role: 'owner', joinedAt: 'x' }], next: 'a' });

    const page = await api.listConversationMembers('o', 'c', 'a0');

    expect(page).toEqual({ members: [{ accountId: 'a', role: 'owner', joinedAt: 'x' }], next: 'a' });
    expect(sent(fetchSpy).url).toBe(`${base}/conversations/c/members?limit=50&after=a0`);
  });

  it('ManageRoutes_UseTheContractsMethodsAndBodies', async () => {
    const calls: unknown[] = [];
    const run = async (call: () => Promise<unknown>) => {
      const fetchSpy = answer({});
      await call();
      calls.push(sent(fetchSpy));
      fetchSpy.mockRestore();
    };

    await run(() => api.getConversation('o', 'c'));
    await run(() => api.updateConversation('o', 'c', { name: 'n', description: '' }));
    await run(() => api.archiveConversation('o', 'c'));
    await run(() => api.unarchiveConversation('o', 'c'));
    await run(() => api.addConversationMembers('o', 'c', ['a', 'b']));
    await run(() => api.transferOwnership('o', 'c', 'a'));

    expect(calls).toEqual([
      { url: `${base}/conversations/c`, method: 'GET', body: undefined },
      { url: `${base}/conversations/c`, method: 'PATCH', body: { name: 'n', description: '' } },
      { url: `${base}/conversations/c/archive`, method: 'POST', body: undefined },
      { url: `${base}/conversations/c/unarchive`, method: 'POST', body: undefined },
      { url: `${base}/conversations/c/members`, method: 'POST', body: { accountIds: ['a', 'b'] } },
      { url: `${base}/conversations/c/owner`, method: 'PUT', body: { accountId: 'a' } },
    ]);
  });

  it.each([
    [200, true],
    [403, false],
  ])('CanManagePublicChannels_WhenTheAdminsCountAnswers%i_Is%s', async (status, expected) => {
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValue(
        Response.json(status === 200 ? { count: 3 } : { code: 'permissions.forbidden' }, { status }),
      );

    await expect(api.canManagePublicChannels('o')).resolves.toBe(expected);
    expect(fetchSpy.mock.calls[0]?.[0]).toBe(`${base}/private-channels/count`);
  });

  it('CanManagePublicChannels_WhenTheCellFails_Throws', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      Response.json({ code: 'messaging.system' }, { status: 500 }),
    );

    await expect(api.canManagePublicChannels('o')).rejects.toMatchObject({ status: 500 });
  });
});
