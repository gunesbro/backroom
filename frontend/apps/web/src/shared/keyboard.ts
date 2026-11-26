// A key that confirms an input method's composition (Japanese, Chinese, Korean and others) belongs to the input method,
// so it must never act as a command such as send or add. WebKit (Safari, the macOS and Linux desktop shells) delivers
// compositionend before the confirming keydown, so `isComposing` is already false there; that keydown still carries
// keyCode 229 (VK_PROCESSKEY), which is why both are checked.

/** Key code browsers report for a keydown that an input method handles. */
const PROCESS_KEY = 229;

/** True when the keydown belongs to an input method's composition and must not run a shortcut. */
export function isComposingKey(event: KeyboardEvent): boolean {
  // keyCode is deprecated, but it is the only signal WebKit gives for the confirming key.
  // eslint-disable-next-line @typescript-eslint/no-deprecated
  return event.isComposing || event.keyCode === PROCESS_KEY;
}
