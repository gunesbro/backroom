import { describe, expect, it } from 'vitest';
import { isComposingKey } from './keyboard';

describe('isComposingKey', () => {
  it('IsComposingKey_WhenComposing_ReturnsTrue', () => {
    expect(isComposingKey(new KeyboardEvent('keydown', { key: 'Enter', isComposing: true }))).toBe(true);
  });

  it('IsComposingKey_WhenWebKitConfirmsAfterCompositionEnd_ReturnsTrue', () => {
    // WebKit: isComposing is already false, keyCode is 229.
    expect(isComposingKey(new KeyboardEvent('keydown', { key: 'Enter', keyCode: 229 }))).toBe(true);
  });

  it('IsComposingKey_WhenPlainEnter_ReturnsFalse', () => {
    expect(isComposingKey(new KeyboardEvent('keydown', { key: 'Enter', keyCode: 13 }))).toBe(false);
  });
});
