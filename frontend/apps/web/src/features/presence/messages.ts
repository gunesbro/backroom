import type { Messages } from '@backroom/i18n';

// Turkish texts from docs/design (Thread, the status menu) where it shows them; the dialogs are not drawn there, so
// their texts are written to match (the Turkish UI says "sen"). "awayHint" differs from the design on purpose: away
// can be chosen by hand since the owner's decision of 2026-11-25, so it no longer says "automatic" only.
type Key =
  | 'online'
  | 'away'
  | 'awayHint'
  | 'doNotDisturb'
  | 'dndHint'
  | 'dndEnds'
  | 'customStatus'
  | 'clearStatus'
  | 'editStatus'
  | 'statusUntil'
  | 'close'
  | 'cancel'
  | 'save'
  | 'dndTitle'
  | 'dndLead'
  | 'dnd30'
  | 'dnd60'
  | 'dndTomorrow'
  | 'dndCustom'
  | 'dndCustomTime'
  | 'dndTurnOn'
  | 'dndTurnOff'
  | 'dndTimeInvalid'
  | 'statusTitle'
  | 'emoji'
  | 'emojiHint'
  | 'statusText'
  | 'statusTextCount'
  | 'clearAfter'
  | 'clearNever'
  | 'clear30'
  | 'clear60'
  | 'clearToday'
  | 'clearCustom'
  | 'clearCustomTime'
  | 'statusEmpty'
  | 'emojiInvalid'
  | 'textInvalid'
  | 'expiryInvalid'
  | 'invalid'
  | 'rateLimited'
  | 'unavailable'
  | 'generic';

export const messages: Messages<Key> = {
  en: {
    online: 'Online',
    away: 'Away',
    awayHint: 'Until you change it · automatic after 10 min idle',
    doNotDisturb: 'Do not disturb',
    dndHint: '30 min · 1 hr · until tomorrow · custom',
    dndEnds: 'Until {time}',
    customStatus: 'Set a custom status…',
    clearStatus: 'Clear status',
    editStatus: 'Edit status: {status}',
    statusUntil: '{status} · {duration}',
    close: 'Close',
    cancel: 'Cancel',
    save: 'Save',
    dndTitle: 'Do not disturb',
    dndLead: 'Notifications collect in the bell without a sound while do not disturb is on.',
    dnd30: '30 minutes',
    dnd60: '1 hour',
    dndTomorrow: 'Until tomorrow',
    dndCustom: 'Custom',
    dndCustomTime: 'Until',
    dndTurnOn: 'Turn on',
    dndTurnOff: 'Turn off do not disturb',
    dndTimeInvalid: 'Choose a time in the next 7 days.',
    statusTitle: 'Custom status',
    emoji: 'Emoji',
    emojiHint: 'One emoji, for example 🎯',
    statusText: "What's your status?",
    statusTextCount: '{count}/100',
    clearAfter: 'Clear after',
    clearNever: "Don't clear",
    clear30: '30 minutes',
    clear60: '1 hour',
    clearToday: 'Today',
    clearCustom: 'Choose a time',
    clearCustomTime: 'Clear at',
    statusEmpty: 'Add an emoji or a text.',
    emojiInvalid: 'Use exactly one emoji.',
    textInvalid: 'Use at most 100 characters, on one line.',
    expiryInvalid: 'Choose a time in the next 30 days.',
    invalid: "The status couldn't be saved. Check it and try again.",
    rateLimited: 'Too many changes. Wait a minute and try again.',
    unavailable: "Status isn't available right now.",
    generic: 'Something went wrong. Try again.',
  },
  tr: {
    online: 'Çevrimiçi',
    away: 'Boşta',
    awayHint: 'Sen değiştirene kadar · 10 dk hareketsizlikte otomatik',
    doNotDisturb: 'Rahatsız etme',
    dndHint: '30 dk · 1 sa · yarına kadar · özel',
    dndEnds: 'Bitiş: {time}',
    customStatus: 'Özel durum ayarla…',
    clearStatus: 'Durumu temizle',
    editStatus: 'Durumu düzenle: {status}',
    statusUntil: '{status} · {duration}',
    close: 'Kapat',
    cancel: 'Vazgeç',
    save: 'Kaydet',
    dndTitle: 'Rahatsız etme',
    dndLead: 'Rahatsız etme açıkken bildirimler burada birikir.',
    dnd30: '30 dakika',
    dnd60: '1 saat',
    dndTomorrow: 'Yarına kadar',
    dndCustom: 'Özel',
    dndCustomTime: 'Bitiş zamanı',
    dndTurnOn: 'Aç',
    dndTurnOff: 'Rahatsız etmeyi kapat',
    dndTimeInvalid: 'Önümüzdeki 7 gün içinde bir zaman seç.',
    statusTitle: 'Özel durum',
    emoji: 'Emoji',
    emojiHint: 'Tek bir emoji, örneğin 🎯',
    statusText: 'Durumun ne?',
    statusTextCount: '{count}/100',
    clearAfter: 'Şu süre sonra temizle',
    clearNever: 'Temizleme',
    clear30: '30 dakika',
    clear60: '1 saat',
    clearToday: 'Bugün',
    clearCustom: 'Zaman seç',
    clearCustomTime: 'Temizlenme zamanı',
    statusEmpty: 'Bir emoji ya da metin ekle.',
    emojiInvalid: 'Yalnızca bir emoji kullan.',
    textInvalid: 'En fazla 100 karakter, tek satır kullan.',
    expiryInvalid: 'Önümüzdeki 30 gün içinde bir zaman seç.',
    invalid: 'Durum kaydedilemedi. Kontrol edip tekrar dene.',
    rateLimited: 'Çok fazla değişiklik yaptın. Bir dakika bekleyip tekrar dene.',
    unavailable: 'Durum şu an kullanılamıyor.',
    generic: 'Bir şeyler ters gitti. Tekrar dene.',
  },
};
