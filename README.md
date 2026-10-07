# LiDPI

**Windows için tek tıkla açılıp kapanan, sade bir DPI engeli aşma aracı.**

[![Build](https://github.com/LiebertDev/LiDPI/actions/workflows/build.yml/badge.svg)](https://github.com/LiebertDev/LiDPI/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/LiebertDev/LiDPI)](https://github.com/LiebertDev/LiDPI/releases/latest)

**[LiDPI.exe indir (son sürüm)](https://github.com/LiebertDev/LiDPI/releases/latest)** · [English](#english)

## Neler yapabilir?

- **Tek düğme.** Korumayı büyük düğmeyle açıp kapatırsınız.
- **İki mod.**
  - **Tüm Uygulamalar:** WinDivert sürücüsüyle çalışır, bilgisayardaki bütün programların trafiğine uygulanır.
  - **Sadece Tarayıcı:** Sürücü kurmaz, yerel bir proxy üzerinden yalnızca tarayıcıda çalışır.
- **Hazır profiller.** Türkiye Standart, Hafif, Agresif, Superonline ve Türk Telekom.
- **En iyi ayarı otomatik bul.** Birkaç ayarı test sitelerinde dener ve hattınızda çalışan en hızlısını seçer. Hazır profillerden biri kazanmazsa bulduğu ayarı "Otomatik (hattıma özel)" adıyla kaydeder. Test yaklaşık iki dakika sürer, bu sırada internet birkaç kez kısa süreliğine kesilebilir. Yalnızca "Tüm Uygulamalar" modunda çalışır.
- **Şifreli DNS (DoH).** Cloudflare, Google, Quad9, AdGuard ya da kendi DoH adresiniz. Özel adres için isteğe bağlı bir başlangıç IP'si girebilir, "Test et" ile deneyebilirsiniz.
- **Kendi test siteleriniz.** Otomatik bulma ve bağlantı kontrolünün denediği siteleri Ayarlar → Test siteleri bölümünden değiştirebilirsiniz.
- **Bağlantı kontrolü.** Bağlıyken engelli sitelerin hâlâ açıldığını 5 ya da 15 dakikada bir kontrol eder. Art arda iki kez açılmazlarsa haber verir.
- **Ağ değişince yeniden bağlanma.** Wi-Fi değiştiğinde ya da bilgisayar uykudan uyandığında korumayı kendisi yeniden başlatır.
- **Tanı raporu.** Sorun bildirirken ekleyebileceğiniz bir `.txt` dosyası. Kişisel bilgiler rapordan çıkarılır.
- **Türkçe ve İngilizce arayüz.** Dil, Ayarlar'dan yeniden başlatmadan değiştirilir. Başka bir dil eklemek isterseniz [CONTRIBUTING.md](CONTRIBUTING.md) dosyasına bakın.
- **Tepside çalışma ve Windows ile başlama.**
- **Temiz kapanış.** Kapattığınızda arkada süreç ya da değiştirilmiş ayar bırakmaz.

## Ekran görüntüleri

| Kapalı | Açık |
|---|---|
| ![Kapalı](docs/screenshots/stopped.png) | ![Açık](docs/screenshots/running.png) |

| Profiller | Ayarlar |
|---|---|
| ![Profiller](docs/screenshots/profiles.png) | ![Ayarlar](docs/screenshots/settings.png) |

## Kurulum ve kullanım

1. [Releases](https://github.com/LiebertDev/LiDPI/releases/latest) sayfasından `LiDPI.exe` dosyasını indirin. İsterseniz yanındaki SHA256 değeriyle dosyayı doğrulayın.
2. `LiDPI.exe`'yi çalıştırın. Program yönetici izni ister, çünkü "Tüm Uygulamalar" modu WinDivert sürücüsünü yükler.
3. İlk açılışta LiDPI sizin için en iyi ayarı bulmayı önerir. İsterseniz listeden kendiniz de seçebilirsiniz.
4. Büyük düğmeye basın.
5. Bir site yine açılmıyorsa "Otomatik bul"u tekrar çalıştırın ya da başka bir profil (ör. **Agresif**) deneyin.

Kurulum gerekmez, tek bir `.exe` dosyasıdır. Bilgisayarınızda .NET kurulu olması da gerekmez.

## Nasıl çalışır?

DPI (derin paket inceleme) cihazları, bağlandığınız siteyi çoğunlukla TLS bağlantısının başındaki **SNI** alanından ve DNS sorgularından anlar. LiDPI bunu zorlaştırmak için birkaç yöntem kullanır:

- **ClientHello'yu bölme.** Bağlantının ilk paketi, site adı tek parçada görünmeyecek şekilde bölünür.
- **Düşük TTL'li sahte paket.** Önce, sunucuya varmadan yolda düşecek kadar kısa ömürlü sahte bir paket gönderilir. DPI cihazı bunu gerçek trafik sanar.
- **QUIC engelleme.** Tarayıcıların UDP/443 (QUIC) denemeleri engellenir. Böylece trafik yukarıdaki yöntemlerin işlediği TCP/TLS'e geçer.
- **DNS over HTTPS.** DNS sorguları şifreli olarak seçtiğiniz DoH sağlayıcısına gider. DNS ile yapılan engelleme ve yönlendirme işe yaramaz.

"Tüm Uygulamalar" modu bunları WinDivert sürücüsüyle paket düzeyinde yapar. "Sadece Tarayıcı" modu ise sürücü kurmadan, yerel bir proxy üzerinden benzer bölme yöntemlerini uygular.

## Sık sorulan sorular

**Antivirüsüm LiDPI'yi tehlikeli olarak gösteriyor. Neden?**
"Tüm Uygulamalar" modunun kullandığı WinDivert sürücüsü ağ paketlerini yakalayabildiği için bazı antivirüsler (ör. Kaspersky, ESET) onu "riskli araç" olarak işaretleyebilir. Bu yanlış bir alarmdır. WinDivert açık kaynaklıdır, LiDPI'nin kaynak kodu da bu depodadır. İçiniz rahat etmezse sürücü gerektirmeyen "Sadece Tarayıcı" modunu kullanabilirsiniz.

**Windows "Bilgisayarınız korundu" (SmartScreen) uyarısı veriyor.**
LiDPI'nin dijital imzası yok ve henüz çok indirilmemiş bir program. SmartScreen bu yüzden uyarır. Dosyayı Releases sayfasından indirdiyseniz ve SHA256 değeri tutuyorsa "Ek bilgi" → "Yine de çalıştır" diyerek açabilirsiniz.

**Başka bir DPI aracıyla birlikte kullanabilir miyim?**
Hayır. Bu tür araçlar aynı paketlere müdahale ettiği için birbirini bozar. LiDPI'yi açmadan önce diğer araçları kapatın, varsa hizmetlerini durdurun. LiDPI çakışan bir araç görürse kayıtlarda uyarır.

**İnternetim yavaşlar mı?**
LiDPI yalnızca bağlantıların ilk paketlerine dokunur. Günlük kullanımda fark edilir bir yavaşlama olmaz.

**Hangi Windows hesabıyla çalıştırmalıyım?**
Kendi hesabınızla (yönetici izni alabilen bir hesap) çalıştırın. İzin isteminde başka bir yönetici hesabının bilgilerini girerseniz program o hesap adına çalışır ve "Sadece Tarayıcı" modunun proxy ayarı sizin hesabınıza değil, o hesaba uygulanır.

**Kapatınca geride bir şey kalıyor mu?**
Hayır. Durdurduğunuzda sürücü ve proxy kapanır, sistem ayarları eski hâline döner.

**Discord hâlâ açılmıyor.**
Bazı hatlarda Discord IP adresi üzerinden engellenir. Bu durumda paket hileleri yetmez, bir VPN gerekir. Otomatik bul bu durumu fark ederse size söyler.

## Gizlilik

LiDPI hiçbir veri toplamaz ve hiçbir yere göndermez. Telemetri, analiz ya da hesap yoktur. Programın kurduğu bağlantılar yalnızca şunlardır:

- seçtiğiniz DoH sağlayıcısına DNS sorguları,
- otomatik bulma ve bağlantı kontrolü sırasında test sitelerinize (ve karşılaştırma için `www.google.com`'a) bağlantı denemeleri,
- yalnızca "Güncellemeleri denetle" düğmesine bastığınızda GitHub'a (`api.github.com`) sürüm kontrolü. LiDPI kendi başına güncelleme denetlemez, bir şey de indirmez. Yeni sürüm varsa sayfası tarayıcınızda açılır.

Tanı raporu yalnızca siz oluşturduğunuzda bilgisayarınıza kaydedilir ve hiçbir yere gönderilmez. Kullanıcı adı, bilgisayar adı, yerel IP ve MAC adresleri rapordan çıkarılır.

## Bilinen sorunlar

- Tek dosyalık `LiDPI.exe`, çalışırken bazı .NET dosyalarını `%TEMP%\.net` klasörüne açar. Program yönetici izniyle çalıştığı için bilgisayarınızda güvenmediğiniz yazılım bulundurmamaya özen gösterin. İleride klasör hâlinde (zip) bir sürüm de sunulabilir.

## Kaynaktan derleme

Gerekenler: Windows ve [.NET 9 SDK](https://dotnet.microsoft.com/download).

```
dotnet publish src/LiDPI -c Release
```

Tek dosyalık `LiDPI.exe`, `src/LiDPI/bin/Release/.../publish/` altında oluşur. Ayrıntılar ve çeviri ekleme adımları için [CONTRIBUTING.md](CONTRIBUTING.md).

## Lisans

LiDPI [MIT lisansı](LICENSE) ile dağıtılır.

Üçüncü taraf bileşen: **WinDivert** (LGPLv3 / GPLv2). Ayrıntılar [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) dosyasında.

## Sorumluluk reddi

LiDPI, bilgiye erişim ve gizlilik için yazıldı. Program olduğu gibi sunulur, herhangi bir garanti verilmez. Kullandığınız ülkenin yasalarına uymak sizin sorumluluğunuzdadır.

---

## English

**LiDPI is a small Windows app that gets you past DPI blocking with one button.**

**[Download LiDPI.exe (latest)](https://github.com/LiebertDev/LiDPI/releases/latest)**

### Features

- **One button** to turn protection on and off.
- **Two modes.** **All apps** uses the WinDivert driver and covers every program on the PC. **Browser only** needs no driver and works through a local proxy.
- **Preset profiles** tuned for Turkish ISPs: Türkiye Standard, Light, Aggressive, Superonline and Türk Telekom.
- **Find the best setting automatically.** LiDPI tries a few settings against your test sites and picks the fastest one that works on your line. If none of the presets wins, it saves the result as "Auto (tuned for my line)". It takes about two minutes and your connection may drop for a moment a few times. All apps mode only.
- **Encrypted DNS (DoH)** through Cloudflare, Google, Quad9, AdGuard or your own DoH address (with an optional bootstrap IP and a Test button).
- **Your own test sites** for auto-find and the connection check (Settings → Test sites).
- **Connection check.** While connected, LiDPI checks every 5 or 15 minutes that blocked sites still open, and tells you if they fail twice in a row.
- **Reconnects** after a Wi-Fi change or when the PC wakes from sleep.
- **Diagnostic report**: a `.txt` file you can attach to a bug report, with personal details removed.
- **Turkish and English UI.** Switch in Settings, no restart needed. Want another language? See [CONTRIBUTING.md](CONTRIBUTING.md).
- Tray icon, start with Windows, and a clean stop that leaves nothing behind.

### Install and use

1. Download `LiDPI.exe` from [Releases](https://github.com/LiebertDev/LiDPI/releases/latest). You can check it against the SHA256 file next to it.
2. Run it. It asks for administrator rights because All apps mode loads the WinDivert driver.
3. On first start LiDPI offers to find the best setting for you. You can also pick a profile yourself.
4. Press the big button.
5. If a site still doesn't open, run auto-find again or try another profile (for example **Aggressive**).

No installer, just one `.exe`. You don't need .NET installed.

### How it works

DPI boxes usually tell which site you're visiting from the **SNI** field at the start of a TLS connection and from your DNS lookups. LiDPI makes that harder:

- **Splitting the ClientHello** so the site name never shows up in one piece.
- **A low-TTL fake packet** sent first. It expires on the way and never reaches the server, but the DPI box reads it as real traffic.
- **Blocking QUIC** (UDP/443), so browsers fall back to TCP/TLS where the tricks above work.
- **DNS over HTTPS**, so DNS-based blocking and redirects don't apply.

All apps mode does this at packet level with WinDivert. Browser only mode applies similar splitting through a local proxy, without a driver.

### FAQ

**My antivirus flags LiDPI.** The WinDivert driver used by All apps mode can capture network packets, so some antivirus products (Kaspersky, ESET and others) mark it as a "risky tool". It's a false positive. WinDivert is open source and so is LiDPI. If you'd rather avoid the driver, use Browser only mode.

**Windows SmartScreen says it protected my PC.** LiDPI isn't code-signed and isn't downloaded often enough yet, so SmartScreen warns about it. If you got the file from the Releases page and the SHA256 matches, click "More info" → "Run anyway".

**Can I use it together with another DPI tool?** No. They interfere with the same packets and break each other. Close other tools (and stop their services) first. LiDPI warns in the logs if it sees one.

**Will it slow down my internet?** No. LiDPI only touches the first packets of a connection.

**Which Windows account should I use?** Your own (one that can get admin rights). If you type in a different admin account at the prompt, the app runs as that account and Browser only mode sets the proxy for that account, not yours.

**Does anything stay behind when I close it?** No. The driver and proxy are stopped and system settings are put back.

**Discord still doesn't open.** Some providers block Discord by IP address. Packet tricks can't help with that, you'd need a VPN. Auto-find tells you when this seems to be the case.

### Privacy

LiDPI doesn't collect or send any data. No telemetry, no analytics, no accounts. The only connections it makes are:

- DNS queries to the DoH provider you chose,
- test requests to your test sites (plus `www.google.com` as a control) during auto-find and the connection check,
- a release check against GitHub (`api.github.com`), **only when you press "Check for updates"**. LiDPI never checks for updates on its own and never downloads anything. If there's a new version, its release page opens in your browser.

The diagnostic report is only written when you create it, stays on your PC, and has your user name, computer name, local IPs and MAC addresses removed.

### Known issues

- The single-file `LiDPI.exe` unpacks some .NET files to `%TEMP%\.net` while it runs. Since the app runs as administrator, keep untrusted software off your PC. A zip (folder) version may come later.

### Build from source

You need Windows and the [.NET 9 SDK](https://dotnet.microsoft.com/download).

```
dotnet publish src/LiDPI -c Release
```

The single-file `LiDPI.exe` ends up under `src/LiDPI/bin/Release/.../publish/`. See [CONTRIBUTING.md](CONTRIBUTING.md) for more, including how to add a translation.

### License

LiDPI is released under the [MIT license](LICENSE). Third-party component: **WinDivert** (LGPLv3 / GPLv2), see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

LiDPI is meant for access to information and privacy. It comes as is, with no warranty. You're responsible for following the laws where you live.
