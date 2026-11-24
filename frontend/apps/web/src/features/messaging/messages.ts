import type { Messages } from '@backroom/i18n';

// Turkish texts from docs/design (Main: the channel list and composer; Dialogs: create channel) where they show
// them; the rest as approved in review.
type Key =
  | 'channels'
  | 'addChannel'
  | 'createChannelTitle'
  | 'channelName'
  | 'cancel'
  | 'create'
  | 'close'
  | 'nameInvalid'
  | 'nameTaken'
  | 'createForbidden'
  | 'showMoreChannels'
  | 'noChannels'
  | 'selectChannel'
  | 'loading'
  | 'tryAgain'
  | 'generic'
  | 'yourOrganizations'
  | 'messagesLabel'
  | 'loadingEarlier'
  | 'emptyChannel'
  | 'composerLabel'
  | 'send'
  | 'sending'
  | 'sendFailed'
  | 'messageInvalid'
  | 'channelGone'
  | 'retry'
  | 'discard'
  | 'charactersLeft'
  | 'charactersOver'
  | 'you'
  | 'member';

export const messages: Messages<Key> = {
  en: {
    channels: 'Channels',
    addChannel: 'Add channel',
    createChannelTitle: 'Create channel',
    channelName: 'Channel name',
    cancel: 'Cancel',
    create: 'Create',
    close: 'Close',
    nameInvalid: 'The name must be 1–80 characters without control characters.',
    nameTaken: 'A channel with this name already exists.',
    createForbidden: "You don't have permission to create channels.",
    showMoreChannels: 'Show more channels',
    noChannels: 'No channels yet. Create the first one.',
    selectChannel: 'Select a channel to start.',
    loading: 'Loading…',
    tryAgain: 'Try again',
    generic: 'Something went wrong. Try again.',
    yourOrganizations: 'Your organizations',
    messagesLabel: 'Messages in #{name}',
    loadingEarlier: 'Loading earlier messages…',
    emptyChannel: 'No messages yet. Write the first one.',
    composerLabel: 'Message #{name}',
    send: 'Send',
    sending: 'Sending…',
    sendFailed: "Couldn't send.",
    messageInvalid: "This message can't be sent. Check the text.",
    channelGone: 'This channel is no longer available.',
    retry: 'Try again',
    discard: 'Discard',
    charactersLeft: '{count} characters left',
    charactersOver: '{count} characters over the limit. Shorten the message to send it.',
    you: 'You',
    member: 'Member',
  },
  tr: {
    channels: 'Kanallar',
    addChannel: 'Kanal ekle',
    createChannelTitle: 'Kanal oluştur',
    channelName: 'Kanal adı',
    cancel: 'İptal',
    create: 'Oluştur',
    close: 'Kapat',
    nameInvalid: 'Ad 1–80 karakter olmalı ve denetim karakteri içermemeli.',
    nameTaken: 'Bu adda bir kanal zaten var.',
    createForbidden: 'Kanal oluşturma iznin yok.',
    showMoreChannels: 'Daha fazla kanal göster',
    noChannels: 'Henüz kanal yok. İlkini oluştur.',
    selectChannel: 'Başlamak için bir kanal seç.',
    loading: 'Yükleniyor…',
    tryAgain: 'Tekrar dene',
    generic: 'Bir şeyler ters gitti. Tekrar dene.',
    yourOrganizations: 'Organizasyonların',
    messagesLabel: '#{name} kanalındaki mesajlar',
    loadingEarlier: 'Önceki mesajlar yükleniyor…',
    emptyChannel: 'Henüz mesaj yok. İlk mesajı sen yaz.',
    composerLabel: '#{name} kanalına mesaj yaz',
    send: 'Gönder',
    sending: 'Gönderiliyor…',
    sendFailed: 'Gönderilemedi.',
    messageInvalid: 'Bu mesaj gönderilemiyor. Metni kontrol et.',
    channelGone: 'Bu kanal artık kullanılamıyor.',
    retry: 'Tekrar dene',
    discard: 'Sil',
    charactersLeft: '{count} karakter kaldı',
    charactersOver: 'Sınırı {count} karakter aşıyorsun. Göndermek için mesajı kısalt.',
    you: 'Sen',
    member: 'Üye',
  },
};
