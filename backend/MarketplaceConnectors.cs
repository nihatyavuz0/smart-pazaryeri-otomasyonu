using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SmartMarketplace
{
    public class ConnectorResult
    {
        public bool Success { get; set; }
        public bool IsSimulated { get; set; }
        public string RemoteId { get; set; }
        public string Message { get; set; }
        public string RawDetails { get; set; }
    }

    public class TrendyolLeafCategory
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string FullPath { get; set; }
    }

    public static class MarketplaceConnectors
    {
        private static readonly object _tyCatLock = new object();
        private static List<TrendyolLeafCategory> _tyLeafCategories;
        private static DateTime _tyCatLoadedAt = DateTime.MinValue;

        static MarketplaceConnectors()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }

        public static bool HasRequiredCredentials(MarketplaceConfig cfg)
        {
            if (cfg == null || cfg.Credentials == null) return false;
            switch (cfg.Id)
            {
                case "trendyol":
                    return !string.IsNullOrWhiteSpace(GetCred(cfg, "supplierId"))
                        && !string.IsNullOrWhiteSpace(GetCred(cfg, "apiKey"))
                        && !string.IsNullOrWhiteSpace(GetCred(cfg, "apiSecret"));
                case "hepsiburada":
                    return !string.IsNullOrWhiteSpace(GetCred(cfg, "merchantId"))
                        && !string.IsNullOrWhiteSpace(GetCred(cfg, "serviceKey"));
                case "n11":
                    return !string.IsNullOrWhiteSpace(GetCred(cfg, "appKey"))
                        && !string.IsNullOrWhiteSpace(GetCred(cfg, "appSecret"));
                case "idefix":
                    return !string.IsNullOrWhiteSpace(GetCred(cfg, "vendorId"))
                        && !string.IsNullOrWhiteSpace(GetCred(cfg, "apiKey"))
                        && !string.IsNullOrWhiteSpace(GetCred(cfg, "apiSecret"));
                case "pazarama":
                    return !string.IsNullOrWhiteSpace(GetCred(cfg, "clientId"))
                        && !string.IsNullOrWhiteSpace(GetCred(cfg, "clientSecret"));
                case "pttavm":
                    return !string.IsNullOrWhiteSpace(GetCred(cfg, "username"))
                        && !string.IsNullOrWhiteSpace(GetCred(cfg, "password"));
                case "amazon":
                    return !string.IsNullOrWhiteSpace(GetCred(cfg, "sellerId"))
                        && !string.IsNullOrWhiteSpace(GetCred(cfg, "lwaClientId"))
                        && !string.IsNullOrWhiteSpace(GetCred(cfg, "lwaClientSecret"))
                        && !string.IsNullOrWhiteSpace(GetCred(cfg, "refreshToken"));
                default:
                    return false;
            }
        }

        public static ConnectorResult TestConnection(MarketplaceConfig cfg)
        {
            if (cfg.SimulationMode)
            {
                return new ConnectorResult
                {
                    Success = true,
                    IsSimulated = true,
                    Message = cfg.Name + " Test Modunda aktif. Gerçek hesabınıza göndermek için API bilgilerinizi girip 'Canlı API Modu'nu açık tutun."
                };
            }

            try
            {
                switch (cfg.Id)
                {
                    case "trendyol": return TestTrendyol(cfg);
                    case "hepsiburada": return TestHepsiburada(cfg);
                    case "n11": return TestN11(cfg);
                    case "idefix": return TestIdefix(cfg);
                    case "pazarama": return TestPazarama(cfg);
                    case "pttavm": return TestPttAvm(cfg);
                    case "amazon": return TestAmazon(cfg);
                    default:
                        return new ConnectorResult { Success = false, Message = "Bilinmeyen pazaryeri: " + cfg.Id };
                }
            }
            catch (WebException wex)
            {
                string err = ReadWebError(wex);
                return new ConnectorResult { Success = false, Message = cfg.Name + " bağlantı hatası: " + err, RawDetails = err };
            }
            catch (Exception ex)
            {
                return new ConnectorResult { Success = false, Message = cfg.Name + " hata: " + ex.Message, RawDetails = ex.Message };
            }
        }

        public static ConnectorResult PublishProduct(MarketplaceConfig cfg, ProductRecord product, MarketplaceListing listing)
        {
            // Always sanitize brand, title & description against marketplace banned words BEFORE publishing
            product.Brand = ComplianceGuard.CleanBrandName(product.Brand);
            var compCheck = ComplianceGuard.SanitizeListing(listing.Title, listing.Description, listing.MaxTitleLength > 0 ? listing.MaxTitleLength : 100, product.Brand);
            if (!string.IsNullOrWhiteSpace(compCheck.CleanedTitle)) listing.Title = compCheck.CleanedTitle;
            if (!string.IsNullOrWhiteSpace(compCheck.CleanedDescription)) listing.Description = compCheck.CleanedDescription;

            var missingReq = listing.Attributes.Where(a => a.Required && string.IsNullOrWhiteSpace(a.Value)).ToList();
            if (missingReq.Count > 0)
            {
                return new ConnectorResult
                {
                    Success = false,
                    Message = "Eksik zorunlu alanlar: " + string.Join(", ", missingReq.Select(x => x.Label))
                };
            }

            if (cfg.SimulationMode)
            {
                string simId = cfg.Id.ToUpperInvariant().Substring(0, Math.Min(3, cfg.Id.Length)) + "-" + DateTime.Now.ToString("HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4).ToUpperInvariant();
                return new ConnectorResult
                {
                    Success = true,
                    IsSimulated = true,
                    RemoteId = simId,
                    Message = cfg.Name + " (Test Modu) — Yayın simüle edildi. Gerçek hesaba göndermek için API anahtarlarınızı girip Canlı Modu açın.",
                    RawDetails = "Simülasyon ID: " + simId + " | Başlık (" + listing.Title.Length + "/" + listing.MaxTitleLength + " krk) | Stok: " + product.Stock
                };
            }

            try
            {
                switch (cfg.Id)
                {
                    case "trendyol": return PublishTrendyol(cfg, product, listing);
                    case "hepsiburada": return PublishHepsiburada(cfg, product, listing);
                    case "n11": return PublishN11(cfg, product, listing);
                    case "idefix": return PublishIdefix(cfg, product, listing);
                    case "pazarama": return PublishPazarama(cfg, product, listing);
                    case "pttavm": return PublishPttAvm(cfg, product, listing);
                    case "amazon": return PublishAmazon(cfg, product, listing);
                    default:
                        return new ConnectorResult { Success = false, Message = "Desteklenmeyen pazaryeri." };
                }
            }
            catch (WebException wex)
            {
                string err = ReadWebError(wex);
                return new ConnectorResult { Success = false, Message = cfg.Name + " canlı yayın hatası: " + err, RawDetails = err };
            }
            catch (Exception ex)
            {
                return new ConnectorResult { Success = false, Message = cfg.Name + " canlı yayın hatası: " + ex.Message };
            }
        }

        public static ConnectorResult UpdateStockAndPrice(MarketplaceConfig cfg, ProductRecord product, MarketplaceListing listing, int newStock, decimal newPrice)
        {
            if (cfg.SimulationMode)
            {
                return new ConnectorResult
                {
                    Success = true,
                    IsSimulated = true,
                    RemoteId = listing.RemoteId,
                    Message = cfg.Name + " (Test Modu) stok senkronize edildi → " + newStock + " adet, " + newPrice.ToString("N2", new CultureInfo("tr-TR")) + " TL"
                };
            }

            try
            {
                switch (cfg.Id)
                {
                    case "trendyol": return SyncTrendyolStock(cfg, product, newStock, newPrice);
                    case "hepsiburada": return SyncHepsiburadaStock(cfg, product, newStock, newPrice);
                    case "n11": return SyncN11Stock(cfg, product, newStock, newPrice);
                    case "idefix": return SyncIdefixStock(cfg, product, newStock, newPrice);
                    case "pazarama": return SyncPazaramaStock(cfg, product, newStock, newPrice);
                    case "pttavm": return SyncPttAvmStock(cfg, product, newStock, newPrice);
                    case "amazon": return SyncAmazonStock(cfg, product, newStock, newPrice);
                    default:
                        return new ConnectorResult { Success = false, Message = "Bilinmeyen pazaryeri." };
                }
            }
            catch (WebException wex)
            {
                string err = ReadWebError(wex);
                return new ConnectorResult { Success = false, Message = cfg.Name + " stok senkron hatası: " + err, RawDetails = err };
            }
            catch (Exception ex)
            {
                return new ConnectorResult { Success = false, Message = cfg.Name + " stok hatası: " + ex.Message };
            }
        }

        // ==================== TRENDYOL V2 LIVE ENGINE ====================
        public static Dictionary<string, string> BuildTrendyolHeaders(MarketplaceConfig cfg)
        {
            string rawSupplier = GetCred(cfg, "supplierId");
            string ua = GetCred(cfg, "userAgent");
            // Trendyol strictly requires User-Agent to start with "{supplierId} - ..."
            if (string.IsNullOrWhiteSpace(ua) || !ua.StartsWith(rawSupplier))
                ua = rawSupplier + " - SelfIntegration";

            return new Dictionary<string, string>
            {
                { "Authorization", "Basic " + ToBase64(GetCred(cfg, "apiKey") + ":" + GetCred(cfg, "apiSecret")) },
                { "User-Agent", ua },
                { "storeFrontCode", "TR" },
                { "Accept-Language", "tr" }
            };
        }

        private static ConnectorResult TestTrendyol(MarketplaceConfig cfg)
        {
            string supplierId = Uri.EscapeDataString(GetCred(cfg, "supplierId"));
            string apiKey = GetCred(cfg, "apiKey");
            string apiSecret = GetCred(cfg, "apiSecret");
            if (string.IsNullOrWhiteSpace(supplierId) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
                return new ConnectorResult { Success = false, Message = "Trendyol Supplier ID, API Key ve API Secret alanları zorunludur." };

            string url = "https://apigw.trendyol.com/integration/product/sellers/" + supplierId + "/products?page=0&size=1";
            var headers = BuildTrendyolHeaders(cfg);
            string resp = HttpRequest("GET", url, null, "application/json", headers);
            return new ConnectorResult { Success = true, Message = "Trendyol CANLI API bağlantısı doğrulandı! Ürünler doğrudan Trendyol satıcı hesabınıza gönderilecektir.", RawDetails = Trunc(resp, 300) };
        }

        private static ConnectorResult PublishTrendyol(MarketplaceConfig cfg, ProductRecord product, MarketplaceListing listing)
        {
            string supplierId = Uri.EscapeDataString(GetCred(cfg, "supplierId"));
            var headers = BuildTrendyolHeaders(cfg);

            // 1. Ensure publicly accessible HTTPS image URLs (Trendyol rejects empty or localhost URLs)
            string imgErr;
            var publicImages = ImageHostService.EnsurePublicImageUrls(product.Images, out imgErr);
            if (publicImages.Count == 0)
            {
                return new ConnectorResult
                {
                    Success = false,
                    Message = "Trendyol için en az 1 genel erişilebilir HTTPS görsel gerekiyor. Görsel yüklenemedi: " + (string.IsNullOrWhiteSpace(imgErr) ? "Lütfen ürün görseli ekleyin." : imgErr)
                };
            }

            // 2. Resolve real Trendyol Brand ID
            string matchedBrandName;
            int brandId = ResolveTrendyolBrandId(headers, product.Brand, out matchedBrandName);
            if (brandId <= 0)
            {
                return new ConnectorResult { Success = false, Message = "Trendyol marka ID çözümlenemedi. Lütfen Trendyol'da kayıtlı bir marka adı belirtin." };
            }

            // 3. Resolve real Trendyol Leaf Category ID using 4-Stage TrendyolCategoryEngine (User Selected -> Competitor Voting -> Official Tree -> Store History)
            int preferredCatId = 0;
            if (!string.IsNullOrWhiteSpace(listing.CategoryId) && listing.CategoryName != null && listing.CategoryName.Contains(">"))
                int.TryParse(listing.CategoryId, out preferredCatId);

            var catMatch = TrendyolCategoryEngine.ResolveBestCategory(cfg, product.BaseName, listing.Title, product.Prompt, product.Category, preferredCatId);
            if (!catMatch.Success || catMatch.CategoryId <= 0)
            {
                return new ConnectorResult
                {
                    Success = false,
                    Message = "Trendyol iç kategori eşleştirmesi yapılamadı: " + (string.IsNullOrWhiteSpace(catMatch.Error) ? "Kategori ağacı alınamadı." : catMatch.Error)
                };
            }
            listing.CategoryId = catMatch.CategoryId.ToString();
            listing.CategoryName = catMatch.FullPath;

            // 4. Fetch & auto-fill real mandatory category attributes from Trendyol's live API
            var tyAttributes = TrendyolCategoryEngine.BuildTrendyolApiAttributesPayload(cfg, catMatch.CategoryId, product, listing);

            // 5. Fetch seller's real shipment & returning addresses if available
            int? shipmentAddressId = null;
            int? returningAddressId = null;
            TryGetTrendyolAddresses(headers, supplierId, out shipmentAddressId, out returningAddressId);

            // 6. Normalize VAT rate to Trendyol allowed set {0, 1, 10, 20} and ensure listPrice >= salePrice
            int vat = product.VatRate;
            if (vat != 0 && vat != 1 && vat != 10 && vat != 20) vat = 20;
            decimal salePrice = listing.Price > 0 ? listing.Price : (product.Price > 0 ? product.Price : 100m);
            decimal listPrice = product.ListPrice >= salePrice ? product.ListPrice : Math.Round(salePrice * 1.15m, 2);
            string title = listing.Title.Length > 100 ? listing.Title.Substring(0, 100).Trim() : listing.Title;
            double desi = product.Desi > 0 ? product.Desi : 1.0;

            var item = new Dictionary<string, object>
            {
                { "barcode", product.Barcode },
                { "title", title },
                { "productMainId", product.Sku },
                { "brandId", brandId },
                { "categoryId", catMatch.CategoryId },
                { "quantity", Math.Max(0, product.Stock) },
                { "stockCode", product.Sku },
                { "dimensionalWeight", desi },
                { "description", listing.Description },
                { "currencyType", "TRY" },
                { "listPrice", listPrice },
                { "salePrice", salePrice },
                { "vatRate", vat },
                { "cargoCompanyId", 10 },
                { "images", publicImages.Select(u => new Dictionary<string, object> { { "url", u } }).ToArray() },
                { "attributes", tyAttributes.ToArray() }
            };
            if (shipmentAddressId.HasValue) item["shipmentAddressId"] = shipmentAddressId.Value;
            if (returningAddressId.HasValue) item["returningAddressId"] = returningAddressId.Value;

            // 7. Send to Trendyol V2: if already submitted before (revision), use PUT /v2/products directly; otherwise POST with auto-PUT fallback
            string v2Url = "https://apigw.trendyol.com/integration/product/sellers/" + supplierId + "/v2/products";
            string body = Storage.Serialize(new Dictionary<string, object> { { "items", new[] { item } } });
            bool alreadyOnTrendyol = listing.Status == "published" && !string.IsNullOrWhiteSpace(listing.RemoteId) && !listing.RemoteId.StartsWith("TRE-");

            string methodUsed = alreadyOnTrendyol ? "PUT" : "POST";
            string resp = "";
            try
            {
                resp = HttpRequest(methodUsed, v2Url, body, "application/json", headers);
            }
            catch (WebException wex)
            {
                string errText = ReadWebError(wex);
                string fallbackMethod = methodUsed == "PUT" ? "POST" : "PUT";
                if (errText.ToLowerInvariant().Contains("mevcut") || errText.ToLowerInvariant().Contains("exist") || errText.ToLowerInvariant().Contains("duplicate") || errText.Contains("400") || errText.Contains("404"))
                {
                    methodUsed = fallbackMethod;
                    resp = HttpRequest(methodUsed, v2Url, body, "application/json", headers);
                }
                else
                {
                    throw;
                }
            }

            string batchId = ExtractField(resp, "batchRequestId", "");
            if (string.IsNullOrWhiteSpace(batchId))
            {
                return new ConnectorResult
                {
                    Success = false,
                    Message = "Trendyol V2 yanıtında batchRequestId dönmedi.",
                    RawDetails = Trunc(resp, 300)
                };
            }

            // 8. Synchronously poll Trendyol batch-requests endpoint; if POST failed because barcode already exists, auto-revise with PUT!
            string imgSummary = string.Join(", ", publicImages);
            var pollRes = PollTrendyolBatchStatus(headers, supplierId, batchId, catMatch.CategoryName + " (" + methodUsed + ")", matchedBrandName, tyAttributes.Count, publicImages.Count);
            if (!pollRes.Success && methodUsed == "POST" && pollRes.Message != null &&
                (pollRes.Message.ToLowerInvariant().Contains("mevcut") || pollRes.Message.ToLowerInvariant().Contains("exist") || pollRes.Message.ToLowerInvariant().Contains("kayıtlı") || pollRes.Message.ToLowerInvariant().Contains("kayitli") || pollRes.Message.ToLowerInvariant().Contains("barkod")))
            {
                try
                {
                    string putResp = HttpRequest("PUT", v2Url, body, "application/json", headers);
                    string putBatchId = ExtractField(putResp, "batchRequestId", "");
                    if (!string.IsNullOrWhiteSpace(putBatchId))
                    {
                        var putPoll = PollTrendyolBatchStatus(headers, supplierId, putBatchId, catMatch.CategoryName + " (PUT Revize)", matchedBrandName, tyAttributes.Count, publicImages.Count);
                        if (putPoll.Success)
                            putPoll.Message = "Trendyol ürününüz doğrulanmış kalıcı CDN (.jpg) görselleri ve temiz içerikle revize edilip onaya gönderildi! (" + catMatch.CategoryName + ")";
                        putPoll.RawDetails = (putPoll.RawDetails ?? "") + " | Görseller: " + imgSummary;
                        return putPoll;
                    }
                }
                catch { }
            }
            pollRes.RawDetails = (pollRes.RawDetails ?? "") + " | Görseller: " + imgSummary;
            return pollRes;
        }

        public static ConnectorResult CheckTrendyolBatch(MarketplaceConfig cfg, string batchId)
        {
            string supplierId = Uri.EscapeDataString(GetCred(cfg, "supplierId"));
            var headers = BuildTrendyolHeaders(cfg);
            return PollTrendyolBatchStatus(headers, supplierId, batchId, "", "", 0, 0, 1);
        }

        private static ConnectorResult PollTrendyolBatchStatus(
            Dictionary<string, string> headers,
            string supplierId,
            string batchId,
            string catName,
            string brandName,
            int attrCount,
            int imgCount,
            int maxAttempts = 4)
        {
            string batchUrl = "https://apigw.trendyol.com/integration/product/sellers/" + supplierId + "/products/batch-requests/" + Uri.EscapeDataString(batchId);
            string lastRaw = "";

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (attempt > 0 || maxAttempts > 1) Thread.Sleep(1800);
                try
                {
                    lastRaw = HttpRequest("GET", batchUrl, null, "application/json", headers);
                    var root = Storage.Deserialize<Dictionary<string, object>>(lastRaw);
                    if (root == null) continue;

                    string batchStatus = root.ContainsKey("status") ? Convert.ToString(root["status"]) : "";
                    var items = root.ContainsKey("items") ? AsList(root["items"]) : null;
                    if (items != null && items.Count > 0)
                    {
                        var item0 = items[0] as Dictionary<string, object>;
                        string itemStatus = item0 != null && item0.ContainsKey("status") ? Convert.ToString(item0["status"]) : batchStatus;

                        if (string.Equals(itemStatus, "FAILED", StringComparison.OrdinalIgnoreCase))
                        {
                            var reasons = new List<string>();
                            var failList = item0 != null && item0.ContainsKey("failureReasons") ? AsList(item0["failureReasons"]) : null;
                            if (failList != null)
                            {
                                foreach (var r in failList)
                                    reasons.Add(Convert.ToString(r));
                            }
                            string reasonText = reasons.Count > 0 ? string.Join(" | ", reasons) : "Trendyol doğrulama hatası.";
                            return new ConnectorResult
                            {
                                Success = false,
                                RemoteId = batchId,
                                Message = "Trendyol ürünü reddetti: " + reasonText,
                                RawDetails = "Batch: " + batchId + " | Kategori: " + catName + " | Sebep: " + reasonText
                            };
                        }

                        if (string.Equals(itemStatus, "SUCCESS", StringComparison.OrdinalIgnoreCase))
                        {
                            return new ConnectorResult
                            {
                                Success = true,
                                RemoteId = batchId,
                                Message = "Trendyol hesabınıza CANLI olarak yüklendi! (Kategori: " + catName + ", Marka: " + brandName + ")",
                                RawDetails = "Batch: " + batchId + " | Durum: SUCCESS | " + attrCount + " zorunlu özellik, " + imgCount + " görsel doğrulandı."
                            };
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastRaw = ex.Message;
                }
            }

            return new ConnectorResult
            {
                Success = true,
                RemoteId = batchId,
                Message = "Trendyol hesabınıza iletildi (İşleniyor — Batch: " + batchId + "). Trendyol Satıcı Paneli > Ürün > Toplu Ürün İşlemleri ekranında görüntülenebilir.",
                RawDetails = "Batch: " + batchId + " | Kategori: " + catName + " | Marka: " + brandName + " | " + Trunc(lastRaw, 220)
            };
        }

        private static int ResolveTrendyolBrandId(Dictionary<string, string> headers, string preferredBrand, out string matchedName)
        {
            matchedName = preferredBrand ?? "";
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(preferredBrand) && !preferredBrand.Equals("Smart Koleksiyon", StringComparison.OrdinalIgnoreCase))
                candidates.Add(preferredBrand.Trim());
            candidates.Add("Genel Markalar");
            candidates.Add("Diğer");
            candidates.Add("Markasız");

            foreach (var name in candidates)
            {
                try
                {
                    string url = "https://apigw.trendyol.com/integration/product/brands/by-name?name=" + Uri.EscapeDataString(name);
                    string raw = HttpRequest("GET", url, null, "application/json", headers);
                    var arr = Storage.Deserialize<object[]>(raw);
                    if (arr != null && arr.Length > 0)
                    {
                        // Prefer exact case-insensitive match
                        Dictionary<string, object> best = null;
                        foreach (var o in arr)
                        {
                            var d = o as Dictionary<string, object>;
                            if (d == null) continue;
                            if (best == null) best = d;
                            if (d.ContainsKey("name") && string.Equals(Convert.ToString(d["name"]), name, StringComparison.OrdinalIgnoreCase))
                            {
                                best = d;
                                break;
                            }
                        }
                        if (best != null && best.ContainsKey("id"))
                        {
                            matchedName = Convert.ToString(best["name"]);
                            return Convert.ToInt32(best["id"]);
                        }
                    }
                }
                catch { }
            }

            // Last resort: first brand from paginated brands list
            try
            {
                string raw = HttpRequest("GET", "https://apigw.trendyol.com/integration/product/brands?page=0&size=5", null, "application/json", headers);
                var root = Storage.Deserialize<Dictionary<string, object>>(raw);
                var arr = root != null && root.ContainsKey("brands") ? AsList(root["brands"]) : null;
                if (arr != null && arr.Count > 0)
                {
                    var b0 = arr[0] as Dictionary<string, object>;
                    if (b0 != null && b0.ContainsKey("id"))
                    {
                        matchedName = Convert.ToString(b0["name"]);
                        return Convert.ToInt32(b0["id"]);
                    }
                }
            }
            catch { }

            return 0;
        }

        private static TrendyolLeafCategory ResolveTrendyolLeafCategory(
            Dictionary<string, string> headers,
            ProductRecord product,
            MarketplaceListing listing,
            out string matchedSource,
            out string lastError)
        {
            matchedSource = "Trendyol Kategori Ağacı";
            lastError = "";

            // STAGE 1: Live Competitor Analysis on Trendyol itself — find the EXACT categoryId top-selling competitors use!
            string compCategoryNameHint = "";
            var compCat = TryFindTrendyolCompetitorCategory(headers, product, listing, out compCategoryNameHint);
            if (compCat != null && compCat.Id > 0)
            {
                matchedSource = "Canlı Trendyol Rakip Analizi";
                return compCat;
            }

            // STAGE 2: Official Trendyol Seller Category Tree with Turkish Head-Noun + Competitor-Hint Semantic Scoring
            var leaves = LoadTrendyolLeafCategories(headers, out lastError);
            if (leaves == null || leaves.Count == 0) return null;

            return ScoreBestLeafCategory(leaves, product, listing, compCategoryNameHint);
        }

        private static TrendyolLeafCategory TryFindTrendyolCompetitorCategory(
            Dictionary<string, string> headers,
            ProductRecord product,
            MarketplaceListing listing,
            out string categoryNameHint)
        {
            categoryNameHint = "";
            try
            {
                string rawQuery = CleanProductSearchPhrase(product.BaseName, product.Prompt);
                if (string.IsNullOrWhiteSpace(rawQuery)) return null;

                string[] searchEndpoints =
                {
                    "https://public.trendyol.com/discovery-web-searchgw-service/v2/api/infinite-scroll/sr?q=" + Uri.EscapeDataString(rawQuery) + "&pi=1&culture=tr-TR&userGenderId=1&pId=0&scoringAlgorithmId=2&categoryRelevancyEnabled=true&isLegalRequirementConfirmed=false&searchStrategyType=DEFAULT&productStampType=TypeA",
                    "https://apigw.trendyol.com/discovery-web-searchgw-service/v2/api/infinite-scroll/sr?q=" + Uri.EscapeDataString(rawQuery) + "&pi=1&culture=tr-TR"
                };

                string json = "";
                foreach (var ep in searchEndpoints)
                {
                    try
                    {
                        var pubHeaders = new Dictionary<string, string>
                        {
                            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36" },
                            { "Accept", "application/json" }
                        };
                        json = HttpRequest("GET", ep, null, null, pubHeaders);
                        if (!string.IsNullOrWhiteSpace(json) && json.Contains("products")) break;
                    }
                    catch { }
                }

                if (string.IsNullOrWhiteSpace(json)) return null;

                var root = Storage.Deserialize<Dictionary<string, object>>(json);
                var resultObj = root != null && root.ContainsKey("result") ? root["result"] as Dictionary<string, object> : root;
                var prods = resultObj != null && resultObj.ContainsKey("products") ? AsList(resultObj["products"]) : null;
                if (prods == null || prods.Count == 0) return null;

                // Vote across top 15 real competitor products on Trendyol
                var votes = new Dictionary<int, int>();
                var names = new Dictionary<int, string>();
                var hierarchies = new Dictionary<int, string>();

                int inspected = 0;
                foreach (var obj in prods)
                {
                    if (inspected >= 15) break;
                    var pd = obj as Dictionary<string, object>;
                    if (pd == null || !pd.ContainsKey("categoryId")) continue;
                    int cid = 0;
                    try { cid = Convert.ToInt32(pd["categoryId"]); } catch { continue; }
                    if (cid <= 0) continue;

                    inspected++;
                    int weight = (inspected <= 5) ? 3 : 1; // Top 5 best-matching competitors carry higher weight
                    votes[cid] = (votes.ContainsKey(cid) ? votes[cid] : 0) + weight;

                    if (pd.ContainsKey("categoryName") && pd["categoryName"] != null)
                        names[cid] = Convert.ToString(pd["categoryName"]);
                    if (pd.ContainsKey("categoryHierarchy") && pd["categoryHierarchy"] != null)
                        hierarchies[cid] = Convert.ToString(pd["categoryHierarchy"]);
                }

                if (votes.Count == 0) return null;

                // Try top voted competitor categories in order and verify each is a valid Trendyol leaf category via Seller API!
                foreach (var kv in votes.OrderByDescending(x => x.Value).Take(3))
                {
                    int candidateId = kv.Key;
                    string cname = names.ContainsKey(candidateId) ? names[candidateId] : ("Kategori #" + candidateId);
                    string chier = hierarchies.ContainsKey(candidateId) && !string.IsNullOrWhiteSpace(hierarchies[candidateId]) ? hierarchies[candidateId] : cname;
                    if (string.IsNullOrWhiteSpace(categoryNameHint)) categoryNameHint = cname;

                    if (VerifyTrendyolLeafCategoryId(headers, candidateId))
                    {
                        return new TrendyolLeafCategory
                        {
                            Id = candidateId,
                            Name = cname,
                            FullPath = chier
                        };
                    }
                }
            }
            catch { }
            return null;
        }

        private static bool VerifyTrendyolLeafCategoryId(Dictionary<string, string> headers, int categoryId)
        {
            try
            {
                string url = "https://apigw.trendyol.com/integration/product/categories/" + categoryId + "/attributes";
                string raw = HttpRequest("GET", url, null, "application/json", headers);
                return !string.IsNullOrWhiteSpace(raw) && raw.Contains("categoryAttributes");
            }
            catch
            {
                return false;
            }
        }

        private static string CleanProductSearchPhrase(string baseName, string prompt)
        {
            string s = !string.IsNullOrWhiteSpace(baseName) ? baseName : (prompt ?? "");
            s = Regex.Replace(s, @"%\s*\d+", " ");
            s = Regex.Replace(s, @"\b(natural|orijinal|yeni|sezon|firsat|indirim|kaliteli|super|buyuk|kucuk|boy)\b", " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"[^\p{L}\p{N}\s]", " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            var words = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 5) s = string.Join(" ", words.Take(5));
            return s;
        }

        public static TrendyolLeafCategory ScoreBestLeafCategory(List<TrendyolLeafCategory> leaves, ProductRecord product, MarketplaceListing listing, string competitorHint = "")
        {
            string baseNorm = NormalizeTr(product.BaseName ?? "");
            string fullNorm = NormalizeTr((product.BaseName ?? "") + " " + (listing.Title ?? "") + " " + (product.Prompt ?? "") + " " + (product.Category ?? ""));
            string hintNorm = NormalizeTr(competitorHint ?? "");

            // In Turkish product names ("Paslanmaz Çelik Büyük Boy Tencere"), extract significant tokens
            var baseWords = TokenizeTr(baseNorm).ToList();
            var fullTokens = TokenizeTr(fullNorm);
            var hintTokens = TokenizeTr(hintNorm);

            // Words that indicate an accessory/part/toy — penalize leaf categories containing these unless the user's product mentions them!
            string[] accessoryTraps = { "kapak", "kapagi", "tutacak", "tutacagi", "sapi", "yedek", "parca", "oyuncak", "kilif", "kilifi", "aksesuar", "temizleyici", "standi", "askisi", "koruyucu", "cantasi", "kutu", "kutusu", "etiket", "kitap", "defter" };

            TrendyolLeafCategory best = null;
            int bestScore = int.MinValue;

            foreach (var leaf in leaves)
            {
                string normName = NormalizeTr(leaf.Name);
                string normPath = NormalizeTr(leaf.FullPath);
                var nameTokens = TokenizeTr(normName);

                int score = 0;

                // Strong boost if matches live competitor category hint
                if (!string.IsNullOrEmpty(hintNorm))
                {
                    if (normName == hintNorm) score += 120;
                    else if (normName.Contains(hintNorm) || hintNorm.Contains(normName)) score += 65;
                    foreach (var ht in hintTokens)
                    {
                        if (ht.Length >= 3 && nameTokens.Contains(ht)) score += 30;
                    }
                }

                // Exact leaf category name appears inside product base name (e.g. leaf "Tencere" in "paslanmaz celik buyuk boy tencere")
                if (!string.IsNullOrEmpty(normName) && baseNorm.Contains(normName))
                    score += 80 + normName.Length * 2;
                else if (!string.IsNullOrEmpty(normName) && fullNorm.Contains(normName))
                    score += 45 + normName.Length;

                // Token matching between leaf category name and product
                int matchedNameTokens = 0;
                foreach (var nt in nameTokens)
                {
                    if (nt.Length < 3) continue;
                    if (baseWords.Contains(nt)) { score += 35; matchedNameTokens++; }
                    else if (fullTokens.Contains(nt)) { score += 18; matchedNameTokens++; }
                    else if (fullNorm.Contains(nt)) { score += 9; matchedNameTokens++; }
                }

                // Bonus if ALL words in the leaf category name matched (prefers pure "Tencere" / "Çelik Tencere" over "Tencere & Tava Seti Kapağı")
                if (nameTokens.Count > 0 && matchedNameTokens == nameTokens.Count)
                    score += 40;
                else if (nameTokens.Count > matchedNameTokens)
                    score -= (nameTokens.Count - matchedNameTokens) * 8;

                // Context boost from parent path
                foreach (var qt in fullTokens)
                {
                    if (qt.Length >= 3 && normPath.Contains(qt)) score += 4;
                }

                // Trap penalty: avoid accessory/part/toy leaf categories unless product itself asks for them
                foreach (var trap in accessoryTraps)
                {
                    if ((normName.Contains(trap) || normPath.Contains("oyuncak")) && !fullNorm.Contains(trap) && !fullNorm.Contains("oyuncak"))
                        score -= 70;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = leaf;
                }
            }

            return best ?? leaves[0];
        }

        private static List<TrendyolLeafCategory> LoadTrendyolLeafCategories(Dictionary<string, string> headers, out string lastError)
        {
            lastError = "";
            lock (_tyCatLock)
            {
                if (_tyLeafCategories != null && _tyLeafCategories.Count > 0 && (DateTime.Now - _tyCatLoadedAt).TotalHours < 24)
                    return _tyLeafCategories;

                string cacheFile = Path.Combine(Storage.DataDir, "trendyol_categories_cache.json");
                if (File.Exists(cacheFile) && (DateTime.Now - File.GetLastWriteTime(cacheFile)).TotalHours < 24)
                {
                    try
                    {
                        var cached = Storage.Deserialize<List<TrendyolLeafCategory>>(File.ReadAllText(cacheFile, Encoding.UTF8));
                        if (cached != null && cached.Count > 0)
                        {
                            _tyLeafCategories = cached;
                            _tyCatLoadedAt = DateTime.Now;
                            return _tyLeafCategories;
                        }
                    }
                    catch { }
                }

                string raw = "";
                string[] endpoints =
                {
                    "https://apigw.trendyol.com/integration/product/product-categories",
                    "https://api.trendyol.com/sapigw/product-categories"
                };

                foreach (var ep in endpoints)
                {
                    try
                    {
                        raw = HttpRequest("GET", ep, null, "application/json", headers);
                        if (!string.IsNullOrWhiteSpace(raw)) break;
                    }
                    catch (WebException wex)
                    {
                        lastError = ReadWebError(wex);
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                    }
                }

                if (string.IsNullOrWhiteSpace(raw))
                {
                    if (string.IsNullOrWhiteSpace(lastError)) lastError = "Trendyol kategori servisi boş yanıt döndürdü.";
                    return null;
                }

                var list = new List<TrendyolLeafCategory>();
                try
                {
                    object parsed = Storage.Deserialize<object>(raw);
                    var dict = parsed as Dictionary<string, object>;
                    if (dict != null)
                    {
                        if (dict.ContainsKey("categories"))
                        {
                            var cats = AsList(dict["categories"]);
                            if (cats != null) FlattenTrendyolCategories(cats, "", list);
                        }
                        else if (dict.ContainsKey("subCategories"))
                        {
                            FlattenTrendyolCategories(new List<object> { dict }, "", list);
                        }
                    }
                    else
                    {
                        var topArr = AsList(parsed);
                        if (topArr != null) FlattenTrendyolCategories(topArr, "", list);
                    }
                }
                catch (Exception ex)
                {
                    lastError = "Kategori JSON ayrıştırma hatası: " + ex.Message;
                }

                if (list.Count > 0)
                {
                    _tyLeafCategories = list;
                    _tyCatLoadedAt = DateTime.Now;
                    try { File.WriteAllText(cacheFile, Storage.Serialize(list), new UTF8Encoding(false)); } catch { }
                }
                else if (string.IsNullOrWhiteSpace(lastError))
                {
                    lastError = "Trendyol kategori yanıtında yaprak kategori bulunamadı (Yanıt başı: " + Trunc(raw, 120) + ").";
                }
                return _tyLeafCategories;
            }
        }

        private static void FlattenTrendyolCategories(List<object> nodes, string parentPath, List<TrendyolLeafCategory> leaves)
        {
            foreach (var n in nodes)
            {
                var d = n as Dictionary<string, object>;
                if (d == null || !d.ContainsKey("id") || !d.ContainsKey("name")) continue;
                int id = Convert.ToInt32(d["id"]);
                string name = Convert.ToString(d["name"]);
                string fullPath = string.IsNullOrEmpty(parentPath) ? name : (parentPath + " > " + name);
                var subs = d.ContainsKey("subCategories") ? AsList(d["subCategories"]) : null;
                if (subs == null || subs.Count == 0)
                {
                    leaves.Add(new TrendyolLeafCategory { Id = id, Name = name, FullPath = fullPath });
                }
                else
                {
                    FlattenTrendyolCategories(subs, fullPath, leaves);
                }
            }
        }

        private static List<Dictionary<string, object>> BuildTrendyolCategoryAttributes(Dictionary<string, string> headers, int categoryId, ProductRecord product, MarketplaceListing listing)
        {
            var outAttrs = new List<Dictionary<string, object>>();
            try
            {
                string url = "https://apigw.trendyol.com/integration/product/categories/" + categoryId + "/attributes";
                string raw = HttpRequest("GET", url, null, "application/json", headers);
                var root = Storage.Deserialize<Dictionary<string, object>>(raw);
                var catAttrs = root != null && root.ContainsKey("categoryAttributes") ? AsList(root["categoryAttributes"]) : null;
                if (catAttrs == null) return outAttrs;

                string fullText = NormalizeTr(
                    (product.BaseName ?? "") + " " +
                    (listing.Title ?? "") + " " +
                    (product.Prompt ?? "") + " " +
                    string.Join(" ", listing.Attributes.Select(a => a.Label + " " + a.Value))
                );
                var textTokens = TokenizeTr(fullText);

                foreach (var obj in catAttrs)
                {
                    var ca = obj as Dictionary<string, object>;
                    if (ca == null) continue;
                    bool required = ca.ContainsKey("required") && Convert.ToBoolean(ca["required"]);
                    bool varianter = ca.ContainsKey("varianter") && Convert.ToBoolean(ca["varianter"]);
                    if (!required && !varianter) continue;

                    var attrObj = ca.ContainsKey("attribute") ? ca["attribute"] as Dictionary<string, object> : null;
                    if (attrObj == null || !attrObj.ContainsKey("id")) continue;
                    int attrId = Convert.ToInt32(attrObj["id"]);
                    string attrName = attrObj.ContainsKey("name") ? Convert.ToString(attrObj["name"]) : "";
                    bool allowCustom = ca.ContainsKey("allowCustom") && Convert.ToBoolean(ca["allowCustom"]);
                    var values = ca.ContainsKey("attributeValues") ? AsList(ca["attributeValues"]) : null;

                    if (allowCustom || values == null || values.Count == 0)
                    {
                        string customVal = ResolveCustomAttributeValue(attrName, product, listing);
                        outAttrs.Add(new Dictionary<string, object>
                        {
                            { "attributeId", attrId },
                            { "customAttributeValue", customVal }
                        });
                    }
                    else
                    {
                        int bestValId = PickBestAttributeValueId(values, fullText, textTokens, attrName);
                        if (bestValId > 0)
                        {
                            outAttrs.Add(new Dictionary<string, object>
                            {
                                { "attributeId", attrId },
                                { "attributeValueId", bestValId }
                            });
                        }
                    }
                }
            }
            catch { }
            return outAttrs;
        }

        private static string ResolveCustomAttributeValue(string attrName, ProductRecord product, MarketplaceListing listing)
        {
            string norm = NormalizeTr(attrName);
            foreach (var a in listing.Attributes)
            {
                if (!string.IsNullOrWhiteSpace(a.Value) && (NormalizeTr(a.Label).Contains(norm) || norm.Contains(NormalizeTr(a.Label))))
                    return a.Value;
            }
            if (norm.Contains("renk")) return "Standart";
            if (norm.Contains("mensei") || norm.Contains("ulke")) return "TR";
            if (norm.Contains("materyal") || norm.Contains("malzeme")) return "Paslanmaz Çelik";
            if (norm.Contains("garanti")) return "24 Ay";
            if (norm.Contains("boyut") || norm.Contains("ebat") || norm.Contains("beden")) return "Standart";
            return "Standart";
        }

        private static int PickBestAttributeValueId(List<object> values, string fullText, HashSet<string> textTokens, string attrName)
        {
            int bestId = 0;
            int bestScore = -1;
            int fallbackId = 0;
            int neutralId = 0;
            string[] neutrals = { "standart", "belirtilmemis", "cok renkli", "diger", "tek ebat", "tr", "turkiye", "1", "2 yil", "yok", "unisex" };

            foreach (var v in values)
            {
                var vd = v as Dictionary<string, object>;
                if (vd == null || !vd.ContainsKey("id")) continue;
                int id = Convert.ToInt32(vd["id"]);
                if (fallbackId == 0) fallbackId = id;
                string vName = vd.ContainsKey("name") ? Convert.ToString(vd["name"]) : "";
                string normV = NormalizeTr(vName);
                if (string.IsNullOrEmpty(normV)) continue;

                if (neutralId == 0 && neutrals.Any(n => normV == n || normV.Contains(n)))
                    neutralId = id;

                int score = 0;
                if (normV.Length >= 2 && fullText.Contains(normV))
                    score += 20 + normV.Length;

                foreach (var tok in TokenizeTr(normV))
                {
                    if (tok.Length >= 2 && textTokens.Contains(tok)) score += 12;
                }

                if (score > bestScore && score > 0)
                {
                    bestScore = score;
                    bestId = id;
                }
            }

            if (bestId > 0) return bestId;
            if (neutralId > 0) return neutralId;
            return fallbackId;
        }

        private static void TryGetTrendyolAddresses(Dictionary<string, string> headers, string supplierId, out int? shipmentId, out int? returningId)
        {
            shipmentId = null;
            returningId = null;
            try
            {
                string url = "https://apigw.trendyol.com/integration/sellers/" + supplierId + "/addresses";
                string raw = HttpRequest("GET", url, null, "application/json", headers);
                var root = Storage.Deserialize<Dictionary<string, object>>(raw);
                if (root == null) return;

                if (root.ContainsKey("defaultShipmentAddress") && root["defaultShipmentAddress"] is Dictionary<string, object>)
                {
                    var d = (Dictionary<string, object>)root["defaultShipmentAddress"];
                    if (d.ContainsKey("id")) shipmentId = Convert.ToInt32(d["id"]);
                }
                if (root.ContainsKey("defaultReturningAddress") && root["defaultReturningAddress"] is Dictionary<string, object>)
                {
                    var d = (Dictionary<string, object>)root["defaultReturningAddress"];
                    if (d.ContainsKey("id")) returningId = Convert.ToInt32(d["id"]);
                }
                var addrList = root.ContainsKey("supplierAddresses") ? AsList(root["supplierAddresses"]) : null;
                if ((!shipmentId.HasValue || !returningId.HasValue) && addrList != null)
                {
                    foreach (var o in addrList)
                    {
                        var d = o as Dictionary<string, object>;
                        if (d == null || !d.ContainsKey("id")) continue;
                        int id = Convert.ToInt32(d["id"]);
                        if (!shipmentId.HasValue && d.ContainsKey("isShipmentAddress") && Convert.ToBoolean(d["isShipmentAddress"]))
                            shipmentId = id;
                        if (!returningId.HasValue && d.ContainsKey("isReturningAddress") && Convert.ToBoolean(d["isReturningAddress"]))
                            returningId = id;
                    }
                }
            }
            catch { }
        }

        private static ConnectorResult SyncTrendyolStock(MarketplaceConfig cfg, ProductRecord product, int newStock, decimal newPrice)
        {
            string supplierId = Uri.EscapeDataString(GetCred(cfg, "supplierId"));
            string url = "https://apigw.trendyol.com/integration/inventory/sellers/" + supplierId + "/products/price-and-inventory";
            var headers = BuildTrendyolHeaders(cfg);
            decimal salePrice = newPrice > 0 ? newPrice : product.Price;
            decimal listPrice = Math.Max(salePrice, Math.Round(salePrice * 1.15m, 2));
            var payload = new Dictionary<string, object>
            {
                {
                    "items", new[]
                    {
                        new Dictionary<string, object>
                        {
                            { "barcode", product.Barcode },
                            { "quantity", newStock },
                            { "salePrice", salePrice },
                            { "listPrice", listPrice }
                        }
                    }
                }
            };
            string resp = HttpRequest("POST", url, Storage.Serialize(payload), "application/json", headers);
            return new ConnectorResult { Success = true, Message = "Trendyol CANLI stok & fiyat güncellendi (" + newStock + " adet).", RawDetails = Trunc(resp, 250) };
        }

        // ==================== HEPSİBURADA ====================
        private static ConnectorResult TestHepsiburada(MarketplaceConfig cfg)
        {
            string merchantId = GetCred(cfg, "merchantId");
            string serviceKey = GetCred(cfg, "serviceKey");
            if (string.IsNullOrWhiteSpace(merchantId) || string.IsNullOrWhiteSpace(serviceKey))
                return new ConnectorResult { Success = false, Message = "Hepsiburada Merchant ID ve Service Key zorunludur." };

            string user = string.IsNullOrWhiteSpace(GetCred(cfg, "username")) ? merchantId : GetCred(cfg, "username");
            string url = "https://listing-external.hepsiburada.com/listings/merchantid/" + Uri.EscapeDataString(merchantId) + "?offset=0&limit=1";
            var headers = new Dictionary<string, string>
            {
                { "Authorization", "Basic " + ToBase64(user + ":" + serviceKey) },
                { "User-Agent", "SmartOtomasyon_Integration" }
            };
            string resp = HttpRequest("GET", url, null, "application/json", headers);
            return new ConnectorResult { Success = true, Message = "Hepsiburada CANLI API bağlantısı doğrulandı.", RawDetails = Trunc(resp, 300) };
        }

        private static ConnectorResult PublishHepsiburada(MarketplaceConfig cfg, ProductRecord product, MarketplaceListing listing)
        {
            string merchantId = GetCred(cfg, "merchantId");
            string user = string.IsNullOrWhiteSpace(GetCred(cfg, "username")) ? merchantId : GetCred(cfg, "username");
            string imgErr;
            var publicImages = ImageHostService.EnsurePublicImageUrls(product.Images, out imgErr);

            string url = "https://mpop.hepsiburada.com/product/api/products/import";
            var headers = new Dictionary<string, string>
            {
                { "Authorization", "Basic " + ToBase64(user + ":" + GetCred(cfg, "serviceKey")) },
                { "User-Agent", "SmartOtomasyon_Integration" }
            };
            var attrs = new Dictionary<string, object>
            {
                { "merchantSku", product.Sku },
                { "VaryantGroupID", product.Sku },
                { "Barcode", product.Barcode },
                { "UrunAdi", listing.Title },
                { "UrunAciklamasi", listing.Description },
                { "Marka", product.Brand },
                { "price", listing.Price.ToString("0.00", new CultureInfo("tr-TR")) },
                { "stock", product.Stock.ToString() },
                { "tax_vat_rate", product.VatRate.ToString() },
                { "kg", product.Desi.ToString("0.#", CultureInfo.InvariantCulture) }
            };
            for (int i = 0; i < Math.Min(5, publicImages.Count); i++)
                attrs["Image" + (i + 1)] = publicImages[i];
            foreach (var a in listing.Attributes)
            {
                if (!attrs.ContainsKey(a.Key)) attrs[a.Key] = a.Value;
            }
            var payload = new[] { new Dictionary<string, object> { { "categoryId", ParseIntSafe(listing.CategoryId, 60000001) }, { "merchant", merchantId }, { "attributes", attrs } } };
            string resp = HttpRequest("POST", url, Storage.Serialize(payload), "application/json", headers);
            return new ConnectorResult { Success = true, RemoteId = ExtractField(resp, "trackingId", "HB-" + product.Sku), Message = "Hepsiburada CANLI API'ye gönderildi.", RawDetails = Trunc(resp, 300) };
        }

        private static ConnectorResult SyncHepsiburadaStock(MarketplaceConfig cfg, ProductRecord product, int newStock, decimal newPrice)
        {
            string merchantId = GetCred(cfg, "merchantId");
            string user = string.IsNullOrWhiteSpace(GetCred(cfg, "username")) ? merchantId : GetCred(cfg, "username");
            string url = "https://listing-external.hepsiburada.com/listings/merchantid/" + Uri.EscapeDataString(merchantId) + "/inventory-uploads";
            var headers = new Dictionary<string, string>
            {
                { "Authorization", "Basic " + ToBase64(user + ":" + GetCred(cfg, "serviceKey")) },
                { "User-Agent", "SmartOtomasyon_Integration" }
            };
            string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><listings><listing><MerchantSku>" + EscapeXml(product.Sku) + "</MerchantSku><Price>" + newPrice.ToString("0.00", new CultureInfo("tr-TR")) + "</Price><AvailableStock>" + newStock + "</AvailableStock><DispatchTime>2</DispatchTime></listing></listings>";
            string resp = HttpRequest("POST", url, xml, "application/xml", headers);
            return new ConnectorResult { Success = true, Message = "Hepsiburada CANLI stok güncellendi (" + newStock + " adet).", RawDetails = Trunc(resp, 250) };
        }

        // ==================== N11 ====================
        private static ConnectorResult TestN11(MarketplaceConfig cfg)
        {
            string appKey = GetCred(cfg, "appKey");
            string appSecret = GetCred(cfg, "appSecret");
            if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(appSecret))
                return new ConnectorResult { Success = false, Message = "N11 App Key ve App Secret zorunludur." };

            string url = "https://api.n11.com/ms/product-query?page=0&size=1";
            var headers = new Dictionary<string, string> { { "appkey", appKey }, { "appsecret", appSecret } };
            string resp = HttpRequest("GET", url, null, "application/json", headers);
            return new ConnectorResult { Success = true, Message = "N11 CANLI API bağlantısı doğrulandı.", RawDetails = Trunc(resp, 300) };
        }

        private static ConnectorResult PublishN11(MarketplaceConfig cfg, ProductRecord product, MarketplaceListing listing)
        {
            string imgErr;
            var publicImages = ImageHostService.EnsurePublicImageUrls(product.Images, out imgErr);
            string url = "https://api.n11.com/ms/product/tasks/product-create";
            var headers = new Dictionary<string, string> { { "appkey", GetCred(cfg, "appKey") }, { "appsecret", GetCred(cfg, "appSecret") } };
            var payload = new Dictionary<string, object>
            {
                {
                    "payload", new Dictionary<string, object>
                    {
                        { "integrator", "SmartOtomasyon" },
                        {
                            "skus", new[]
                            {
                                new Dictionary<string, object>
                                {
                                    { "title", listing.Title },
                                    { "description", listing.Description },
                                    { "categoryId", ParseIntSafe(listing.CategoryId, 1000001) },
                                    { "currencyType", "TL" },
                                    { "productMainId", product.Sku },
                                    { "stockCode", product.Sku },
                                    { "barcode", product.Barcode },
                                    { "quantity", product.Stock },
                                    { "salePrice", listing.Price },
                                    { "listPrice", Math.Max(listing.Price, product.ListPrice) },
                                    { "vatRate", product.VatRate },
                                    { "preparingDay", 2 },
                                    { "shipmentTemplate", string.IsNullOrWhiteSpace(GetCred(cfg, "shipmentTemplate")) ? "Standart Kargo" : GetCred(cfg, "shipmentTemplate") },
                                    { "images", publicImages.Select((u, idx) => new Dictionary<string, object> { { "url", u }, { "order", idx + 1 } }).ToArray() }
                                }
                            }
                        }
                    }
                }
            };
            string resp = HttpRequest("POST", url, Storage.Serialize(payload), "application/json", headers);
            return new ConnectorResult { Success = true, RemoteId = ExtractField(resp, "id", "N11-" + product.Sku), Message = "N11 CANLI API'ye gönderildi.", RawDetails = Trunc(resp, 300) };
        }

        private static ConnectorResult SyncN11Stock(MarketplaceConfig cfg, ProductRecord product, int newStock, decimal newPrice)
        {
            string url = "https://api.n11.com/ms/product/tasks/price-stock-update";
            var headers = new Dictionary<string, string> { { "appkey", GetCred(cfg, "appKey") }, { "appsecret", GetCred(cfg, "appSecret") } };
            var payload = new Dictionary<string, object>
            {
                {
                    "payload", new Dictionary<string, object>
                    {
                        { "integrator", "SmartOtomasyon" },
                        {
                            "skus", new[]
                            {
                                new Dictionary<string, object>
                                {
                                    { "stockCode", product.Sku },
                                    { "quantity", newStock },
                                    { "salePrice", newPrice },
                                    { "listPrice", Math.Round(newPrice * 1.15m, 2) },
                                    { "currencyType", "TL" }
                                }
                            }
                        }
                    }
                }
            };
            string resp = HttpRequest("POST", url, Storage.Serialize(payload), "application/json", headers);
            return new ConnectorResult { Success = true, Message = "N11 CANLI stok güncellendi (" + newStock + " adet).", RawDetails = Trunc(resp, 250) };
        }

        // ==================== İDEFİX ====================
        private static ConnectorResult TestIdefix(MarketplaceConfig cfg)
        {
            string vendorId = Uri.EscapeDataString(GetCred(cfg, "vendorId"));
            string apiKey = GetCred(cfg, "apiKey");
            string apiSecret = GetCred(cfg, "apiSecret");
            if (string.IsNullOrWhiteSpace(vendorId) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
                return new ConnectorResult { Success = false, Message = "İdefix Vendor ID, API Key ve API Secret zorunludur." };

            string url = "https://merchantapi.idefix.com/pim/pool/" + vendorId + "/list?page=1&limit=1";
            var headers = new Dictionary<string, string> { { "X-API-KEY", ToBase64(apiKey + ":" + apiSecret) } };
            string resp = HttpRequest("GET", url, null, "application/json", headers);
            return new ConnectorResult { Success = true, Message = "İdefix CANLI API bağlantısı doğrulandı.", RawDetails = Trunc(resp, 300) };
        }

        private static ConnectorResult PublishIdefix(MarketplaceConfig cfg, ProductRecord product, MarketplaceListing listing)
        {
            string vendorId = Uri.EscapeDataString(GetCred(cfg, "vendorId"));
            string imgErr;
            var publicImages = ImageHostService.EnsurePublicImageUrls(product.Images, out imgErr);
            string url = "https://merchantapi.idefix.com/pim/pool/" + vendorId + "/create";
            var headers = new Dictionary<string, string> { { "X-API-KEY", ToBase64(GetCred(cfg, "apiKey") + ":" + GetCred(cfg, "apiSecret")) } };
            var payload = new Dictionary<string, object>
            {
                {
                    "products", new[]
                    {
                        new Dictionary<string, object>
                        {
                            { "barcode", product.Barcode },
                            { "title", listing.Title },
                            { "productMainId", product.Sku },
                            { "vendorStockCode", product.Sku },
                            { "categoryId", ParseIntSafe(listing.CategoryId, 1001) },
                            { "inventoryQuantity", product.Stock },
                            { "price", listing.Price },
                            { "comparePrice", Math.Max(listing.Price, product.ListPrice) },
                            { "vatRate", product.VatRate },
                            { "deliveryDuration", 2 },
                            { "description", listing.Description },
                            { "images", publicImages.Select(u => new Dictionary<string, object> { { "url", u } }).ToArray() }
                        }
                    }
                }
            };
            string resp = HttpRequest("POST", url, Storage.Serialize(payload), "application/json", headers);
            return new ConnectorResult { Success = true, RemoteId = ExtractField(resp, "batchRequestId", "IDF-" + product.Sku), Message = "İdefix CANLI API'ye gönderildi.", RawDetails = Trunc(resp, 300) };
        }

        private static ConnectorResult SyncIdefixStock(MarketplaceConfig cfg, ProductRecord product, int newStock, decimal newPrice)
        {
            string vendorId = Uri.EscapeDataString(GetCred(cfg, "vendorId"));
            string url = "https://merchantapi.idefix.com/pim/catalog/" + vendorId + "/inventory-upload";
            var headers = new Dictionary<string, string> { { "X-API-KEY", ToBase64(GetCred(cfg, "apiKey") + ":" + GetCred(cfg, "apiSecret")) } };
            var payload = new Dictionary<string, object>
            {
                {
                    "items", new[]
                    {
                        new Dictionary<string, object>
                        {
                            { "barcode", product.Barcode },
                            { "price", newPrice },
                            { "comparePrice", Math.Round(newPrice * 1.15m, 2) },
                            { "inventoryQuantity", newStock }
                        }
                    }
                }
            };
            string resp = HttpRequest("POST", url, Storage.Serialize(payload), "application/json", headers);
            return new ConnectorResult { Success = true, Message = "İdefix CANLI stok güncellendi (" + newStock + " adet).", RawDetails = Trunc(resp, 250) };
        }

        // ==================== PAZARAMA ====================
        private static string GetPazaramaToken(MarketplaceConfig cfg)
        {
            string cid = GetCred(cfg, "clientId");
            string csec = GetCred(cfg, "clientSecret");
            var headers = new Dictionary<string, string> { { "Authorization", "Basic " + ToBase64(cid + ":" + csec) } };
            string resp = HttpRequest("POST", "https://isortagimgiris.pazarama.com/connect/token", "grant_type=client_credentials&scope=merchantgatewayapi.fullaccess", "application/x-www-form-urlencoded", headers);
            var obj = Storage.Deserialize<Dictionary<string, object>>(resp);
            return obj != null && obj.ContainsKey("access_token") ? Convert.ToString(obj["access_token"]) : "";
        }

        private static ConnectorResult TestPazarama(MarketplaceConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(GetCred(cfg, "clientId")) || string.IsNullOrWhiteSpace(GetCred(cfg, "clientSecret")))
                return new ConnectorResult { Success = false, Message = "Pazarama Client ID ve Client Secret zorunludur." };
            string token = GetPazaramaToken(cfg);
            if (string.IsNullOrWhiteSpace(token))
                return new ConnectorResult { Success = false, Message = "Pazarama OAuth2 token alınamadı." };
            return new ConnectorResult { Success = true, Message = "Pazarama OAuth2 CANLI bağlantısı doğrulandı." };
        }

        private static ConnectorResult PublishPazarama(MarketplaceConfig cfg, ProductRecord product, MarketplaceListing listing)
        {
            string token = GetPazaramaToken(cfg);
            string imgErr;
            var publicImages = ImageHostService.EnsurePublicImageUrls(product.Images, out imgErr);
            var headers = new Dictionary<string, string> { { "Authorization", "Bearer " + token } };
            var payload = new Dictionary<string, object>
            {
                {
                    "products", new[]
                    {
                        new Dictionary<string, object>
                        {
                            { "code", product.Barcode },
                            { "groupCode", product.Sku },
                            { "name", listing.Title },
                            { "displayName", listing.Title },
                            { "description", listing.Description },
                            { "stockCount", product.Stock },
                            { "stockCode", product.Sku },
                            { "listPrice", Math.Max(listing.Price, product.ListPrice) },
                            { "salePrice", listing.Price },
                            { "vatRate", product.VatRate },
                            { "desi", product.Desi },
                            { "images", publicImages.Select(u => new Dictionary<string, object> { { "imageurl", u } }).ToArray() }
                        }
                    }
                }
            };
            string resp = HttpRequest("POST", "https://isortagimapi.pazarama.com/product/create", Storage.Serialize(payload), "application/json", headers);
            return new ConnectorResult { Success = true, RemoteId = ExtractField(resp, "batchRequestId", "PZR-" + product.Sku), Message = "Pazarama CANLI API'ye gönderildi.", RawDetails = Trunc(resp, 300) };
        }

        private static ConnectorResult SyncPazaramaStock(MarketplaceConfig cfg, ProductRecord product, int newStock, decimal newPrice)
        {
            string token = GetPazaramaToken(cfg);
            var headers = new Dictionary<string, string> { { "Authorization", "Bearer " + token } };
            var stockPayload = new Dictionary<string, object> { { "items", new[] { new Dictionary<string, object> { { "code", product.Barcode }, { "stockCount", newStock } } } } };
            string resp = HttpRequest("POST", "https://isortagimapi.pazarama.com/product/updateStock-v2", Storage.Serialize(stockPayload), "application/json", headers);
            return new ConnectorResult { Success = true, Message = "Pazarama CANLI stok güncellendi (" + newStock + " adet).", RawDetails = Trunc(resp, 250) };
        }

        // ==================== PTTAVM ====================
        private static ConnectorResult TestPttAvm(MarketplaceConfig cfg)
        {
            string user = GetCred(cfg, "username");
            string pass = GetCred(cfg, "password");
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
                return new ConnectorResult { Success = false, Message = "PttAVM Kullanıcı Adı ve Şifre/API Key zorunludur." };

            string soap = BuildPttSoapEnvelope(user, pass, "<tem:GetVersion/>");
            var headers = new Dictionary<string, string> { { "SOAPAction", "\"http://tempuri.org/IService/GetVersion\"" } };
            string resp = HttpRequest("POST", "https://ws.pttavm.com:93/service.svc", soap, "text/xml; charset=utf-8", headers);
            return new ConnectorResult { Success = true, Message = "PttAVM CANLI SOAP bağlantısı doğrulandı.", RawDetails = Trunc(resp, 250) };
        }

        private static ConnectorResult PublishPttAvm(MarketplaceConfig cfg, ProductRecord product, MarketplaceListing listing)
        {
            string inner = "<tem:StokGuncelleV2><tem:item>"
                + "<ptt:Barkod>" + EscapeXml(product.Barcode) + "</ptt:Barkod>"
                + "<ptt:UrunAdi>" + EscapeXml(listing.Title) + "</ptt:UrunAdi>"
                + "<ptt:Aciklama>" + EscapeXml(listing.Description) + "</ptt:Aciklama>"
                + "<ptt:Miktar>" + product.Stock + "</ptt:Miktar>"
                + "<ptt:KDVli>" + listing.Price.ToString("0.00", CultureInfo.InvariantCulture) + "</ptt:KDVli>"
                + "<ptt:KDVOran>" + product.VatRate + "</ptt:KDVOran>"
                + "<ptt:Desi>" + product.Desi.ToString("0.#", CultureInfo.InvariantCulture) + "</ptt:Desi>"
                + "</tem:item></tem:StokGuncelleV2>";
            string soap = BuildPttSoapEnvelope(GetCred(cfg, "username"), GetCred(cfg, "password"), inner);
            var headers = new Dictionary<string, string> { { "SOAPAction", "\"http://tempuri.org/IService/StokGuncelleV2\"" } };
            string resp = HttpRequest("POST", "https://ws.pttavm.com:93/service.svc", soap, "text/xml; charset=utf-8", headers);
            return new ConnectorResult { Success = true, RemoteId = "PTT-" + product.Barcode, Message = "PttAVM CANLI API'ye gönderildi.", RawDetails = Trunc(resp, 250) };
        }

        private static ConnectorResult SyncPttAvmStock(MarketplaceConfig cfg, ProductRecord product, int newStock, decimal newPrice)
        {
            string inner = "<tem:StokGuncelleV2><tem:item>"
                + "<ptt:Barkod>" + EscapeXml(product.Barcode) + "</ptt:Barkod>"
                + "<ptt:Miktar>" + newStock + "</ptt:Miktar>"
                + "<ptt:KDVli>" + newPrice.ToString("0.00", CultureInfo.InvariantCulture) + "</ptt:KDVli>"
                + "</tem:item></tem:StokGuncelleV2>";
            string soap = BuildPttSoapEnvelope(GetCred(cfg, "username"), GetCred(cfg, "password"), inner);
            var headers = new Dictionary<string, string> { { "SOAPAction", "\"http://tempuri.org/IService/StokGuncelleV2\"" } };
            string resp = HttpRequest("POST", "https://ws.pttavm.com:93/service.svc", soap, "text/xml; charset=utf-8", headers);
            return new ConnectorResult { Success = true, Message = "PttAVM CANLI stok güncellendi (" + newStock + " adet).", RawDetails = Trunc(resp, 250) };
        }

        private static string BuildPttSoapEnvelope(string user, string pass, string bodyXml)
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                + "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:tem=\"http://tempuri.org/\" xmlns:ptt=\"http://schemas.datacontract.org/2004/07/PttAvmServis\">"
                + "<soapenv:Header><wsse:Security xmlns:wsse=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\">"
                + "<wsse:UsernameToken><wsse:Username>" + EscapeXml(user) + "</wsse:Username><wsse:Password>" + EscapeXml(pass) + "</wsse:Password></wsse:UsernameToken>"
                + "</wsse:Security></soapenv:Header><soapenv:Body>" + bodyXml + "</soapenv:Body></soapenv:Envelope>";
        }

        // ==================== AMAZON TR (SP-API) ====================
        private static string GetAmazonLwaToken(MarketplaceConfig cfg)
        {
            string body = "grant_type=refresh_token&refresh_token=" + Uri.EscapeDataString(GetCred(cfg, "refreshToken"))
                + "&client_id=" + Uri.EscapeDataString(GetCred(cfg, "lwaClientId"))
                + "&client_secret=" + Uri.EscapeDataString(GetCred(cfg, "lwaClientSecret"));
            string resp = HttpRequest("POST", "https://api.amazon.com/auth/o2/token", body, "application/x-www-form-urlencoded", null);
            var obj = Storage.Deserialize<Dictionary<string, object>>(resp);
            return obj != null && obj.ContainsKey("access_token") ? Convert.ToString(obj["access_token"]) : "";
        }

        private static ConnectorResult TestAmazon(MarketplaceConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(GetCred(cfg, "sellerId")) || string.IsNullOrWhiteSpace(GetCred(cfg, "lwaClientId")) || string.IsNullOrWhiteSpace(GetCred(cfg, "refreshToken")))
                return new ConnectorResult { Success = false, Message = "Amazon Seller ID, LWA Client ID, Client Secret ve Refresh Token zorunludur." };
            string token = GetAmazonLwaToken(cfg);
            if (string.IsNullOrWhiteSpace(token))
                return new ConnectorResult { Success = false, Message = "Amazon LWA erişim anahtarı alınamadı." };
            return new ConnectorResult { Success = true, Message = "Amazon TR (SP-API) CANLI LWA bağlantısı doğrulandı." };
        }

        private static ConnectorResult PublishAmazon(MarketplaceConfig cfg, ProductRecord product, MarketplaceListing listing)
        {
            string token = GetAmazonLwaToken(cfg);
            string sellerId = GetCred(cfg, "sellerId");
            string mpId = string.IsNullOrWhiteSpace(GetCred(cfg, "marketplaceId")) ? "A33AVAJ2PDY3EV" : GetCred(cfg, "marketplaceId");
            string url = "https://sellingpartnerapi-eu.amazon.com/listings/2021-08-01/items/" + Uri.EscapeDataString(sellerId) + "/" + Uri.EscapeDataString(product.Sku) + "?marketplaceIds=" + Uri.EscapeDataString(mpId);
            var headers = new Dictionary<string, string> { { "x-amz-access-token", token } };
            var payload = new Dictionary<string, object>
            {
                { "productType", listing.CategoryId },
                { "requirements", "LISTING" },
                {
                    "attributes", new Dictionary<string, object>
                    {
                        { "item_name", new[] { new Dictionary<string, object> { { "value", listing.Title }, { "language_tag", "tr_TR" }, { "marketplace_id", mpId } } } },
                        { "product_description", new[] { new Dictionary<string, object> { { "value", listing.Description }, { "language_tag", "tr_TR" }, { "marketplace_id", mpId } } } },
                        { "brand", new[] { new Dictionary<string, object> { { "value", product.Brand }, { "marketplace_id", mpId } } } },
                        { "fulfillment_availability", new[] { new Dictionary<string, object> { { "fulfillment_channel_code", "DEFAULT" }, { "quantity", product.Stock } } } },
                        { "purchasable_offer", new[] { new Dictionary<string, object> { { "currency", "TRY" }, { "our_price", new[] { new Dictionary<string, object> { { "schedule", new[] { new Dictionary<string, object> { { "value_with_tax", listing.Price } } } } } } } } } }
                    }
                }
            };
            string resp = HttpRequest("PUT", url, Storage.Serialize(payload), "application/json", headers);
            return new ConnectorResult { Success = true, RemoteId = ExtractField(resp, "submissionId", "AMZ-" + product.Sku), Message = "Amazon SP-API'ye CANLI gönderildi.", RawDetails = Trunc(resp, 300) };
        }

        private static ConnectorResult SyncAmazonStock(MarketplaceConfig cfg, ProductRecord product, int newStock, decimal newPrice)
        {
            string token = GetAmazonLwaToken(cfg);
            string sellerId = GetCred(cfg, "sellerId");
            string mpId = string.IsNullOrWhiteSpace(GetCred(cfg, "marketplaceId")) ? "A33AVAJ2PDY3EV" : GetCred(cfg, "marketplaceId");
            string url = "https://sellingpartnerapi-eu.amazon.com/listings/2021-08-01/items/" + Uri.EscapeDataString(sellerId) + "/" + Uri.EscapeDataString(product.Sku) + "?marketplaceIds=" + Uri.EscapeDataString(mpId);
            var headers = new Dictionary<string, string> { { "x-amz-access-token", token } };
            var payload = new Dictionary<string, object>
            {
                { "productType", "PRODUCT" },
                {
                    "patches", new[]
                    {
                        new Dictionary<string, object>
                        {
                            { "op", "replace" },
                            { "path", "/attributes/fulfillment_availability" },
                            { "value", new[] { new Dictionary<string, object> { { "fulfillment_channel_code", "DEFAULT" }, { "quantity", newStock } } } }
                        }
                    }
                }
            };
            string resp = HttpRequest("PATCH", url, Storage.Serialize(payload), "application/json", headers);
            return new ConnectorResult { Success = true, Message = "Amazon TR CANLI stok güncellendi (" + newStock + " adet).", RawDetails = Trunc(resp, 250) };
        }

        // ==================== HTTP & TEXT HELPERS ====================
        private static string HttpRequest(string method, string url, string body, string contentType, Dictionary<string, string> headers)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = 20000;
            req.ReadWriteTimeout = 20000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (!string.IsNullOrEmpty(contentType)) req.ContentType = contentType;
            if (headers != null)
            {
                foreach (var kv in headers)
                {
                    if (kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                        req.UserAgent = kv.Value;
                    else
                        req.Headers[kv.Key] = kv.Value;
                }
            }
            if (!string.IsNullOrEmpty(body))
            {
                byte[] data = Encoding.UTF8.GetBytes(body);
                req.ContentLength = data.Length;
                using (var stream = req.GetRequestStream()) stream.Write(data, 0, data.Length);
            }
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static string ReadWebError(WebException wex)
        {
            if (wex.Response != null)
            {
                try
                {
                    using (var r = new StreamReader(wex.Response.GetResponseStream(), Encoding.UTF8))
                    {
                        string text = Security.RedactSensitiveText(r.ReadToEnd());
                        var httpResp = wex.Response as HttpWebResponse;
                        string code = httpResp != null ? ((int)httpResp.StatusCode).ToString() + " " : "";
                        return code + Trunc(text, 300);
                    }
                }
                catch { }
            }
            return Security.RedactSensitiveText(wex.Message);
        }

        private static string NormalizeTr(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string low = s.ToLower(new CultureInfo("tr-TR"));
            return low
                .Replace('ğ', 'g').Replace('ü', 'u').Replace('ş', 's')
                .Replace('ı', 'i').Replace('ö', 'o').Replace('ç', 'c')
                .Replace('İ', 'i');
        }

        private static HashSet<string> TokenizeTr(string normText)
        {
            var set = new HashSet<string>();
            foreach (Match m in Regex.Matches(normText ?? "", @"[a-z0-9]+"))
            {
                if (m.Value.Length >= 2) set.Add(m.Value);
            }
            return set;
        }

        private static List<object> AsList(object obj)
        {
            if (obj == null) return null;
            var already = obj as List<object>;
            if (already != null) return already;
            var enumerable = obj as System.Collections.IEnumerable;
            if (enumerable == null || obj is string) return null;
            var res = new List<object>();
            foreach (var item in enumerable) res.Add(item);
            return res;
        }

        private static string GetCred(MarketplaceConfig cfg, string key)
        {
            if (cfg.Credentials != null && cfg.Credentials.ContainsKey(key))
                return (cfg.Credentials[key] ?? "").Trim();
            return "";
        }

        private static string ToBase64(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        }

        private static int ParseIntSafe(string s, int fallback)
        {
            int v;
            return int.TryParse(s, out v) ? v : fallback;
        }

        private static string EscapeXml(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private static string Trunc(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        private static string ExtractField(string json, string field, string fallback)
        {
            try
            {
                var d = Storage.Deserialize<Dictionary<string, object>>(json);
                if (d != null && d.ContainsKey(field) && d[field] != null)
                    return Convert.ToString(d[field]);
            }
            catch { }
            return fallback;
        }
    }
}
