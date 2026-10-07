# Katkıda bulunma

LiDPI'ye katkı vermek istediğiniz için teşekkürler. Bu dosya iki şeyi anlatıyor: yeni bir dil eklemeyi ve programı derlemeyi. [English below](#contributing).

## Yeni bir dil eklemek

Arayüzdeki bütün yazılar `src/LiDPI/Localization/` klasöründeki JSON dosyalarında durur. Her dosya düz bir anahtar → metin listesidir:

```json
{
  "LanguageName": "Türkçe",
  "StatusRunning": "Koruma Aktif",
  "HealthMinutesAgo": "{0} dk önce"
}
```

1. `src/LiDPI/Localization/en.json` dosyasını kopyalayın ve dilin koduyla adlandırın: Almanca için `de.json`, Brezilya Portekizcesi için `pt-BR.json` gibi.
2. `LanguageName` değerine dilin kendi adını yazın (`Deutsch`, `Português (Brasil)`). Dil seçicide bu görünür.
3. İsterseniz `LanguageCulture` değerine sayı ve tarih biçimi için bir kültür kodu yazın (`de-DE`). Boş bırakırsanız dosya adı kullanılır.
4. Değerleri çevirin. **Anahtarlara dokunmayın.**
   - `{0}`, `{1}` gibi yer tutucuları aynen bırakın. Cümle içindeki yerlerini dilinize göre değiştirebilirsiniz. `{3:0}` gibi biçimler de aynı kalmalı.
   - `✓`, `✦`, `…` gibi işaretleri koruyun.
   - `"` karakteri metin içinde `\"` olarak yazılır.
   - Bir anahtarı silerseniz ya da çeviremezseniz program o yazı için İngilizceyi kullanır, yani yarım bir çeviri de çalışır.
5. Dosyayı derlemeden denemek için `%AppData%\LiDPI\lang\` klasörüne koyun (klasör yoksa oluşturun). `LiDPI.exe` ile aynı yerdeki bir `lang` klasörü de olur. LiDPI'yi yeniden açın, diliniz Ayarlar → Dil bölümünde görünür. Dosyayı düzenledikten sonra dili yeniden seçmek değişiklikleri yükler.
6. Uzun yazıların taşıp taşmadığına bakın. Pencere dar (400 piksel), bu yüzden kısa ifadeler daha iyi durur. Özellikle düğmelere, sekmelere ve istatistik kartlarına göz atın.
7. Dosyayı `src/LiDPI/Localization/` klasörüne ekleyip bir pull request açın. Dosyalar programa otomatik olarak gömülür, proje dosyasını değiştirmeniz gerekmez.

Var olan bir çeviriyi düzeltmek için de aynı yol geçerli: örneğin `tr.json`'u `%AppData%\LiDPI\lang\` içine koyarsanız, içindeki değerler yerleşik Türkçe metinlerin üzerine yazılır.

Birkaç ipucu:

- Arayüzün sesi samimi ve kısa. Resmî ya da reklam diliyle yazmayın.
- `Log...` ile başlayan anahtarlar Kayıtlar penceresinde görünür, `Err...` ile başlayanlar hata mesajlarıdır. Bunları da çevirmeniz iyi olur ama önce arayüzdeki yazılarla başlayın.
- Profil adları (`Profile.ttnet.Name` gibi) marka adıysa çevrilmez.

## Derleme

Gerekenler: Windows 10/11 (64 bit) ve [.NET 9 SDK](https://dotnet.microsoft.com/download).

```
git clone https://github.com/LiebertDev/LiDPI.git
cd LiDPI
dotnet build src/LiDPI -c Debug
dotnet publish src/LiDPI -c Release
```

`publish` komutu tek dosyalık `LiDPI.exe` üretir (`src/LiDPI/bin/Release/net9.0-windows/win-x64/publish/`). "Tüm Uygulamalar" modunu denemek için programı yönetici olarak çalıştırmanız gerekir.

Kod hakkında birkaç not:

- Arayüz WPF ile yazıldı. Kullanıcının gördüğü her yazı JSON dosyalarından gelir. XAML'da `{l:T Anahtar}`, C# tarafında `Strings.Anahtar` kullanın. Koda doğrudan metin yazmayın.
- Pencere tepsiye küçültülünce tamamen kapatılır ve yeniden açılınca baştan kurulur. Pencerenin ömrüne bağlı bir şey eklerken bunu aklınızda tutun. Arka planda sürekli çalışan zamanlayıcılardan kaçının.
- Ağ bağlantısı ekleyen bir değişiklik gizlilik bölümünü (README ve uygulama içi metin) de güncellemelidir.

Hata bildirirken Ayarlar → Hakkında → "Tanı raporu oluştur" ile aldığınız raporu eklerseniz sorunu bulmak çok kolaylaşır.

---

# Contributing

Thanks for wanting to help with LiDPI. This file covers two things: adding a language and building the app.

## Adding a language

Every piece of text in the UI lives in the JSON files under `src/LiDPI/Localization/`. Each file is a flat list of key → text:

```json
{
  "LanguageName": "English",
  "StatusRunning": "Protected",
  "HealthMinutesAgo": "{0} min ago"
}
```

1. Copy `src/LiDPI/Localization/en.json` and name it after the language code: `de.json` for German, `pt-BR.json` for Brazilian Portuguese, and so on.
2. Set `LanguageName` to the language's own name (`Deutsch`, `Português (Brasil)`). That's what the language picker shows.
3. Optionally set `LanguageCulture` to a culture code for number and date formatting (`de-DE`). If you leave it out, the file name is used.
4. Translate the values. **Don't change the keys.**
   - Keep placeholders like `{0}` and `{1}` exactly as they are. You can move them around in the sentence. Formats like `{3:0}` stay as they are too.
   - Keep symbols like `✓`, `✦` and `…`.
   - Write a `"` inside a text as `\"`.
   - If you delete a key or leave it out, the app falls back to English for that text, so a partial translation still works.
5. To try your file without building, put it in `%AppData%\LiDPI\lang\` (create the folder if needed). A `lang` folder next to `LiDPI.exe` works too. Restart LiDPI and your language shows up under Settings → Language. After editing the file, pick the language again to reload it.
6. Check that longer texts don't overflow. The window is narrow (400 px), so shorter wording works better. Look closely at buttons, the mode switch and the stat cards.
7. Add the file to `src/LiDPI/Localization/` and open a pull request. Files there are embedded automatically, no project changes needed.

The same works for fixing an existing translation: drop `tr.json` into `%AppData%\LiDPI\lang\` and its values override the built-in Turkish ones.

A few tips:

- The app's voice is friendly and short. Avoid formal or marketing language.
- Keys starting with `Log...` show up in the Logs drawer, keys starting with `Err...` are error messages. It's great if you translate them too, but start with the UI texts.
- Profile names that are brand names (like `Profile.ttnet.Name`) stay as they are.

## Building

You need Windows 10/11 (64-bit) and the [.NET 9 SDK](https://dotnet.microsoft.com/download).

```
git clone https://github.com/LiebertDev/LiDPI.git
cd LiDPI
dotnet build src/LiDPI -c Debug
dotnet publish src/LiDPI -c Release
```

`publish` creates the single-file `LiDPI.exe` (in `src/LiDPI/bin/Release/net9.0-windows/win-x64/publish/`). To try All apps mode, run it as administrator.

Some notes on the code:

- The UI is WPF. Every user-visible text comes from the JSON files. Use `{l:T Key}` in XAML and `Strings.Key` in C#. Don't put literal text in the code.
- The window is destroyed when it's hidden to the tray and rebuilt when it's shown again. Keep that in mind when you add something tied to the window, and avoid timers that run all the time.
- A change that adds a network connection should also update the privacy section (README and the in-app text).

When you report a bug, attaching a report from Settings → About → "Create a diagnostic report" makes it much easier to find the problem.
