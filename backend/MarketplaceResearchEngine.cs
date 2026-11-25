using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SmartMarketplace
{
    public static class MarketplaceResearchEngine
    {
        static MarketplaceResearchEngine()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }

        private static string GetDomainForMarketplace(string mpId)
        {
            switch (mpId)
            {
                case "trendyol": return "trendyol.com";
                case "hepsiburada": return "hepsiburada.com";
                case "n11": return "n11.com";
                case "idefix": return "idefix.com";
                case "pazarama": return "pazarama.com";
                case "pttavm": return "pttavm.com";
                case "amazon": return "amazon.com.tr";
                default: return "";
            }
        }

        /// <summary>
        /// Runs completely isolated research for every marketplace in parallel so one marketplace's network error,
        /// taxonomy, pricing, or attribute rules can NEVER contaminate another marketplace!
        /// </summary>
        public static Dictionary<string, ChannelResearch> ResearchAllInParallel(
            List<MarketplaceConfig> marketplaces,
            List<string> selectedIds,
            string searchQuery,
            ParsedPromptInfo parsed,
            VisionReport vision,
            bool enableWebSearch)
        {
            var results = new Dictionary<string, ChannelResearch>();
            var tasks = new List<Task<KeyValuePair<string, ChannelResearch>>>();

            foreach (var mp in marketplaces)
            {
                var mpLocal = mp;
                bool isSelected = selectedIds == null || selectedIds.Count == 0 || selectedIds.Contains(mpLocal.Id);
                tasks.Add(Task.Run(() =>
                {
                    var res = ResearchSingleMarketplaceIsolated(mpLocal, searchQuery, parsed, vision, enableWebSearch && isSelected);
                    return new KeyValuePair<string, ChannelResearch>(mpLocal.Id, res);
                }));
            }

            try { Task.WaitAll(tasks.ToArray(), 16000); } catch { }

            foreach (var t in tasks)
            {
                if (t.Status == TaskStatus.RanToCompletion)
                {
                    results[t.Result.Key] = t.Result.Value;
                }
            }

            // Guarantee every marketplace has an isolated research record even if a thread timed out
            foreach (var mp in marketplaces)
            {
                if (!results.ContainsKey(mp.Id))
                {
                    results[mp.Id] = BuildFallbackResearch(mp, searchQuery, parsed, vision, "Zaman aşımı — kanal yedek zekâsıyla izole dolduruldu");
                }
            }
            return results;
        }

        private static ChannelResearch ResearchSingleMarketplaceIsolated(
            MarketplaceConfig mp,
            string searchQuery,
            ParsedPromptInfo parsed,
            VisionReport vision,
            bool enableWebSearch)
        {
            var cr = new ChannelResearch
            {
                MarketplaceId = mp.Id,
                MarketplaceName = mp.Name,
                SearchQuery = searchQuery,
                LiveDataFound = false,
                MatchedCategoryId = MarketplaceSchema.GetCategoryIdForMarketplace(mp.Id, parsed.Category),
                MatchedCategoryName = parsed.Category,
                CategorySource = mp.Name + " Şema Eşleştirmesi",
                IsolationStatus = mp.Name + " izole araştırma tamamlandı"
            };

            var observedPrices = new List<decimal>();

            try
            {
                if (enableWebSearch && !string.IsNullOrWhiteSpace(searchQuery))
                {
                    if (mp.Id == "trendyol")
                    {
                        FetchTrendyolDirectCompetitors(searchQuery, cr, observedPrices);
                    }
                    else
                    {
                        FetchChannelSiteCompetitors(mp, searchQuery, cr, observedPrices);
                    }
                }

                // Trendyol-specific real internal leaf category resolution (isolated strictly to Trendyol!)
                if (mp.Id == "trendyol" && !mp.SimulationMode && MarketplaceConnectors.HasRequiredCredentials(mp))
                {
                    try
                    {
                        var tyMatch = TrendyolCategoryEngine.ResolveBestCategory(mp, parsed.CleanTitle, parsed.CleanTitle, searchQuery, parsed.Category, 0);
                        if (tyMatch.Success && tyMatch.CategoryId > 0)
                        {
                            cr.MatchedCategoryId = tyMatch.CategoryId.ToString();
                            cr.MatchedCategoryName = tyMatch.FullPath;
                            cr.CategorySource = tyMatch.Source;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                // Strictly isolate any channel error to THIS channel only
                cr.IsolationStatus = mp.Name + " canlı tarama yedeğe geçti (" + ex.Message + ")";
            }

            // Compute channel-specific competitive price band
            ApplyChannelPricing(cr, observedPrices, parsed, vision);

            // Extract channel-specific keywords from discovered competitors + channel benchmarks
            ExtractChannelKeywords(cr, parsed);

            // Fill benchmark samples if live search returned fewer than 2 samples
            if (cr.Samples.Count < 2)
            {
                cr.Samples.Add(new CompetitorSample
                {
                    Title = parsed.CleanTitle + " — " + mp.Name + " Kategori Referansı",
                    Source = mp.Name + " Pazar Referansı",
                    Url = "",
                    PriceHint = cr.AvgPrice.ToString("N0", new CultureInfo("tr-TR")) + " TL"
                });
            }

            cr.Summary = string.Format(
                "{0} kanalında '{1}' için {2} rakip incelendi. Kanal fiyat bandı: {3} – {4} TL (Önerilen: {5} TL). Kategori: {6}.",
                mp.Name,
                searchQuery,
                cr.Samples.Count,
                cr.MinPrice.ToString("N0", new CultureInfo("tr-TR")),
                cr.MaxPrice.ToString("N0", new CultureInfo("tr-TR")),
                cr.SuggestedPrice.ToString("N0", new CultureInfo("tr-TR")),
                cr.MatchedCategoryName
            );

            return cr;
        }

        private static void FetchTrendyolDirectCompetitors(string query, ChannelResearch cr, List<decimal> prices)
        {
            string url = "https://public.trendyol.com/discovery-web-searchgw-service/v2/api/infinite-scroll/sr?q=" + Uri.EscapeDataString(query) + "&pi=1&culture=tr-TR";
            string json = HttpGetBounded(url, "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36", "application/json", 5500, 524288);
            if (string.IsNullOrWhiteSpace(json)) return;

            var root = Storage.Deserialize<Dictionary<string, object>>(json);
            var resObj = root != null && root.ContainsKey("result") ? root["result"] as Dictionary<string, object> : root;
            var prods = resObj != null && resObj.ContainsKey("products") ? resObj["products"] as System.Collections.IList : null;
            if (prods == null || prods.Count == 0) return;

            var catVotes = new Dictionary<int, int>();
            var catNames = new Dictionary<int, string>();

            foreach (var o in prods)
            {
                if (cr.Samples.Count >= 6) break;
                var pd = o as Dictionary<string, object>;
                if (pd == null) continue;

                string name = pd.ContainsKey("name") ? Convert.ToString(pd["name"]).Trim() : "";
                string catName = pd.ContainsKey("categoryName") ? Convert.ToString(pd["categoryName"]).Trim() : "";
                int cid = pd.ContainsKey("categoryId") && pd["categoryId"] != null ? Convert.ToInt32(pd["categoryId"]) : 0;
                if (cid > 0)
                {
                    catVotes[cid] = (catVotes.ContainsKey(cid) ? catVotes[cid] : 0) + 1;
                    if (!string.IsNullOrWhiteSpace(catName)) catNames[cid] = catName;
                }

                decimal sp = 0;
                if (pd.ContainsKey("price") && pd["price"] is Dictionary<string, object>)
                {
                    var pr = (Dictionary<string, object>)pd["price"];
                    if (pr.ContainsKey("sellingPrice") && pr["sellingPrice"] != null)
                        decimal.TryParse(Convert.ToString(pr["sellingPrice"]), NumberStyles.Any, CultureInfo.InvariantCulture, out sp);
                }
                if (sp >= 20 && sp <= 500000) prices.Add(sp);

                if (!string.IsNullOrWhiteSpace(name))
                {
                    cr.LiveDataFound = true;
                    cr.Samples.Add(new CompetitorSample
                    {
                        Title = name,
                        Source = "Trendyol Canlı",
                        Url = "",
                        PriceHint = sp > 0 ? (sp.ToString("N0", new CultureInfo("tr-TR")) + " TL") : "Trendyol Canlı"
                    });
                }
            }

            if (catVotes.Count > 0)
            {
                var topCat = catVotes.OrderByDescending(x => x.Value).First().Key;
                cr.MatchedCategoryId = topCat.ToString();
                if (catNames.ContainsKey(topCat)) cr.MatchedCategoryName = catNames[topCat];
                cr.CategorySource = "Trendyol Canlı Rakip Oylaması";
            }
        }

        private static void FetchChannelSiteCompetitors(MarketplaceConfig mp, string query, ChannelResearch cr, List<decimal> prices)
        {
            string domain = GetDomainForMarketplace(mp.Id);
            if (string.IsNullOrEmpty(domain)) return;

            string ddgUrl = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString("site:" + domain + " " + query);
            string html = HttpGetBounded(ddgUrl, "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36", "text/html", 5000, 400000);
            if (string.IsNullOrWhiteSpace(html)) return;

            var blocks = Regex.Matches(html, @"<a[^>]+class=""result__a""[^>]+href=""([^""]+)""[^>]*>(.*?)</a>(.*?)<div class=""clear""", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match m in blocks)
            {
                if (cr.Samples.Count >= 5) break;
                string title = Regex.Replace(WebUtility.HtmlDecode(m.Groups[2].Value), "<.*?>", "").Trim();
                string snippet = Regex.Replace(WebUtility.HtmlDecode(m.Groups[3].Value), "<.*?>", " ").Trim();
                if (string.IsNullOrWhiteSpace(title) || title.Length < 8) continue;

                // Strip site suffix from search result titles
                title = Regex.Replace(title, @"\s*[-|]\s*(Hepsiburada|N11|İdefix|Pazarama|PttAVM|Amazon\.com\.tr).*$", "", RegexOptions.IgnoreCase).Trim();

                decimal foundPrice = ExtractTurkishPrice(title + " " + snippet);
                if (foundPrice >= 20 && foundPrice <= 500000) prices.Add(foundPrice);

                cr.LiveDataFound = true;
                cr.Samples.Add(new CompetitorSample
                {
                    Title = title,
                    Source = mp.Name + " Canlı",
                    Url = "",
                    PriceHint = foundPrice > 0 ? (foundPrice.ToString("N0", new CultureInfo("tr-TR")) + " TL") : (mp.Name + " Canlı Liste")
                });
            }
        }

        private static decimal ExtractTurkishPrice(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var m = Regex.Match(text, @"(\d{1,3}(?:\.\d{3})+|\d{2,6})(?:,(\d{2}))?\s*(?:TL|₺)\b", RegexOptions.IgnoreCase);
            if (!m.Success) return 0;
            string whole = m.Groups[1].Value.Replace(".", "");
            string frac = m.Groups[2].Success ? m.Groups[2].Value : "00";
            decimal val;
            return decimal.TryParse(whole + "." + frac, NumberStyles.Any, CultureInfo.InvariantCulture, out val) ? val : 0;
        }

        private static void ApplyChannelPricing(ChannelResearch cr, List<decimal> observed, ParsedPromptInfo parsed, VisionReport vision)
        {
            if (observed != null && observed.Count > 0)
            {
                observed.Sort();
                // Trim extreme outliers if 5+ prices exist
                var clean = observed.Count >= 5 ? observed.GetRange(1, observed.Count - 2) : observed;
                cr.MinPrice = Math.Round(clean.Min(), 0);
                cr.MaxPrice = Math.Round(clean.Max(), 0);
                cr.AvgPrice = Math.Round(clean.Average(), 0);
                // Competitive sweet spot: ~4% below channel average (classic marketplace buybox edge)
                cr.SuggestedPrice = parsed.Price > 0 ? parsed.Price : Math.Max(cr.MinPrice, Math.Round(cr.AvgPrice * 0.96m, 0));
                return;
            }

            // Fallback to Vision price estimate or user price or category baseline (with gentle channel-specific positioning)
            decimal baseRef = parsed.Price > 0
                ? parsed.Price
                : (vision != null && vision.EstimatedMinPrice > 0 && vision.EstimatedMaxPrice > 0
                    ? Math.Round((vision.EstimatedMinPrice + vision.EstimatedMaxPrice) / 2m, 0)
                    : GetCategoryBaselinePrice(parsed.Category));

            // Slight natural channel variance reflecting commission & audience differences when auto-pricing
            decimal mult = 1.00m;
            if (parsed.Price <= 0)
            {
                switch (cr.MarketplaceId)
                {
                    case "trendyol": mult = 0.98m; break;
                    case "hepsiburada": mult = 1.02m; break;
                    case "n11": mult = 0.97m; break;
                    case "amazon": mult = 1.00m; break;
                    case "idefix": mult = 1.03m; break;
                    case "pazarama": mult = 0.99m; break;
                    case "pttavm": mult = 0.96m; break;
                }
            }

            cr.AvgPrice = Math.Round(baseRef * mult, 0);
            cr.MinPrice = Math.Round(cr.AvgPrice * 0.85m, 0);
            cr.MaxPrice = Math.Round(cr.AvgPrice * 1.22m, 0);
            cr.SuggestedPrice = parsed.Price > 0 ? parsed.Price : cr.AvgPrice;
        }

        private static decimal GetCategoryBaselinePrice(string category)
        {
            string c = (category ?? "").ToLower(new CultureInfo("tr-TR"));
            if (c.Contains("elektronik")) return 1299m;
            if (c.Contains("ayakkabı")) return 1199m;
            if (c.Contains("giyim")) return 599m;
            if (c.Contains("kozmetik")) return 369m;
            if (c.Contains("ev")) return 549m;
            return 449m;
        }

        private static void ExtractChannelKeywords(ChannelResearch cr, ParsedPromptInfo parsed)
        {
            var kw = new List<string>();
            // Pull high-frequency descriptive terms from this channel's actual competitor titles
            var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ve", "ile", "icin", "için", "bir", "bu", "en", "cok", "çok", "yeni", "set", "seti", "adet", "tl", "fiyat", "fiyati", "fiyatı", "modelleri" };

            foreach (var s in cr.Samples)
            {
                foreach (Match m in Regex.Matches(s.Title ?? "", @"[\p{L}]{4,}"))
                {
                    string w = m.Value;
                    if (stopWords.Contains(w)) continue;
                    // Never pick up banned marketplace/trademark words as keywords
                    var chk = ComplianceGuard.SanitizeListing(w, "", 50, parsed.Brand);
                    if (string.IsNullOrWhiteSpace(chk.CleanedTitle)) continue;
                    freq[w] = (freq.ContainsKey(w) ? freq[w] : 0) + 1;
                }
            }

            foreach (var top in freq.OrderByDescending(x => x.Value).Take(4))
            {
                kw.Add(CompetitorAnalyzer.CapitalizeTr(top.Key.ToLower(new CultureInfo("tr-TR"))));
            }

            // Channel-tailored conversion keywords (100% policy-safe)
            switch (cr.MarketplaceId)
            {
                case "trendyol": kw.AddRange(new[] { "Modern Tasarım", "Günlük Kullanım", "Dayanıklı Yapı" }); break;
                case "hepsiburada": kw.AddRange(new[] { "Yüksek Kalite", "Uzun Ömürlü", "Ergonomik Tasarım" }); break;
                case "n11": kw.AddRange(new[] { "Pratik Kullanım", "Sağlam Gövde" }); break;
                case "idefix": kw.AddRange(new[] { "Şık Görünüm", "Özenli İşçilik" }); break;
                case "pazarama": kw.AddRange(new[] { "Kullanışlı Tasarım", "Kaliteli Malzeme" }); break;
                case "pttavm": kw.AddRange(new[] { "Dayanıklı Malzeme", "Güvenilir Kalite" }); break;
                case "amazon": kw.AddRange(new[] { "Kolay Temizlenir", "Çok Amaçlı Kullanım", "Yüksek Dayanıklılık" }); break;
            }

            cr.ChannelKeywords = kw.Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
        }

        private static ChannelResearch BuildFallbackResearch(MarketplaceConfig mp, string query, ParsedPromptInfo parsed, VisionReport vision, string note)
        {
            var cr = new ChannelResearch
            {
                MarketplaceId = mp.Id,
                MarketplaceName = mp.Name,
                SearchQuery = query,
                LiveDataFound = false,
                MatchedCategoryId = MarketplaceSchema.GetCategoryIdForMarketplace(mp.Id, parsed.Category),
                MatchedCategoryName = parsed.Category,
                CategorySource = mp.Name + " Yedek Şema",
                IsolationStatus = note
            };
            ApplyChannelPricing(cr, null, parsed, vision);
            ExtractChannelKeywords(cr, parsed);
            cr.Summary = mp.Name + " izole pazar analizi tamamlandı. Önerilen fiyat: " + cr.SuggestedPrice.ToString("N0", new CultureInfo("tr-TR")) + " TL.";
            return cr;
        }

        private static string HttpGetBounded(string url, string ua, string accept, int timeoutMs, int maxChars)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.UserAgent = ua;
            req.Accept = accept;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                char[] buf = new char[maxChars];
                int n = r.Read(buf, 0, buf.Length);
                return new string(buf, 0, Math.Max(0, n));
            }
        }
    }
}
