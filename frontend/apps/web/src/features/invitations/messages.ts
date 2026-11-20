import type { Messages } from '@backroom/i18n';

// Turkish texts from docs/design (InviteAccept) where it shows them. The inviter and the role are not shown (the
// control plane does not know the inviter's name, decided 2026-11-15), and an address is never shown for another
// account; those sentences and the rest are new, as approved in review.
type Key =
  | 'checking'
  | 'join'
  | 'joinTitle'
  | 'validUntil'
  | 'joinAs'
  | 'otherAccount'
  | 'mismatchTitle'
  | 'mismatchBody'
  | 'signOutAndSwitch'
  | 'close'
  | 'invalidTitle'
  | 'invalidBody'
  | 'askForNewLink'
  | 'continue'
  | 'rateLimitedTitle'
  | 'rateLimitedBody'
  | 'emailNotVerified'
  | 'joining'
  | 'stillJoining'
  | 'checkAgain'
  | 'failedTitle'
  | 'failedNotPending'
  | 'failedExpired'
  | 'failedEmailMismatch'
  | 'failedInviterNotAuthorized'
  | 'failedMemberDeactivated'
  | 'failedOther'
  | 'generic'
  | 'tryAgain';

export const messages: Messages<Key> = {
  en: {
    checking: 'Checking the invitation…',
    join: 'Join',
    joinTitle: 'Join {organization}',
    validUntil: 'This invitation is valid until {date}.',
    joinAs: "You'll join as {email}",
    otherAccount: 'Continue with another account',
    mismatchTitle: 'This invitation is for another email address',
    mismatchBody:
      "It wasn't sent to the email address of the account you're signed in with. Sign in with the address it was sent to.",
    signOutAndSwitch: 'Sign out and sign in with the right account',
    close: 'Close',
    invalidTitle: "This invitation link isn't valid",
    invalidBody: 'The link has expired, reached its use limit or was cancelled.',
    askForNewLink: 'Ask the person who invited you for a new link.',
    continue: 'Continue',
    rateLimitedTitle: 'Too many attempts',
    rateLimitedBody: 'Try again in {time}.',
    emailNotVerified: 'Verify your email address to join.',
    joining: 'Joining the organization…',
    stillJoining:
      "Still joining. You can close this page; the organization will appear among your organizations when it's ready.",
    checkAgain: 'Check again',
    failedTitle: "You couldn't join the organization",
    failedNotPending: 'The invitation was cancelled or has already been used.',
    failedExpired: 'The invitation has expired.',
    failedEmailMismatch: 'The invitation was sent to another email address.',
    failedInviterNotAuthorized: 'The person who invited you can no longer invite people.',
    failedMemberDeactivated: 'Your account in this organization has been deactivated.',
    failedOther: "The organization's server couldn't add you.",
    generic: 'Something went wrong. Try again.',
    tryAgain: 'Try again',
  },
  tr: {
    checking: 'Davet kontrol ediliyor…',
    join: 'Katıl',
    joinTitle: '{organization} organizasyonuna katıl',
    validUntil: 'Bu davet {date} tarihine kadar geçerli.',
    joinAs: '{email} olarak katılacaksın',
    otherAccount: 'Başka bir hesapla devam et',
    mismatchTitle: 'Bu davet başka bir e-posta için',
    mismatchBody:
      'Davet, giriş yaptığın hesabın e-posta adresine gönderilmedi. Davetin gönderildiği adresle giriş yap.',
    signOutAndSwitch: 'Çıkış yap ve doğru hesapla gir',
    close: 'Kapat',
    invalidTitle: 'Bu davet bağlantısı geçersiz',
    invalidBody: 'Bağlantının süresi dolmuş, kullanım sınırına ulaşmış ya da iptal edilmiş.',
    askForNewLink: 'Seni davet eden kişiden yeni bir bağlantı iste.',
    continue: 'Devam et',
    rateLimitedTitle: 'Çok fazla deneme yapıldı',
    rateLimitedBody: '{time} sonra tekrar dene.',
    emailNotVerified: 'Katılmak için e-posta adresini doğrula.',
    joining: 'Organizasyona katılıyorsun…',
    stillJoining:
      'Hâlâ sürüyor. Bu sayfayı kapatabilirsin; hazır olunca organizasyon, organizasyonların arasında görünecek.',
    checkAgain: 'Tekrar kontrol et',
    failedTitle: 'Organizasyona katılamadın',
    failedNotPending: 'Davet iptal edilmiş ya da zaten kullanılmış.',
    failedExpired: 'Davetin süresi dolmuş.',
    failedEmailMismatch: 'Davet başka bir e-posta adresine gönderilmiş.',
    failedInviterNotAuthorized: 'Seni davet eden kişi artık davet gönderemiyor.',
    failedMemberDeactivated: 'Bu organizasyondaki hesabın devre dışı bırakılmış.',
    failedOther: 'Organizasyonun sunucusu seni ekleyemedi.',
    generic: 'Bir şeyler ters gitti. Tekrar dene.',
    tryAgain: 'Tekrar dene',
  },
};
