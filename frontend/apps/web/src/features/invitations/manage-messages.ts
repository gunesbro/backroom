import type { Messages } from '@backroom/i18n';

// Turkish texts from docs/design (Dialogs, the invite dialog) where it shows them: the email field, its hint, the
// role field and the buttons. The title is fixed text because a Turkish suffix cannot be put on a name; the list of
// pending invitations, the errors and the rest are new, as approved in review.
type Key =
  | 'title'
  | 'inOrganization'
  | 'close'
  | 'emailsLabel'
  | 'emailsHint'
  | 'addEmail'
  | 'remove'
  | 'role'
  | 'roleMember'
  | 'roleAdmin'
  | 'send'
  | 'sending'
  | 'cancel'
  | 'done'
  | 'emailInvalid'
  | 'emailRepeated'
  | 'tooMany'
  | 'noAddress'
  | 'sent'
  | 'notSent'
  | 'notAttempted'
  | 'alreadyMember'
  | 'deactivated'
  | 'alreadyInvited'
  | 'roleForbidden'
  | 'forbidden'
  | 'notFound'
  | 'rateLimited'
  | 'rateLimitedAfter'
  | 'generic'
  | 'pendingTitle'
  | 'pendingNone'
  | 'pendingFailed'
  | 'loading'
  | 'tryAgain'
  | 'showMore'
  | 'expires'
  | 'expired'
  | 'revoke'
  | 'resend'
  | 'sendAsMember'
  | 'confirmRevoke'
  | 'keep'
  | 'resent'
  | 'revoked'
  | 'notPending'
  | 'inviterNotAuthorized'
  | 'invitationGone';

export const messages: Messages<Key> = {
  en: {
    title: 'Invite people',
    inOrganization: 'Invite to {organization}',
    close: 'Close',
    emailsLabel: 'Email addresses',
    emailsHint: 'Each invitation is single-use and valid only for the account opened with this email.',
    addEmail: 'Add email…',
    remove: 'Remove {email}',
    role: 'Role',
    roleMember: 'Member',
    roleAdmin: 'Admin',
    send: 'Send invitations',
    sending: 'Sending…',
    cancel: 'Cancel',
    done: 'Done',
    emailInvalid: '"{email}" is not a valid email address.',
    emailRepeated: '"{email}" is already in the list.',
    tooMany: 'You can invite at most {count} people at a time.',
    noAddress: 'Add at least one email address.',
    sent: 'Invitation sent to {email}.',
    notSent: '{email}: {reason}',
    notAttempted: 'Not sent yet: {count} more. Send them again after the wait.',
    alreadyMember: 'This address already belongs to a member.',
    deactivated: "This member was deactivated and can't return by invitation.",
    alreadyInvited: 'There is already a pending invitation for this address.',
    roleForbidden: "You don't have permission to choose a role.",
    forbidden: "You don't have permission to invite people.",
    notFound: "This organization isn't available.",
    rateLimited: 'Too many invitations. Try again later.',
    rateLimitedAfter: 'Too many invitations. Try again in {wait}.',
    generic: 'Something went wrong. Try again.',
    pendingTitle: 'Pending invitations',
    pendingNone: 'No pending invitations.',
    pendingFailed: "The invitations can't be loaded right now.",
    loading: 'Loading…',
    tryAgain: 'Try again',
    showMore: 'Show more',
    expires: 'Expires {date}',
    expired: 'Expired {date}',
    revoke: 'Revoke',
    resend: 'Resend',
    sendAsMember: 'Send as Member instead',
    confirmRevoke: 'Revoke the invitation for {email}? The link will stop working.',
    keep: 'Keep',
    resent: 'Invitation sent again.',
    revoked: 'Invitation revoked.',
    notPending: 'This invitation was already accepted, revoked or has expired.',
    inviterNotAuthorized: "This invitation can't be sent again because its sender can no longer invite.",
    invitationGone: 'This invitation no longer exists.',
  },
  tr: {
    title: 'Kişileri davet et',
    inOrganization: '{organization} organizasyonuna davet',
    close: 'Kapat',
    emailsLabel: 'E-posta adresleri',
    emailsHint: 'Her davet tek kullanımlık ve yalnızca bu e-postayla açılan hesapta geçerli.',
    addEmail: 'E-posta ekle…',
    remove: '{email} adresini kaldır',
    role: 'Rol',
    roleMember: 'Üye',
    roleAdmin: 'Yönetici',
    send: 'Davetleri gönder',
    sending: 'Gönderiliyor…',
    cancel: 'İptal',
    done: 'Bitti',
    emailInvalid: '"{email}" geçerli bir e-posta adresi değil.',
    emailRepeated: '"{email}" listede zaten var.',
    tooMany: 'Bir seferde en fazla {count} kişiyi davet edebilirsin.',
    noAddress: 'En az bir e-posta adresi ekle.',
    sent: '{email} adresine davet gönderildi.',
    notSent: '{email}: {reason}',
    notAttempted: 'Henüz gönderilmedi: {count} adres daha. Bekleme süresinden sonra tekrar gönder.',
    alreadyMember: 'Bu adres zaten bir üyeye ait.',
    deactivated: 'Bu üye deaktif edildi ve davetle geri dönemez.',
    alreadyInvited: 'Bu adres için bekleyen bir davet zaten var.',
    roleForbidden: 'Rol seçme iznin yok.',
    forbidden: 'Kişi davet etme iznin yok.',
    notFound: 'Bu organizasyona ulaşılamıyor.',
    rateLimited: 'Çok fazla davet. Daha sonra tekrar dene.',
    rateLimitedAfter: 'Çok fazla davet. {wait} sonra tekrar dene.',
    generic: 'Bir şeyler ters gitti. Tekrar dene.',
    pendingTitle: 'Bekleyen davetler',
    pendingNone: 'Bekleyen davet yok.',
    pendingFailed: 'Davetler şu anda yüklenemiyor.',
    loading: 'Yükleniyor…',
    tryAgain: 'Tekrar dene',
    showMore: 'Daha fazla göster',
    expires: 'Bitiş: {date}',
    expired: 'Bitti: {date}',
    revoke: 'İptal et',
    resend: 'Tekrar gönder',
    sendAsMember: 'Üye olarak gönder',
    confirmRevoke: '{email} için daveti iptal et? Bağlantı çalışmayacak.',
    keep: 'Vazgeç',
    resent: 'Davet tekrar gönderildi.',
    revoked: 'Davet iptal edildi.',
    notPending: 'Bu davet zaten kabul edilmiş, iptal edilmiş veya süresi dolmuş.',
    inviterNotAuthorized: 'Bu davet tekrar gönderilemiyor, çünkü gönderenin artık davet etme yetkisi yok.',
    invitationGone: 'Bu davet artık yok.',
  },
};
