# Smart — Pazaryeri Ürün Yükleme & Stok Otomasyonu (v3.4)

**Smart**, Türkiye'nin önde gelen pazaryerleri (**Trendyol, Hepsiburada, N11, İdefix, Pazarama, PttAVM ve Amazon TR**) için tek merkezden akıllı ürün yükleme, rakip analizi, SEO içerik üretimi, görsel optimizasyonu ve anlık stok senkronizasyonu sağlayan otomasyon programıdır.

---

## Hızlı Başlatma (Kurulum Gerektirmez)

Windows 10/11 üzerinde yerleşik `.NET Framework 4.8` ile çalışır; Node.js veya Python kurulumu gerektirmez.

1. Klasör içindeki **`Smart.bat`** dosyasına çift tıklayın *(ilk çalıştırmada `SmartServer.exe` otomatik derlenir)*.
2. Tarayıcınızda otomatik olarak **`http://localhost:8765`** açılır.
3. **Mobilden erişmek için:** Telefonunuz bilgisayar ile aynı Wi-Fi ağındayken sol alt menüdeki **`Mobil`** butonunda görünen yerel adresi (`http://192.168.x.x:8765/`) telefon tarayıcınızda açın; *"Ana Ekrana Ekle"* ile uygulama gibi kullanabilirsiniz.

---

## Temel Özellikler

- **Görsel + Prompt ile Tam Otomatik Ürün Yükleme:** Görselleri sürükleyip ürün hakkında birkaç cümle yazın. Smart tüm pazaryerlerinin karakter limitine, SEO yapısına ve zorunlu alanlarına uygun başlık, açıklama ve teknik özellikleri otomatik oluşturur.
- **Trendyol V2 Canlı İç Kategori & Zorunlu Özellik Motoru (`TrendyolCategoryEngine.cs`):**
  - Canlı Trendyol rakip analizi + 3.360+ resmi Trendyol yaprak kategori ağacı ile en doğru iç kategoriyi otomatik eşleştirir.
  - Seçilen kategorinin Trendyol V2 zorunlu özelliklerini (`attributeId` ve `attributeValueId`) canlı olarak çeker ve otomatik doldurur.
  - Mevcut onay/revize bekleyen ürünleri `PUT /v2/products` ile otomatik revize eder.
- **Yasaklı Kelime & Pazaryeri Politika Kalkanı (`ComplianceGuard.cs`):** Başlık ve açıklamalardaki pazaryeri adlarını, tescilli marka kelimelerini, promosyon/spam ve yasaklı ifadeleri yayın öncesi otomatik temizler.
- **Otomatik `1200×1800` RGB `.jpg` Görsel Hazırlama & Kalıcı CDN (`ImageHostService.cs`):** Her görseli beyaz zeminli `1200×1800` (2:3), `96 DPI`, 24-bit sRGB `.jpg` standardına dönüştürür, kalıcı HTTPS CDN'e yükler ve gönderim öncesi `HTTP 200` + JPEG doğrulaması yapar.
- **7 Pazaryeri API Entegrasyonu & Anlık Stok Senkronizasyonu:** Trendyol, Hepsiburada, N11, İdefix, Pazarama, PttAVM ve Amazon TR desteklenir; gerçek anahtarlar girildiğinde otomatik **Canlı API Modu** açılır.
- **Siber Güvenlik Kalkanı (`Security.cs`):**
  - Tüm API anahtarları diskte **Windows DPAPI** (`CurrentUser`) ile şifrelenir; yedek dışa aktarımlarında gizli şifreler temizlenir.
  - 256-bit tek kullanımlık oturum anahtarı (`X-Smart-Token`), DNS Rebinding & CSRF koruması, sıkı CSP (`script-src 'self'`), dizin gezinme koruması ve yalnızca yerel Wi-Fi (RFC1918) erişim kilidi.
- **Kalıcı Kayıt & Otomatik Canlı Güncelleme:** Tüm ürünler, ayarlar ve yarım kalan taslaklar otomatik kaydedilir (`data/smart_db.json` + otomatik çökme yedeği).
- **Sakin Taş Grisi & Yumuşak Haki Arayüz + Tam Mobil Uyum (PWA):** Masaüstü ve mobilde göz yormayan sade tasarım, Smart'a özgü yumuşak bildirim ve onay pencereleri.

---

## Proje Yapısı

```text
├── Smart.bat                        # Tek tıkla başlatıcı & canlı oto-güncelleme yöneticisi
├── build.bat                        # .NET Framework 4.8 C# derleyicisi
├── .gitignore                       # Özel verileri (data/) ve derleme çıktılarını korur
├── backend/
│   ├── Program.cs                   # Yerel HTTP sunucusu, REST API, zamanlanmış stok senkronu
│   ├── Models.cs                    # Veri modelleri
│   ├── Security.cs                  # DPAPI şifreleme, CSRF/Host/LAN koruması, hız sınırı
│   ├── Storage.cs                   # Atomik JSON veritabanı ve otomatik yedekleme
│   ├── ComplianceGuard.cs           # Yasaklı kelime ve pazaryeri politika filtresi
│   ├── ImageHostService.cs          # 1200x1800 RGB .jpg dönüştürücü ve CDN yükleyici
│   ├── TrendyolCategoryEngine.cs    # 4 aşamalı Trendyol iç kategori & V2 özellik motoru
│   ├── MarketplaceSchema.cs         # 7 pazaryeri zorunlu alan şemaları
│   ├── CompetitorAnalyzer.cs        # Canlı rakip analizi ve eksik bilgi tamamlama
│   ├── AiContentEngine.cs           # Gemini / OpenAI / Yerleşik Türkçe SEO motoru
│   └── MarketplaceConnectors.cs     # 7 pazaryeri canlı API bağlantıları
└── frontend/
    ├── index.html                   # Masaüstü + Mobil PWA arayüz iskeleti
    ├── styles.css                   # Taş grisi & yumuşak haki tema + mobil stil katmanı
    ├── app.js                       # SPA denetleyici, Smart popup/toast ve oto-kayıt
    ├── manifest.json                # Mobil PWA manifest dosyası
    ├── logo.png                     # Smart şeffaf PNG logosu
    └── favicon.png                  # Tarayıcı sekme ikonu
```
