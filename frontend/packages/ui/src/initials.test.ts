import { describe, expect, it } from 'vitest';
import { initials } from './initials';

describe('initials', () => {
  it.each([
    ['Ali Kaya', 'AK'],
    ['Bob Test', 'BT'],
    ['  Ayşe   Demir Yılmaz ', 'AD'],
    ['Atlas', 'AT'],
    ['şule öz', 'ŞÖ'],
    ['(Acme) Tasarım', 'AT'],
    ['', ''],
    ['… —', ''],
  ])('Initials_Of_%j_Are_%j', (name, expected) => {
    expect(initials(name)).toBe(expected);
  });
});
