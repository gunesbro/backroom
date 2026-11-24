import type { Messages } from '@backroom/i18n';

export const messages: Messages<
  | 'signingIn'
  | 'signInFailed'
  | 'tryAgain'
  | 'desktopSignIn'
  | 'desktopSignInText'
  | 'signInWithBrowser'
  | 'signInInBrowser'
  | 'signInInBrowserText'
  | 'waitingForBrowser'
  | 'reopenBrowser'
  | 'copySignInLink'
  | 'signInLinkCopied'
  | 'signInLinkCopyFailed'
  | 'cancelSignIn'
  | 'cellSignIn'
  | 'cellSignInText'
> = {
  en: {
    signingIn: 'Signing you in…',
    signInFailed: "We couldn't sign you in.",
    tryAgain: 'Try again',
    desktopSignIn: 'Sign in',
    desktopSignInText:
      "For your security, sign-in opens in your default browser. When you're done, your browser will ask whether to open the app.",
    signInWithBrowser: 'Sign in with your browser',
    signInInBrowser: 'Sign in in your browser',
    signInInBrowserText:
      "For your security, sign-in opened in your default browser. When you're done, your browser will ask whether to open the app.",
    waitingForBrowser: 'Waiting for sign-in in the browser…',
    reopenBrowser: 'Open the browser again',
    copySignInLink: 'Copy the sign-in link',
    signInLinkCopied: 'Link copied. Paste it into your browser.',
    signInLinkCopyFailed: "The link couldn't be copied.",
    cancelSignIn: 'Cancel',
    cellSignIn: 'One more step in your browser',
    cellSignInText:
      "This organization's server needs one more confirmation in your browser. Sign-in opens in your default browser; when you're done, come back to the app.",
  },
  tr: {
    signingIn: 'Giriş yapılıyor…',
    signInFailed: 'Giriş yapılamadı.',
    tryAgain: 'Tekrar dene',
    desktopSignIn: 'Giriş yap',
    desktopSignInText:
      'Giriş, güvenlik için varsayılan tarayıcında açılır. Giriş bitince tarayıcın uygulamayı açmak isteyip istemediğini sorar.',
    signInWithBrowser: 'Tarayıcıda giriş yap',
    signInInBrowser: 'Tarayıcında giriş yap',
    signInInBrowserText:
      'Giriş, güvenlik için varsayılan tarayıcında açıldı. Giriş bitince tarayıcın uygulamayı açmak isteyip istemediğini sorar.',
    waitingForBrowser: 'Tarayıcıda giriş bekleniyor…',
    reopenBrowser: 'Tarayıcıyı yeniden aç',
    copySignInLink: 'Giriş bağlantısını kopyala',
    signInLinkCopied: 'Bağlantı kopyalandı. Tarayıcına yapıştır.',
    signInLinkCopyFailed: 'Bağlantı kopyalanamadı.',
    cancelSignIn: 'Vazgeç',
    cellSignIn: 'Tarayıcında bir adım daha',
    cellSignInText:
      'Bu organizasyonun sunucusu tarayıcında bir onay daha istiyor. Giriş varsayılan tarayıcında açılır; bitince uygulamaya geri dön.',
  },
};
