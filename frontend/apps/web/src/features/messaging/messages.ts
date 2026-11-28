import type { Messages } from '@backroom/i18n';

// Turkish texts from docs/design (Main: the shell, the channel list, the message actions and the composer; Dialogs:
// create channel) where they show them; the rest as approved in review.
type Key =
  | 'channels'
  | 'addChannel'
  | 'browseChannels'
  | 'join'
  | 'joinChannel'
  | 'joined'
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
  | 'notAMember'
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
  | 'linkAddress'
  | 'linkApply'
  | 'linkRemove'
  | 'linkInvalid'
  | 'formattingTooLarge'
  | 'errorLinksLimit'
  | 'errorLinkHref'
  | 'errorBlocksDisabled'
  | 'errorTextLength'
  | 'errorEmpty'
  | 'errorCharacters'
  | 'errorMention'
  | 'errorMentionLimit'
  | 'errorGroupMention'
  | 'linksLimit'
  | 'addMenu'
  | 'toggleFormatting'
  | 'emoji'
  | 'messageTools'
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
  | 'visibilityPrivateDetail'
  | 'descriptionInvalid'
  | 'addPeople'
  | 'searchPeople'
  | 'chosenPeople'
  | 'removePerson'
  | 'noPeopleFound'
  | 'peopleLimit'
  | 'peoplePartial'
  | 'morePeopleMatch'
  | 'privateNote'
  | 'sameNameWarning'
  | 'privateChannel'
  | 'memberCount'
  | 'memberCountOne'
  | 'noDescription'
  | 'members'
  | 'channelOwner'
  | 'you'
  | 'showMoreMembers'
  | 'add'
  | 'edit'
  | 'editChannel'
  | 'leaveChannel'
  | 'leaveConfirm'
  | 'leavePublicNote'
  | 'leavePrivateNote'
  | 'leaveOwnerNote'
  | 'leaveLastNote'
  | 'archive'
  | 'unarchive'
  | 'archiveConfirm'
  | 'archiveNote'
  | 'archived'
  | 'archivedBanner'
  | 'previewNotice'
  | 'joinPreviewed'
  | 'transferOwnership'
  | 'transferTo'
  | 'transferConfirm'
  | 'transferNote'
  | 'lostAccess'
  | 'rateLimited'
  | 'rateLimitedIn'
  | 'membershipLimit'
  | 'memberNotInOrganization'
  | 'channelArchivedError'
  | 'notAllowed'
  | 'ownerMustBeMember'
  | 'notAvailableYet'
  | 'archivedChannels'
  | 'noArchivedChannels'
  | 'open'
  | 'openChannel'
  | 'sendArchived'
  | 'editMessage'
  | 'deleteMessage'
  | 'saveEdit'
  | 'edited'
  | 'messageDeleted'
  | 'editHint'
  | 'deleteTitle'
  | 'deleteBody'
  | 'deleteConfirm'
  | 'messageGone'
  | 'editEmpty'
  | 'replyingTo'
  | 'cancelReply'
  | 'repliedMessage'
  | 'jumpToLatest'
  | 'messageUnavailable'
  | 'replyGone'
  | 'replyUnavailable'
  | 'membersWindow';

export type MessageKey = Key;

export const messages: Messages<Key> = {
  en: {
    channels: 'Channels',
    addChannel: 'Add channel',
    browseChannels: 'Browse channels',
    join: 'Join',
    joinChannel: 'Join {name}',
    joined: 'Joined',
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
    notAMember: 'Join this channel to post in it.',
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
    linkAddress: 'Link address',
    linkApply: 'Apply',
    linkRemove: 'Remove link',
    linkInvalid: 'Enter a valid web or email address (http, https or mailto).',
    formattingTooLarge: 'This message has too much formatting to send. Simplify it.',
    errorLinksLimit: 'This message has too many different links (50 at most). Remove some and send it again.',
    errorLinkHref: 'One of the links is not a valid web or email address. Fix or remove it.',
    errorBlocksDisabled: "Formatted messages aren't available on this server yet. Send plain text.",
    errorTextLength: 'This message is too long. Shorten it to 4,000 characters.',
    errorEmpty: 'This message has no text. Write something before sending.',
    errorCharacters: 'This message has characters that cannot be sent. Remove them and try again.',
    errorMention: "One of the people you mentioned can't be mentioned. Remove the mention and try again.",
    errorMentionLimit: 'This message mentions too many people (50 at most).',
    errorGroupMention: "Mentioning everyone or a group isn't available yet.",
    linksLimit: 'This message has {count} different links; 50 is the most. Remove some to send it.',
    addMenu: 'Add',
    toggleFormatting: 'Show or hide formatting',
    emoji: 'Emoji',
    messageTools: 'Message tools',
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
    visibilityPrivateDetail:
      'Only the people added can see it. Members can add people; removing, renaming and archiving belong to the channel owner (you).',
    descriptionInvalid: 'The description can have at most 250 characters and no control characters.',
    addPeople: 'Add people',
    searchPeople: 'Type a name…',
    chosenPeople: 'People to add',
    removePerson: 'Remove {name}',
    noPeopleFound: 'Nobody matches.',
    peopleLimit: 'You can add up to {count} people at a time.',
    peoplePartial: 'Only the first {count} members are searched.',
    morePeopleMatch: 'Showing the first {count}. Type more of the name to narrow the list.',
    privateNote:
      'Nobody who is not a member, admins included, can see the name or the content of a private channel.',
    sameNameWarning: 'You can already see another channel with this name.',
    privateChannel: 'Private channel',
    memberCount: '{count} members',
    memberCountOne: '1 member',
    noDescription: 'No description.',
    members: 'Members',
    channelOwner: 'Channel owner',
    you: '(you)',
    showMoreMembers: 'Show more members',
    add: 'Add',
    edit: 'Edit',
    editChannel: 'Edit channel',
    leaveChannel: 'Leave channel',
    leaveConfirm: 'Leave {name}?',
    leavePublicNote: 'You can join again from "Browse channels".',
    leavePrivateNote: 'To come back, a member has to add you again.',
    leaveOwnerNote:
      'You are the channel owner. When you leave, ownership passes to the member who joined earliest.',
    leaveLastNote:
      'You are the last member. When you leave, the channel is archived and nobody can reach it again.',
    archive: 'Archive',
    unarchive: 'Unarchive',
    archiveConfirm: 'Archive {name}?',
    archiveNote: 'Messages stay readable, but nobody can send new ones until the channel is unarchived.',
    archived: 'Archived',
    archivedBanner: 'This channel is archived. You can read it, but nobody can send messages.',
    previewNotice: 'Join this channel to send messages.',
    joinPreviewed: 'Join channel',
    transferOwnership: 'Make owner',
    transferTo: 'Make {name} the owner',
    transferConfirm: 'Make {name} the owner of this channel?',
    transferNote: 'You stay a member, but only the new owner can rename, archive or hand over the channel.',
    lostAccess: 'You no longer have access to that channel.',
    rateLimited: 'You have done this too often. Try again later.',
    rateLimitedIn: 'You have done this too often. Try again in {duration}.',
    membershipLimit:
      'One of these people is already in the largest number of conversations a person can be in. Nothing was changed.',
    memberNotInOrganization:
      'One of these people is no longer an active member of the organization. Review the list and try again.',
    channelArchivedError: 'This channel is archived, so it cannot be changed.',
    notAllowed: 'You are not allowed to do this.',
    ownerMustBeMember: 'That person is no longer a member of this channel.',
    notAvailableYet: 'This is not available yet.',
    archivedChannels: 'Archived channels',
    noArchivedChannels: 'You are in no archived channels.',
    open: 'Open',
    openChannel: 'Open {name}',
    sendArchived: 'This channel is archived. The message was not sent.',
    editMessage: 'Edit message',
    deleteMessage: 'Delete message',
    saveEdit: 'Save',
    edited: 'edited',
    messageDeleted: 'This message was deleted',
    editHint: 'Enter saves, Esc cancels',
    deleteTitle: 'Delete this message?',
    deleteBody: "The message is deleted for everyone. This can't be undone.",
    deleteConfirm: 'Delete',
    messageGone: 'This message no longer exists.',
    editEmpty: 'A message needs some text. To remove it, delete it instead.',
    replyingTo: 'Replying to {name}',
    cancelReply: 'Cancel reply',
    repliedMessage: 'Replied message: {name}: {text}',
    jumpToLatest: 'Jump to latest',
    messageUnavailable: 'This message can no longer be shown.',
    replyGone: 'The message you replied to was deleted or no longer exists. Your message was not sent.',
    replyUnavailable: 'Replying to a message is not available yet. Your message was not sent.',
    membersWindow: 'Showing the last {count} members loaded.',
  },
  tr: {
    channels: 'Kanallar',
    addChannel: 'Kanal ekle',
    browseChannels: 'Kanallara göz at',
    join: 'Katıl',
    joinChannel: '{name} kanalına katıl',
    joined: 'Katıldın',
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
    notAMember: 'Bu kanala yazmak için önce katıl.',
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
    linkAddress: 'Bağlantı adresi',
    linkApply: 'Uygula',
    linkRemove: 'Bağlantıyı kaldır',
    linkInvalid: 'Geçerli bir web veya e-posta adresi gir (http, https veya mailto).',
    formattingTooLarge: 'Bu mesajda çok fazla biçimlendirme var, gönderilemiyor. Sadeleştir.',
    errorLinksLimit:
      'Bu mesajda çok fazla farklı bağlantı var (en fazla 50). Birkaçını kaldırıp yeniden gönder.',
    errorLinkHref: 'Bağlantılardan biri geçerli bir web ya da e-posta adresi değil. Düzelt ya da kaldır.',
    errorBlocksDisabled: 'Biçimli mesajlar bu sunucuda henüz kullanılamıyor. Düz metin gönder.',
    errorTextLength: 'Bu mesaj çok uzun. 4.000 karaktere kısalt.',
    errorEmpty: 'Bu mesajda metin yok. Göndermeden önce bir şey yaz.',
    errorCharacters: 'Bu mesajda gönderilemeyen karakterler var. Kaldırıp yeniden dene.',
    errorMention: 'Anmak istediğin kişilerden biri anılamıyor. Anmayı kaldırıp yeniden dene.',
    errorMentionLimit: 'Bu mesajda çok fazla kişi anılıyor (en fazla 50).',
    errorGroupMention: 'Herkesi ya da bir grubu anma henüz kullanılamıyor.',
    linksLimit:
      'Bu mesajda {count} farklı bağlantı var; en fazla 50 olabilir. Göndermek için birkaçını kaldır.',
    addMenu: 'Ekle',
    toggleFormatting: 'Biçimlendirmeyi göster/gizle',
    emoji: 'Emoji',
    messageTools: 'Mesaj araçları',
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
    visibilityPrivateDetail:
      'Yalnızca eklenen kişiler görür. Üyeler kişi ekleyebilir; çıkarma, ad değiştirme ve arşivleme kanal sahibinde (sen).',
    descriptionInvalid: 'Açıklama en fazla 250 karakter olabilir ve denetim karakteri içeremez.',
    addPeople: 'Kişi ekle',
    searchPeople: 'Ad yaz…',
    chosenPeople: 'Eklenecek kişiler',
    removePerson: '{name} kişisini kaldır',
    noPeopleFound: 'Eşleşen kimse yok.',
    peopleLimit: 'Bir seferde en fazla {count} kişi ekleyebilirsin.',
    peoplePartial: 'Yalnızca ilk {count} üye aranıyor.',
    morePeopleMatch: 'İlk {count} kişi gösteriliyor. Listeyi daraltmak için adın devamını yaz.',
    privateNote: 'Özel kanalın adını ve içeriğini üye olmayan kimse, yöneticiler dahil, göremez.',
    sameNameWarning: 'Bu adda görebildiğin başka bir kanal var.',
    privateChannel: 'Özel kanal',
    memberCount: '{count} üye',
    memberCountOne: '1 üye',
    noDescription: 'Açıklama yok.',
    members: 'Üyeler',
    channelOwner: 'Kanal sahibi',
    you: '(sen)',
    showMoreMembers: 'Daha fazla üye göster',
    add: 'Ekle',
    edit: 'Düzenle',
    editChannel: 'Kanalı düzenle',
    leaveChannel: 'Kanaldan ayrıl',
    leaveConfirm: '{name} kanalından ayrılmak istiyor musun?',
    leavePublicNote: '"Kanallara göz at" üzerinden yeniden katılabilirsin.',
    leavePrivateNote: 'Geri dönmek için bir üyenin seni yeniden eklemesi gerekir.',
    leaveOwnerNote: 'Kanal sahibisin. Ayrıldığında sahiplik, kanala en önce katılan üyeye geçer.',
    leaveLastNote: 'Son üyesin. Ayrıldığında kanal arşivlenir ve kimse bir daha erişemez.',
    archive: 'Arşivle',
    unarchive: 'Arşivden çıkar',
    archiveConfirm: '{name} kanalını arşivlemek istiyor musun?',
    archiveNote: 'Mesajlar okunabilir kalır; kanal arşivden çıkarılana kadar kimse yeni mesaj gönderemez.',
    archived: 'Arşivlendi',
    archivedBanner: 'Bu kanal arşivlendi. Okuyabilirsin, ama kimse mesaj gönderemez.',
    previewNotice: 'Mesaj göndermek için kanala katıl.',
    joinPreviewed: 'Kanala katıl',
    transferOwnership: 'Sahipliği devret',
    transferTo: 'Sahipliği {name} kişisine devret',
    transferConfirm: 'Kanalın sahipliğini {name} kişisine devretmek istiyor musun?',
    transferNote:
      'Üye olarak kalırsın; ancak kanalın adını değiştirmek, kanalı arşivlemek veya devretmek yalnızca yeni sahibin elinde olur.',
    lostAccess: 'O kanala artık erişimin yok.',
    rateLimited: 'Bunu çok sık yaptın. Daha sonra tekrar dene.',
    rateLimitedIn: 'Bunu çok sık yaptın. {duration} sonra tekrar dene.',
    membershipLimit:
      'Bu kişilerden biri, bir kişinin üye olabileceği en fazla sohbet sayısına ulaşmış. Hiçbir şey değişmedi.',
    memberNotInOrganization:
      'Bu kişilerden biri artık organizasyonun aktif üyesi değil. Listeyi gözden geçirip tekrar dene.',
    channelArchivedError: 'Bu kanal arşivlendiği için değiştirilemiyor.',
    notAllowed: 'Bunu yapma iznin yok.',
    ownerMustBeMember: 'Bu kişi artık bu kanalın üyesi değil.',
    notAvailableYet: 'Bu henüz kullanılamıyor.',
    archivedChannels: 'Arşivlenmiş kanallar',
    noArchivedChannels: 'Üyesi olduğun arşivlenmiş kanal yok.',
    open: 'Aç',
    openChannel: '{name} kanalını aç',
    sendArchived: 'Bu kanal arşivlendi. Mesaj gönderilmedi.',
    editMessage: 'Mesajı düzenle',
    deleteMessage: 'Mesajı sil',
    saveEdit: 'Kaydet',
    edited: 'düzenlendi',
    messageDeleted: 'Bu mesaj silindi',
    editHint: 'Enter kaydeder, Esc iptal eder',
    deleteTitle: 'Mesaj silinsin mi?',
    deleteBody: 'Mesaj herkes için silinir. Bu işlem geri alınamaz.',
    deleteConfirm: 'Sil',
    messageGone: 'Bu mesaj artık yok.',
    editEmpty: 'Mesajda metin olmalı. Kaldırmak için mesajı sil.',
    replyingTo: '{name} kişisine yanıt veriyorsun',
    cancelReply: 'Yanıtı iptal et',
    repliedMessage: 'Yanıtlanan mesaj: {name}: {text}',
    jumpToLatest: 'Son mesajlara dön',
    messageUnavailable: 'Bu mesaj artık görüntülenemiyor.',
    replyGone: 'Yanıt verdiğin mesaj silindi ya da artık yok. Mesajın gönderilmedi.',
    replyUnavailable: 'Mesaja yanıt verme henüz kullanılamıyor. Mesajın gönderilmedi.',
    membersWindow: 'Yüklenen son {count} üye gösteriliyor.',
  },
};
