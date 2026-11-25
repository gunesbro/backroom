import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import axe from 'axe-core';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Locale } from '@backroom/i18n';
import type { ConnectionStatus, RealtimeClient, RealtimeEventName, RealtimeEvents } from '@backroom/realtime';
import { AuthProvider } from '@/features/auth';
import { LocaleProvider } from '@/shared/locale-provider';
import { fakeAuthClient } from '@/testing/fakes';
import { createMessagingApi, MessagingApiContext, type ConversationKind } from './api';
import { conversationScopeKey } from './queries';
import { RealtimeContext } from './realtime-context';
import { forgetSeqs } from './seq-memory';
import { Workspace } from './workspace';

// Private channels and channel management (Phase 2, step 7): creating, the sidebar, the channel's info, archived
// channels and the errors, against a cell served by MSW and a live connection the test drives by hand.

const CELL = 'https://cell.example.test';
const ORG = '0b6f3c6e-6a49-4c1e-9d0a-3f1f0c2b7a11';
const BASE = `${CELL}/api/v1/orgs/${ORG}`;
const OWN_ID = '00000000-0000-4000-8000-000000000001';
const VELI_ID = '00000000-0000-4000-8000-000000000002';
const AYSE_ID = '00000000-0000-4000-8000-000000000003';
const CAN_ID = '00000000-0000-4000-8000-000000000004';
const CREATED_AT = '2026-11-20T09:00:00Z';

type Role = 'owner' | 'member';
type Conversation = {
  id: string;
  kind: ConversationKind;
  name: string;
  description: string | null;
  archivedAt: string | null;
  /** Own first when the person is in it. */
  members: { accountId: string; role: Role }[];
};
type Sent = { method: string; path: string; body: unknown };

let conversations: Conversation[] = [];
let canManagePublic = false;
let sent: Sent[] = [];
let people: { accountId: string; displayName: string }[] = [];

const general = (): Conversation => ({
  id: 'c-general',
  kind: 'public_channel',
  name: 'general',
  description: 'Everything else',
  archivedAt: null,
  members: [
    { accountId: OWN_ID, role: 'member' },
    { accountId: VELI_ID, role: 'member' },
  ],
});

const plans = (ownRole: Role = 'owner'): Conversation => ({
  id: 'c-plans',
  kind: 'private_channel',
  name: 'plans',
  description: null,
  archivedAt: null,
  members: [
    { accountId: OWN_ID, role: ownRole },
    { accountId: VELI_ID, role: ownRole === 'owner' ? 'member' : 'owner' },
    { accountId: AYSE_ID, role: 'member' },
  ],
});

const ownRole = (conversation: Conversation) =>
  conversation.members.find(({ accountId }) => accountId === OWN_ID)?.role ?? null;
const readable = (conversation: Conversation | undefined): conversation is Conversation =>
  conversation !== undefined && (conversation.kind === 'public_channel' || ownRole(conversation) !== null);
const find = (id: unknown) => conversations.find((conversation) => conversation.id === id);
const detail = (conversation: Conversation) => ({
  id: conversation.id,
  kind: conversation.kind,
  name: conversation.name,
  description: conversation.description,
  createdAt: CREATED_AT,
  archivedAt: conversation.archivedAt,
  memberCount: conversation.members.length,
  ownerAccountId: conversation.members.find(({ role }) => role === 'owner')?.accountId ?? null,
  role: ownRole(conversation),
});
const problem = (status: number, code: string, headers: Record<string, string> = {}) =>
  HttpResponse.json({ code }, { status, headers });
const notFound = () => problem(404, 'messaging.conversation_not_found');
const done = () => new HttpResponse(null, { status: 204 });

const server = setupServer(
  http.get(`${BASE}/members`, () =>
    HttpResponse.json({
      members: [
        { accountId: OWN_ID, displayName: 'Alice Example', email: 'alice@example.test' },
        ...people.map((person) => ({ ...person, email: null })),
      ],
      next: null,
    }),
  ),
  http.get(`${BASE}/conversations`, () =>
    HttpResponse.json({
      items: conversations
        .filter((conversation) => ownRole(conversation) !== null && conversation.archivedAt === null)
        .map(({ id, kind, name }) => ({ id, kind, name, createdAt: CREATED_AT })),
      next: null,
    }),
  ),
  http.get(`${BASE}/channels`, () => HttpResponse.json({ channels: [], next: null })),
  http.get(`${BASE}/private-channels/count`, () =>
    canManagePublic ? HttpResponse.json({ count: 1 }) : problem(403, 'permissions.forbidden'),
  ),
  http.get(`${BASE}/conversations/:id`, ({ params }) => {
    const conversation = find(params.id);
    return readable(conversation) ? HttpResponse.json(detail(conversation)) : notFound();
  }),
  http.get(`${BASE}/conversations/:id/messages`, ({ params }) =>
    readable(find(params.id)) ? HttpResponse.json({ items: [], next: null }) : notFound(),
  ),
  // Two members a page, by account id, so paging shows with few people.
  http.get(`${BASE}/conversations/:id/members`, ({ params, request }) => {
    const conversation = find(params.id);
    if (!readable(conversation)) return notFound();
    const after = new URL(request.url).searchParams.get('after') ?? '';
    const rest = conversation.members
      .toSorted((a, b) => a.accountId.localeCompare(b.accountId, 'en-US'))
      .filter(({ accountId }) => accountId > after);
    return HttpResponse.json({
      items: rest.slice(0, 2).map((member) => ({ ...member, joinedAt: CREATED_AT })),
      next: rest.length > 2 ? rest[1]?.accountId : null,
    });
  }),
  http.post(`${BASE}/channels`, async ({ request }) => {
    const body = (await request.json()) as { name: string; private?: boolean; description?: string };
    sent.push({ method: 'POST', path: '/channels', body });
    const created: Conversation = {
      id: `c-${body.name}`,
      kind: body.private ? 'private_channel' : 'public_channel',
      name: body.name,
      description: body.description ?? null,
      archivedAt: null,
      members: [{ accountId: OWN_ID, role: 'owner' }],
    };
    conversations.push(created);
    return HttpResponse.json(
      { id: created.id, kind: created.kind, name: created.name, description: created.description },
      { status: 201 },
    );
  }),
  http.patch(`${BASE}/conversations/:id`, async ({ params, request }) => {
    const body = (await request.json()) as { name?: string; description?: string };
    sent.push({ method: 'PATCH', path: `/conversations/${String(params.id)}`, body });
    const conversation = find(params.id);
    if (!readable(conversation)) return notFound();
    if (body.name !== undefined) conversation.name = body.name;
    if (body.description !== undefined) conversation.description = body.description || null;
    return HttpResponse.json(detail(conversation));
  }),
  http.post(`${BASE}/conversations/:id/:action`, async ({ params, request }) => {
    const action = String(params.action);
    const body: unknown = action === 'members' ? await request.json() : undefined;
    sent.push({ method: 'POST', path: `/conversations/${String(params.id)}/${action}`, body });
    const conversation = find(params.id);
    if (!readable(conversation)) return notFound();
    if (action === 'archive') conversation.archivedAt = '2026-11-25T10:00:00Z';
    if (action === 'unarchive') conversation.archivedAt = null;
    if (action === 'leave') {
      conversation.members = conversation.members.filter(({ accountId }) => accountId !== OWN_ID);
    }
    if (action === 'members') {
      for (const accountId of (body as { accountIds: string[] }).accountIds) {
        conversation.members.push({ accountId, role: 'member' });
      }
    }
    return done();
  }),
  http.put(`${BASE}/conversations/:id/owner`, async ({ params, request }) => {
    const body = (await request.json()) as { accountId: string };
    sent.push({ method: 'PUT', path: `/conversations/${String(params.id)}/owner`, body });
    const conversation = find(params.id);
    if (!readable(conversation)) return notFound();
    for (const member of conversation.members)
      member.role = member.accountId === body.accountId ? 'owner' : 'member';
    return done();
  }),
);

beforeAll(() => {
  server.listen({ onUnhandledFrame: 'error' });
});

beforeEach(() => {
  conversations = [general(), plans()];
  canManagePublic = false;
  sent = [];
  people = [
    { accountId: VELI_ID, displayName: 'Veli Yılmaz' },
    { accountId: AYSE_ID, displayName: 'Ayşe Demir' },
    { accountId: CAN_ID, displayName: 'Can Öztürk' },
  ];
});

afterEach(() => {
  server.resetHandlers();
  forgetSeqs();
});

afterAll(() => {
  server.close();
});

/** A hub client the test drives by hand. */
function fakeRealtime() {
  const listeners = new Map<RealtimeEventName, ((payload: never) => void)[]>();
  const client = {
    start: vi.fn(),
    stop: vi.fn(() => Promise.resolve()),
    subscribeOrganization: vi.fn(),
    unsubscribeOrganization: vi.fn(),
    subscribeConversation: vi.fn(),
    unsubscribeConversation: vi.fn(),
    watchPresence: vi.fn(),
    reportActivity: vi.fn(),
    on: (event: RealtimeEventName, listener: (payload: never) => void) => {
      listeners.set(event, [...(listeners.get(event) ?? []), listener]);
      return () => undefined;
    },
    onStatus: () => () => undefined,
    status: (): ConnectionStatus => 'connected',
  };
  return {
    client: client as unknown as RealtimeClient,
    spies: client,
    emit: <K extends RealtimeEventName>(event: K, payload: RealtimeEvents[K]) => {
      act(() => {
        for (const listener of listeners.get(event) ?? []) listener(payload as never);
      });
    },
  };
}

function renderWorkspace(locale: Locale = 'en') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const realtime = fakeRealtime();
  const auth = fakeAuthClient({
    start: () =>
      Promise.resolve({
        email: 'alice@example.test',
        name: 'Alice Example',
        givenName: 'Alice',
        accountId: OWN_ID,
      }),
  });
  render(
    <LocaleProvider locale={locale}>
      <QueryClientProvider client={queryClient}>
        <AuthProvider client={auth}>
          <MessagingApiContext.Provider
            value={(cell) => createMessagingApi(cell.url, () => Promise.resolve('cell-token'))}
          >
            <RealtimeContext.Provider value={() => realtime.client}>
              <Workspace organization={{ id: ORG, name: 'Acme', cell: { id: 'a', url: CELL } }} />
            </RealtimeContext.Provider>
          </MessagingApiContext.Provider>
        </AuthProvider>
      </QueryClientProvider>
    </LocaleProvider>,
  );
  return { queryClient, realtime };
}

const updated = (conversation: Conversation): RealtimeEvents['ConversationUpdated'] => ({
  orgId: ORG,
  conversationId: conversation.id,
  name: conversation.name,
  description: conversation.description,
  archivedAt: conversation.archivedAt,
  memberCount: conversation.members.length,
  changeSeq: 9,
  epoch: '1',
});

const click = (name: string | RegExp, within_: HTMLElement = document.body) => {
  fireEvent.click(within(within_).getByRole('button', { name }));
};
const type = (field: HTMLElement, value: string) => {
  fireEvent.change(field, { target: { value } });
};

/** Opens a channel from the sidebar and waits for its header. */
async function open(name: string, sidebarName = name) {
  fireEvent.click(await screen.findByRole('button', { name: sidebarName }));
  await screen.findByRole('heading', { level: 2, name: sidebarName });
}

/** Opens the open channel's info and waits until its members are listed. */
async function openInfo(label = 'Channel info') {
  fireEvent.click(await screen.findByRole('button', { name: label }));
  const dialog = await screen.findByRole('dialog');
  await within(dialog).findAllByRole('listitem');
  return dialog;
}

async function openCreate() {
  await screen.findByRole('button', { name: 'general' });
  click('Add channel');
  return screen.findByRole('dialog', { name: 'Create channel' });
}

async function expectNoViolations(element: HTMLElement) {
  const results = await axe.run(element, { rules: { 'color-contrast': { enabled: false } } });
  expect(results.violations.map((violation) => violation.id)).toEqual([]);
}

describe('Create a private channel', () => {
  it('Create_Private_SendsTheDescriptionAndThePeopleAndOpensTheChannelWithALock', async () => {
    renderWorkspace();
    const dialog = await openCreate();

    type(within(dialog).getByLabelText('Channel name'), ' launch ');
    type(within(dialog).getByLabelText(/^Description/), 'Launch planning');
    fireEvent.click(within(dialog).getByRole('radio', { name: /^Private/ }));
    type(within(dialog).getByRole('searchbox', { name: 'Type a name…' }), 'veli');
    fireEvent.click(await within(dialog).findByRole('checkbox', { name: 'Veli Yılmaz' }));
    expect(within(dialog).queryByRole('checkbox', { name: 'Ayşe Demir' })).not.toBeInTheDocument();
    click('Create', dialog);

    expect(await screen.findByRole('heading', { level: 2, name: 'Private channel launch' })).toBeVisible();
    expect(sent).toEqual([
      {
        method: 'POST',
        path: '/channels',
        body: { name: 'launch', private: true, description: 'Launch planning', memberAccountIds: [VELI_ID] },
      },
    ]);
    expect(await screen.findByRole('button', { name: 'Private channel launch' })).toHaveAttribute(
      'aria-current',
      'true',
    );
  });

  it('Create_Public_SendsNoPeopleAndNoPrivateFlag', async () => {
    renderWorkspace();
    const dialog = await openCreate();

    expect(within(dialog).queryByRole('searchbox')).not.toBeInTheDocument();
    type(within(dialog).getByLabelText('Channel name'), 'news');
    click('Create', dialog);

    await screen.findByRole('heading', { level: 2, name: 'news' });
    expect(sent[0]?.body).toEqual({ name: 'news' });
  });

  it('Picker_NeverOffersThePersonThemself_AndChosenPeopleCanBeRemoved', async () => {
    renderWorkspace();
    const dialog = await openCreate();
    fireEvent.click(within(dialog).getByRole('radio', { name: /^Private/ }));

    fireEvent.click(await within(dialog).findByRole('checkbox', { name: 'Ayşe Demir' }));
    expect(within(dialog).queryByRole('checkbox', { name: 'Alice Example' })).not.toBeInTheDocument();
    const chosen = within(dialog).getByRole('list', { name: 'People to add' });
    expect(within(chosen).getByText('Ayşe Demir')).toBeVisible();
    click('Remove Ayşe Demir', dialog);

    expect(within(dialog).queryByRole('list', { name: 'People to add' })).not.toBeInTheDocument();
    expect(within(dialog).getByRole('checkbox', { name: 'Ayşe Demir' })).not.toBeChecked();
  });

  it('Picker_WithFiftyPeopleChosen_TakesNoMore', async () => {
    people = Array.from({ length: 52 }, (_, index) => ({
      accountId: `00000000-0000-4000-8000-0000000001${String(index).padStart(2, '0')}`,
      displayName: `Person ${String(index).padStart(2, '0')}`,
    }));
    renderWorkspace();
    const dialog = await openCreate();
    fireEvent.click(within(dialog).getByRole('radio', { name: /^Private/ }));
    await within(dialog).findByRole('checkbox', { name: 'Person 00' });
    expect(within(dialog).getByText(/Showing the first 50/)).toBeVisible();

    for (const box of within(dialog).getAllByRole('checkbox')) fireEvent.click(box);

    expect(within(dialog).getAllByRole('checkbox', { checked: true })).toHaveLength(50);
    type(within(dialog).getByRole('searchbox'), 'Person 5');
    expect(within(dialog).getByRole('checkbox', { name: 'Person 50' })).toBeDisabled();
    expect(within(dialog).getByText('You can add up to 50 people at a time.')).toBeVisible();
  });

  it('Create_PrivateWithTheNameOfAVisibleChannel_WarnsButDoesNotRefuse', async () => {
    renderWorkspace();
    const dialog = await openCreate();

    type(within(dialog).getByLabelText('Channel name'), 'General');
    expect(within(dialog).queryByText(/another channel with this name/)).not.toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole('radio', { name: /^Private/ }));

    expect(within(dialog).getByText('You can already see another channel with this name.')).toBeVisible();
    click('Create', dialog);
    await screen.findByRole('heading', { level: 2, name: 'Private channel General' });
  });

  it('Create_WithTooLongADescription_IsRefusedBeforeSending', async () => {
    renderWorkspace();
    const dialog = await openCreate();

    type(within(dialog).getByLabelText('Channel name'), 'news');
    type(within(dialog).getByLabelText(/^Description/), 'x'.repeat(251));
    click('Create', dialog);

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('at most 250 characters');
    expect(sent).toEqual([]);
  });

  it.each([
    ['en', 429, 'messaging.rate_limited', 'You have done this too often. Try again in 2 minutes.'],
    ['tr', 429, 'messaging.rate_limited', 'Bunu çok sık yaptın. 2 dakika sonra tekrar dene.'],
    ['en', 409, 'messaging.membership_limit', /largest number of conversations/],
    ['tr', 409, 'messaging.membership_limit', /en fazla sohbet sayısına ulaşmış/],
    ['en', 400, 'messaging.member_not_in_organization', /no longer an active member of the organization/],
    ['tr', 400, 'messaging.member_not_in_organization', /artık organizasyonun aktif üyesi değil/],
    ['en', 409, 'messaging.channel_name_taken', 'A channel with this name already exists.'],
    ['tr', 409, 'messaging.channel_name_taken', 'Bu adda bir kanal zaten var.'],
  ] as const)('Create_In%s_When%iWith%s_SaysWhy', async (locale, status, code, text) => {
    server.use(http.post(`${BASE}/channels`, () => problem(status, code, { 'Retry-After': '120' })));
    renderWorkspace(locale);
    await screen.findByRole('button', { name: 'general' });
    click(locale === 'tr' ? 'Kanal ekle' : 'Add channel');
    const dialog = await screen.findByRole('dialog');

    type(
      within(dialog).getByRole('textbox', { name: locale === 'tr' ? 'Kanal adı' : 'Channel name' }),
      'news',
    );
    click(locale === 'tr' ? 'Oluştur' : 'Create', dialog);

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(text);
  });

  it('Create_Dialog_WithThePeoplePicker_HasNoAccessibilityViolations', async () => {
    renderWorkspace('tr');
    await screen.findByRole('button', { name: 'general' });
    click('Kanal ekle');
    const dialog = await screen.findByRole('dialog', { name: 'Kanal oluştur' });
    fireEvent.click(within(dialog).getByRole('radio', { name: /^Özel/ }));
    fireEvent.click(await within(dialog).findByRole('checkbox', { name: 'Veli Yılmaz' }));

    expect(
      within(dialog).getByText(
        'Özel kanalın adını ve içeriğini üye olmayan kimse, yöneticiler dahil, göremez.',
      ),
    ).toBeVisible();
    await expectNoViolations(dialog);
  });
});

describe('Sidebar and live changes', () => {
  it('Sidebar_ShowsALockForPrivateChannelsOnly', async () => {
    renderWorkspace();

    expect(await screen.findByRole('button', { name: 'Private channel plans' })).toBeVisible();
    expect(screen.getByRole('button', { name: 'general' })).toBeVisible();
  });

  it('MembershipChanged_Added_ShowsTheChannelAndJoinsItsGroup', async () => {
    const { realtime } = renderWorkspace();
    await screen.findByRole('button', { name: 'general' });
    conversations.push({ ...plans('member'), id: 'c-secret', name: 'secret' });

    realtime.emit('MembershipChanged', { orgId: ORG, conversationId: 'c-secret', change: 'added' });

    expect(await screen.findByRole('button', { name: 'Private channel secret' })).toBeVisible();
    await waitFor(() => {
      expect(realtime.spies.subscribeConversation).toHaveBeenCalledWith(ORG, 'c-secret');
    });
  });

  it('MembershipChanged_RemovedFromTheOpenChannel_LeavesItSaysSoAndForgetsIt', async () => {
    const { realtime, queryClient } = renderWorkspace();
    await open('plans', 'Private channel plans');
    conversations = [general()];

    realtime.emit('MembershipChanged', { orgId: ORG, conversationId: 'c-plans', change: 'removed' });

    expect(await screen.findByRole('heading', { level: 2, name: 'general' })).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Private channel plans' })).not.toBeInTheDocument();
    expect(screen.getByText('You no longer have access to that channel.')).toBeVisible();
    expect(queryClient.getQueryCache().findAll({ queryKey: conversationScopeKey(ORG, 'c-plans') })).toEqual(
      [],
    );
  });

  it('MembershipChanged_RemovedThenAddedAgain_JoinsTheGroupAgain', async () => {
    const { realtime } = renderWorkspace();
    await screen.findByRole('button', { name: 'Private channel plans' });
    await waitFor(() => {
      expect(realtime.spies.subscribeConversation).toHaveBeenCalledWith(ORG, 'c-plans');
    });
    realtime.spies.subscribeConversation.mockClear();

    realtime.emit('MembershipChanged', { orgId: ORG, conversationId: 'c-plans', change: 'removed' });
    await waitFor(() => {
      expect(screen.queryByRole('button', { name: 'Private channel plans' })).not.toBeInTheDocument();
    });
    expect(screen.queryByText('You no longer have access to that channel.')).not.toBeInTheDocument();
    realtime.emit('MembershipChanged', { orgId: ORG, conversationId: 'c-plans', change: 'added' });

    await screen.findByRole('button', { name: 'Private channel plans' });
    await waitFor(() => {
      expect(realtime.spies.subscribeConversation).toHaveBeenCalledWith(ORG, 'c-plans');
    });
  });

  it('ConversationUpdated_Renamed_ChangesTheSidebarTheHeaderAndTheMemberCount', async () => {
    const { realtime } = renderWorkspace();
    await open('plans', 'Private channel plans');
    expect(await screen.findByText('3 members')).toBeVisible();

    realtime.emit('ConversationUpdated', {
      ...updated({ ...plans(), name: 'roadmap', members: plans().members.slice(0, 1) }),
    });

    expect(await screen.findByRole('heading', { level: 2, name: 'Private channel roadmap' })).toBeVisible();
    expect(screen.getByRole('button', { name: 'Private channel roadmap' })).toBeVisible();
    expect(screen.getByText('1 member')).toBeVisible();
  });

  it('ConversationUpdated_Archived_ShowsTheBannerInsteadOfTheComposer_AndUnarchivedBringsItBack', async () => {
    const { realtime } = renderWorkspace();
    await open('general');
    expect(await screen.findByRole('textbox', { name: 'Message #general' })).toBeVisible();
    const channel = find('c-general');
    if (!channel) throw new Error('no channel');

    channel.archivedAt = '2026-11-25T10:00:00Z';
    realtime.emit('ConversationUpdated', updated(channel));

    expect(await screen.findByText(/This channel is archived\./)).toBeVisible();
    expect(screen.queryByRole('textbox', { name: 'Message #general' })).not.toBeInTheDocument();
    // The list of the server leaves archived channels out; the open one stays open.
    expect(screen.queryByRole('button', { name: 'general' })).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 2, name: 'general' })).toBeVisible();

    channel.archivedAt = null;
    realtime.emit('ConversationUpdated', updated(channel));

    expect(await screen.findByRole('textbox', { name: 'Message #general' })).toBeVisible();
    expect(await screen.findByRole('button', { name: 'general' })).toBeVisible();
  });
});

describe('Archived channels and lost access', () => {
  it('ArchivedChannel_WhenOpened_IsReadOnly', async () => {
    const archived = { ...general(), archivedAt: '2026-11-25T10:00:00Z' };
    conversations = [archived];
    // Still listed here, as right after the archive and before the list is read again.
    server.use(
      http.get(`${BASE}/conversations`, () =>
        HttpResponse.json({
          items: [{ id: archived.id, kind: archived.kind, name: archived.name, createdAt: CREATED_AT }],
          next: null,
        }),
      ),
    );
    renderWorkspace('tr');

    expect(
      await screen.findByText('Bu kanal arşivlendi. Okuyabilirsin, ama kimse mesaj gönderemez.'),
    ).toBeVisible();
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
  });

  it('Send_WhenTheChannelWasArchivedMeanwhile_ShowsTheBanner', async () => {
    server.use(
      http.post(`${BASE}/conversations/:id/messages`, () => {
        const channel = find('c-general');
        if (channel) channel.archivedAt = '2026-11-25T10:00:00Z';
        return problem(409, 'messaging.conversation_archived');
      }),
    );
    renderWorkspace();
    await open('general');
    const composer = await screen.findByRole('textbox', { name: 'Message #general' });

    type(composer, 'hello');
    fireEvent.keyDown(composer, { key: 'Enter' });

    expect(await screen.findByText(/This channel is archived\./)).toBeVisible();
    expect(screen.queryByRole('textbox', { name: 'Message #general' })).not.toBeInTheDocument();
  });

  it('Channel_ThatAnswersNotFound_IsLeftWithANotice', async () => {
    renderWorkspace();
    await screen.findByRole('button', { name: 'Private channel plans' });
    conversations = [general()];

    fireEvent.click(screen.getByRole('button', { name: 'Private channel plans' }));

    expect(await screen.findByText('You no longer have access to that channel.')).toBeVisible();
    expect(await screen.findByRole('heading', { level: 2, name: 'general' })).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Private channel plans' })).not.toBeInTheDocument();
    expect(screen.queryByText('Something went wrong. Try again.')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'general' }));
    expect(screen.queryByText('You no longer have access to that channel.')).not.toBeInTheDocument();
  });
});

describe('Channel info', () => {
  it('Info_ListsMembersPageByPage_WithTheOwnerAndThePersonMarked', async () => {
    renderWorkspace();
    await open('plans', 'Private channel plans');
    const dialog = await openInfo();

    const list = within(dialog).getByRole('list', { name: /^Members/ });
    expect(
      within(list)
        .getAllByRole('listitem')
        .map((item) => item.textContent),
    ).toEqual([
      expect.stringMatching(/Alice Example \(you\).*Channel owner/),
      expect.stringMatching(/^VYVeli Yılmaz/),
    ]);
    click('Show more members', dialog);

    expect(await within(list).findByText('Ayşe Demir')).toBeVisible();
    expect(within(dialog).queryByRole('button', { name: 'Show more members' })).not.toBeInTheDocument();
    expect(within(dialog).getByRole('heading', { name: 'Members 3 members' })).toBeVisible();
    // Removing other people is switched off on the server: it is not offered.
    expect(within(dialog).queryByRole('button', { name: /^Remove/ })).not.toBeInTheDocument();
  });

  it('Info_ForTheOwnerOfAPrivateChannel_OffersEveryActionAndHasNoAccessibilityViolations', async () => {
    renderWorkspace('tr');
    fireEvent.click(await screen.findByRole('button', { name: 'Özel kanal plans' }));
    const dialog = await openInfo('Kanal bilgisi');

    for (const name of ['Düzenle', 'Kişi ekle', 'Kanaldan ayrıl', 'Arşivle']) {
      expect(within(dialog).getByRole('button', { name })).toBeVisible();
    }
    expect(
      within(dialog).getByRole('button', { name: 'Sahipliği Veli Yılmaz kişisine devret' }),
    ).toBeVisible();
    await expectNoViolations(dialog);

    click('Kişi ekle', dialog);
    await within(dialog).findByRole('checkbox', { name: 'Can Öztürk' });
    await expectNoViolations(dialog);

    click('İptal', dialog);
    click('Kanaldan ayrıl', dialog);
    await expectNoViolations(dialog);
  });

  it('Info_ForAMemberOfAPrivateChannel_OffersOnlyAddingPeopleAndLeaving', async () => {
    conversations = [general(), plans('member')];
    renderWorkspace();
    await open('plans', 'Private channel plans');
    const dialog = await openInfo();

    expect(within(dialog).getByRole('button', { name: 'Add people' })).toBeVisible();
    expect(within(dialog).getByRole('button', { name: 'Leave channel' })).toBeVisible();
    for (const name of ['Edit', 'Archive', 'Unarchive', /^Make/]) {
      expect(within(dialog).queryByRole('button', { name })).not.toBeInTheDocument();
    }
  });

  it.each([
    [false, 0],
    [true, 1],
  ])(
    'Info_ForAPublicChannel_WhenManagingIs%s_ShowsRenameAndArchiveToThoseAllowed',
    async (allowed, count) => {
      canManagePublic = allowed;
      renderWorkspace();
      await open('general');
      const dialog = await openInfo();

      await waitFor(() => {
        expect(within(dialog).queryAllByRole('button', { name: 'Archive' })).toHaveLength(count);
      });
      expect(within(dialog).queryAllByRole('button', { name: 'Edit' })).toHaveLength(count);
      expect(within(dialog).getByText('Everything else')).toBeVisible();
      // A public channel has no owner to hand over and nobody is added to it.
      expect(within(dialog).queryByRole('button', { name: 'Add people' })).not.toBeInTheDocument();
      expect(within(dialog).queryByRole('button', { name: /^Make/ })).not.toBeInTheDocument();
      expect(within(dialog).queryByText('Channel owner')).not.toBeInTheDocument();
    },
  );

  it('AddPeople_PostsTheChosenAccounts_AndLeavesOutPeopleAlreadyListed', async () => {
    renderWorkspace();
    await open('plans', 'Private channel plans');
    const dialog = await openInfo();

    click('Add people', dialog);
    fireEvent.click(await within(dialog).findByRole('checkbox', { name: 'Can Öztürk' }));
    expect(within(dialog).queryByRole('checkbox', { name: 'Veli Yılmaz' })).not.toBeInTheDocument();
    click('Add', dialog);

    await waitFor(() => {
      expect(sent).toEqual([
        { method: 'POST', path: '/conversations/c-plans/members', body: { accountIds: [CAN_ID] } },
      ]);
    });
    expect(await within(dialog).findByRole('heading', { name: 'Members 4 members' })).toBeVisible();
  });

  it('AddPeople_WhenRateLimited_SaysWhenToTryAgainAndKeepsTheChoice', async () => {
    server.use(
      http.post(`${BASE}/conversations/:id/members`, () =>
        problem(429, 'messaging.rate_limited', { 'Retry-After': '3600' }),
      ),
    );
    renderWorkspace();
    await open('plans', 'Private channel plans');
    const dialog = await openInfo();

    click('Add people', dialog);
    fireEvent.click(await within(dialog).findByRole('checkbox', { name: 'Can Öztürk' }));
    click('Add', dialog);

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Try again in 1 hour.');
    expect(within(dialog).getByRole('checkbox', { name: 'Can Öztürk' })).toBeChecked();
  });

  it('Edit_SendsOnlyWhatChanged_AndShowsIt', async () => {
    renderWorkspace();
    await open('plans', 'Private channel plans');
    const dialog = await openInfo();

    click('Edit', dialog);
    expect(within(dialog).getByRole('group', { name: 'Edit channel' })).toHaveFocus();
    type(within(dialog).getByLabelText(/^Description/), 'Quarterly plans');
    click('Save', dialog);

    expect(await within(dialog).findByText('Quarterly plans')).toBeVisible();
    expect(sent).toEqual([
      { method: 'PATCH', path: '/conversations/c-plans', body: { description: 'Quarterly plans' } },
    ]);
  });

  it('Edit_RenamingAPublicChannelToATakenName_SaysSoAtTheField', async () => {
    canManagePublic = true;
    server.use(http.patch(`${BASE}/conversations/:id`, () => problem(409, 'messaging.channel_name_taken')));
    renderWorkspace();
    await open('general');
    const dialog = await openInfo();

    click(
      await within(dialog)
        .findByRole('button', { name: 'Edit' })
        .then(() => 'Edit'),
      dialog,
    );
    const field = within(dialog).getByLabelText('Channel name');
    type(field, 'news');
    click('Save', dialog);

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'A channel with this name already exists.',
    );
    expect(field).toHaveAttribute('aria-invalid', 'true');
  });

  it('Archive_AfterConfirming_ArchivesAndThenOffersUnarchive', async () => {
    renderWorkspace();
    await open('plans', 'Private channel plans');
    const dialog = await openInfo();

    click('Archive', dialog);
    expect(within(dialog).getByRole('group', { name: 'Archive plans?' })).toHaveFocus();
    expect(within(dialog).getByText(/Messages stay readable/)).toBeVisible();
    click('Archive', dialog);

    expect(await within(dialog).findByText('Archived')).toBeVisible();
    expect(within(dialog).queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: 'Add people' })).not.toBeInTheDocument();
    expect(await screen.findByText(/This channel is archived\./)).toBeVisible();
    click('Unarchive', dialog);

    expect(await within(dialog).findByRole('button', { name: 'Archive' })).toBeVisible();
    expect(sent.map(({ path }) => path)).toEqual([
      '/conversations/c-plans/archive',
      '/conversations/c-plans/unarchive',
    ]);
  });

  it('TransferOwnership_AfterConfirming_MakesTheMemberTheOwner', async () => {
    renderWorkspace();
    await open('plans', 'Private channel plans');
    const dialog = await openInfo();

    click('Make Veli Yılmaz the owner', dialog);
    expect(within(dialog).getByText(/only the new owner can rename/)).toBeVisible();
    click('Make owner', dialog);

    await waitFor(() => {
      expect(sent).toEqual([
        { method: 'PUT', path: '/conversations/c-plans/owner', body: { accountId: VELI_ID } },
      ]);
    });
    await waitFor(() => {
      expect(within(dialog).queryByRole('button', { name: /^Make/ })).not.toBeInTheDocument();
    });
    expect(within(dialog).queryByRole('button', { name: 'Archive' })).not.toBeInTheDocument();
    expect(
      within(within(dialog).getByText('Veli Yılmaz').closest('li') as HTMLElement).getByText('Channel owner'),
    ).toBeVisible();
  });

  it('Leave_AsTheOwner_SaysWhoInherits_ThenLeavesTheChannelWithoutANotice', async () => {
    const { realtime } = renderWorkspace('tr');
    fireEvent.click(await screen.findByRole('button', { name: 'Özel kanal plans' }));
    const dialog = await openInfo('Kanal bilgisi');

    click('Kanaldan ayrıl', dialog);
    expect(
      within(dialog).getByText('Kanal sahibisin. Ayrıldığında sahiplik, kanala en önce katılan üyeye geçer.'),
    ).toBeVisible();
    expect(within(dialog).queryByText(/Son üyesin/)).not.toBeInTheDocument();
    click('Kanaldan ayrıl', dialog);

    expect(await screen.findByRole('heading', { level: 2, name: 'general' })).toBeVisible();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Özel kanal plans' })).not.toBeInTheDocument();
    expect(sent).toEqual([{ method: 'POST', path: '/conversations/c-plans/leave', body: undefined }]);
    // The live event of the person's own leave follows: it is not news.
    realtime.emit('MembershipChanged', { orgId: ORG, conversationId: 'c-plans', change: 'removed' });
    expect(screen.queryByText('O kanala artık erişimin yok.')).not.toBeInTheDocument();
  });

  it('Leave_AsTheLastMember_SaysTheChannelWillBeArchived', async () => {
    conversations = [general(), { ...plans(), members: [{ accountId: OWN_ID, role: 'owner' }] }];
    renderWorkspace();
    await open('plans', 'Private channel plans');
    const dialog = await openInfo();

    click('Leave channel', dialog);

    expect(
      within(dialog).getByText(
        'You are the last member. When you leave, the channel is archived and nobody can reach it again.',
      ),
    ).toBeVisible();
    expect(within(dialog).queryByText(/ownership passes/)).not.toBeInTheDocument();
    click('Cancel', dialog);
    expect(within(dialog).getByRole('button', { name: 'Leave channel' })).toBeVisible();
    expect(sent).toEqual([]);
  });

  it('Leave_APublicChannel_SaysHowToComeBack', async () => {
    renderWorkspace();
    await open('general');
    const dialog = await openInfo();

    click('Leave channel', dialog);

    expect(within(dialog).getByText('You can join again from "Browse channels".')).toBeVisible();
    expect(within(dialog).queryByText(/ownership passes|last member/)).not.toBeInTheDocument();
  });

  it('Info_WhenTheChannelIsTakenAwayWhileOpen_ClosesAndLeavesTheChannel', async () => {
    renderWorkspace();
    await open('plans', 'Private channel plans');
    const dialog = await openInfo();
    conversations = [general()];

    click('Archive', dialog);
    click('Archive', dialog);

    expect(await screen.findByText('You no longer have access to that channel.')).toBeVisible();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(await screen.findByRole('heading', { level: 2, name: 'general' })).toBeVisible();
  });

  it('ConversationUpdated_WhileTheInfoIsOpen_ReadsTheOwnerAndTheMembersAgain', async () => {
    conversations = [general(), plans('member')];
    const { realtime } = renderWorkspace();
    await open('plans', 'Private channel plans');
    const dialog = await openInfo();
    expect(within(dialog).queryByRole('button', { name: 'Archive' })).not.toBeInTheDocument();
    const channel = find('c-plans');
    if (!channel) throw new Error('no channel');

    // The owner handed the channel to this person: the event names no owner.
    for (const member of channel.members) member.role = member.accountId === OWN_ID ? 'owner' : 'member';
    realtime.emit('ConversationUpdated', updated(channel));

    expect(await within(dialog).findByRole('button', { name: 'Archive' })).toBeVisible();
    expect(
      within(within(dialog).getByText('Alice Example').closest('li') as HTMLElement).getByText(
        'Channel owner',
      ),
    ).toBeVisible();
  });
});
