using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SmartMarketplace
{
    public class ComplianceResult
    {
        public string CleanedTitle { get; set; }
        public string CleanedDescription { get; set; }
        public List<string> RemovedTerms { get; set; }

        public ComplianceResult()
        {
            RemovedTerms = new List<string>();
        }
    }

    public static class ComplianceGuard
    {
        // Registered trademarks, marketplace names, promotional spam, and health-claim terms banned by Trendyol / HB / N11 / Amazon TR
        private static readonly string[] BannedPhrases =
        {
            // Marketplace & competitor platform names (strictly banned in titles & descriptions)
            "trendyol", "hepsiburada", "hepsi burada", "n11", "idefix", "pazarama", "pttavm", "ptt avm",
            "amazon", "çiçeksepeti", "ciceksepeti", "sahibinden", "gittigidiyor", "morhipo",
            "dolap", "gardrops", "letgo", "aliexpress", "temu", "shein", "ebay", "etsy",
            // Known trademark words that trigger Trendyol's automatic brand/trademark filter
            "koleksiyon", "smart koleksiyon", "collection",
            // Promotional / shipping / pricing spam forbidden in titles & descriptions
            "ücretsiz kargo", "ucretsiz kargo", "bedava kargo", "aynı gün kargo", "ayni gun kargo",
            "hızlı kargo", "hizli kargo", "kapıda ödeme", "kapida odeme", "şok fiyat", "sok fiyat",
            "en ucuz", "en iyi fiyat", "fırsat ürünü", "firsat urunu", "büyük indirim", "buyuk indirim",
            "süper indirim", "super indirim", "kaçırmayın", "kacirmayin", "stoklarla sınırlı",
            "kampanyalı fiyat", "kampanyali fiyat", "taksit imkanı", "hediyeli",
            // Authenticity / counterfeit trigger words
            "replika", "imitasyon", "çakma", "cakma", "a+ kalite", "aaa kalite", "1. kalite",
            "birinci kalite", "orijinal değil", "orijinal degil", "muadil", "orijinal ürün", "orijinal urun",
            // Health / medical claim triggers
            "kesin çözüm", "kesin cozum", "mucize", "tedavi eder", "iyileştirir", "iyilestirir",
            "zayıflatır", "zayiflatir", "şifa verir", "sifa verir", "hastalık", "hastalik",
            // Off-platform social/contact triggers
            "whatsapp", "instagram", "facebook", "telegram", "tiktok", "youtube"
        };

        public static bool IsGenericBrand(string brand)
        {
            if (string.IsNullOrWhiteSpace(brand)) return true;
            string b = brand.Trim().ToLower(new CultureInfo("tr-TR"));
            return b == "genel markalar" || b == "diğer" || b == "diger" || b == "markasız" || b == "markasiz" || b == "smart koleksiyon" || b == "smart";
        }

        public static string CleanBrandName(string brand)
        {
            if (string.IsNullOrWhiteSpace(brand)) return "Genel Markalar";
            string trimmed = brand.Trim();
            if (trimmed.ToLower(new CultureInfo("tr-TR")).Contains("koleksiyon") || trimmed.Equals("Smart", StringComparison.OrdinalIgnoreCase))
                return "Genel Markalar";
            return trimmed;
        }

        public static ComplianceResult SanitizeListing(string title, string description, int maxTitleLength, string ownBrand = "")
        {
            var res = new ComplianceResult();
            var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string cleanTitle = SanitizeText(title ?? "", ownBrand, removed, true);
            string cleanDesc = SanitizeText(description ?? "", ownBrand, removed, false);

            // Clean punctuation leftovers in title
            cleanTitle = Regex.Replace(cleanTitle, @"\s*[—\-|,]+\s*$", "");
            cleanTitle = Regex.Replace(cleanTitle, @"^\s*[—\-|,.:;]+\s*", "");
            cleanTitle = Regex.Replace(cleanTitle, @"\s{2,}", " ").Trim();
            if (maxTitleLength > 0 && cleanTitle.Length > maxTitleLength)
            {
                string cut = cleanTitle.Substring(0, maxTitleLength);
                int lastSpace = cut.LastIndexOf(' ');
                if (lastSpace > maxTitleLength / 2) cut = cut.Substring(0, lastSpace);
                cleanTitle = cut.Trim(' ', '-', '|', ',', '.');
            }

            // Clean double empty lines & awkward leftovers in description
            cleanDesc = Regex.Replace(cleanDesc, @"\r\n", "\n");
            cleanDesc = Regex.Replace(cleanDesc, @"[ \t]{2,}", " ");
            cleanDesc = Regex.Replace(cleanDesc, @"\n{3,}", "\n\n").Trim();
            cleanDesc = cleanDesc.Replace("\n", Environment.NewLine);

            res.CleanedTitle = cleanTitle;
            res.CleanedDescription = cleanDesc;
            res.RemovedTerms = removed.ToList();
            return res;
        }

        private static string SanitizeText(string input, string ownBrand, HashSet<string> removed, bool isTitle)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            string s = input;
            string ownBrandLower = (ownBrand ?? "").Trim().ToLower(new CultureInfo("tr-TR"));

            // Remove URLs, emails, phone numbers (strictly forbidden by all marketplaces)
            if (Regex.IsMatch(s, @"https?://\S+|www\.\S+", RegexOptions.IgnoreCase))
            {
                s = Regex.Replace(s, @"https?://\S+|www\.\S+", " ", RegexOptions.IgnoreCase);
                removed.Add("URL/Link");
            }
            if (Regex.IsMatch(s, @"\b[\w\.-]+@[\w\.-]+\.\w{2,}\b"))
            {
                s = Regex.Replace(s, @"\b[\w\.-]+@[\w\.-]+\.\w{2,}\b", " ");
                removed.Add("E-posta");
            }
            if (Regex.IsMatch(s, @"\b(?:\+90|0)?\s*5\d{2}[\s\-]?\d{3}[\s\-]?\d{2}[\s\-]?\d{2}\b"))
            {
                s = Regex.Replace(s, @"\b(?:\+90|0)?\s*5\d{2}[\s\-]?\d{3}[\s\-]?\d{2}[\s\-]?\d{2}\b", " ");
                removed.Add("Telefon Numarası");
            }

            // Remove marketplace closing phrases cleanly before single-word sweep
            s = Regex.Replace(s, @"\b(Trendyol|Hepsiburada|N11|İdefix|Pazarama|PttAVM|Amazon(?:\s*TR)?)\s+güvencesiyle\b", "Kalite güvencesiyle", RegexOptions.IgnoreCase);

            // Remove all banned phrases/words (case-insensitive Turkish-aware)
            foreach (var phrase in BannedPhrases)
            {
                if (!string.IsNullOrEmpty(ownBrandLower) && ownBrandLower == phrase) continue;

                // Match word boundary or Turkish suffix variations (e.g., "Trendyol'da", "koleksiyonu")
                string pattern = @"(?i)(?<![\p{L}\p{N}])" + Regex.Escape(phrase) + @"[\p{L}'’]*(?![\p{L}\p{N}])";
                if (Regex.IsMatch(s, pattern))
                {
                    s = Regex.Replace(s, pattern, " ");
                    removed.Add(phrase);
                }
            }

            // Remove leftover "Smart " brand prefix if it came from our old default "Smart Koleksiyon"
            s = Regex.Replace(s, @"(?i)(?<![\p{L}\p{N}])Smart(?=\s+(?:Paslanmaz|Kadın|Erkek|Unisex|El\s+Yapımı|Aktif|Kablosuz|Yeni|—|\-))", "");

            // Strip excessive punctuation/spam symbols
            s = Regex.Replace(s, @"[!?*]{2,}", "");
            if (isTitle)
            {
                s = s.Replace("!", "").Replace("?", "").Replace("*", "");
                s = Regex.Replace(s, @"\.{2,}", " ");
                s = Regex.Replace(s, @"\s+([,.;:])", "$1");
            }
            else
            {
                // Clean broken phrases like "  güvencesiyle" or "- Marka: Smart"
                s = Regex.Replace(s, @"(?m)^-\s*Marka:\s*Smart\s*$", "- Marka: Genel Markalar");
                s = Regex.Replace(s, @"[ \t]+([,.;:])", "$1");
            }

            return s;
        }
    }
}
