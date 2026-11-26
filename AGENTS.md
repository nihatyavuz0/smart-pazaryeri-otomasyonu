# Smart Pazaryeri Otomasyonu — Workspace Kuralları

## Otomatik GitHub & Vercel Senkronizasyonu (Zorunlu)
1. **Her kod, özellik, arayüz veya güvenlik güncellemesi tamamlandığında** kullanıcı ayrıca istemese bile şu sırayı otomatik uygula:
   - `cmd /c build.bat` ile projeyi hatasız derle ve ilgili doğrulama testlerini çalıştır.
   - `git status --short` çıktısını kontrol ederek `data/`, `*.exe`, `.env`, şifre veya kullanıcı verisi gibi hassas/derleme dosyalarının stage edilmediğinden emin ol.
   - Değişiklikleri açıklayıcı bir commit mesajıyla `git commit` et ve **anında `git push origin main`** ile GitHub'a gönder *(böylece Vercel Production otomatik güncellenir ve diğer bilgisayar her zaman güncel kalır)*.
2. **Başka bir bilgisayardan devam edildiğinde** yeni geliştirmeye başlamadan önce `git fetch origin` / `git pull --ff-only origin main` ile uzaktaki son değişikliklerin yerelde mevcut olduğunu kontrol et.
3. **Masaüstü arayüz korunumu:** Kullanıcı açıkça istemedikçe masaüstü düzenini (`> 820px`) bozma; sıkı CSP (`script-src 'self'`, sıfır satır içi JS) ve güvenlik kurallarını her değişiklikte koru.
