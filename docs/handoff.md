# Backroom · Başlangıç notu

Bu not, tasarımın yapıldığı Claude oturumundan bu Claude Code oturumuna geçiş için yazıldı (25 Ekim 2026). Tasarım tamamlandı; Faz 0'ı engelleyen açık konu yok.

## Kaynaklar

- **Gereksinimler:** `docs/requirements.md`. Bütün kararlar, mimari, modül planı, klasör yapısı, test stratejisi ve yol haritası burada.
- **Asıl doküman (Claude Docs):** https://claude.ai/artifact/6BPW2Ei7EHJZcTUd3ExLKX. `docs/requirements.md` bunun anlık kopyasıdır; ikisi çelişirse asıl doküman geçerlidir.
- **Arayüz taslakları:** `docs/design/`. 33 ekranın görüntüleri (`screens/`) ve kaynakları (`source/`); dizin `docs/design/README.md`. Asıl kaynak Claude'daki tasarım tuvalidir: https://claude.ai/artifact/XdghumYTHrDYNmeHTB3kDm. Faz 0'da gerekmiyor; arayüz fazlarında her ekran bu dosyalardan uygulanır, ekrandaki metinler kaynaktan birebir alınır.

## Öne çıkan kararlar

Hepsi `docs/requirements.md` içinde; hızlı bakış için:

- **Mimari:** Modüler monolit ve hücre mimarisi. İnce kontrol düzlemi kimlik (Keycloak), dizin ve lisansı tutar; organizasyon verisi hücrededir. Hücre kontrol düzlemine dışa tek bir gRPC bağlantısı açar. İstemci hücreye REST ve SignalR ile bağlanır.
- **Veri yerleşimi:** Kimlik global, içerik bölgesel. Tek global hesap; içerik, organizasyonun seçtiği bölgedeki hücrede durur.
- **Planlar:** Ücretsiz, Takım, İşletme, Kurumsal. Kota ve sınır yalnızca Ücretsiz planda.
- **Kapsam dışı:** Organizasyonlar arası ortak kanal yok.
- **İsimler:** Proje Backroom; repo `backroom`; .NET isim alanları `Backroom.*`; web paketleri `@backroom/*`. Ürün adı ve logo sonra belirlenecek.
- **Para:** Şimdilik hiçbir şeye para harcanmaz; deploy en sona bırakıldı.
- **E-posta:** Geliştirmede gerçek e-posta çıkmaz; her şey Mailpit'e gider.

## Çalışma kuralları

- Kullanıcıyla Türkçe konuş. Kod, isimler, kod yorumları, commit ve log mesajları, CLAUDE.md, README ve ADR'ler İngilizcedir. Türkçe yalnızca arayüz çeviri dosyalarında bulunur.
- Dokümandaki "Ajanlarla çalışma kuralları" geçerlidir: küçük işler (bir PR, tek davranış), önce sözleşme sonra kod, bitti tanımı, ayrı bir inceleyici ajan, kimlik, izin, şifreleme, misafir erişimi ve e-posta kodunda insan incelemesi, yarım özellikler bayrak arkasında, ana dal her zaman çalışır.
- Yeni kütüphane eklemeden önce lisansını, son sürüm tarihini ve yaygınlığını kontrol et; sonucu PR açıklamasına yaz. Kaçınılan kütüphaneler dokümanda listeli (MediatR v13 ve sonrası, AutoMapper v15 ve sonrası, MassTransit v9, ImageSharp, FluentAssertions v8 ve sonrası, Moq, Bitnami imajları, MinIO).
- Bilgisayara sistem yazılımı kurmadan, sistem ayarı değiştirmeden ya da dosya silmeden önce kullanıcıya sor.
- Anahtar, parola ve belirteçler repoya girmez; yerelde .NET user-secrets ya da git dışında tutulan `.env` kullanılır.

## Faz 0 işleri

1. **Önkoşul kontrolü:** `git --version`, `gh auth status`, `dotnet --version` (10.x), `node --version` (LTS), `pnpm --version`, `rustc --version`, `cargo --version`, `docker version` (WSL 2 motoru). Eksikleri listele; kurulumu kullanıcı yapar.
2. **Repo:** Bu klasörde git'i başlat, ana dal `main`. `gh repo create backroom --private --source . --remote origin` ile özel repoyu aç, ilk commit'i gönder. Sonraki her iş ayrı dalda yapılır ve PR ile birleşir.
3. **Kök dosyalar:** CLAUDE.md (ajan kuralları, gizlilik kurallarının özeti, sık komutlar, `docs/requirements.md` ve asıl dokümana bağlantı), README.md (Windows kurulum adımları ve tek komutla çalıştırma), `.gitignore`, `.gitattributes` (satır sonları), `.editorconfig`, `docs/adr/` altında ana mimari kararlar için ilk ADR'ler.
4. **Sunucu iskeleti** (dokümandaki "Repo ve klasör yapısı"): `backend/Backroom.slnx`, `Directory.Build.props` (nullable açık, uyarılar hata), `Directory.Packages.props` (merkezi paket yönetimi), `global.json` (.NET 10), `src/Aspire` (AppHost, ServiceDefaults ve OpenTelemetry), `src/Hosts` (ControlPlane.Host ve Cell.Host; app ve worker rolleri aynı ikiliden), `src/BuildingBlocks`, `tests/ArchitectureTests` (modül kuralları), `tests/Testing`. AppHost'a dokümandaki geliştirme servisleri eklenir: PostgreSQL, Valkey, RabbitMQ, Keycloak, Mailpit, SeaweedFS, yerel LiveKit. İş modülleri Faz 1'deki dikey dilimle gelir.
5. **İstemci iskeleti:** `frontend/pnpm-workspace.yaml`; `apps/web` (React, TypeScript, Vite), `apps/ops`, `apps/desktop` (Tauri v2); `packages/` altında ui, api-client, realtime, local-store ve i18n için boş iskelet; TypeScript strict, Vitest, ESLint ve Prettier.
6. **CI (GitHub Actions):** Özel repoda ayda 2.000 dakika ücretsiz; Windows ve özellikle macOS dakikaları bu kotadan daha hızlı düşer. Her PR'da sunucu derleme ve testleri (mimari testleri dahil) ile istemci lint, test ve derleme çalışır. Tauri'nin Windows ve Linux paketleri elle tetiklenen bir işte üretilir; macOS derlemesi yalnızca elle tetiklenir.
7. **Tauri denemesi (Faz 0 kapısı):** `apps/desktop` içinde LiveKit odasına bağlanan küçük bir ekran: kamera, mikrofon ve ekran paylaşımı.
    - Windows'ta gerçek cihazda, yerel LiveKit sunucusuyla denenir.
    - Linux'ta CI'ın ürettiği paket, aynı bilgisayarda Ubuntu canlı USB ile denenir. Bu sırada Windows kapalı olacağı için ücretsiz LiveKit Cloud hesabı kullanılır.
    - macOS yalnızca CI'da derlenir; gerçek cihaz testi betadan önce yapılacak.
    - LiveKit belirteci geliştirme için LiveKit CLI ya da küçük bir geliştirme ucuyla üretilir; API anahtarı repoya girmez.
    - Sonuç `docs/spikes/tauri.md` dosyasına yazılır: neyin çalıştığı, sürümler, sorunlar.
    - Kapı: Kamera, mikrofon ve ekran paylaşımı Windows ve Linux'ta çalışıyor. Olumsuz çıkarsa masaüstü teknolojisi Faz 1'den önce yeniden değerlendirilir.

## Sonraki işler (faz dışı)

- **Masaüstü otomatik güncelleme** (25 Ekim 2026'da istendi): Tauri updater eklentisi ve imzalı güncelleme paketleri; imza anahtar çifti (gizli anahtar CI secret'ında), masaüstü paketleme iş akışında güncelleme bildirimi ve herkese açık bir güncelleme adresi (repo özel olduğu için GitHub Releases değil; barındırma ile birlikte deploy aşamasında). Windows kod imzalama zaten deploy listesinde.
- **Linux masaüstü cihaz testi:** Windows'ta Tauri kapısı geçti; Linux şimdilik önemsiz bulunup ertelendi (`docs/spikes/tauri.md`).

## İlk adım

Bu dosyayı ve `docs/requirements.md` dosyasını oku, önkoşulları kontrol et, sonra Faz 0 için kısa bir plan sun ve kullanıcının onayını bekle.
