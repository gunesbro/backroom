// Trusted Types: the DOM sinks that parse HTML or load script (innerHTML, DOMParser, script URLs and so on) accept
// only values made by a named policy, so a string can never become markup or code by accident (DOM-based XSS).
// Each app lists the policies it allows next to its Vite config; a new library or code path that needs a sink fails
// loudly instead of opening a hole, and gets a named, reviewed policy there. Deployments add the same directives to
// the web hosts' Content-Security-Policy header (deploy phase).

/** The directives for an app that allows these named policies (none: no sink may be used at all). */
export function trustedTypesCsp(policies: readonly string[]): string {
  return `require-trusted-types-for 'script'; trusted-types ${policies.length > 0 ? policies.join(' ') : "'none'"}`;
}

/** The part of a Vite plugin used here; typed locally because this folder does not depend on Vite. */
interface IndexHtmlPlugin {
  name: string;
  transformIndexHtml: {
    order: 'pre';
    handler: (
      html: string,
      context: { path: string; server?: unknown },
    ) => { tag: string; attrs: Record<string, string>; injectTo: 'head-prepend' }[];
  };
}

/**
 * Puts the Trusted Types directives into the built index.html as a CSP meta element, and into the pages of the
 * browser smoke tests (e2e/), so those run under the same rule. The rest of the development server stays without it,
 * because its error overlay writes HTML.
 */
export function trustedTypes(csp: string): IndexHtmlPlugin {
  return {
    name: 'backroom-trusted-types',
    transformIndexHtml: {
      order: 'pre',
      handler(_html, context) {
        if (context.server && !context.path.startsWith('/e2e/')) return [];
        return [
          {
            tag: 'meta',
            attrs: { 'http-equiv': 'Content-Security-Policy', content: csp },
            injectTo: 'head-prepend',
          },
        ];
      },
    },
  };
}
