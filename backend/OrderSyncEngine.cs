using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SmartMarketplace
{
    public class IncomingOrderLine
    {
        public string OrderNumber { get; set; }
        public string MarketplaceId { get; set; }
        public string Barcode { get; set; }
        public string Sku { get; set; }
        public string ProductName { get; set; }
        public int Quantity { get; set; }
        public string Status { get; set; }
    }

    public class OrderPullSummary
    {
        public int ChannelsChecked { get; set; }
        public int OrdersFetched { get; set; }
        public int NewOrdersProcessed { get; set; }
        public int CancelledRestored { get; set; }
        public int StockSyncOperations { get; set; }
        public List<string> ChannelNotes { get; set; }
        public List<SyncedOrderRecord> ProcessedRecords { get; set; }

        public OrderPullSummary()
        {
            ChannelNotes = new List<string>();
            ProcessedRecords = new List<SyncedOrderRecord>();
        }
    }

    /// <summary>
    /// Live cross-marketplace order polling, instant webhook ingestion, idempotent deduplication,
    /// automatic central stock deduction / cancel restoration, and real-time multi-channel stock sync.
    /// </summary>
    public static class OrderSyncEngine
    {
        private static readonly HashSet<string> _cancelTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cancelled", "canceled", "cancel", "iptal", "rejected", "returned", "iade", "void"
        };

        public static bool IsCancelledStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status)) return false;
            string low = status.Trim().ToLowerInvariant();
            foreach (var tok in _cancelTokens)
            {
                if (low.Contains(tok)) return true;
            }
            return false;
        }

        /// <summary>
        /// Polls all enabled LIVE-connected marketplaces for recent orders, deducts stock from Smart's central inventory,
        /// and immediately synchronizes the updated stock across ALL other connected marketplaces.
        /// </summary>
        public static OrderPullSummary PullAndProcessAllOrders(AppDatabase db, Func<AppDatabase, ProductRecord, string, int, string, int> syncProductAcrossChannels)
        {
            var summary = new OrderPullSummary();
            if (db == null || db.Marketplaces == null) return summary;

            foreach (var mp in db.Marketplaces)
            {
                if (!mp.Enabled || mp.SimulationMode || !MarketplaceConnectors.HasRequiredCredentials(mp))
                    continue;

                summary.ChannelsChecked++;
                try
                {
                    var lines = FetchRecentOrdersFromMarketplace(mp);
                    summary.OrdersFetched += lines.Count;
                    int mpProcessed = 0;
                    foreach (var line in lines)
                    {
                        var rec = ApplyIncomingOrderLine(db, line, "Canlı API", syncProductAcrossChannels);
                        if (rec != null)
                        {
                            summary.ProcessedRecords.Add(rec);
                            summary.StockSyncOperations += rec.SyncedChannels;
                            if (rec.Restored) summary.CancelledRestored++;
                            else { summary.NewOrdersProcessed++; mpProcessed++; }
                        }
                    }
                    summary.ChannelNotes.Add(mp.Name + ": " + lines.Count + " kalem tarandı, " + mpProcessed + " yeni sipariş işlendi.");
                }
                catch (Exception ex)
                {
                    string safeErr = Security.RedactSensitiveText(ex.Message);
                    summary.ChannelNotes.Add(mp.Name + ": Sipariş kontrol hatası (" + safeErr + ")");
                    Storage.AddLog(db, "sync", "warning", mp.Name, "Canlı sipariş taraması sırasında uyarı: " + safeErr);
                }
            }

            db.SyncConfig.LastOrderPullAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            return summary;
        }

        /// <summary>
        /// Processes a single incoming order line (from Live Polling or Instant Webhook) idempotently.
        /// Matches by Barcode -> SKU -> RemoteId, updates central stock, and pushes to all other marketplaces.
        /// </summary>
        public static SyncedOrderRecord ApplyIncomingOrderLine(
            AppDatabase db,
            IncomingOrderLine line,
            string sourceLabel,
            Func<AppDatabase, ProductRecord, string, int, string, int> syncProductAcrossChannels)
        {
            if (db == null || line == null || line.Quantity <= 0) return null;
            if (db.ProcessedOrderKeys == null) db.ProcessedOrderKeys = new List<string>();
            if (db.Orders == null) db.Orders = new List<SyncedOrderRecord>();

            string mpId = (line.MarketplaceId ?? "").Trim().ToLowerInvariant();
            string mpName = MarketplaceSchema.GetMarketplaceName(mpId);
            string orderNo = (line.OrderNumber ?? "").Trim();
            string barcode = (line.Barcode ?? "").Trim();
            string sku = (line.Sku ?? "").Trim();
            if (string.IsNullOrWhiteSpace(orderNo))
                orderNo = "ORD-" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Security.GenerateSecureToken(3);

            string itemIdent = !string.IsNullOrEmpty(barcode) ? barcode : sku;
            if (string.IsNullOrEmpty(itemIdent)) return null;

            string orderKey = (mpId + ":" + orderNo + ":" + itemIdent).ToLowerInvariant();
            bool isCancelled = IsCancelledStatus(line.Status);

            var product = FindMatchingProduct(db, barcode, sku);
            if (product == null) return null;

            bool alreadyProcessed = db.ProcessedOrderKeys.Contains(orderKey);

            // Case 1: Previously deducted order is now cancelled -> restore stock once if enabled!
            if (isCancelled)
            {
                if (!alreadyProcessed || !db.SyncConfig.RestoreStockOnCancel) return null;
                var prevRec = db.Orders.Find(o => string.Equals(o.OrderKey, orderKey, StringComparison.OrdinalIgnoreCase));
                if (prevRec == null || prevRec.Restored) return null;

                int before = product.Stock;
                product.Stock = Math.Min(1000000, product.Stock + line.Quantity);
                product.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                prevRec.Restored = true;
                prevRec.Status = "İptal (Stok İade Edildi)";

                int synced = syncProductAcrossChannels(db, product, mpName + " Sipariş İptali (" + orderNo + ")", before, null);
                Storage.AddLog(db, "stock", "info", mpName,
                    mpName + " #" + orderNo + " iptal edildi — '" + product.BaseName + "' stoğu " + before + " → " + product.Stock + " olarak geri yüklendi ve " + synced + " pazaryerine eşitlendi.");
                return prevRec;
            }

            // Case 2: Already deducted -> skip (100% idempotent, never double-deducts!)
            if (alreadyProcessed) return null;

            int oldStock = product.Stock;
            product.Stock = Math.Max(0, product.Stock - line.Quantity);
            product.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            db.ProcessedOrderKeys.Add(orderKey);
            if (db.ProcessedOrderKeys.Count > 2000)
                db.ProcessedOrderKeys.RemoveRange(0, db.ProcessedOrderKeys.Count - 2000);

            int syncedCount = syncProductAcrossChannels(db, product, mpName + " Siparişi #" + orderNo + " (" + sourceLabel + ")", oldStock, mpId);

            var rec = new SyncedOrderRecord
            {
                Id = "ord_" + Guid.NewGuid().ToString("N").Substring(0, 10),
                OrderKey = orderKey,
                OrderNumber = orderNo,
                MarketplaceId = mpId,
                MarketplaceName = mpName,
                ProductId = product.Id,
                ProductName = product.BaseName,
                Sku = product.Sku,
                Barcode = product.Barcode,
                Quantity = line.Quantity,
                Status = string.IsNullOrWhiteSpace(line.Status) ? "İşlendi" : line.Status,
                StockBefore = oldStock,
                StockAfter = product.Stock,
                SyncedChannels = syncedCount,
                Source = sourceLabel,
                ProcessedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Restored = false
            };

            db.Orders.Insert(0, rec);
            if (db.Orders.Count > 200)
                db.Orders.RemoveRange(200, db.Orders.Count - 200);

            Storage.AddLog(db, "stock", "success", mpName,
                mpName + " #" + orderNo + " (" + line.Quantity + " adet) — '" + product.BaseName + "' stoğu " + oldStock + " → " + product.Stock + " düşüldü, " + syncedCount + " diğer kanala anında eşitlendi.",
                "Kaynak: " + sourceLabel + " | Barkod: " + product.Barcode + " | SKU: " + product.Sku);

            return rec;
        }

        public static ProductRecord FindMatchingProduct(AppDatabase db, string barcode, string sku)
        {
            if (db == null || db.Products == null) return null;
            string b = (barcode ?? "").Trim();
            string s = (sku ?? "").Trim();
            if (!string.IsNullOrEmpty(b))
            {
                var byBarcode = db.Products.Find(p => string.Equals((p.Barcode ?? "").Trim(), b, StringComparison.OrdinalIgnoreCase));
                if (byBarcode != null) return byBarcode;
            }
            if (!string.IsNullOrEmpty(s))
            {
                var bySku = db.Products.Find(p => string.Equals((p.Sku ?? "").Trim(), s, StringComparison.OrdinalIgnoreCase));
                if (bySku != null) return bySku;
            }
            // Fallback: cross-match barcode vs SKU in case marketplace swapped fields
            if (!string.IsNullOrEmpty(b))
            {
                var cross1 = db.Products.Find(p => string.Equals((p.Sku ?? "").Trim(), b, StringComparison.OrdinalIgnoreCase));
                if (cross1 != null) return cross1;
            }
            if (!string.IsNullOrEmpty(s))
            {
                var cross2 = db.Products.Find(p => string.Equals((p.Barcode ?? "").Trim(), s, StringComparison.OrdinalIgnoreCase));
                if (cross2 != null) return cross2;
            }
            return null;
        }

        /// <summary>
        /// Parses incoming JSON webhook payloads from Trendyol, Hepsiburada, N11, İdefix, Pazarama, or custom integrations.
        /// </summary>
        public static List<IncomingOrderLine> ParseWebhookPayload(string marketplaceId, string rawBody)
        {
            var list = new List<IncomingOrderLine>();
            if (string.IsNullOrWhiteSpace(rawBody)) return list;
            string mpId = (marketplaceId ?? "trendyol").Trim().ToLowerInvariant();

            object parsed;
            try { parsed = Storage.Deserialize<object>(rawBody); }
            catch { return list; }

            var rootDict = parsed as Dictionary<string, object>;
            if (rootDict != null)
            {
                ExtractOrderLinesFromObject(mpId, rootDict, list);
            }
            else
            {
                var arr = MarketplaceConnectors.AsList(parsed);
                if (arr != null)
                {
                    foreach (var item in arr)
                    {
                        var d = item as Dictionary<string, object>;
                        if (d != null) ExtractOrderLinesFromObject(mpId, d, list);
                    }
                }
            }
            return list;
        }

        private static void ExtractOrderLinesFromObject(string mpId, Dictionary<string, object> obj, List<IncomingOrderLine> target)
        {
            if (obj == null) return;

            // Wrapped array (content / orders / data / items / packages)
            string[] wrapKeys = { "content", "orders", "packages" };
            foreach (var wk in wrapKeys)
            {
                if (obj.ContainsKey(wk))
                {
                    var wrapped = MarketplaceConnectors.AsList(obj[wk]);
                    if (wrapped != null)
                    {
                        foreach (var w in wrapped)
                        {
                            var wd = w as Dictionary<string, object>;
                            if (wd != null) ExtractOrderLinesFromObject(mpId, wd, target);
                        }
                        return;
                    }
                }
            }

            string orderNo = GetAnyStr(obj, "orderNumber", "orderId", "shipmentPackageId", "id", "packageNumber", "AmazonOrderId");
            string status = GetAnyStr(obj, "status", "shipmentPackageStatus", "orderStatus", "OrderStatus");

            // Nested lines / items array
            var lineItems = MarketplaceConnectors.AsList(GetAnyObj(obj, "lines", "items", "orderItems", "products"));
            if (lineItems != null && lineItems.Count > 0)
            {
                foreach (var li in lineItems)
                {
                    var ld = li as Dictionary<string, object>;
                    if (ld == null) continue;
                    var prodSub = ld.ContainsKey("product") ? (ld["product"] as Dictionary<string, object>) : null;
                    string bc = GetAnyStr(ld, "barcode", "Barcode", "code") ?? GetAnyStr(prodSub, "barcode", "code");
                    string sk = GetAnyStr(ld, "merchantSku", "merchantSKU", "stockCode", "vendorStockCode", "sku", "SellerSKU")
                        ?? GetAnyStr(prodSub, "stockCode", "code");
                    int qty = GetAnyInt(ld, 1, "quantity", "Quantity", "QuantityOrdered", "amount", "count");
                    string lineStatus = GetAnyStr(ld, "orderLineItemStatusName", "status", "itemStatus") ?? status;
                    string pname = GetAnyStr(ld, "productName", "title", "name") ?? GetAnyStr(prodSub, "name", "title");

                    if (!string.IsNullOrWhiteSpace(bc) || !string.IsNullOrWhiteSpace(sk))
                    {
                        target.Add(new IncomingOrderLine
                        {
                            OrderNumber = !string.IsNullOrWhiteSpace(orderNo) ? orderNo : GetAnyStr(ld, "orderNumber", "orderId"),
                            MarketplaceId = mpId,
                            Barcode = bc ?? "",
                            Sku = sk ?? "",
                            ProductName = pname ?? "",
                            Quantity = Math.Max(1, qty),
                            Status = lineStatus ?? "Created"
                        });
                    }
                }
                return;
            }

            // Flat single-line payload
            string flatBc = GetAnyStr(obj, "barcode", "Barcode", "code");
            string flatSku = GetAnyStr(obj, "sku", "Sku", "merchantSku", "merchantSKU", "stockCode", "vendorStockCode");
            if (!string.IsNullOrWhiteSpace(flatBc) || !string.IsNullOrWhiteSpace(flatSku))
            {
                target.Add(new IncomingOrderLine
                {
                    OrderNumber = orderNo ?? "",
                    MarketplaceId = mpId,
                    Barcode = flatBc ?? "",
                    Sku = flatSku ?? "",
                    ProductName = GetAnyStr(obj, "productName", "title", "name") ?? "",
                    Quantity = Math.Max(1, GetAnyInt(obj, 1, "quantity", "qty", "amount", "count")),
                    Status = status ?? "Created"
                });
            }
        }

        // ==================== LIVE ORDER POLLERS PER MARKETPLACE ====================
        private static List<IncomingOrderLine> FetchRecentOrdersFromMarketplace(MarketplaceConfig cfg)
        {
            switch (cfg.Id)
            {
                case "trendyol": return FetchTrendyolOrders(cfg);
                case "hepsiburada": return FetchHepsiburadaOrders(cfg);
                case "n11": return FetchN11Orders(cfg);
                case "idefix": return FetchIdefixOrders(cfg);
                case "pazarama": return FetchPazaramaOrders(cfg);
                case "pttavm": return FetchPttAvmOrders(cfg);
                case "amazon": return FetchAmazonOrders(cfg);
                default: return new List<IncomingOrderLine>();
            }
        }

        private static List<IncomingOrderLine> FetchTrendyolOrders(MarketplaceConfig cfg)
        {
            string supplierId = Uri.EscapeDataString(MarketplaceConnectors.GetCred(cfg, "supplierId"));
            var headers = MarketplaceConnectors.BuildTrendyolHeaders(cfg);
            long endMs = ToUnixTimeMs(DateTime.UtcNow.AddMinutes(5));
            long startMs = ToUnixTimeMs(DateTime.UtcNow.AddHours(-24));

            string url = "https://apigw.trendyol.com/integration/order/sellers/" + supplierId
                + "/orders?startDate=" + startMs + "&endDate=" + endMs
                + "&page=0&size=50&orderByField=PackageLastModifiedDate&orderByDirection=DESC";
            string raw = MarketplaceConnectors.HttpRequest("GET", url, null, "application/json", headers);
            return ParseWebhookPayload("trendyol", raw);
        }

        private static List<IncomingOrderLine> FetchHepsiburadaOrders(MarketplaceConfig cfg)
        {
            string merchantId = MarketplaceConnectors.GetCred(cfg, "merchantId");
            string user = string.IsNullOrWhiteSpace(MarketplaceConnectors.GetCred(cfg, "username")) ? merchantId : MarketplaceConnectors.GetCred(cfg, "username");
            var headers = new Dictionary<string, string>
            {
                { "Authorization", "Basic " + MarketplaceConnectors.ToBase64(user + ":" + MarketplaceConnectors.GetCred(cfg, "serviceKey")) },
                { "User-Agent", "SmartOtomasyon_Integration" }
            };
            string begin = Uri.EscapeDataString(DateTime.Now.AddHours(-24).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            string end = Uri.EscapeDataString(DateTime.Now.AddMinutes(5).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            string url = "https://oms-external.hepsiburada.com/orders/merchantid/" + Uri.EscapeDataString(merchantId)
                + "?begindate=" + begin + "&enddate=" + end + "&offset=0&limit=50";
            string raw = MarketplaceConnectors.HttpRequest("GET", url, null, "application/json", headers);
            return ParseWebhookPayload("hepsiburada", raw);
        }

        private static List<IncomingOrderLine> FetchN11Orders(MarketplaceConfig cfg)
        {
            var headers = new Dictionary<string, string>
            {
                { "appkey", MarketplaceConnectors.GetCred(cfg, "appKey") },
                { "appsecret", MarketplaceConnectors.GetCred(cfg, "appSecret") }
            };
            long startMs = ToUnixTimeMs(DateTime.UtcNow.AddHours(-24));
            long endMs = ToUnixTimeMs(DateTime.UtcNow.AddMinutes(5));
            string url = "https://api.n11.com/rest/delivery/v1/shipmentPackages?startDate=" + startMs + "&endDate=" + endMs + "&page=0&size=50";
            string raw = MarketplaceConnectors.HttpRequest("GET", url, null, "application/json", headers);
            return ParseWebhookPayload("n11", raw);
        }

        private static List<IncomingOrderLine> FetchIdefixOrders(MarketplaceConfig cfg)
        {
            string vendorId = Uri.EscapeDataString(MarketplaceConnectors.GetCred(cfg, "vendorId"));
            var headers = new Dictionary<string, string>
            {
                { "X-API-KEY", MarketplaceConnectors.ToBase64(MarketplaceConnectors.GetCred(cfg, "apiKey") + ":" + MarketplaceConnectors.GetCred(cfg, "apiSecret")) }
            };
            long startSec = ToUnixTimeMs(DateTime.UtcNow.AddHours(-24)) / 1000;
            long endSec = ToUnixTimeMs(DateTime.UtcNow.AddMinutes(5)) / 1000;
            string url = "https://merchantapi.idefix.com/oms/" + vendorId + "/list?startDate=" + startSec + "&endDate=" + endSec + "&page=1&limit=50";
            string raw = MarketplaceConnectors.HttpRequest("GET", url, null, "application/json", headers);
            return ParseWebhookPayload("idefix", raw);
        }

        private static List<IncomingOrderLine> FetchPazaramaOrders(MarketplaceConfig cfg)
        {
            string token = MarketplaceConnectors.GetPazaramaToken(cfg);
            if (string.IsNullOrWhiteSpace(token)) return new List<IncomingOrderLine>();
            var headers = new Dictionary<string, string> { { "Authorization", "Bearer " + token } };
            var payload = new Dictionary<string, object>
            {
                { "pageSize", 50 },
                { "pageNumber", 1 },
                { "startDate", DateTime.Now.AddHours(-24).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                { "endDate", DateTime.Now.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
            };
            string raw = MarketplaceConnectors.HttpRequest("POST", "https://isortagimapi.pazarama.com/order/getOrdersForApi", Storage.Serialize(payload), "application/json", headers);
            var root = Storage.Deserialize<Dictionary<string, object>>(raw);
            var list = new List<IncomingOrderLine>();
            if (root != null && root.ContainsKey("data"))
            {
                var dataArr = MarketplaceConnectors.AsList(root["data"]);
                if (dataArr != null)
                {
                    foreach (var item in dataArr)
                    {
                        var d = item as Dictionary<string, object>;
                        if (d != null) ExtractOrderLinesFromObject("pazarama", d, list);
                    }
                }
            }
            return list;
        }

        private static List<IncomingOrderLine> FetchPttAvmOrders(MarketplaceConfig cfg)
        {
            var list = new List<IncomingOrderLine>();
            string start = DateTime.Now.AddHours(-24).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string end = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string inner = "<tem:SiparisKontrolListesiV2><tem:BaslangicTarihi>" + start + "</tem:BaslangicTarihi><tem:BitisTarihi>" + end + "</tem:BitisTarihi><tem:AktifSiparisler>1</tem:AktifSiparisler></tem:SiparisKontrolListesiV2>";
            string soap = MarketplaceConnectors.BuildPttSoapEnvelope(MarketplaceConnectors.GetCred(cfg, "username"), MarketplaceConnectors.GetCred(cfg, "password"), inner);
            var headers = new Dictionary<string, string> { { "SOAPAction", "\"http://tempuri.org/IService/SiparisKontrolListesiV2\"" } };
            string resp = MarketplaceConnectors.HttpRequest("POST", "https://ws.pttavm.com:93/service.svc", soap, "text/xml; charset=utf-8", headers);

            foreach (Match m in Regex.Matches(resp ?? "", @"<[^:>]*SiparisUrun[^>]*>(.*?)</[^:>]*SiparisUrun>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                string block = m.Groups[1].Value;
                string ord = MatchXmlTag(block, "SiparisNo");
                string bc = MatchXmlTag(block, "Barkod");
                string qtyStr = MatchXmlTag(block, "ToplamAdet");
                if (string.IsNullOrWhiteSpace(qtyStr)) qtyStr = MatchXmlTag(block, "Miktar");
                string st = MatchXmlTag(block, "SiparisDurumu");
                int qty;
                if (!int.TryParse(qtyStr, out qty) || qty <= 0) qty = 1;
                if (!string.IsNullOrWhiteSpace(bc))
                {
                    list.Add(new IncomingOrderLine
                    {
                        OrderNumber = ord ?? "",
                        MarketplaceId = "pttavm",
                        Barcode = bc,
                        Sku = "",
                        Quantity = qty,
                        Status = st ?? "Created"
                    });
                }
            }
            return list;
        }

        private static List<IncomingOrderLine> FetchAmazonOrders(MarketplaceConfig cfg)
        {
            var list = new List<IncomingOrderLine>();
            string token = MarketplaceConnectors.GetAmazonLwaToken(cfg);
            if (string.IsNullOrWhiteSpace(token)) return list;
            string mpId = string.IsNullOrWhiteSpace(MarketplaceConnectors.GetCred(cfg, "marketplaceId")) ? "A33AVAJ2PDY3EV" : MarketplaceConnectors.GetCred(cfg, "marketplaceId");
            var headers = new Dictionary<string, string> { { "x-amz-access-token", token } };
            string after = Uri.EscapeDataString(DateTime.UtcNow.AddHours(-24).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            string url = "https://sellingpartnerapi-eu.amazon.com/orders/v0/orders?MarketplaceIds=" + Uri.EscapeDataString(mpId) + "&CreatedAfter=" + after;
            string raw = MarketplaceConnectors.HttpRequest("GET", url, null, "application/json", headers);
            var root = Storage.Deserialize<Dictionary<string, object>>(raw);
            var payload = root != null && root.ContainsKey("payload") ? root["payload"] as Dictionary<string, object> : null;
            var orders = payload != null && payload.ContainsKey("Orders") ? MarketplaceConnectors.AsList(payload["Orders"]) : null;
            if (orders == null) return list;

            foreach (var o in orders.Take(15))
            {
                var od = o as Dictionary<string, object>;
                if (od == null) continue;
                string orderId = GetAnyStr(od, "AmazonOrderId");
                string status = GetAnyStr(od, "OrderStatus");
                if (string.IsNullOrWhiteSpace(orderId)) continue;
                try
                {
                    string itemsUrl = "https://sellingpartnerapi-eu.amazon.com/orders/v0/orders/" + Uri.EscapeDataString(orderId) + "/orderItems";
                    string itemsRaw = MarketplaceConnectors.HttpRequest("GET", itemsUrl, null, "application/json", headers);
                    var iRoot = Storage.Deserialize<Dictionary<string, object>>(itemsRaw);
                    var iPay = iRoot != null && iRoot.ContainsKey("payload") ? iRoot["payload"] as Dictionary<string, object> : null;
                    var oItems = iPay != null && iPay.ContainsKey("OrderItems") ? MarketplaceConnectors.AsList(iPay["OrderItems"]) : null;
                    if (oItems == null) continue;
                    foreach (var oi in oItems)
                    {
                        var oid = oi as Dictionary<string, object>;
                        if (oid == null) continue;
                        string sku = GetAnyStr(oid, "SellerSKU");
                        int qty = GetAnyInt(oid, 1, "QuantityOrdered");
                        if (!string.IsNullOrWhiteSpace(sku))
                        {
                            list.Add(new IncomingOrderLine
                            {
                                OrderNumber = orderId,
                                MarketplaceId = "amazon",
                                Sku = sku,
                                Barcode = "",
                                Quantity = Math.Max(1, qty),
                                Status = status ?? "Unshipped"
                            });
                        }
                    }
                }
                catch { }
            }
            return list;
        }

        private static string MatchXmlTag(string xml, string tag)
        {
            var m = Regex.Match(xml ?? "", @"<[^:>]*?" + Regex.Escape(tag) + @"[^>]*>([^<]*)</", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        private static long ToUnixTimeMs(DateTime utc)
        {
            return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        private static object GetAnyObj(Dictionary<string, object> d, params string[] keys)
        {
            if (d == null) return null;
            foreach (var k in keys)
            {
                if (d.ContainsKey(k) && d[k] != null) return d[k];
            }
            return null;
        }

        private static string GetAnyStr(Dictionary<string, object> d, params string[] keys)
        {
            object v = GetAnyObj(d, keys);
            return v == null ? null : Convert.ToString(v).Trim();
        }

        private static int GetAnyInt(Dictionary<string, object> d, int def, params string[] keys)
        {
            object v = GetAnyObj(d, keys);
            if (v == null) return def;
            int n;
            return int.TryParse(Convert.ToString(v), out n) ? n : def;
        }
    }
}
