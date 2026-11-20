import { StrictMode } from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { fakeAuthClient } from '@/testing/fakes';
import { SignInRequiredError, type BrowserSignIn } from './auth-client';
import { AuthProvider } from './auth-provider';

const alice = { email: 'alice@example.test', name: 'Alice Example', givenName: 'Alice' };

function desktopClient(browser: Partial<BrowserSignIn> = {}) {
  const browserSignIn: BrowserSignIn = {
    run: vi.fn(() => new Promise<void>(() => undefined)),
    reopen: vi.fn(() => Promise.resolve()),
    link: vi.fn(() => Promise.resolve('https://id.example.test/auth?state=x')),
    cancel: vi.fn(() => Promise.resolve()),
    ...browser,
  };
  const start = vi.fn<() => Promise<typeof alice>>().mockRejectedValue(new SignInRequiredError());
  return fakeAuthClient({ start, browserSignIn });
}

function renderProvider(client = desktopClient()) {
  render(
    <StrictMode>
      <AuthProvider client={client}>
        <p>Signed in as {alice.name}</p>
      </AuthProvider>
    </StrictMode>,
  );
  return client;
}

async function startSignIn() {
  fireEvent.click(await screen.findByRole('button', { name: 'Sign in with your browser' }));
}

describe('AuthProvider in the desktop app', () => {
  it('Start_WithoutASession_WaitsForThePersonToStartTheSignIn', async () => {
    const client = renderProvider();

    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
    expect(client.browserSignIn?.run).not.toHaveBeenCalled();
  });

  it('SignInWithBrowser_WhenClicked_OpensTheBrowserOnceAndWaits', async () => {
    const client = renderProvider();

    await startSignIn();

    expect(screen.getByRole('heading', { name: 'Sign in in your browser' })).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Waiting for sign-in in the browser…');
    expect(client.browserSignIn?.run).toHaveBeenCalledOnce();
    expect(client.browserSignIn?.run).toHaveBeenCalledWith('en');
  });

  it('BrowserSignIn_WhenDone_ShowsTheApp', async () => {
    let signedIn = false;
    const client = desktopClient({
      run: vi.fn(() => {
        signedIn = true;
        return Promise.resolve();
      }),
    });
    vi.mocked(client.start).mockImplementation(() =>
      signedIn ? Promise.resolve(alice) : Promise.reject(new SignInRequiredError()),
    );
    renderProvider(client);

    await startSignIn();

    expect(await screen.findByText('Signed in as Alice Example')).toBeInTheDocument();
  });

  it('BrowserSignIn_OpenAgainAndCopyTheLink_UseTheWaitingSignIn', async () => {
    const writeText = vi.fn(() => Promise.resolve());
    Object.assign(navigator, { clipboard: { writeText } });
    const client = renderProvider();
    await startSignIn();

    fireEvent.click(screen.getByRole('button', { name: 'Open the browser again' }));
    fireEvent.click(screen.getByRole('button', { name: 'Copy the sign-in link' }));

    expect(client.browserSignIn?.reopen).toHaveBeenCalledOnce();
    expect(await screen.findByText('Link copied. Paste it into your browser.')).toBeInTheDocument();
    expect(writeText).toHaveBeenCalledWith('https://id.example.test/auth?state=x');
  });

  it('CopyTheLink_WhenTheClipboardRefuses_SaysSo', async () => {
    Object.assign(navigator, { clipboard: { writeText: vi.fn(() => Promise.reject(new Error('denied'))) } });
    renderProvider();
    await startSignIn();

    fireEvent.click(screen.getByRole('button', { name: 'Copy the sign-in link' }));

    expect(await screen.findByText("The link couldn't be copied.")).toBeInTheDocument();
  });

  it('Cancel_WhileWaiting_GoesBackToTheStartScreen', async () => {
    let reject: (reason: unknown) => void = () => undefined;
    const run = vi.fn(
      () =>
        new Promise<void>((_, fail) => {
          reject = fail;
        }),
    );
    const cancel = vi.fn(() => {
      // The shell rejects a command with its error code, a string, not an Error.
      reject('cancelled');
      return Promise.resolve();
    });
    renderProvider(desktopClient({ run, cancel }));
    await startSignIn();

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(await screen.findByRole('button', { name: 'Sign in with your browser' })).toBeInTheDocument();
    expect(cancel).toHaveBeenCalledOnce();
  });

  it('BrowserSignIn_WhenItTimesOut_OffersToTryAgainInTheBrowser', async () => {
    const run = vi
      .fn<(locale: string) => Promise<void>>()
      .mockRejectedValueOnce('timed_out')
      .mockReturnValue(new Promise<void>(() => undefined));
    const client = renderProvider(desktopClient({ run }));
    await startSignIn();

    fireEvent.click(await screen.findByRole('button', { name: 'Try again' }));

    expect(await screen.findByRole('heading', { name: 'Sign in in your browser' })).toBeInTheDocument();
    expect(run).toHaveBeenCalledTimes(2);
    expect(client.signIn).not.toHaveBeenCalled();
  });
});
