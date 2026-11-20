import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  ACCEPTING_EXPIRES_MS,
  captureInvitationCode,
  clearInvitationCode,
  hasPendingInvitation,
  INVITATION_ACCEPTING_KEY,
  INVITATION_CODE_EXPIRES_MS,
  INVITATION_CODE_KEY,
  isAccepting,
  leaveInvitePath,
  readInvitationCode,
  saveInvitationCode,
  setAccepting,
  sweepInvitationCode,
} from './pending-invitation';

const code = 'abcDEF0123456789_-abcDEF0123456789_-abcDEF0';
const otherCode = 'ZYXwvu9876543210_-ZYXwvu9876543210_-ZYXwvu9';
const t0 = 1_800_000_000_000;

function open(url: string) {
  window.history.replaceState(null, '', url);
}

function stored(): unknown {
  return JSON.parse(localStorage.getItem(INVITATION_CODE_KEY) ?? 'null');
}

afterEach(() => {
  vi.restoreAllMocks();
  open('/');
  localStorage.clear();
  sessionStorage.clear();
});

describe('captureInvitationCode', () => {
  it('Capture_FromAnInvitationLink_KeepsTheCodeInLocalStorageAndRemovesTheFragment', () => {
    open(`/invite#${code}`);
    const entries = window.history.length;

    captureInvitationCode(() => t0);

    expect(window.location.pathname).toBe('/invite');
    expect(window.location.hash).toBe('');
    expect(window.location.href).not.toContain(code);
    expect(window.history.length).toBe(entries);
    expect(stored()).toEqual({ code, expiresAt: t0 + INVITATION_CODE_EXPIRES_MS });
    expect(sessionStorage.getItem(INVITATION_CODE_KEY)).toBeNull();
  });

  it('Capture_WithANewerLink_OverwritesTheStoredCode', () => {
    saveInvitationCode(otherCode, () => t0);
    open(`/invite#${code}`);

    captureInvitationCode(() => t0 + 1000);

    expect(readInvitationCode(() => t0 + 1000)).toBe(code);
  });

  it('Capture_WithAMalformedFragment_RemovesItAndKeepsNoCode', () => {
    open('/invite#not-a-code');

    captureInvitationCode();

    expect(window.location.hash).toBe('');
    expect(localStorage.getItem(INVITATION_CODE_KEY)).toBeNull();
  });

  it('Capture_WithAMalformedFragment_LeavesAStoredCodeAlone', () => {
    saveInvitationCode(code, () => t0);
    open('/invite#not-a-code');

    captureInvitationCode(() => t0 + 1000);

    expect(window.location.hash).toBe('');
    expect(stored()).toEqual({ code, expiresAt: t0 + INVITATION_CODE_EXPIRES_MS });
  });

  it('Capture_WithANewLink_StopsWaitingForAnOlderAcceptance', () => {
    sessionStorage.setItem(INVITATION_ACCEPTING_KEY, '1');
    open(`/invite#${code}`);

    captureInvitationCode();

    expect(sessionStorage.getItem(INVITATION_ACCEPTING_KEY)).toBeNull();
  });

  it('Capture_OnAnotherPage_LeavesTheAddressAlone', () => {
    open(`/settings#${code}`);

    captureInvitationCode();

    expect(window.location.hash).toBe(`#${code}`);
    expect(localStorage.getItem(INVITATION_CODE_KEY)).toBeNull();
  });
});

describe('pending invitation', () => {
  it('Save_WithAValidCode_KeepsItUntilCleared', () => {
    saveInvitationCode(code);

    expect(readInvitationCode()).toBe(code);
    expect(hasPendingInvitation()).toBe(true);
    clearInvitationCode();
    expect(readInvitationCode()).toBeUndefined();
    expect(hasPendingInvitation()).toBe(false);
  });

  it('Read_InAFreshTab_FindsTheCodeStoredInAnotherTab', () => {
    saveInvitationCode(code, () => t0);
    // A new tab: its own, empty session storage; the same local storage.
    sessionStorage.clear();

    expect(readInvitationCode(() => t0 + 60_000)).toBe(code);
    expect(hasPendingInvitation(() => t0 + 60_000)).toBe(true);
  });

  it('Read_AfterItExpired_ReturnsNothingAndDeletesIt', () => {
    saveInvitationCode(code, () => t0);

    expect(readInvitationCode(() => t0 + INVITATION_CODE_EXPIRES_MS - 1)).toBe(code);
    expect(readInvitationCode(() => t0 + INVITATION_CODE_EXPIRES_MS)).toBeUndefined();
    expect(localStorage.getItem(INVITATION_CODE_KEY)).toBeNull();
  });

  it('Read_WithAnExpiryBeyondTheLifetime_ReturnsNothingAndDeletesIt', () => {
    localStorage.setItem(
      INVITATION_CODE_KEY,
      JSON.stringify({ code, expiresAt: t0 + INVITATION_CODE_EXPIRES_MS + 1 }),
    );

    expect(readInvitationCode(() => t0)).toBeUndefined();
    expect(localStorage.getItem(INVITATION_CODE_KEY)).toBeNull();
  });

  it('Clear_ForACodeThatWasReplacedByANewerLink_KeepsTheNewerCode', () => {
    saveInvitationCode(code);
    saveInvitationCode(otherCode);

    clearInvitationCode(code);
    expect(readInvitationCode()).toBe(otherCode);

    clearInvitationCode(otherCode);
    expect(readInvitationCode()).toBeUndefined();
  });

  it('Save_WithSomethingElse_Throws', () => {
    expect(() => {
      saveInvitationCode('https://example.test/');
    }).toThrow();
    expect(sessionStorage.length + localStorage.length).toBe(0);
  });

  it.each([
    ['a tampered code', JSON.stringify({ code: `${code}x`, expiresAt: t0 + 1000 })],
    ['a missing expiry', JSON.stringify({ code })],
    ['a text expiry', JSON.stringify({ code, expiresAt: 'soon' })],
    ['an array', '[]'],
    ['null', 'null'],
    ['a bare code', code],
    ['broken JSON', '{"code":'],
  ])('Read_With%s_ReturnsNothingAndDeletesIt', (_name, value) => {
    localStorage.setItem(INVITATION_CODE_KEY, value);

    expect(readInvitationCode(() => t0)).toBeUndefined();
    expect(localStorage.getItem(INVITATION_CODE_KEY)).toBeNull();
  });

  it('Storage_ThatThrows_MeansNoCodeInsteadOfACrash', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new Error('blocked');
    });

    expect(() => {
      saveInvitationCode(code);
    }).not.toThrow();
    expect(readInvitationCode()).toBeUndefined();
    expect(hasPendingInvitation()).toBe(false);
    expect(() => {
      clearInvitationCode();
    }).not.toThrow();
  });

  it.each([
    ['/invite', undefined],
    ['/', INVITATION_CODE_KEY],
    ['/', INVITATION_ACCEPTING_KEY],
  ])('HasPendingInvitation_On%sWithState_IsTrue', (path, key) => {
    open(path);
    if (key === INVITATION_CODE_KEY) saveInvitationCode(code);
    if (key === INVITATION_ACCEPTING_KEY) sessionStorage.setItem(key, String(Date.now()));

    expect(hasPendingInvitation()).toBe(true);
  });

  it('LeaveInvitePath_OnTheInvitePage_GoesToTheApp', () => {
    open('/invite');

    leaveInvitePath();

    expect(window.location.pathname).toBe('/');
  });

  it('Accepting_WithinItsLifetime_KeepsWaiting', () => {
    setAccepting(true);

    expect(isAccepting()).toBe(true);
    setAccepting(false);
    expect(isAccepting()).toBe(false);
  });

  it.each([String(Date.now() - ACCEPTING_EXPIRES_MS), '1', 'garbage'])(
    'Accepting_WhenExpiredOrMalformed_IsForgotten (%s)',
    (value) => {
      sessionStorage.setItem(INVITATION_ACCEPTING_KEY, value);

      expect(isAccepting()).toBe(false);
      expect(hasPendingInvitation()).toBe(false);
      expect(sessionStorage.getItem(INVITATION_ACCEPTING_KEY)).toBeNull();
    },
  );
});

describe('sweepInvitationCode', () => {
  it('Sweep_WithAnExpiredCode_RemovesIt', () => {
    saveInvitationCode(code, () => t0);

    sweepInvitationCode(() => t0 + INVITATION_CODE_EXPIRES_MS);

    expect(localStorage.getItem(INVITATION_CODE_KEY)).toBeNull();
  });

  it('Sweep_WithAMalformedEntry_RemovesIt', () => {
    localStorage.setItem(INVITATION_CODE_KEY, 'not json');

    sweepInvitationCode(() => t0);

    expect(localStorage.getItem(INVITATION_CODE_KEY)).toBeNull();
  });

  it('Sweep_WithAValidCode_KeepsIt', () => {
    saveInvitationCode(code, () => t0);

    sweepInvitationCode(() => t0 + 1000);

    expect(stored()).toEqual({ code, expiresAt: t0 + INVITATION_CODE_EXPIRES_MS });
  });
});
