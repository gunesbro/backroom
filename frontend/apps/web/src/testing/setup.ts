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
