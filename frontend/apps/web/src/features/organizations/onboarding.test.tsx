import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { AuthProvider } from '@/features/auth';
import { ApiError } from '@/shared/http';
import { LocaleProvider } from '@/shared/locale-provider';
import { fakeAuthClient, fakeOrganizationsApi } from '@/testing/fakes';
import { OrganizationsApiContext, type OrganizationsApi } from './api';
import { Onboarding } from './onboarding';

function renderForm(
  api: OrganizationsApi,
  props: Partial<Parameters<typeof Onboarding>[0]> = {},
  locale: 'en' | 'tr' = 'en',
) {
  const onCreated = vi.fn();
  render(
    <LocaleProvider locale={locale}>
      <QueryClientProvider client={new QueryClient()}>
        <AuthProvider client={fakeAuthClient()}>
          <OrganizationsApiContext.Provider value={api}>
            <Onboarding regions={['eu-dev']} onCreated={onCreated} {...props} />
          </OrganizationsApiContext.Provider>
        </AuthProvider>
      </QueryClientProvider>
    </LocaleProvider>,
  );
  return { onCreated };
}

async function submitName(name: string, label = 'Organization name', button = 'Set up organization') {
  fireEvent.change(await screen.findByLabelText(label), { target: { value: name } });
  fireEvent.click(screen.getByRole('button', { name: button }));
}

function failingCreate(error: Error) {
  return fakeOrganizationsApi({ create: () => Promise.reject(error) });
}

describe('Onboarding', () => {
  it('Create_WithABlankName_ShowsTheNameErrorWithoutCalling', async () => {
    const api = fakeOrganizationsApi();
    renderForm(api);

    await submitName('   ');

    expect(screen.getByLabelText('Organization name')).toHaveAccessibleDescription(
      expect.stringContaining('The name must be 1–100 characters') as string,
    );
    expect(api.create).not.toHaveBeenCalled();
  });

  it('Create_WhenTheServerRejectsTheName_ShowsItOnTheField', async () => {
    renderForm(failingCreate(new ApiError(400, 'directory.organization_invalid')));

    await submitName('​');

    expect(await screen.findByRole('alert')).toHaveTextContent('The name must be 1–100 characters');
    expect(screen.getByLabelText('Organization name')).toHaveAttribute('aria-invalid', 'true');
  });

  it.each([
    [
      new ApiError(403, 'directory.email_not_verified'),
      'Verify your email address to set up an organization.',
    ],
    [new ApiError(429, 'directory.organization_limit', 7200), 'Try again in 2 hours.'],
    [new ApiError(429, 'directory.organization_limit'), 'Try again in 24 hours.'],
    [
      new ApiError(409, 'directory.no_cell_available'),
      "New organizations can't be set up in this region right now.",
    ],
    [new ApiError(429, 'directory.organization_creation_busy', 1), 'Something went wrong. Try again.'],
    [new ApiError(500, 'server.unexpected'), 'Something went wrong. Try again.'],
    [new TypeError('Failed to fetch'), 'Something went wrong. Try again.'],
  ])('Create_WhenTheServerAnswers_%s_ShowsATranslatedError', async (error, text) => {
    renderForm(failingCreate(error));

    await submitName('Acme');

    expect(await screen.findByRole('alert')).toHaveTextContent(text);
  });

  it('Create_InTurkish_ShowsTheLimitInTurkish', async () => {
    renderForm(failingCreate(new ApiError(429, 'directory.organization_limit', 3601)), {}, 'tr');

    await submitName('Acme', 'Organizasyon adı', 'Organizasyonu kur');

    expect(await screen.findByRole('alert')).toHaveTextContent('2 saat sonra tekrar dene.');
  });

  it('Create_WhenAccepted_ReportsTheNewId', async () => {
    const { onCreated } = renderForm(fakeOrganizationsApi());

    await submitName('Acme');

    await waitFor(() => {
      expect(onCreated).toHaveBeenCalledWith('id-Acme');
    });
  });

  it('Create_WithOneRegion_OffersItAsTheDefault', async () => {
    renderForm(fakeOrganizationsApi());

    expect(await screen.findByRole('radio', { name: /Europe/ })).toBeChecked();
    expect(screen.getByRole('group', { name: 'Data region' })).toHaveTextContent('Default region');
    expect(
      screen.getByText('Messages, files and meetings are stored in the region you choose.'),
    ).toBeInTheDocument();
  });

  it('Create_WithAnUnknownRegion_ShowsItsIdNotAGuessedName', async () => {
    renderForm(fakeOrganizationsApi(), { regions: ['eu-dev', 'eu-central'] });

    expect(await screen.findByRole('radio', { name: /Europe/ })).toHaveAttribute('value', 'eu-dev');
    expect(screen.getByRole('radio', { name: 'eu-central' })).toBeInTheDocument();
  });

  it('Create_WithSeveralRegions_SendsTheChosenOne', async () => {
    const api = fakeOrganizationsApi();
    renderForm(api, { regions: ['eu-dev', 'us-test'] });

    fireEvent.click(await screen.findByRole('radio', { name: /us-test/ }));
    await submitName('Acme');

    await waitFor(() => {
      expect(api.create).toHaveBeenCalledWith({ name: 'Acme', region: 'us-test' });
    });
  });

  it('Render_WithoutJoin_WelcomesByFirstNameAndOffersOnlySettingUp', async () => {
    renderForm(fakeOrganizationsApi());

    expect(await screen.findByRole('heading', { level: 1, name: 'Welcome, Alice' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 2, name: 'Set up an organization' })).toBeInTheDocument();
    expect(screen.getByText('Alice Example')).toBeInTheDocument();
    expect(screen.queryByLabelText('Invitation link')).not.toBeInTheDocument();
  });

  it('Join_WithAnInvitationLink_PassesOnlyTheCode', async () => {
    const onJoin = vi.fn();
    const code = 'A'.repeat(43);
    renderForm(fakeOrganizationsApi(), { onJoin });

    expect(await screen.findByRole('heading', { name: 'Have an invitation link?' })).toBeInTheDocument();
    await submitName(`https://app.example.test/invite#${code}`, 'Invitation link', 'Join');

    expect(onJoin).toHaveBeenCalledWith(code);
  });

  it('Join_WithSomethingElse_ShowsTheLinkError', async () => {
    const onJoin = vi.fn();
    renderForm(fakeOrganizationsApi(), { onJoin });

    await submitName('https://app.example.test/', 'Invitation link', 'Join');

    expect(await screen.findByRole('alert')).toHaveTextContent("This invitation link isn't valid.");
    expect(onJoin).not.toHaveBeenCalled();
  });
});
