// Web storage for per-viewer conveniences only. Storage can be missing or blocked (private windows, previews),
// so every access is guarded and the app works without it.

type Area = 'local' | 'session';

function area(name: Area): Storage | undefined {
  try {
    return name === 'local' ? window.localStorage : window.sessionStorage;
  } catch {
    return undefined;
  }
}

export function readItem(name: Area, key: string): string | undefined {
  try {
    return area(name)?.getItem(key) ?? undefined;
  } catch {
    return undefined;
  }
}

export function writeItem(name: Area, key: string, value: string | undefined): void {
  try {
    const storage = area(name);
    if (value === undefined) {
      storage?.removeItem(key);
    } else {
      storage?.setItem(key, value);
    }
  } catch {
    // Full or blocked: the convenience is lost, nothing else.
  }
}
