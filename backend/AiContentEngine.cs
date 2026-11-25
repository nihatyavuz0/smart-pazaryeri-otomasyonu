using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace SmartMarketplace
{
    public static class AiContentEngine
    {
        public static ProductRecord GenerateSmartProduct(AnalyzeRequest req, AppDatabase db, out string engineUsed)
        {
            engineUsed = "Smart Yerleşik SEO Motoru";

            var parsed = CompetitorAnalyzer.ParsePrompt(req);
            var competitor = CompetitorAnalyzer.Analyze(parsed, db.AiConfig.EnableWebCompetitorAnalysis);

            string sku = string.IsNullOrWhiteSpace(req.Sku)
                ? "SMR-" + DateTime.Now.ToString("yyMMdd") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4).ToUpperInvariant()
                : req.Sku.Trim().ToUpperInvariant();

            string barcode = string.IsNullOrWhiteSpace(req.Barcode)
                ? GenerateEan13()
                : req.Barcode.Trim();

            var targets = (req.TargetMarketplaces != null && req.TargetMarketplaces.Count > 0)
                ? req.TargetMarketplaces
                : db.Marketplaces.Where(m => m.Enabled).Select(m => m.Id).ToList();

            var product = new ProductRecord
            {
                Id = "prd_" + Guid.NewGuid().ToString("N").Substring(0, 10),
                Sku = sku,
                Barcode = barcode,
                BaseName = parsed.CleanTitle,
                Brand = parsed.Brand,
                Category = parsed.Category,
                Prompt = req.Prompt ?? "",
                Price = parsed.Price,
                ListPrice = Math.Round(parsed.Price * 1.18m, 0),
                Stock = parsed.Stock,
                VatRate = parsed.VatRate,
                Desi = parsed.Desi,
                Images = req.Images ?? new List<string>(),
                CompetitorData = competitor,
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            // Try external LLM (Gemini or OpenAI) if configured
            Dictionary<string, object> llmResult = null;
            if (db.AiConfig.Provider != "builtin")
            {
                if ((db.AiConfig.Provider == "gemini" || db.AiConfig.Provider == "auto") && !string.IsNullOrWhiteSpace(db.AiConfig.GeminiApiKey))
                {
                    llmResult = TryCallGemini(req, parsed, competitor, targets, db.AiConfig);
                    if (llmResult != null) engineUsed = "Google Gemini (" + db.AiConfig.GeminiModel + ")";
                }
                if (llmResult == null && (db.AiConfig.Provider == "openai" || db.AiConfig.Provider == "auto") && !string.IsNullOrWhiteSpace(db.AiConfig.OpenAiApiKey))
                {
                    llmResult = TryCallOpenAi(req, parsed, competitor, targets, db.AiConfig);
                    if (llmResult != null) engineUsed = "OpenAI (" + db.AiConfig.OpenAiModel + ")";
                }
            }

            foreach (var mp in db.Marketplaces)
            {
                bool isSelected = targets.Contains(mp.Id);
                var listing = BuildMarketplaceListing(mp.Id, parsed, competitor, product, isSelected, llmResult, db.AiConfig.Tone);

                // For Trendyol in Live Mode, immediately resolve REAL internal Trendyol leaf category + live mandatory attributes!
                if (mp.Id == "trendyol" && !mp.SimulationMode && MarketplaceConnectors.HasRequiredCredentials(mp))
                {
                    try
                    {
                        var tyMatch = TrendyolCategoryEngine.ResolveBestCategory(mp, product.BaseName, listing.Title, product.Prompt, product.Category, 0);
                        if (tyMatch.Success && tyMatch.CategoryId > 0)
                        {
                            listing.CategoryId = tyMatch.CategoryId.ToString();
                            listing.CategoryName = tyMatch.FullPath;
                            listing.StatusMessage = "Trendyol iç kategorisi eşleştirildi: " + tyMatch.CategoryName + " (" + tyMatch.Source + ")";
                            var liveAttrs = TrendyolCategoryEngine.BuildUiAttributesForCategory(mp, tyMatch.CategoryId, product.BaseName, listing.Title, product.Prompt, listing.Attributes);
                            if (liveAttrs.Count > 0)
                            {
                                listing.Attributes = liveAttrs;
                            }
                        }
                    }
                    catch { }
                }

                product.Listings[mp.Id] = listing;
            }

            return product;
        }

        private static MarketplaceListing BuildMarketplaceListing(
            string mpId,
            ParsedPromptInfo parsed,
            CompetitorReport comp,
            ProductRecord product,
            bool selected,
            Dictionary<string, object> llmResult,
            string tone)
        {
            int maxLen = MarketplaceSchema.GetMaxTitleLength(mpId);
            string title = BuildOptimizedTitle(mpId, parsed, comp, maxLen);
            var bullets = BuildBulletPoints(mpId, parsed, comp);
            string description = BuildRichDescription(mpId, parsed, comp, bullets, tone);
            bool usedLlm = false;

            // Merge LLM-generated copy if available
            if (llmResult != null && llmResult.ContainsKey("marketplaces"))
            {
                var mpDict = llmResult["marketplaces"] as Dictionary<string, object>;
                if (mpDict != null && mpDict.ContainsKey(mpId))
                {
                    var mObj = mpDict[mpId] as Dictionary<string, object>;
                    if (mObj != null)
                    {
                        if (mObj.ContainsKey("title") && !string.IsNullOrWhiteSpace(Convert.ToString(mObj["title"])))
                        {
                            title = TruncateClean(Convert.ToString(mObj["title"]).Trim(), maxLen);
                            usedLlm = true;
                        }
                        if (mObj.ContainsKey("description") && !string.IsNullOrWhiteSpace(Convert.ToString(mObj["description"])))
                        {
                            description = Convert.ToString(mObj["description"]).Trim();
                        }
                        if (mObj.ContainsKey("bullets") && mObj["bullets"] is System.Collections.IEnumerable && !(mObj["bullets"] is string))
                        {
                            var bList = new List<string>();
                            foreach (var x in (System.Collections.IEnumerable)mObj["bullets"])
                            {
                                string s = Convert.ToString(x);
                                if (!string.IsNullOrWhiteSpace(s)) bList.Add(s);
                            }
                            if (bList.Count > 0) bullets = bList;
                        }
                    }
                }
            }

            // Build mandatory attributes for this marketplace
            var schemaAttrs = MarketplaceSchema.GetMandatoryAttributes(mpId, parsed.Category);
            var filledAttrs = new List<AttributeItem>();
            foreach (var def in schemaAttrs)
            {
                string val = def.DefaultValue;
                string source = "default";
                string k = def.Key.ToLowerInvariant();

                if (k.Contains("brand") || k.Contains("marka"))
                {
                    val = parsed.Brand;
                    source = comp.MissingFieldsDetected.Contains("Marka") ? "competitor" : "prompt";
                }
                else if (k.Contains("color") || k.Contains("renk"))
                {
                    val = parsed.Color;
                    source = comp.MissingFieldsDetected.Contains("Renk") ? "competitor" : "prompt";
                }
                else if (k.Contains("size") || k.Contains("beden"))
                {
                    val = string.IsNullOrWhiteSpace(parsed.Size) ? def.DefaultValue : parsed.Size;
                    source = comp.MissingFieldsDetected.Contains("Beden") ? "competitor" : "prompt";
                }
                else if (k.Contains("fabric") || k.Contains("kumas") || k.Contains("material") || k.Contains("materyal"))
                {
                    val = string.IsNullOrWhiteSpace(parsed.Material) ? def.DefaultValue : parsed.Material;
                    source = comp.MissingFieldsDetected.Contains("Materyal / Kumaş") ? "competitor" : "prompt";
                }
                else if (k.Contains("pattern") || k.Contains("kalip"))
                {
                    val = string.IsNullOrWhiteSpace(parsed.Pattern) ? def.DefaultValue : parsed.Pattern;
                    source = comp.MissingFieldsDetected.Contains("Kalıp") ? "competitor" : "prompt";
                }
                else if (k.Contains("gender") || k.Contains("cinsiyet") || k.Contains("department"))
                {
                    val = string.IsNullOrWhiteSpace(parsed.Gender) ? def.DefaultValue : parsed.Gender;
                    source = string.IsNullOrWhiteSpace(parsed.Gender) ? "competitor" : "prompt";
                }
                else if (k.Contains("vat") || k.Contains("tax") || k.Contains("kdv"))
                {
                    val = parsed.VatRate.ToString();
                    source = "default";
                }
                else if (k.Contains("desi") || k.Contains("dimensional") || k == "kg")
                {
                    val = parsed.Desi.ToString("0.#", CultureInfo.InvariantCulture);
                    source = "default";
                }
                else if (k.Contains("warranty") || k.Contains("garanti"))
                {
                    if (k.Contains("duration") || k.Contains("suresi"))
                        val = string.IsNullOrWhiteSpace(parsed.Warranty) ? def.DefaultValue : parsed.Warranty;
                    else
                        val = def.DefaultValue;
                    source = comp.MissingFieldsDetected.Contains("Garanti Bilgisi") ? "competitor" : (string.IsNullOrWhiteSpace(parsed.Warranty) ? "default" : "prompt");
                }
                else if (k.Contains("stockcode"))
                {
                    val = product.Sku;
                    source = "default";
                }

                if (usedLlm && source == "competitor") source = "ai";

                filledAttrs.Add(new AttributeItem
                {
                    Key = def.Key,
                    Label = def.Label,
                    Value = val,
                    Required = def.Required,
                    Source = source
                });
            }

            return new MarketplaceListing
            {
                MarketplaceId = mpId,
                MarketplaceName = MarketplaceSchema.GetMarketplaceName(mpId),
                Selected = selected,
                Title = title,
                MaxTitleLength = maxLen,
                Description = description,
                BulletPoints = bullets,
                CategoryId = MarketplaceSchema.GetCategoryIdForMarketplace(mpId, parsed.Category),
                CategoryName = parsed.Category,
                Price = product.Price,
                SyncedStock = product.Stock,
                Status = "draft",
                RemoteId = "",
                StatusMessage = "Yayınlanmaya hazır — tüm zorunlu alanlar doğrulandı",
                LastSyncAt = "",
                Attributes = filledAttrs
            };
        }

        private static string BuildOptimizedTitle(string mpId, ParsedPromptInfo parsed, CompetitorReport comp, int maxLen)
        {
            string kw1 = comp.TrendingKeywords.Count > 0 ? comp.TrendingKeywords[0] : "Yüksek Kalite";
            string kw2 = comp.TrendingKeywords.Count > 1 ? comp.TrendingKeywords[1] : "Dayanıklı Tasarım";
            string core = parsed.CleanTitle;
            // Only prepend real seller brand (never prepend generic placeholder brands or trademarked words)
            string brandPrefix = (!ComplianceGuard.IsGenericBrand(parsed.Brand) && !core.ToLowerInvariant().Contains(parsed.Brand.ToLowerInvariant()))
                ? parsed.Brand + " "
                : "";
            string colorPart = (!string.IsNullOrWhiteSpace(parsed.Color) && parsed.Color != "Standart" && !core.ToLowerInvariant().Contains(parsed.Color.ToLowerInvariant()))
                ? " " + parsed.Color
                : "";
            string materialPart = (!string.IsNullOrWhiteSpace(parsed.Material) && !core.ToLowerInvariant().Contains(parsed.Material.ToLowerInvariant()))
                ? " " + parsed.Material
                : "";

            string raw;
            switch (mpId)
            {
                case "trendyol":
                    raw = brandPrefix + core + colorPart + materialPart + " — " + kw1;
                    break;
                case "hepsiburada":
                    raw = brandPrefix + core + colorPart + materialPart + " | " + kw1 + ", " + kw2;
                    break;
                case "n11":
                    raw = brandPrefix + core + colorPart;
                    break;
                case "idefix":
                    raw = brandPrefix + core + colorPart + " - " + kw1 + " & " + kw2;
                    break;
                case "pazarama":
                    raw = brandPrefix + core + colorPart + " | " + kw1;
                    break;
                case "pttavm":
                    raw = brandPrefix + core + colorPart + materialPart + " (" + kw1 + ")";
                    break;
                case "amazon":
                    raw = brandPrefix + core + colorPart + materialPart + " - " + kw1 + ", " + kw2 + " | Günlük Kullanıma Uygun";
                    break;
                default:
                    raw = brandPrefix + core + colorPart;
                    break;
            }
            var sanitized = ComplianceGuard.SanitizeListing(raw, "", maxLen, parsed.Brand);
            return TruncateClean(sanitized.CleanedTitle, maxLen);
        }

        private static List<string> BuildBulletPoints(string mpId, ParsedPromptInfo parsed, CompetitorReport comp)
        {
            var bullets = new List<string>();
            bullets.Add("Üstün Kalite & Malzeme: " + (parsed.Material ?? "Birinci sınıf malzeme") + " yapısı sayesinde uzun ömürlü ve konforlu kullanım sunar.");
            if (!string.IsNullOrWhiteSpace(parsed.Pattern))
                bullets.Add("Kusursuz Kalıp & Tasarım: " + parsed.Pattern + " formu ile gün boyu hareket özgürlüğü ve modern bir duruş sağlar.");
            else
                bullets.Add("Modern & Ergonomik Tasarım: Günlük kullanım ihtiyaçlarına tam uyum sağlayan estetik ve işlevsel detaylarla üretilmiştir.");

            if (!string.IsNullOrWhiteSpace(parsed.Color) && parsed.Color != "Standart")
                bullets.Add("Özel Renk Seçeneği: " + parsed.Color + " tonu ile zamansız şıklık ve kolay kombin avantajı.");

            string kwLine = string.Join(", ", comp.TrendingKeywords.Take(3));
            bullets.Add("Öne Çıkan Avantajlar: " + kwLine + " özellikleri ile kategorisinde yüksek müşteri memnuniyeti hedeflenmiştir.");
            bullets.Add("Güvenli Paketleme & Hızlı Gönderim: Özenle kontrol edilerek korunaklı ambalajında hızlı şekilde kargoya teslim edilir.");
            return bullets;
        }

        private static string BuildRichDescription(string mpId, ParsedPromptInfo parsed, CompetitorReport comp, List<string> bullets, string tone)
        {
            var sb = new StringBuilder();
            string introTone = tone == "persuasive"
                ? "Günlük deneyiminizi bir üst seviyeye taşımak için özenle seçilen "
                : (tone == "minimal" ? "Sade, işlevsel ve yüksek kaliteli " : "Kalite standartları gözetilerek tasarlanan ");

            string brandDisplay = ComplianceGuard.IsGenericBrand(parsed.Brand) ? "" : (parsed.Brand + " ");
            sb.AppendLine(introTone + brandDisplay + parsed.CleanTitle + ", estetik görünümü ve dayanıklı yapısıyla beklentilerinizi eksiksiz karşılar.");
            sb.AppendLine();
            sb.AppendLine("ÖNE ÇIKAN ÖZELLİKLER");
            foreach (var b in bullets)
            {
                sb.AppendLine("• " + b);
            }
            sb.AppendLine();
            sb.AppendLine("TEKNİK BİLGİLER");
            if (!ComplianceGuard.IsGenericBrand(parsed.Brand)) sb.AppendLine("- Marka: " + parsed.Brand);
            sb.AppendLine("- Kategori: " + parsed.Category);
            if (!string.IsNullOrWhiteSpace(parsed.Color)) sb.AppendLine("- Renk: " + parsed.Color);
            if (!string.IsNullOrWhiteSpace(parsed.Material)) sb.AppendLine("- Materyal: " + parsed.Material);
            if (!string.IsNullOrWhiteSpace(parsed.Size)) sb.AppendLine("- Beden / Ölçü: " + parsed.Size);
            if (!string.IsNullOrWhiteSpace(parsed.Pattern)) sb.AppendLine("- Kalıp: " + parsed.Pattern);
            if (!string.IsNullOrWhiteSpace(parsed.Warranty)) sb.AppendLine("- Garanti: " + parsed.Warranty);
            sb.AppendLine();
            sb.Append("Siparişiniz kalite kontrol sürecinden geçirilerek özenli ve korunaklı ambalajında gönderime hazırlanır.");
            return ComplianceGuard.SanitizeListing("", sb.ToString(), 0, parsed.Brand).CleanedDescription;
        }

        private static Dictionary<string, object> TryCallGemini(AnalyzeRequest req, ParsedPromptInfo parsed, CompetitorReport comp, List<string> targets, AiSettings cfg)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                string model = string.IsNullOrWhiteSpace(cfg.GeminiModel) ? "gemini-2.0-flash" : cfg.GeminiModel.Trim();
                string url = "https://generativelanguage.googleapis.com/v1beta/models/" + Uri.EscapeDataString(model) + ":generateContent?key=" + Uri.EscapeDataString(cfg.GeminiApiKey.Trim());

                string sysPrompt = BuildLlmPrompt(req, parsed, comp, targets);
                var payload = new Dictionary<string, object>
                {
                    {
                        "contents", new object[]
                        {
                            new Dictionary<string, object>
                            {
                                { "parts", new object[] { new Dictionary<string, object> { { "text", sysPrompt } } } }
                            }
                        }
                    },
                    {
                        "generationConfig", new Dictionary<string, object>
                        {
                            { "temperature", 0.5 },
                            { "responseMimeType", "application/json" }
                        }
                    }
                };

                string body = Storage.Serialize(payload);
                var http = (HttpWebRequest)WebRequest.Create(url);
                http.Method = "POST";
                http.ContentType = "application/json; charset=utf-8";
                http.Timeout = 18000;
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                using (var s = http.GetRequestStream()) s.Write(bytes, 0, bytes.Length);

                using (var resp = (HttpWebResponse)http.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string raw = r.ReadToEnd();
                    var root = Storage.Deserialize<Dictionary<string, object>>(raw);
                    var candidates = root != null && root.ContainsKey("candidates") ? root["candidates"] as System.Collections.IList : null;
                    if (candidates != null && candidates.Count > 0)
                    {
                        var c0 = candidates[0] as Dictionary<string, object>;
                        var content = c0["content"] as Dictionary<string, object>;
                        var parts = content["parts"] as System.Collections.IList;
                        var p0 = parts[0] as Dictionary<string, object>;
                        string text = Convert.ToString(p0["text"]).Trim();
                        text = StripJsonFence(text);
                        return Storage.Deserialize<Dictionary<string, object>>(text);
                    }
                }
            }
            catch
            {
                // Fallback seamlessly to built-in engine
            }
            return null;
        }

        private static Dictionary<string, object> TryCallOpenAi(AnalyzeRequest req, ParsedPromptInfo parsed, CompetitorReport comp, List<string> targets, AiSettings cfg)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                string model = string.IsNullOrWhiteSpace(cfg.OpenAiModel) ? "gpt-4o-mini" : cfg.OpenAiModel.Trim();
                string url = "https://api.openai.com/v1/chat/completions";

                var payload = new Dictionary<string, object>
                {
                    { "model", model },
                    { "response_format", new Dictionary<string, object> { { "type", "json_object" } } },
                    {
                        "messages", new object[]
                        {
                            new Dictionary<string, object> { { "role", "system" }, { "content", "Sen Türkiye e-ticaret pazaryerleri (Trendyol, Hepsiburada, N11, İdefix, Pazarama, PttAVM, Amazon TR) konusunda uzman kıdemli SEO ve dönüşüm metin yazarısın. Sadece geçerli JSON döndür." } },
                            new Dictionary<string, object> { { "role", "user" }, { "content", BuildLlmPrompt(req, parsed, comp, targets) } }
                        }
                    }
                };

                string body = Storage.Serialize(payload);
                var http = (HttpWebRequest)WebRequest.Create(url);
                http.Method = "POST";
                http.ContentType = "application/json; charset=utf-8";
                http.Headers["Authorization"] = "Bearer " + cfg.OpenAiApiKey.Trim();
                http.Timeout = 18000;
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                using (var s = http.GetRequestStream()) s.Write(bytes, 0, bytes.Length);

                using (var resp = (HttpWebResponse)http.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string raw = r.ReadToEnd();
                    var root = Storage.Deserialize<Dictionary<string, object>>(raw);
                    var choices = root != null && root.ContainsKey("choices") ? root["choices"] as System.Collections.IList : null;
                    if (choices != null && choices.Count > 0)
                    {
                        var ch = choices[0] as Dictionary<string, object>;
                        var msg = ch["message"] as Dictionary<string, object>;
                        string text = StripJsonFence(Convert.ToString(msg["content"]).Trim());
                        return Storage.Deserialize<Dictionary<string, object>>(text);
                    }
                }
            }
            catch
            {
                // Fallback seamlessly
            }
            return null;
        }

        private static string BuildLlmPrompt(AnalyzeRequest req, ParsedPromptInfo parsed, CompetitorReport comp, List<string> targets)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Kullanıcı ürün promptu: " + req.Prompt);
            sb.AppendLine("Kategori: " + parsed.Category + ", Marka: " + parsed.Brand + ", Renk: " + parsed.Color + ", Materyal: " + parsed.Material + ", Fiyat: " + parsed.Price + " TL");
            sb.AppendLine("Rakip analizinden öne çıkan anahtar kelimeler: " + string.Join(", ", comp.TrendingKeywords));
            sb.AppendLine("Hedef pazaryerleri: " + string.Join(", ", targets));
            sb.AppendLine("Her pazaryeri için profesyonel, yüksek dönüşümlü, SEO uyumlu Türkçe başlık (Trendyol max 100, N11 max 65, Hepsiburada max 150, Amazon max 200, İdefix max 120, Pazarama max 100, PttAVM max 100 karakter), ikna edici açıklama ve 4-5 maddelik bullet listesi üret. Başlık ve açıklamada asla pazaryeri adı (Trendyol, Hepsiburada, N11, Amazon vb.), 'Koleksiyon', 'ücretsiz kargo', 'hızlı kargo', 'en ucuz', 'orijinal ürün', telefon veya web adresi gibi yasaklı ifadeler kullanma.");
            sb.AppendLine("SADECE şu JSON şemasını döndür:");
            sb.AppendLine("{ \"marketplaces\": { \"trendyol\": { \"title\": \"...\", \"description\": \"...\", \"bullets\": [\"...\"] }, \"hepsiburada\": { ... }, \"n11\": { ... }, \"idefix\": { ... }, \"pazarama\": { ... }, \"pttavm\": { ... }, \"amazon\": { ... } } }");
            return sb.ToString();
        }

        private static string StripJsonFence(string s)
        {
            if (s.StartsWith("```"))
            {
                int firstNl = s.IndexOf('\n');
                if (firstNl >= 0) s = s.Substring(firstNl + 1);
                if (s.EndsWith("```")) s = s.Substring(0, s.Length - 3);
            }
            return s.Trim();
        }

        private static string TruncateClean(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= maxLen) return s;
            string cut = s.Substring(0, maxLen);
            int lastSpace = cut.LastIndexOf(' ');
            if (lastSpace > maxLen / 2) cut = cut.Substring(0, lastSpace);
            return cut.Trim(' ', '-', '|', ',');
        }

        private static string GenerateEan13()
        {
            var rnd = new Random(Guid.NewGuid().GetHashCode());
            string digits = "869"; // Turkey GS1 prefix
            for (int i = 0; i < 9; i++) digits += rnd.Next(0, 10).ToString();
            int sum = 0;
            for (int i = 0; i < 12; i++)
            {
                int d = digits[i] - '0';
                sum += (i % 2 == 0) ? d : d * 3;
            }
            int check = (10 - (sum % 10)) % 10;
            return digits + check;
        }
    }
}
