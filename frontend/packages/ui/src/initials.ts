/** Two letters for an avatar: the first letters of the first two words (Ali Kaya → AK), else of the one word. */
export function initials(name: string): string {
  const words = name
    .split(/\s+/u)
    .map((word) => Array.from(word).filter((character) => /\p{L}|\p{N}/u.test(character)))
    .filter((letters) => letters.length > 0);
  const [first = [], second] = words;
  const letters = second ? [first[0], second[0]] : first.slice(0, 2);
  return letters.join('').toLocaleUpperCase();
}
