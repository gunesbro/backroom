import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import axe from 'axe-core';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { formatDateTime, type Locale } from '@backroom/i18n';
import { LocaleContext } from '@/shared/locale';
import { InviteDialog } from './invite-dialog';
import { createInvitationsApi } from './manage-api';

const CELL = 'https://cell.example.test';
const ORG = '0b6f3c6e-6a49-4c1e-9d0a-3f1f0c2b7a11';
const BASE = `${CELL}/api/v1/orgs/${ORG}/invitations`;

type Seen = { method: string; url: string; body: string; authorization: string | null };
let seen: Seen[] = [];
const server = setupServer();
const cellToken = vi.fn(() => Promise.resolve('cell-token'));

beforeAll(() => {
  server.listen({ onUnhandledFrame: 'error' });
});
afterEach(() => {
  server.resetHandlers();
  seen = [];
  cellToken.mockClear();
});
afterAll(() => {
  server.close();
});

const invitation = (id: string, email: string, role = 'member', expiresAt = '2099-11-29T10:00:00Z') => ({
  id,
  email,
  role,
  createdBy: 'a1',
  createdAt: '2026-11-22T10:00:00Z',
  expiresAt,
});

const problem = (status: number, code: string, headers?: Record<string, string>) =>
  HttpResponse.json({ code, status }, { status, ...(headers ? { headers } : {}) });

async function record(request: Request) {
  seen.push({
    method: request.method,
    url: request.url,
    body: await request.text(),
    authorization: request.headers.get('Authorization'),
  });
}

/** The list endpoint answers with `pages` in turn; creating answers by address from `answers`. */
function serve({
  pages = [{ invitations: [], next: null as string | null }],
  answers = {},
}: {
  pages?: { invitations: ReturnType<typeof invitation>[]; next: string | null }[];
  answers?: Record<string, () => Response>;
} = {}) {
  let page = 0;
  server.use(
    http.get(BASE, async ({ request }) => {
      await record(request);
      return HttpResponse.json(pages[Math.min(page++, pages.length - 1)]);
    }),
    http.post(BASE, async ({ request }) => {
      await record(request);
      const email = (JSON.parse(seen.at(-1)?.body ?? '{}') as { email: string }).email;
      return (
        answers[email] ?? (() => HttpResponse.json(invitation('new', email.toLowerCase()), { status: 201 }))
      )();
    }),
  );
}

/** A small stand-in for a user: types text ("{Enter}" presses Enter), clicks and chooses options. */
function userEvent() {
  return {
    type: (element: HTMLElement, text: string) => {
      const input = element as HTMLInputElement;
      for (const part of text.split(/([{]Enter[}])/u).filter(Boolean)) {
        if (part === '{Enter}') fireEvent.keyDown(input, { key: 'Enter' });
        else fireEvent.change(input, { target: { value: input.value + part } });
      }
      return Promise.resolve();
    },
    click: (element: HTMLElement) => {
      fireEvent.click(element);
      return Promise.resolve();
    },
    selectOptions: (element: HTMLElement, value: string) => {
      fireEvent.change(element, { target: { value } });
      return Promise.resolve();
    },
  };
}
userEvent.setup = userEvent;

function renderDialog(locale: Locale = 'en') {
  const onClose = vi.fn();
  const api = createInvitationsApi(CELL, cellToken);
  const view = render(
    <LocaleContext.Provider value={locale}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <InviteDialog api={api} orgId={ORG} organizationName="Acme" onClose={onClose} />
      </QueryClientProvider>
    </LocaleContext.Provider>,
  );
  return { ...view, onClose };
}

const posts = () => seen.filter((request) => request.method === 'POST');

async function addAndSend(user: ReturnType<typeof userEvent.setup>, ...emails: string[]) {
  const field = screen.getByLabelText(/email addresses/i);
  for (const email of emails) await user.type(field, `${email}{Enter}`);
  await user.click(screen.getByRole('button', { name: 'Send invitations' }));
}

describe('InviteDialog', () => {
  it('Send_SeveralAddresses_PostsEachNormalizedWithTheCellTokenAndDefaultRole', async () => {
    serve();
    const user = userEvent.setup();
    renderDialog();

    await addAndSend(user, 'Deniz@Acme.com', 'emre@acme.com');

    expect(await screen.findByText('Invitation sent to deniz@acme.com.')).toBeInTheDocument();
    expect(screen.getByText('Invitation sent to emre@acme.com.')).toBeInTheDocument();
    expect(posts().map((request) => JSON.parse(request.body) as unknown)).toEqual([
      { email: 'Deniz@Acme.com' },
      { email: 'emre@acme.com' },
    ]);
    expect(posts().every((request) => request.authorization === 'Bearer cell-token')).toBe(true);
    // The list is read again after the mutation.
    await waitFor(() => {
      expect(seen.filter((request) => request.method === 'GET').length).toBeGreaterThan(1);
    });
  });

  it('Send_WithAdminChosen_SendsTheRole', async () => {
    serve();
    const user = userEvent.setup();
    renderDialog();

    await user.selectOptions(screen.getByLabelText('Role'), 'admin');
    await addAndSend(user, 'deniz@acme.com');

    await screen.findByText('Invitation sent to deniz@acme.com.');
    expect(JSON.parse(posts()[0]?.body ?? '{}')).toEqual({ email: 'deniz@acme.com', role: 'admin' });
  });

  it('Send_WithoutAChoosingRight_OffersToSendAsMemberAndSendsTheRest', async () => {
    serve({
      answers: {
        'deniz@acme.com': () => problem(403, 'permissions.forbidden'),
      },
    });
    const user = userEvent.setup();
    renderDialog();

    await user.selectOptions(screen.getByLabelText('Role'), 'admin');
    await addAndSend(user, 'deniz@acme.com', 'emre@acme.com');

    expect(await screen.findByText("You don't have permission to choose a role.")).toBeInTheDocument();
    expect(posts()).toHaveLength(1);
    expect(screen.queryByText(/Not sent yet/)).not.toBeInTheDocument();

    server.use(
      http.post(BASE, async ({ request }) => {
        await record(request);
        return HttpResponse.json(invitation('new', 'x'), { status: 201 });
      }),
    );
    await user.click(screen.getByRole('button', { name: 'Send as Member instead' }));

    await waitFor(() => {
      expect(screen.getAllByText('Invitation sent to x.')).toHaveLength(2);
    });
    const retried = posts()
      .slice(1)
      .map((request) => JSON.parse(request.body) as unknown);
    expect(retried).toEqual([{ email: 'deniz@acme.com' }, { email: 'emre@acme.com' }]);
  });

  it.each([
    ['organization.already_member', 'This address already belongs to a member.'],
    ['organization.member_deactivated', "This member was deactivated and can't return by invitation."],
    ['organization.invitation_pending', 'There is already a pending invitation for this address.'],
    ['organization.invitation_email_invalid', '"deniz@acme.com" is not a valid email address.'],
  ])('Send_ServerAnswers_%s_ShowsItForThatAddressAndContinues', async (code, text) => {
    serve({
      answers: { 'deniz@acme.com': () => problem(code === 'organization.already_member' ? 409 : 400, code) },
    });
    const user = userEvent.setup();
    renderDialog();

    await addAndSend(user, 'deniz@acme.com', 'emre@acme.com');

    expect(await screen.findByText(`deniz@acme.com: ${text}`)).toBeInTheDocument();
    expect(screen.getByText('Invitation sent to emre@acme.com.')).toBeInTheDocument();
    // The refused address stays in the list to fix or remove; the sent one is gone.
    expect(screen.getByRole('button', { name: 'Remove deniz@acme.com' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Remove emre@acme.com' })).not.toBeInTheDocument();
  });

  it('Send_RateLimited_ShowsTheWaitAndDoesNotTryTheRest', async () => {
    serve({
      answers: {
        'deniz@acme.com': () =>
          problem(429, 'organization.invitation_rate_limited', { 'Retry-After': '5400' }),
      },
    });
    const user = userEvent.setup();
    renderDialog();

    await addAndSend(user, 'deniz@acme.com', 'emre@acme.com');

    expect(await screen.findByText('Too many invitations. Try again in 2 hours.')).toBeInTheDocument();
    expect(screen.getByText(/Not sent yet: 2 more/)).toBeInTheDocument();
    expect(posts()).toHaveLength(1);
  });

  it('Send_RateLimitedInTurkish_ShowsTheWaitInTurkish', async () => {
    serve({
      answers: {
        'deniz@acme.com': () =>
          problem(429, 'organization.invitation_rate_limited', { 'Retry-After': '3600' }),
      },
    });
    const user = userEvent.setup();
    renderDialog('tr');

    await user.type(screen.getByLabelText('E-posta adresleri'), 'deniz@acme.com{Enter}');
    await user.click(screen.getByRole('button', { name: 'Davetleri gönder' }));

    expect(await screen.findByText('Çok fazla davet. 1 saat sonra tekrar dene.')).toBeInTheDocument();
  });

  it('Type_InvalidAddress_ShowsAnErrorAndSendsNothing', async () => {
    serve();
    const user = userEvent.setup();
    renderDialog();

    await user.type(screen.getByLabelText(/email addresses/i), 'ılker@acme.com{Enter}');

    expect(screen.getByRole('alert')).toHaveTextContent('"ılker@acme.com" is not a valid email address.');
    await user.click(screen.getByRole('button', { name: 'Send invitations' }));
    expect(posts()).toHaveLength(0);
  });

  it('Type_KeysConfirmingComposition_DoNotAddTheAddress', () => {
    serve();
    renderDialog();
    const field = screen.getByLabelText(/email addresses/i);
    fireEvent.change(field, { target: { value: 'a@acme.com' } });

    fireEvent.keyDown(field, { key: 'Enter', isComposing: true });
    // WebKit: compositionend comes first, so only keyCode 229 marks the confirming key.
    fireEvent.keyDown(field, { key: 'Enter', keyCode: 229 });
    fireEvent.keyDown(field, { key: ' ', keyCode: 229 });

    expect(screen.queryAllByRole('button', { name: /^Remove / })).toHaveLength(0);
    expect(field).toHaveValue('a@acme.com');
  });

  it('Paste_SeveralAddresses_AddsEachOnceAndDropsDuplicates', () => {
    serve();
    renderDialog();
    const field = screen.getByLabelText(/email addresses/i);

    fireEvent.paste(field, { clipboardData: { getData: () => 'a@acme.com, b@acme.com;\nc@acme.com' } });
    expect(screen.getAllByRole('button', { name: /^Remove / })).toHaveLength(3);

    fireEvent.paste(field, { clipboardData: { getData: () => 'A@ACME.COM d@acme.com' } });
    expect(screen.getByRole('alert')).toHaveTextContent('"A@ACME.COM" is already in the list.');
  });

  it('Send_NoAddress_AsksForOne', async () => {
    serve();
    const user = userEvent.setup();
    renderDialog();

    await user.click(screen.getByRole('button', { name: 'Send invitations' }));

    expect(screen.getByRole('alert')).toHaveTextContent('Add at least one email address.');
    expect(posts()).toHaveLength(0);
  });

  it('Send_MoreThanTheLimit_RefusesTheExtraAddress', () => {
    serve();
    renderDialog();
    const many = Array.from({ length: 21 }, (_, index) => `p${String(index)}@acme.com`).join(' ');

    fireEvent.paste(screen.getByLabelText(/email addresses/i), { clipboardData: { getData: () => many } });

    expect(screen.getAllByRole('button', { name: /^Remove / })).toHaveLength(20);
    expect(screen.getByRole('alert')).toHaveTextContent('at most 20 people');
  });

  it('List_PendingInvitations_ShowsThemWithExpiryInTheLocaleAndPages', async () => {
    const first = invitation('i1', 'ali@acme.com', 'member', '2026-12-01T10:00:00Z');
    serve({
      pages: [
        { invitations: [first], next: 'i1' },
        { invitations: [invitation('i2', 'veli@acme.com', 'admin')], next: null },
      ],
    });
    const user = userEvent.setup();
    renderDialog('tr');

    expect(await screen.findByText('ali@acme.com')).toBeInTheDocument();
    expect(
      screen.getByText(new RegExp(`Bitiş: ${formatDateTime(new Date(first.expiresAt), 'tr')}`, 'u')),
    ).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Daha fazla göster' }));
    expect(await screen.findByText('veli@acme.com')).toBeInTheDocument();
    expect(seen.filter((request) => request.method === 'GET').at(-1)?.url).toContain('after=i1');
  });

  it('List_Empty_SaysSo', async () => {
    serve();
    renderDialog();
    expect(await screen.findByText('No pending invitations.')).toBeInTheDocument();
  });

  it('Revoke_APendingInvitation_DeletesItAndReloadsTheList', async () => {
    let revoked = false;
    server.use(
      http.get(BASE, () =>
        HttpResponse.json({ invitations: revoked ? [] : [invitation('i1', 'ali@acme.com')], next: null }),
      ),
      http.delete(`${BASE}/i1`, async ({ request }) => {
        await record(request);
        revoked = true;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    renderDialog();

    await user.click(await screen.findByRole('button', { name: 'Revoke: ali@acme.com' }));
    // Nothing is deleted until it is confirmed.
    expect(screen.getByText(/Revoke the invitation for ali@acme.com?/)).toBeInTheDocument();
    expect(seen).toHaveLength(0);
    await user.click(screen.getByRole('button', { name: 'Revoke: ali@acme.com' }));

    await waitFor(() => {
      expect(screen.queryByText('ali@acme.com')).not.toBeInTheDocument();
    });
    expect(seen[0]).toMatchObject({ method: 'DELETE', authorization: 'Bearer cell-token' });
  });

  it.each([
    [
      409,
      'organization.invitation_not_pending',
      'This invitation was already accepted, revoked or has expired.',
    ],
    [404, 'organization.invitation_not_found', 'This invitation no longer exists.'],
    [403, 'permissions.forbidden', "You don't have permission to invite people."],
  ])('Revoke_ServerAnswers_%i_%s_ShowsATranslatedError', async (status, code, text) => {
    server.use(
      http.get(BASE, () =>
        HttpResponse.json({ invitations: [invitation('i1', 'ali@acme.com')], next: null }),
      ),
      http.delete(`${BASE}/i1`, () => problem(status, code)),
    );
    const user = userEvent.setup();
    renderDialog();

    await user.click(await screen.findByRole('button', { name: 'Revoke: ali@acme.com' }));
    await user.click(screen.getByRole('button', { name: 'Revoke: ali@acme.com' }));

    expect(await screen.findByText(text)).toBeInTheDocument();
  });

  it('Revoke_Kept_DeletesNothingAndShowsTheActionsAgain', async () => {
    server.use(
      http.get(BASE, () =>
        HttpResponse.json({ invitations: [invitation('i1', 'ali@acme.com')], next: null }),
      ),
    );
    const user = userEvent.setup();
    renderDialog();

    await user.click(await screen.findByRole('button', { name: 'Revoke: ali@acme.com' }));
    await user.click(screen.getByRole('button', { name: 'Keep' }));

    expect(screen.getByRole('button', { name: 'Resend: ali@acme.com' })).toBeInTheDocument();
    expect(seen).toHaveLength(0);
  });

  it('Close_ReturnsFocusToWhatOpenedTheDialog', () => {
    serve();
    const opener = document.createElement('button');
    document.body.append(opener);
    opener.focus();
    const { unmount } = renderDialog();
    expect(opener).not.toHaveFocus();

    unmount();

    expect(opener).toHaveFocus();
    opener.remove();
  });

  it('Resend_APendingInvitation_PostsAndConfirms', async () => {
    server.use(
      http.get(BASE, () =>
        HttpResponse.json({ invitations: [invitation('i1', 'ali@acme.com')], next: null }),
      ),
      http.post(`${BASE}/i1/resend`, async ({ request }) => {
        await record(request);
        return HttpResponse.json(invitation('i1', 'ali@acme.com'));
      }),
    );
    const user = userEvent.setup();
    renderDialog();

    await user.click(await screen.findByRole('button', { name: 'Resend: ali@acme.com' }));

    expect(await screen.findByText('Invitation sent again.')).toBeInTheDocument();
    expect(seen[0]).toMatchObject({ method: 'POST', authorization: 'Bearer cell-token' });
  });

  it('Resend_RateLimited_ShowsTheWait', async () => {
    server.use(
      http.get(BASE, () =>
        HttpResponse.json({ invitations: [invitation('i1', 'ali@acme.com')], next: null }),
      ),
      http.post(`${BASE}/i1/resend`, () =>
        problem(429, 'organization.invitation_rate_limited', { 'Retry-After': '1800' }),
      ),
    );
    const user = userEvent.setup();
    renderDialog();

    await user.click(await screen.findByRole('button', { name: 'Resend: ali@acme.com' }));

    expect(await screen.findByText('Too many invitations. Try again in 30 minutes.')).toBeInTheDocument();
  });

  it('Resend_AnExpiredInvitation_IsDisabled', async () => {
    server.use(
      http.get(BASE, () =>
        HttpResponse.json({
          invitations: [invitation('i1', 'ali@acme.com', 'member', '2020-01-01T00:00:00Z')],
          next: null,
        }),
      ),
    );
    renderDialog();

    expect(await screen.findByRole('button', { name: 'Resend: ali@acme.com' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Revoke: ali@acme.com' })).toBeEnabled();
  });

  it('Close_WithEscape_CallsOnClose', async () => {
    serve();
    const { onClose } = renderDialog();
    await screen.findByText('No pending invitations.');

    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    expect(onClose).toHaveBeenCalled();
  });

  it.each<Locale>(['en', 'tr'])(
    'Dialog_WithAddressesAndAList_HasNoAccessibilityViolations_%s',
    async (locale) => {
      serve({ pages: [{ invitations: [invitation('i1', 'ali@acme.com')], next: null }] });
      const user = userEvent.setup();
      const { container } = renderDialog(locale);
      await user.type(screen.getByRole('textbox'), 'deniz@acme.com{Enter}');
      await screen.findByText('ali@acme.com');

      const results = await axe.run(container, { rules: { 'color-contrast': { enabled: false } } });

      expect(results.violations).toEqual([]);
    },
  );
});
