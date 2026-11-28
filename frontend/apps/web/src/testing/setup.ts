import '@testing-library/jest-dom/vitest';
import { cleanup, configure } from '@testing-library/react';
import { afterEach } from 'vitest';

afterEach(() => {
  cleanup();
});

// The default 1 s wait fails although nothing is wrong when the machine is busy: on a shared CI machine, and locally
// when the whole suite runs in parallel workers. A real failure still fails, only later.
configure({ asyncUtilTimeout: import.meta.env.BACKROOM_TEST_CI ? 5000 : 3000 });

// jsdom has no modal dialogs: a minimal stand-in, installed only where the browser API is missing.
if (typeof HTMLDialogElement !== 'undefined') {
  const proto = HTMLDialogElement.prototype;
  if (typeof proto.showModal !== 'function') {
    proto.showModal = function showModal(this: HTMLDialogElement) {
      this.setAttribute('open', '');
      this.dataset.modal = 'true';
    };
  }
  if (typeof proto.close !== 'function') {
    proto.close = function close(this: HTMLDialogElement) {
      if (!this.hasAttribute('open')) return;
      this.removeAttribute('open');
      delete this.dataset.modal;
      this.dispatchEvent(new Event('close'));
    };
  }
}

// jsdom has no layout: when the editor puts a caret in view (after focus or an undo) it asks the selection's range
// where it is.
if (typeof Range !== 'undefined') {
  const rect = () => ({
    x: 0,
    y: 0,
    top: 0,
    left: 0,
    right: 0,
    bottom: 0,
    width: 0,
    height: 0,
    toJSON: () => ({}),
  });
  Range.prototype.getBoundingClientRect = rect;
  Range.prototype.getClientRects = () => ({
    length: 0,
    item: () => null,
    [Symbol.iterator]: [][Symbol.iterator],
  });
}
