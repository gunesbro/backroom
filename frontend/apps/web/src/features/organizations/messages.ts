import type { Messages } from '@backroom/i18n';

// Turkish texts from docs/design (Onboarding) where it shows them; the rest as approved in review.
type Key =
  | 'loading'
  | 'welcome'
  | 'welcomeLead'
  | 'setUp'
  | 'name'
  | 'region'
  | 'regionEurope'
  | 'regionDefault'
  | 'regionInfo'
  | 'create'
  | 'haveInvitation'
  | 'joinDescription'
  | 'invitationLink'
  | 'join'
  | 'invitationInvalid'
  | 'settingUp'
  | 'stillSettingUp'
  | 'createFailed'
  | 'tryAgain'
  | 'yourOrganizations'
  | 'createLink'
  | 'signOut'
  | 'nameInvalid'
  | 'emailNotVerified'
  | 'limit'
  | 'noCell'
  | 'generic';

export const messages: Messages<Key> = {
  en: {
    loading: 'Signing you in…',
    welcome: 'Welcome, {name}',
    welcomeLead:
      'You join organizations only by invitation. If you have an invitation, join it, or set up your own organization.',
    setUp: 'Set up an organization',
    name: 'Organization name',
    region: 'Data region',
    regionEurope: 'Europe',
    regionDefault: 'Default region',
    regionInfo: 'Messages, files and meetings are stored in the region you choose.',
    create: 'Set up organization',
    haveInvitation: 'Have an invitation link?',
    joinDescription: 'Open the link in your invitation email or paste it here.',
    invitationLink: 'Invitation link',
    join: 'Join',
    invitationInvalid: "This invitation link isn't valid.",
    settingUp: 'Setting up your organization…',
    stillSettingUp:
      "Still setting up. You can close this page; it will appear among your organizations when it's ready.",
    createFailed: "We couldn't set up the organization.",
    tryAgain: 'Try again',
    yourOrganizations: 'Your organizations',
    createLink: 'Set up a new organization',
    signOut: 'Sign out',
    nameInvalid: 'The name must be 1–100 characters without invisible characters.',
    emailNotVerified: 'Verify your email address to set up an organization.',
    limit: 'You can set up at most 5 organizations a day. Try again in {time}.',
    noCell: "New organizations can't be set up in this region right now. Try again later.",
    generic: 'Something went wrong. Try again.',
  },
  tr: {
    loading: 'Giriş yapılıyor…',
    welcome: 'Hoş geldin, {name}',
    welcomeLead:
      'Organizasyonlara yalnızca davetle katılınır. Bir davetin varsa ona katıl ya da kendi organizasyonunu kur.',
    setUp: 'Organizasyon kur',
    name: 'Organizasyon adı',
    region: 'Veri bölgesi',
    regionEurope: 'Avrupa',
    regionDefault: 'Varsayılan bölge',
    regionInfo: 'Mesajlar, dosyalar ve toplantılar seçtiğin bölgede saklanır.',
    create: 'Organizasyonu kur',
    haveInvitation: 'Davet bağlantın mı var?',
    joinDescription: 'Davet e-postasındaki bağlantıyı aç ya da buraya yapıştır.',
    invitationLink: 'Davet bağlantısı',
    join: 'Katıl',
    invitationInvalid: 'Bu davet bağlantısı geçersiz.',
    settingUp: 'Organizasyonun hazırlanıyor…',
    stillSettingUp:
      'Hâlâ hazırlanıyor. Bu sayfayı kapatabilirsin; hazır olunca organizasyonların arasında görünecek.',
    createFailed: 'Organizasyon kurulamadı.',
    tryAgain: 'Tekrar dene',
    yourOrganizations: 'Organizasyonların',
    createLink: 'Yeni organizasyon kur',
    signOut: 'Çıkış yap',
    nameInvalid: 'Ad 1–100 karakter olmalı ve görünmez karakter içermemeli.',
    emailNotVerified: 'Organizasyon kurmak için e-posta adresini doğrula.',
    limit: 'Bir günde en fazla 5 organizasyon kurabilirsin. {time} sonra tekrar dene.',
    noCell: 'Bu bölgede şu an yeni organizasyon kurulamıyor. Biraz sonra tekrar dene.',
    generic: 'Bir şeyler ters gitti. Tekrar dene.',
  },
};
