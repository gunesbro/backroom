import { afterEach, describe, expect, it } from 'vitest';
import { createRememberedCells, MAX_REMEMBERED_CELLS } from './remembered-cells';

const DAY = 24 * 60 * 60 * 1000;
const KEY = 'ph.auth.remembered-cells';

afterEach(() => {
  localStorage.clear();
});

describe('createRememberedCells', () => {
  it('Remember_MoreThanTen_KeepsTheTenMostRecentlyUsed', () => {
    let time = 1_000 * DAY;
    const cells = createRememberedCells(() => time);

    for (let index = 0; index < MAX_REMEMBERED_CELLS + 2; index += 1) {
      time += 1;
      cells.remember(`c${String(index)}`);
    }

    expect(cells.list()).toHaveLength(MAX_REMEMBERED_CELLS);
    expect(cells.list()[0]).toBe('c11');
    expect(cells.list()).not.toContain('c0');
    expect(cells.list()).not.toContain('c1');
  });

  it('List_AfterNinetyDaysUnused_ForgetsTheCell', () => {
    let time = 1_000 * DAY;
    const cells = createRememberedCells(() => time);
    cells.remember('old');
    time += 10 * DAY;
    cells.remember('newer');

    time += 85 * DAY;

    expect(cells.list()).toEqual(['newer']);
  });

  it('Touch_ADayAfterTheLastUse_KeepsTheCellAnotherNinetyDays', () => {
    let time = 1_000 * DAY;
    const cells = createRememberedCells(() => time);
    cells.remember('eu-9');

    time += 2 * DAY;
    cells.touch('eu-9');
    time += 89 * DAY;

    expect(cells.list()).toEqual(['eu-9']);
  });

  it('Touch_ACellNotRemembered_RemembersNothing', () => {
    const cells = createRememberedCells(() => 1_000 * DAY);

    cells.touch('shared-1');

    expect(localStorage.getItem(KEY)).toBeNull();
  });

  it('Remember_AnIdBeyondTheSignInLimits_IsIgnored', () => {
    const cells = createRememberedCells(() => 1_000 * DAY);

    cells.remember('a-cell-id-longer-than-16');
    cells.remember('EU 1');

    expect(cells.list()).toEqual([]);
  });

  it('List_WithTamperedStorage_KeepsOnlyValidEntries', () => {
    const time = 1_000 * DAY;
    localStorage.setItem(
      KEY,
      JSON.stringify({ ok: time - 1, 'cell offline_access': time, future: time + DAY, bad: 'x' }),
    );

    expect(createRememberedCells(() => time).list()).toEqual(['ok']);
    localStorage.setItem(KEY, '[not json');
    expect(createRememberedCells(() => time).list()).toEqual([]);
  });

  it('Forget_RemovesEveryCell', () => {
    const cells = createRememberedCells(() => 1_000 * DAY);
    cells.remember('eu-9');

    cells.forget();

    expect(cells.list()).toEqual([]);
    expect(localStorage.getItem(KEY)).toBeNull();
  });
});
