import type { Plugin } from 'vite';

// Trusted Types: the DOM sinks that parse HTML or load script (innerHTML, DOMParser, script URLs and so on) accept
// only values made by a named policy, so a string can never become markup or code by accident (DOM-based XSS).
// The app writes no HTML itself (React builds the DOM, and dangerouslySetInnerHTML is not used). The one policy
// allowed is the rich text editor's own: it parses pasted HTML in an inert document and turns it into editor nodes.
// A new library or code path that needs a sink fails loudly instead of opening a hole; give it a named, reviewed
// policy and list it here.
//
// The same directives are in the desktop shell's CSP (apps/desktop/src-tauri/tauri.conf.json); a test keeps them
// equal (tooling/desktop-csp.test.ts). Deployments add them to the web host's Content-Security-Policy header
// (deploy phase).
export const TRUSTED_TYPES_CSP = "require-trusted-types-for 'script'; trusted-types lexical";

/**
 * Puts the Trusted Types directives into the built index.html as a CSP meta element, and into the pages of the
 * browser smoke tests (e2e/), so those run under the same rule. The rest of the development server stays without it,
 * because its error overlay writes HTML.
 */
export function trustedTypes(): Plugin {
  return {
    name: 'backroom-trusted-types',
    transformIndexHtml: {
      order: 'pre',
      handler(_html, context) {
        if (context.server && !context.path.startsWith('/e2e/')) return [];
        return [
          {
            tag: 'meta',
            attrs: { 'http-equiv': 'Content-Security-Policy', content: TRUSTED_TYPES_CSP },
            injectTo: 'head-prepend',
          },
        ];
      },
    },
  };
}
