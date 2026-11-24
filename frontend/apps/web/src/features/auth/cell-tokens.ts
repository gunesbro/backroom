// Token targets and the cells a sign-in asks for (docs/contracts/phase-1.md, "Audience per target"). Shared by the
// web and the desktop client.

/** Every token names one target: the control plane or one cell. */
export type TokenTarget = 'control-plane' | `cell:${string}`;

export const CONTROL_PLANE: TokenTarget = 'control-plane';

/** The backend's rule for cell ids (and the desktop shell's): lower-case letters, digits and hyphens, at most 63. */
const CELL_ID = /^[a-z0-9][a-z0-9-]{0,62}$/;

/** A sign-in lists at most 20 shared cells, each id at most 16 characters (contract, "Limits"). */
export const MAX_SHARED_CELLS = 20;
const SIGN_IN_CELL_ID = /^[a-z0-9][a-z0-9-]{0,15}$/;

export function isCellId(id: string): boolean {
  return CELL_ID.test(id);
}

/** Whether a cell id fits the sign-in limits, so it may be listed in a sign-in or remembered. */
export function fitsSignIn(id: string): boolean {
  return SIGN_IN_CELL_ID.test(id);
}

export function cellTarget(cellId: string): TokenTarget {
  if (!isCellId(cellId)) {
    throw new Error('The cell id is not valid.');
  }

  return `cell:${cellId}`;
}

/** The cell id of a `cell:{id}` target; undefined for the control plane. */
export function cellOf(target: TokenTarget): string | undefined {
  return target.startsWith('cell:') ? target.slice('cell:'.length) : undefined;
}

/** The targets among a token answer's or a sign-in's scopes; `openid`, `profile` and the like are not targets. */
export function targetsOf(scope: string | undefined): Set<TokenTarget> {
  const targets = new Set<TokenTarget>();
  for (const scope_ of (scope ?? '').split(' ')) {
    if (scope_ === CONTROL_PLANE) targets.add(CONTROL_PLANE);
    else if (scope_.startsWith('cell:') && isCellId(scope_.slice('cell:'.length)))
      targets.add(scope_ as TokenTarget);
  }
  return targets;
}

/** The session has ended (the refresh token was refused): get new tokens through Keycloak's session. */
export class SessionEndedError extends Error {
  constructor() {
    super('The session has ended.');
    this.name = 'SessionEndedError';
  }
}

/**
 * The target's scope is not in this sign-in. `afterRefresh` means it was granted at sign-in but Keycloak left it
 * out of the answer, so asking again would not help.
 */
export class ScopeNotGrantedError extends Error {
  constructor(
    readonly target: TokenTarget,
    readonly afterRefresh: boolean,
  ) {
    super(`The scope ${target} was not granted.`);
    this.name = 'ScopeNotGrantedError';
  }
}

/** Keycloak does not know a requested scope (`invalid_scope`), for example a cell that no longer exists. */
export class InvalidScopeError extends Error {
  constructor() {
    super('A requested scope does not exist.');
    this.name = 'InvalidScopeError';
  }
}

/**
 * The organization's server cannot be used with this sign-in: its scope is still missing after authorizing it once.
 * The client does not try again until the next sign-in, so it never loops.
 */
export class CellUnavailableError extends Error {
  constructor(readonly cellId: string) {
    super('The organization’s server is unavailable.');
    this.name = 'CellUnavailableError';
  }
}

/** The desktop app is authorizing the cell in the browser; the request can be made again once that finishes. */
export class CellSignInPendingError extends Error {
  constructor(readonly cellId: string) {
    super('The organization’s server is being authorized.');
    this.name = 'CellSignInPendingError';
  }
}

/** The sign-in screen must not wait long for the list first. */
const SHARED_CELLS_TIMEOUT_MS = 3000;

/**
 * The ids of the shared cells, whose scopes the sign-in requests. If the list cannot be read, the sign-in goes ahead
 * without them; a cell is then authorized when it is first needed.
 */
export async function fetchSharedCells(controlPlaneUrl: string, fetchFn: typeof fetch): Promise<string[]> {
  try {
    const response = await fetchFn(`${controlPlaneUrl}/api/v1/cells/shared`, {
      headers: { Accept: 'application/json' },
      credentials: 'omit',
      signal: AbortSignal.timeout(SHARED_CELLS_TIMEOUT_MS),
    });
    if (!response.ok) return [];
    const body = (await response.json()) as { cells?: { id?: unknown }[] };
    return (Array.isArray(body.cells) ? body.cells : [])
      .map((cell) => cell.id)
      .filter((id): id is string => typeof id === 'string' && fitsSignIn(id))
      .slice(0, MAX_SHARED_CELLS);
  } catch {
    return [];
  }
}

/** The cells a sign-in asks for, each once: the shared ones, the remembered ones, then the one needed now. */
export function signInCells(
  shared: readonly string[],
  remembered: readonly string[],
  needed?: string,
): string[] {
  const cells = new Set<string>(shared);
  for (const id of remembered) cells.add(id);
  if (needed !== undefined && isCellId(needed)) cells.add(needed);
  return [...cells];
}
