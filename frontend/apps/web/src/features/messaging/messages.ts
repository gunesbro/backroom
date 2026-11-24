import type { Messages } from '@backroom/i18n';

// Turkish texts from docs/design (Main: the shell, the channel list, the message actions and the composer; Dialogs:
// create channel) where they show them; the rest as approved in review.
type Key =
  | 'channels'
  | 'addChannel'
  | 'invitePeople'
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
  | 'serverUnavailable'
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
  | 'composerHint'
  | 'announceMessage'
  | 'member'
  | 'newActivity'
  | 'connecting'
  | 'reconnecting'
  | 'soon'
  | 'search'
  | 'notifications'
  | 'rooms'
  | 'calendar'
  | 'threads'
  | 'saved'
  | 'sectionOptions'
  | 'sectionAdd'
  | 'directMessages'
  | 'allDms'
  | 'channelInfo'
  | 'messageActions'
  | 'addReaction'
  | 'reply'
  | 'startThread'
  | 'forward'
  | 'save'
  | 'more'
  | 'formatting'
  | 'bold'
  | 'italic'
  | 'strikethrough'
  | 'link'
  | 'bulletedList'
  | 'numberedList'
  | 'quote'
  | 'code'
  | 'codeBlock'
  | 'addMenu'
  | 'toggleFormatting'
  | 'emoji'
  | 'mention'
  | 'attachFile'
  | 'composerPlaceholder'
  | 'today'
  | 'yesterday'
  | 'description'
  | 'optional'
  | 'visibility'
  | 'visibilityPublic'
  | 'visibilityPublicDetail'
  | 'visibilityPrivate'
  | 'visibilityPrivateDetail';

export const messages: Messages<Key> = {
  en: {
    channels: 'Channels',
    addChannel: 'Add channel',
    invitePeople: 'Invite people',
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
    serverUnavailable: "Your organization's server can't be reached right now.",
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
    composerHint: 'Enter to send · Shift+Enter for a new line',
    announceMessage: '{name}: {text}.',
    member: 'Member',
    newActivity: 'new messages',
    connecting: 'Connecting…',
    reconnecting: 'Connection lost. Reconnecting…',
    soon: 'Coming soon',
    search: 'Search: messages, files, people, rooms',
    notifications: 'Notifications',
    rooms: 'Rooms',
    calendar: 'Calendar',
    threads: 'Threads',
    saved: 'Saved',
    sectionOptions: '{name} options',
    sectionAdd: 'Add to {name}',
    directMessages: 'Direct messages',
    allDms: 'All DMs',
    channelInfo: 'Channel info',
    messageActions: 'Message actions',
    addReaction: 'Add reaction',
    reply: 'Reply',
    startThread: 'Start thread',
    forward: 'Forward',
    save: 'Save',
    more: 'More',
    formatting: 'Formatting',
    bold: 'Bold',
    italic: 'Italic',
    strikethrough: 'Strikethrough',
    link: 'Link',
    bulletedList: 'Bulleted list',
    numberedList: 'Numbered list',
    quote: 'Quote',
    code: 'Code',
    codeBlock: 'Code block',
    addMenu: 'Add',
    toggleFormatting: 'Show or hide formatting',
    emoji: 'Emoji',
    mention: 'Mention someone',
    attachFile: 'Attach file',
    composerPlaceholder: 'Message #{name}',
    today: 'Today',
    yesterday: 'Yesterday',
    description: 'Description',
    optional: '(optional)',
    visibility: 'Visibility',
    visibilityPublic: 'Public',
    visibilityPublicDetail: 'Everyone in the organization can see, search and join it.',
    visibilityPrivate: 'Private',
    visibilityPrivateDetail: 'Only the people added can see it.',
  },
  tr: {
    channels: 'Kanallar',
    addChannel: 'Kanal ekle',
    invitePeople: 'Kişileri davet et',
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
    serverUnavailable: 'Organizasyonunun sunucusuna şu anda ulaşılamıyor.',
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
    composerHint: 'Enter gönderir · Shift+Enter yeni satır',
    announceMessage: '{name}: {text}.',
    member: 'Üye',
    newActivity: 'yeni mesajlar',
    connecting: 'Bağlanılıyor…',
    reconnecting: 'Bağlantı koptu. Yeniden bağlanılıyor…',
    soon: 'Yakında',
    search: 'Ara: mesaj, dosya, kişi, oda',
    notifications: 'Bildirimler',
    rooms: 'Odalar',
    calendar: 'Takvim',
    threads: "Thread'ler",
    saved: 'Kaydedilenler',
    sectionOptions: '{name} seçenekleri',
    sectionAdd: '{name} bölümüne ekle',
    directMessages: 'Doğrudan mesajlar',
    allDms: "Tüm DM'ler",
    channelInfo: 'Kanal bilgisi',
    messageActions: 'Mesaj işlemleri',
    addReaction: 'Tepki ekle',
    reply: 'Yanıtla',
    startThread: 'Thread başlat',
    forward: 'İlet',
    save: 'Kaydet',
    more: 'Diğer',
    formatting: 'Biçimlendirme',
    bold: 'Kalın',
    italic: 'İtalik',
    strikethrough: 'Üstü çizili',
    link: 'Bağlantı',
    bulletedList: 'Madde işaretli liste',
    numberedList: 'Numaralı liste',
    quote: 'Alıntı',
    code: 'Kod',
    codeBlock: 'Kod bloğu',
    addMenu: 'Ekle',
    toggleFormatting: 'Biçimlendirmeyi göster/gizle',
    emoji: 'Emoji',
    mention: 'Kişi an',
    attachFile: 'Dosya ekle',
    composerPlaceholder: '#{name} kanalına mesaj yaz…',
    today: 'Bugün',
    yesterday: 'Dün',
    description: 'Açıklama',
    optional: '(isteğe bağlı)',
    visibility: 'Görünürlük',
    visibilityPublic: 'Açık',
    visibilityPublicDetail: 'Organizasyondaki herkes görür, arar ve katılabilir.',
    visibilityPrivate: 'Özel',
    visibilityPrivateDetail: 'Yalnızca eklenen kişiler görür.',
  },
};
