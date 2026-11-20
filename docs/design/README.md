# Arayüz taslakları

Bu klasör, Claude'daki tasarım tuvalinin ("Uygulama Arayüz Taslağı (Wireframe)") 29 Ekim 2026 tarihli kopyasıdır. Asıl kaynak: https://claude.ai/artifact/XdghumYTHrDYNmeHTB3kDm

- `screens/`: her ekranın görüntüsü (PNG).
- `source/`: her ekranın kaynağı (`.dc.html`) ve tuvalin dizini (`canvas.json`). Ekrandaki metinler, durumlar ve öğe sırası buradan birebir okunur.

Taslaklar ekranların düzenini, içeriğini ve davranışını gösterir. Renkler, yazı tipi ve marka, ürün adı ve logo belirlenince netleşir; üretim arayüzü `@backroom/ui` tasarım jetonlarıyla kurulur. Taslak ile `docs/requirements.md` çelişirse gereksinim dokümanı geçerlidir.

## Mesajlaşma

| Ekran | Görüntü | Kaynak |
| --- | --- | --- |
| 1 · Kanal sohbeti | [Main.png](screens/Main.png) | [Main.dc.html](source/Main.dc.html) |
| 1b · Kanal sohbeti · koyu tema | [MainDark.png](screens/MainDark.png) | [MainDark.dc.html](source/MainDark.dc.html) |
| 2 · Thread açık + durum menüsü | [Thread.png](screens/Thread.png) | [Thread.dc.html](source/Thread.dc.html) |
| 2b · Organizasyon menüsü + bildirimler | [OrgNotif.png](screens/OrgNotif.png) | [OrgNotif.dc.html](source/OrgNotif.dc.html) |
| 2c · Kanal oluştur, davet et, üyeyi deaktif et | [Dialogs.png](screens/Dialogs.png) | [Dialogs.dc.html](source/Dialogs.dc.html) |
| 2d · Mesajı ilet | [Forward.png](screens/Forward.png) | [Forward.dc.html](source/Forward.dc.html) |
| 2e · Okundu / okunmadı işaretleme | [Unread.png](screens/Unread.png) | [Unread.dc.html](source/Unread.dc.html) |
| 2f · Anket | [Poll.png](screens/Poll.png) | [Poll.dc.html](source/Poll.dc.html) |
| 2g · Tüm DM'ler (çok sayıda grup sohbeti) | [AllDM.png](screens/AllDM.png) | [AllDM.dc.html](source/AllDM.dc.html) |
| 2h · Kenar çubuğu bölümleri | [Sections.png](screens/Sections.png) | [Sections.dc.html](source/Sections.dc.html) |

## Odalar ve toplantı

| Ekran | Görüntü | Kaynak |
| --- | --- | --- |
| 3 · Odalar (kat, asansör, resepsiyon) | [Rooms.png](screens/Rooms.png) | [Rooms.dc.html](source/Rooms.dc.html) |
| 4 · Toplantı (paneller kapalı, misafir isteği) | [Meeting.png](screens/Meeting.png) | [Meeting.dc.html](source/Meeting.dc.html) |
| 4b · Toplantı · açık tema | [MeetingLight.png](screens/MeetingLight.png) | [MeetingLight.dc.html](source/MeetingLight.dc.html) |
| 4c · Toplantı · düzen seçenekleri (Döşeme) | [MeetingLayout.png](screens/MeetingLayout.png) | [MeetingLayout.dc.html](source/MeetingLayout.dc.html) |
| 5 · Toplantı (katılımcılar açık, misafir yetkileri) | [Meeting2.png](screens/Meeting2.png) | [Meeting2.dc.html](source/Meeting2.dc.html) |
| 6 · Toplantıdayken gezinme (yüzen mini pencere) | [NavMeet.png](screens/NavMeet.png) | [NavMeet.dc.html](source/NavMeet.dc.html) |
| 7 · Ayrı pencere | [PopOut.png](screens/PopOut.png) | [PopOut.dc.html](source/PopOut.dc.html) |

## Misafir ve ayarlar

| Ekran | Görüntü | Kaynak |
| --- | --- | --- |
| 8 · Misafir girişi | [GuestJoin.png](screens/GuestJoin.png) | [GuestJoin.dc.html](source/GuestJoin.dc.html) |
| 9 · Hesap ayarları · Cihazlar | [Devices.png](screens/Devices.png) | [Devices.dc.html](source/Devices.dc.html) |
| 9b · Hesap ayarları · Profil, saat dilimi, tema | [ProfileTheme.png](screens/ProfileTheme.png) | [ProfileTheme.dc.html](source/ProfileTheme.dc.html) |
| 10 · Organizasyon · Roller ve izinler | [Roles.png](screens/Roles.png) | [Roles.dc.html](source/Roles.dc.html) |
| 11 · Organizasyon · Altyapı ve plan | [Providers.png](screens/Providers.png) | [Providers.dc.html](source/Providers.dc.html) |

## Takvim

| Ekran | Görüntü | Kaynak |
| --- | --- | --- |
| 12 · Takvimim (geçmiş ve planlı toplantılar) | [Calendar.png](screens/Calendar.png) | [Calendar.dc.html](source/Calendar.dc.html) |
| 13 · Yöneticinin gördüğü takvim | [CalendarOther.png](screens/CalendarOther.png) | [CalendarOther.dc.html](source/CalendarOther.dc.html) |

## Oyun, kişiselleştirme ve entegrasyonlar

| Ekran | Görüntü | Kaynak |
| --- | --- | --- |
| 14 · Oyun odası · çizip tahmin et | [GameRoom.png](screens/GameRoom.png) | [GameRoom.dc.html](source/GameRoom.dc.html) |
| 15 · Odamı düzenle (süs, raf, giriş sesi) | [RoomDecor.png](screens/RoomDecor.png) | [RoomDecor.dc.html](source/RoomDecor.dc.html) |
| 16 · Entegrasyonlar (ücretli plan) | [Integrations.png](screens/Integrations.png) | [Integrations.dc.html](source/Integrations.dc.html) |

## Verimlilik

| Ekran | Görüntü | Kaynak |
| --- | --- | --- |
| 17 · Oda rezervasyonu | [Booking.png](screens/Booking.png) | [Booking.dc.html](source/Booking.dc.html) |
| 17b · Tekrarlı toplantılar | [Recurring.png](screens/Recurring.png) | [Recurring.dc.html](source/Recurring.dc.html) |
| 18 · Grup bahsetme, zamanlanmış mesaj, hatırlatıcı | [MsgExtras.png](screens/MsgExtras.png) | [MsgExtras.dc.html](source/MsgExtras.dc.html) |
| 19 · Çalışma saatleri, izin, otomatik durum | [WorkHours.png](screens/WorkHours.png) | [WorkHours.dc.html](source/WorkHours.dc.html) |
| 20 · Komut paleti (Ctrl+K) | [CmdK.png](screens/CmdK.png) | [CmdK.dc.html](source/CmdK.dc.html) |
| 21 · Arama filtreleri ve kanal dosyaları | [SearchFiles.png](screens/SearchFiles.png) | [SearchFiles.dc.html](source/SearchFiles.dc.html) |

## Giriş ve kayıt

| Ekran | Görüntü | Kaynak |
| --- | --- | --- |
| 22 · Giriş | [Login.png](screens/Login.png) | [Login.dc.html](source/Login.dc.html) |
| 22b · Hatalı giriş, geçici durdurma, oturum kapatıldı | [LoginStates.png](screens/LoginStates.png) | [LoginStates.dc.html](source/LoginStates.dc.html) |
| 23 · Hesap oluştur, e-posta doğrulama ve şifre belirleme | [Signup.png](screens/Signup.png) | [Signup.dc.html](source/Signup.dc.html) |
| 24 · İki adımlı doğrulama (giriş ve kurulum) | [TwoFactor.png](screens/TwoFactor.png) | [TwoFactor.dc.html](source/TwoFactor.dc.html) |
| 25 · Şifremi unuttum | [ResetPassword.png](screens/ResetPassword.png) | [ResetPassword.dc.html](source/ResetPassword.dc.html) |
| 26 · Davete katılma | [InviteAccept.png](screens/InviteAccept.png) | [InviteAccept.dc.html](source/InviteAccept.dc.html) |
| 27 · Organizasyon SSO adımı | [OrgSso.png](screens/OrgSso.png) | [OrgSso.dc.html](source/OrgSso.dc.html) |
| 28 · İlk organizasyon (kur ya da davete katıl) | [Onboarding.png](screens/Onboarding.png) | [Onboarding.dc.html](source/Onboarding.dc.html) |
| 29 · Masaüstü uygulamasında giriş | [DesktopLogin.png](screens/DesktopLogin.png) | [DesktopLogin.dc.html](source/DesktopLogin.dc.html) |
