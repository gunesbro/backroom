import { trustedTypesCsp } from '../../tooling/trusted-types.ts';

// The Trusted Types policies the web app allows (tooling/trusted-types.ts). The app writes no HTML itself (React
// builds the DOM, and dangerouslySetInnerHTML is not used). The one policy allowed is the rich text editor's own:
// it parses pasted HTML in an inert document and turns it into editor nodes. The editor creates it again on every
// paste, hence 'allow-duplicates'.
//
// The same directives are in the desktop shell's CSP (apps/desktop/src-tauri/tauri.conf.json); a test keeps them
// equal (tooling/desktop-csp.test.ts).
export const TRUSTED_TYPES_CSP = trustedTypesCsp(['lexical', "'allow-duplicates'"]);
