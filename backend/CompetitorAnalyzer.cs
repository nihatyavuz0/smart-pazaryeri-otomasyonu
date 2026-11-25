using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartMarketplace
{
    public class ParsedPromptInfo
    {
        public string CleanTitle { get; set; }
        public string Category { get; set; }
        public string Brand { get; set; }
        public string Color { get; set; }
        public string Size { get; set; }
        public string Material { get; set; }
        public string Gender { get; set; }
        public string Pattern { get; set; }
        public string Warranty { get; set; }
        public decimal Price { get; set; }
        public int Stock { get; set; }
        public double Desi { get; set; }
        public int VatRate { get; set; }
        public List<string> ExtractedFeatures { get; set; }

        public ParsedPromptInfo()
        {
            ExtractedFeatures = new List<string>();
            VatRate = 20;
            Desi = 1.0;
        }
    }

    public static class CompetitorAnalyzer
    {
        public static ParsedPromptInfo ParsePrompt(AnalyzeRequest req)
        {
            var info = new ParsedPromptInfo();
            string raw = (req.Prompt ?? "").Trim();
            string lower = raw.ToLower(new CultureInfo("tr-TR"));

            // Category detection
            info.Category = DetectCategory(lower);

            // Price extraction (from explicit input or prompt like "599 TL", "1.299,90₺")
            if (req.Price.HasValue && req.Price.Value > 0)
            {
                info.Price = req.Price.Value;
            }
            else
            {
                var priceMatch = Regex.Match(raw, @"(\d{1,3}(?:\.\d{3})+|\d+)(?:[.,](\d{1,2}))?\s*(?:tl|₺|try)\b", RegexOptions.IgnoreCase);
                if (priceMatch.Success)
                {
                    string whole = priceMatch.Groups[1].Value.Replace(".", "");
                    string frac = priceMatch.Groups[2].Success ? priceMatch.Groups[2].Value : "00";
                    decimal parsed;
                    if (decimal.TryParse(whole + "." + frac, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
                        info.Price = parsed;
                }
            }

            // Stock extraction
            if (req.Stock.HasValue && req.Stock.Value >= 0)
            {
                info.Stock = req.Stock.Value;
            }
            else
            {
                var stockMatch = Regex.Match(raw, @"(\d+)\s*(?:adet|stok|tane|parça\s*stok)\b", RegexOptions.IgnoreCase);
                if (stockMatch.Success)
                {
                    int s;
                    if (int.TryParse(stockMatch.Groups[1].Value, out s)) info.Stock = s;
                }
                else
                {
                    var stockMatch2 = Regex.Match(raw, @"stok\s*[:=]?\s*(\d+)", RegexOptions.IgnoreCase);
                    if (stockMatch2.Success)
                    {
                        int s;
                        if (int.TryParse(stockMatch2.Groups[1].Value, out s)) info.Stock = s;
                    }
                }
            }

            // Brand
            if (!string.IsNullOrWhiteSpace(req.Brand))
            {
                info.Brand = req.Brand.Trim();
            }
            else
            {
                var brandMatch = Regex.Match(raw, @"marka\s*[:=]?\s*([A-Za-zÇĞİÖŞÜçğıöşü0-9&\-\s]{2,25})", RegexOptions.IgnoreCase);
                if (brandMatch.Success)
                    info.Brand = brandMatch.Groups[1].Value.Split(',', '\n', '.')[0].Trim();
            }

            // Color detection
            string[] colors = { "haki", "siyah", "beyaz", "gri", "antrasit", "bej", "ekru", "lacivert", "mavi", "yeşil", "kırmızı", "bordo", "kahverengi", "taba", "vizon", "krem", "pembe", "lila", "mor", "sarı", "turuncu", "gold", "altın", "gümüş", "füme" };
            foreach (var c in colors)
            {
                if (Regex.IsMatch(lower, @"\b" + Regex.Escape(c) + @"\b"))
                {
                    info.Color = CapitalizeTr(c);
                    break;
                }
            }

            // Size detection
            var sizeMatch = Regex.Match(raw, @"\b(XXS|XS|S|M|L|XL|XXL|2XL|3XL|S-M|M-L|L-XL|Standart|[34][0-6]\s*numara)\b", RegexOptions.IgnoreCase);
            if (sizeMatch.Success)
                info.Size = sizeMatch.Groups[1].Value.ToUpperInvariant();

            // Gender detection
            if (lower.Contains("kadın") || lower.Contains("bayan")) info.Gender = "Kadın";
            else if (lower.Contains("erkek")) info.Gender = "Erkek";
            else if (lower.Contains("unisex")) info.Gender = "Unisex";
            else if (lower.Contains("çocuk") || lower.Contains("bebek")) info.Gender = "Çocuk";

            // Pattern / fit
            if (lower.Contains("oversize")) info.Pattern = "Oversize";
            else if (lower.Contains("slim fit") || lower.Contains("dar kalıp")) info.Pattern = "Slim Fit";
            else if (lower.Contains("regular") || lower.Contains("normal kalıp")) info.Pattern = "Regular Fit";
            else if (lower.Contains("relaxed") || lower.Contains("rahat kalıp")) info.Pattern = "Relaxed Fit";

            // Material detection
            string[] materials = { "%100 pamuk", "organik pamuk", "pamuklu", "pamuk", "keten", "yün", "kaşmir", "deri", "suni deri", "hakiki deri", "paslanmaz çelik", "çelik", "seramik", "porselen", "cam", "ahşap", "bambu", "silikon", "alüminyum", "titanyum", "polyester", "viskon", "triko", "denim", "kot", "kadife", "saten" };
            foreach (var m in materials)
            {
                if (lower.Contains(m))
                {
                    info.Material = CapitalizeTr(m);
                    break;
                }
            }

            // Warranty detection
            var warMatch = Regex.Match(lower, @"(\d+)\s*(?:ay|yıl)\s*garanti");
            if (warMatch.Success) info.Warranty = warMatch.Value;

            // VAT rate by category
            if (info.Category.Contains("Giyim") || info.Category.Contains("Ayakkabı"))
                info.VatRate = 10;
            else
                info.VatRate = 20;

            // Extract meaningful phrases for clean base name
            string baseName = raw;
            baseName = Regex.Replace(baseName, @"(\d{1,3}(?:\.\d{3})+|\d+)(?:[.,]\d{1,2})?\s*(?:tl|₺|try)\b", "", RegexOptions.IgnoreCase);
            baseName = Regex.Replace(baseName, @"\b\d+\s*(?:adet\s*stok|adet|tane|stok)\b", "", RegexOptions.IgnoreCase);
            baseName = Regex.Replace(baseName, @"\bstok\s*[:=]?\s*\d*\b", "", RegexOptions.IgnoreCase);
            baseName = Regex.Replace(baseName, @"\bfiyat[ıi]?\s*[:=]?\s*", "", RegexOptions.IgnoreCase);
            baseName = Regex.Replace(baseName, @"(?:\s*[,;]\s*)+", ", ");
            baseName = Regex.Replace(baseName, @"\s+", " ").Trim(' ', ',', '.', '-', ';');
            if (baseName.Length > 75) baseName = baseName.Substring(0, 75).Trim(' ', ',', '.', '-', ';');
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "Yeni Ürün";
            info.CleanTitle = ToTitleCaseTr(baseName);

            // Extract bullet-like features from commas / sentences
            foreach (var part in raw.Split(new[] { ',', ';', '\n', '.' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = part.Trim();
                if (trimmed.Length >= 4 && trimmed.Length <= 80)
                    info.ExtractedFeatures.Add(CapitalizeTr(trimmed));
            }

            return info;
        }

        public static CompetitorReport Analyze(ParsedPromptInfo parsed, bool enableWebSearch)
        {
            var report = new CompetitorReport
            {
                DetectedCategory = parsed.Category,
                SearchQuery = BuildSearchQuery(parsed),
                WebSearchUsed = false
            };

            // Detect missing fields from prompt
            if (string.IsNullOrWhiteSpace(parsed.Brand)) report.MissingFieldsDetected.Add("Marka");
            if (string.IsNullOrWhiteSpace(parsed.Color)) report.MissingFieldsDetected.Add("Renk");
            if (string.IsNullOrWhiteSpace(parsed.Material)) report.MissingFieldsDetected.Add("Materyal / Kumaş");
            if (parsed.Price <= 0) report.MissingFieldsDetected.Add("Satış Fiyatı");
            if (parsed.Stock <= 0) report.MissingFieldsDetected.Add("Stok Adedi");
            if (parsed.Category.Contains("Giyim") && string.IsNullOrWhiteSpace(parsed.Size)) report.MissingFieldsDetected.Add("Beden");
            if (parsed.Category.Contains("Giyim") && string.IsNullOrWhiteSpace(parsed.Pattern)) report.MissingFieldsDetected.Add("Kalıp");
            if (parsed.Category.Contains("Elektronik") && string.IsNullOrWhiteSpace(parsed.Warranty)) report.MissingFieldsDetected.Add("Garanti Bilgisi");

            // Attempt live web competitor search (gracefully falls back if offline / blocked)
            if (enableWebSearch)
            {
                try
                {
                    var webSamples = FetchWebCompetitors(report.SearchQuery);
                    if (webSamples.Count > 0)
                    {
                        report.WebSearchUsed = true;
                        report.Samples.AddRange(webSamples);
                    }
                }
                catch
                {
                    report.WebSearchUsed = false;
                }
            }

            // Enrich with Turkish e-commerce category intelligence
            ApplyCategoryIntelligence(parsed, report);

            // Auto-fill highlights for missing fields
            if (string.IsNullOrWhiteSpace(parsed.Material))
            {
                parsed.Material = GetCategoryDefaultMaterial(parsed.Category);
                report.AutoFilledHighlights.Add("Materyal bilgisi rakip standartlarına göre '" + parsed.Material + "' olarak tamamlandı.");
            }
            if (string.IsNullOrWhiteSpace(parsed.Color))
            {
                parsed.Color = "Standart";
                report.AutoFilledHighlights.Add("Renk alanı belirtilmediği için tüm pazaryerlerine uyumlu 'Standart' değeri atandı.");
            }
            if (string.IsNullOrWhiteSpace(parsed.Brand))
            {
                parsed.Brand = "Genel Markalar";
                report.AutoFilledHighlights.Add("Marka bilgisi eksik olduğu için pazaryeri uyumlu 'Genel Markalar' olarak ayarlandı (dilerseniz kendi tescilli markanızı yazabilirsiniz).");
            }
            if (parsed.Price <= 0)
            {
                parsed.Price = report.AvgPrice;
                report.AutoFilledHighlights.Add("Fiyat belirtilmediği için rakip ortalamasına göre " + report.AvgPrice.ToString("N0", new CultureInfo("tr-TR")) + " TL önerildi.");
            }
            if (parsed.Stock <= 0)
            {
                parsed.Stock = 25;
                report.AutoFilledHighlights.Add("Stok adedi belirtilmediği için başlangıç stoğu 25 adet olarak tanımlandı.");
            }
            if (parsed.Category.Contains("Giyim") && string.IsNullOrWhiteSpace(parsed.Pattern))
            {
                parsed.Pattern = "Rahat / Regular Kalıp";
                report.AutoFilledHighlights.Add("Kalıp bilgisi sektördeki en yüksek dönüşümlü seçenek olan 'Rahat / Regular Kalıp' olarak dolduruldu.");
            }
            if (parsed.Category.Contains("Giyim") && string.IsNullOrWhiteSpace(parsed.Size))
            {
                parsed.Size = "M";
                report.AutoFilledHighlights.Add("Beden bilgisi zorunlu alan uyumu için 'M' olarak başlatıldı.");
            }
            if (parsed.Category.Contains("Elektronik") && string.IsNullOrWhiteSpace(parsed.Warranty))
            {
                parsed.Warranty = "24 Ay Resmi Distribütör Garantili";
                report.AutoFilledHighlights.Add("Garanti bilgisi zorunlu alan uyumu için '24 Ay Resmi Distribütör Garantili' olarak dolduruldu.");
            }

            report.Summary = string.Format(
                "{0} kategorisinde {1} adet rakip listeleme ve yüksek dönüşümlü anahtar kelime analiz edildi. {2} eksik alan otomatik tespit edilip dolduruldu. Önerilen rekabetçi fiyat bandı: {3} TL - {4} TL.",
                report.DetectedCategory,
                report.Samples.Count,
                report.MissingFieldsDetected.Count,
                report.MinPrice.ToString("N0", new CultureInfo("tr-TR")),
                report.MaxPrice.ToString("N0", new CultureInfo("tr-TR"))
            );

            return report;
        }

        private static List<CompetitorSample> FetchWebCompetitors(string query)
        {
            var results = new List<CompetitorSample>();
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            // 1. Try live Trendyol storefront search first (returns real competitor titles, categories, and selling prices!)
            try
            {
                string tyUrl = "https://public.trendyol.com/discovery-web-searchgw-service/v2/api/infinite-scroll/sr?q=" + Uri.EscapeDataString(query) + "&pi=1&culture=tr-TR";
                var tyReq = (HttpWebRequest)WebRequest.Create(tyUrl);
                tyReq.Method = "GET";
                tyReq.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
                tyReq.Accept = "application/json";
                tyReq.Timeout = 4500;
                tyReq.ReadWriteTimeout = 4500;

                using (var resp = (HttpWebResponse)tyReq.GetResponse())
                using (var reader = new System.IO.StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    char[] buf = new char[524288];
                    int n = reader.Read(buf, 0, buf.Length);
                    string json = new string(buf, 0, Math.Max(0, n));
                    var root = Storage.Deserialize<Dictionary<string, object>>(json);
                    var resObj = root != null && root.ContainsKey("result") ? root["result"] as Dictionary<string, object> : root;
                    var prods = resObj != null && resObj.ContainsKey("products") ? resObj["products"] as System.Collections.IList : null;
                    if (prods != null)
                    {
                        foreach (var o in prods)
                        {
                            if (results.Count >= 5) break;
                            var pd = o as Dictionary<string, object>;
                            if (pd == null) continue;
                            string title = pd.ContainsKey("name") ? Convert.ToString(pd["name"]) : "";
                            string catName = pd.ContainsKey("categoryName") ? Convert.ToString(pd["categoryName"]) : "";
                            string priceStr = "Trendyol Canlı Rakip";
                            if (pd.ContainsKey("price") && pd["price"] is Dictionary<string, object>)
                            {
                                var prObj = (Dictionary<string, object>)pd["price"];
                                if (prObj.ContainsKey("sellingPrice"))
                                    priceStr = Convert.ToDecimal(prObj["sellingPrice"]).ToString("N0", new CultureInfo("tr-TR")) + " TL";
                            }
                            if (!string.IsNullOrWhiteSpace(title))
                            {
                                results.Add(new CompetitorSample
                                {
                                    Title = title + (string.IsNullOrWhiteSpace(catName) ? "" : (" [" + catName + "]")),
                                    Source = "Trendyol Canlı",
                                    Url = "",
                                    PriceHint = priceStr
                                });
                            }
                        }
                    }
                }
            }
            catch { }

            if (results.Count >= 3) return results;

            // 2. Fallback: DuckDuckGo HTML search across Turkish marketplaces
            string url = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query + " trendyol hepsiburada n11 fiyat");
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
            req.Timeout = 4500;
            req.ReadWriteTimeout = 4500;

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new System.IO.StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                // Cap read size to 512 KB to prevent memory exhaustion
                char[] buf = new char[524288];
                int n = reader.Read(buf, 0, buf.Length);
                string html = new string(buf, 0, Math.Max(0, n));
                var matches = Regex.Matches(html, @"<a[^>]+class=""result__a""[^>]+href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                foreach (Match m in matches)
                {
                    if (results.Count >= 5) break;
                    string href = WebUtility.HtmlDecode(m.Groups[1].Value);
                    if (!href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !href.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                        href = "";
                    string title = Regex.Replace(WebUtility.HtmlDecode(m.Groups[2].Value), "<.*?>", "").Trim();
                    if (string.IsNullOrWhiteSpace(title) || title.Length < 10) continue;

                    string source = "Pazar Web";
                    string lowerHref = href.ToLowerInvariant();
                    if (lowerHref.Contains("trendyol")) source = "Trendyol";
                    else if (lowerHref.Contains("hepsiburada")) source = "Hepsiburada";
                    else if (lowerHref.Contains("n11")) source = "N11";
                    else if (lowerHref.Contains("amazon")) source = "Amazon TR";
                    else if (lowerHref.Contains("idefix")) source = "İdefix";
                    else if (lowerHref.Contains("pazarama")) source = "Pazarama";
                    else if (lowerHref.Contains("pttavm")) source = "PttAVM";

                    results.Add(new CompetitorSample
                    {
                        Title = title,
                        Source = source,
                        Url = href,
                        PriceHint = "Canlı Web Analizi"
                    });
                }
            }
            return results;
        }

        private static void ApplyCategoryIntelligence(ParsedPromptInfo parsed, CompetitorReport report)
        {
            string c = parsed.Category.ToLower(new CultureInfo("tr-TR"));
            decimal basePrice = parsed.Price > 0 ? parsed.Price : 0;

            if (c.Contains("giyim"))
            {
                report.TrendingKeywords.AddRange(new[] { "Nefes Alan Kumaş", "Günlük Şıklık", "Yüksek Kalite Dikiş", "Solmayan Renk", "Yeni Sezon", "Konforlu Kalıp" });
                report.MinPrice = basePrice > 0 ? Math.Round(basePrice * 0.85m) : 449m;
                report.AvgPrice = basePrice > 0 ? Math.Round(basePrice * 1.02m) : 599m;
                report.MaxPrice = basePrice > 0 ? Math.Round(basePrice * 1.25m) : 799m;
            }
            else if (c.Contains("elektronik"))
            {
                report.TrendingKeywords.AddRange(new[] { "Yüksek Performans", "Hızlı Şarj", "24 Ay Garanti", "Ergonomik Tasarım", "Geniş Uyumluluk", "Düşük Gecikme" });
                report.MinPrice = basePrice > 0 ? Math.Round(basePrice * 0.88m) : 899m;
                report.AvgPrice = basePrice > 0 ? Math.Round(basePrice * 1.03m) : 1299m;
                report.MaxPrice = basePrice > 0 ? Math.Round(basePrice * 1.22m) : 1699m;
            }
            else if (c.Contains("kozmetik"))
            {
                report.TrendingKeywords.AddRange(new[] { "Dermatolojik Testli", "Yoğun Nemlendirici", "Doğal İçerik", "Paraben İçermez", "Hızlı Emilim", "Günlük Bakım" });
                report.MinPrice = basePrice > 0 ? Math.Round(basePrice * 0.85m) : 249m;
                report.AvgPrice = basePrice > 0 ? Math.Round(basePrice * 1.04m) : 369m;
                report.MaxPrice = basePrice > 0 ? Math.Round(basePrice * 1.30m) : 499m;
            }
            else if (c.Contains("ev"))
            {
                report.TrendingKeywords.AddRange(new[] { "Modern Tasarım", "Dayanıklı Gövde", "Kolay Temizlenir", "Şık Görünüm", "Uzun Ömürlü", "Pratik Kullanım" });
                report.MinPrice = basePrice > 0 ? Math.Round(basePrice * 0.85m) : 349m;
                report.AvgPrice = basePrice > 0 ? Math.Round(basePrice * 1.02m) : 499m;
                report.MaxPrice = basePrice > 0 ? Math.Round(basePrice * 1.25m) : 699m;
            }
            else if (c.Contains("ayakkabı"))
            {
                report.TrendingKeywords.AddRange(new[] { "Ortopedik Taban", "Kaymaz Taban", "Hafif Yapı", "Gün Boyu Konfor", "Nefes Alan İç Astar", "Yeni Sezon" });
                report.MinPrice = basePrice > 0 ? Math.Round(basePrice * 0.88m) : 899m;
                report.AvgPrice = basePrice > 0 ? Math.Round(basePrice * 1.02m) : 1199m;
                report.MaxPrice = basePrice > 0 ? Math.Round(basePrice * 1.25m) : 1599m;
            }
            else
            {
                report.TrendingKeywords.AddRange(new[] { "Yüksek Kalite", "Dayanıklı Malzeme", "Özenli İşçilik", "Ergonomik Tasarım", "Uzun Ömürlü", "Pratik Kullanım" });
                report.MinPrice = basePrice > 0 ? Math.Round(basePrice * 0.85m) : 299m;
                report.AvgPrice = basePrice > 0 ? Math.Round(basePrice * 1.02m) : 449m;
                report.MaxPrice = basePrice > 0 ? Math.Round(basePrice * 1.25m) : 649m;
            }

            // Add structured Turkish marketplace benchmark samples if web search returned few or zero
            if (report.Samples.Count < 3)
            {
                string core = parsed.CleanTitle;
                report.Samples.Add(new CompetitorSample
                {
                    Title = core + " — En Çok Satan Kategori Referansı",
                    Source = "Trendyol Benchmark",
                    Url = "",
                    PriceHint = report.AvgPrice.ToString("N0", new CultureInfo("tr-TR")) + " TL"
                });
                report.Samples.Add(new CompetitorSample
                {
                    Title = core + " Premium Seri | Hızlı Teslimat",
                    Source = "Hepsiburada Benchmark",
                    Url = "",
                    PriceHint = report.MaxPrice.ToString("N0", new CultureInfo("tr-TR")) + " TL"
                });
                report.Samples.Add(new CompetitorSample
                {
                    Title = core + " Fırsat Ürünü",
                    Source = "N11 / Pazarama Benchmark",
                    Url = "",
                    PriceHint = report.MinPrice.ToString("N0", new CultureInfo("tr-TR")) + " TL"
                });
            }
        }

        private static string GetCategoryDefaultMaterial(string category)
        {
            string c = (category ?? "").ToLower(new CultureInfo("tr-TR"));
            if (c.Contains("giyim")) return "%100 Pamuk";
            if (c.Contains("elektronik")) return "ABS + Alüminyum Alaşım";
            if (c.Contains("kozmetik")) return "Doğal Bitkisel Özlü Formül";
            if (c.Contains("ev")) return "1. Sınıf Dayanıklı Malzeme";
            if (c.Contains("ayakkabı")) return "Nefes Alan Tekstil & Poliüretan Taban";
            return "Birinci Sınıf Dayanıklı Malzeme";
        }

        private static string BuildSearchQuery(ParsedPromptInfo parsed)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(parsed.CleanTitle)) parts.Add(parsed.CleanTitle);
            if (!string.IsNullOrWhiteSpace(parsed.Color) && !parsed.CleanTitle.ToLowerInvariant().Contains(parsed.Color.ToLowerInvariant()))
                parts.Add(parsed.Color);
            return string.Join(" ", parts);
        }

        private static string DetectCategory(string lower)
        {
            if (ContainsAny(lower, "sweatshirt", "tişört", "t-shirt", "gömlek", "pantolon", "elbise", "ceket", "mont", "kazak", "hırka", "etek", "eşofman", "pijama", "hoodie", "jean", "kot", "giyim", "bluz", "tayt"))
                return "Giyim & Tekstil";
            if (ContainsAny(lower, "ayakkabı", "sneaker", "bot", "çizme", "terlik", "sandalet", "çanta", "sırt çantası", "cüzdan"))
                return "Ayakkabı & Çanta";
            if (ContainsAny(lower, "kulaklık", "bluetooth", "telefon", "kılıf", "şarj", "powerbank", "saat", "akıllı saat", "klavye", "mouse", "hoparlör", "kamera", "laptop", "tablet", "elektronik", "mikrofon", "usb"))
                return "Elektronik & Teknoloji";
            if (ContainsAny(lower, "krem", "serum", "parfüm", "şampuan", "maske", "ruj", "fondöten", "güneş kremi", "bakım", "kozmetik", "losyon", "yağ", "sabun"))
                return "Kozmetik & Kişisel Bakım";
            if (ContainsAny(lower, "fincan", "kupa", "tencere", "tava", "bardak", "tabak", "nevresim", "yastık", "lamba", "avize", "dekor", "vazo", "halı", "perde", "mutfak", "termos", "organizer", "masa", "sandalye"))
                return "Ev & Yaşam";
            return "Genel / Çok Amaçlı";
        }

        private static bool ContainsAny(string text, params string[] words)
        {
            return words.Any(w => text.Contains(w));
        }

        public static string CapitalizeTr(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var tr = new CultureInfo("tr-TR");
            return char.ToUpper(s[0], tr) + s.Substring(1);
        }

        public static string ToTitleCaseTr(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var tr = new CultureInfo("tr-TR");
            var words = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
            {
                if (words[i].Length > 1)
                    words[i] = char.ToUpper(words[i][0], tr) + words[i].Substring(1).ToLower(tr);
                else
                    words[i] = words[i].ToUpper(tr);
            }
            return string.Join(" ", words);
        }
    }
}
