using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartMarketplace
{
    public class TrendyolCategoryMatchResult
    {
        public bool Success { get; set; }
        public int CategoryId { get; set; }
        public string CategoryName { get; set; }
        public string FullPath { get; set; }
        public string Source { get; set; }
        public string Error { get; set; }
        public List<TrendyolLeafCategory> Alternatives { get; set; }
        public List<AttributeItem> ResolvedAttributes { get; set; }

        public TrendyolCategoryMatchResult()
        {
            Alternatives = new List<TrendyolLeafCategory>();
            ResolvedAttributes = new List<AttributeItem>();
        }
    }

    public static class TrendyolCategoryEngine
    {
        private static readonly object _catLock = new object();
        private static List<TrendyolLeafCategory> _leafCategories;
        private static DateTime _loadedAt = DateTime.MinValue;
        private static readonly Dictionary<int, List<Dictionary<string, object>>> _attrCache = new Dictionary<int, List<Dictionary<string, object>>>();
        private static readonly Dictionary<string, List<KeyValuePair<int, string>>> _attrValuesCache = new Dictionary<string, List<KeyValuePair<int, string>>>();

        static TrendyolCategoryEngine()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }

        public static List<TrendyolLeafCategory> EnsureCategoryTreeLoaded(MarketplaceConfig cfg, bool forceRefresh, out string error)
        {
            error = "";
            lock (_catLock)
            {
                if (!forceRefresh && _leafCategories != null && _leafCategories.Count > 0 && (DateTime.Now - _loadedAt).TotalHours < 24)
                    return _leafCategories;

                string cacheFile = Path.Combine(Storage.DataDir, "trendyol_categories_cache.json");
                if (!forceRefresh && File.Exists(cacheFile) && (DateTime.Now - File.GetLastWriteTime(cacheFile)).TotalHours < 24)
                {
                    try
                    {
                        var cached = Storage.Deserialize<List<TrendyolLeafCategory>>(File.ReadAllText(cacheFile, Encoding.UTF8));
                        if (cached != null && cached.Count > 0)
                        {
                            _leafCategories = cached;
                            _loadedAt = DateTime.Now;
                            return _leafCategories;
                        }
                    }
                    catch { }
                }

                var headers = cfg != null ? MarketplaceConnectors.BuildTrendyolHeaders(cfg) : new Dictionary<string, string>
                {
                    { "User-Agent", "SmartOtomasyon/3.0" },
                    { "storeFrontCode", "TR" },
                    { "Accept-Language", "tr" }
                };

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
                        raw = HttpGet(ep, headers, 30000);
                        if (!string.IsNullOrWhiteSpace(raw) && raw.Length > 100) break;
                    }
                    catch (WebException wex)
                    {
                        error = ReadErr(wex);
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                    }
                }

                if (string.IsNullOrWhiteSpace(raw))
                {
                    // If network temporarily failed, fall back to stale cache if it exists
                    if (File.Exists(cacheFile))
                    {
                        try
                        {
                            var stale = Storage.Deserialize<List<TrendyolLeafCategory>>(File.ReadAllText(cacheFile, Encoding.UTF8));
                            if (stale != null && stale.Count > 0)
                            {
                                _leafCategories = stale;
                                _loadedAt = DateTime.Now;
                                return _leafCategories;
                            }
                        }
                        catch { }
                    }
                    if (string.IsNullOrWhiteSpace(error)) error = "Trendyol kategori servisine erişilemedi.";
                    return null;
                }

                var list = ParseCategoryTreeJson(raw, out error);
                if (list.Count > 0)
                {
                    _leafCategories = list;
                    _loadedAt = DateTime.Now;
                    try { File.WriteAllText(cacheFile, Storage.Serialize(list), new UTF8Encoding(false)); } catch { }
                }
                return _leafCategories;
            }
        }

        public static List<TrendyolLeafCategory> ParseCategoryTreeJson(string rawJson, out string error)
        {
            error = "";
            var list = new List<TrendyolLeafCategory>();
            try
            {
                object parsed = Storage.Deserialize<object>(rawJson);
                var dict = parsed as Dictionary<string, object>;
                if (dict != null)
                {
                    if (dict.ContainsKey("categories"))
                    {
                        var cats = ToObjectList(dict["categories"]);
                        if (cats != null) FlattenNodes(cats, "", list);
                    }
                    else if (dict.ContainsKey("subCategories"))
                    {
                        FlattenNodes(new List<object> { dict }, "", list);
                    }
                }
                else
                {
                    var topArr = ToObjectList(parsed);
                    if (topArr != null) FlattenNodes(topArr, "", list);
                }
            }
            catch (Exception ex)
            {
                error = "Trendyol kategori JSON ayrıştırma hatası: " + ex.Message;
            }
            return list;
        }

        private static void FlattenNodes(List<object> nodes, string parentPath, List<TrendyolLeafCategory> leaves)
        {
            foreach (var n in nodes)
            {
                var d = n as Dictionary<string, object>;
                if (d == null || !d.ContainsKey("id") || !d.ContainsKey("name")) continue;
                int id = Convert.ToInt32(d["id"]);
                string name = Convert.ToString(d["name"]);
                string fullPath = string.IsNullOrEmpty(parentPath) ? name : (parentPath + " > " + name);
                var subs = d.ContainsKey("subCategories") ? ToObjectList(d["subCategories"]) : null;
                if (subs == null || subs.Count == 0)
                {
                    leaves.Add(new TrendyolLeafCategory { Id = id, Name = name, FullPath = fullPath });
                }
                else
                {
                    FlattenNodes(subs, fullPath, leaves);
                }
            }
        }

        public static List<TrendyolLeafCategory> SearchCategories(MarketplaceConfig cfg, string query, int limit, out string error)
        {
            var leaves = EnsureCategoryTreeLoaded(cfg, false, out error);
            if (leaves == null || leaves.Count == 0) return new List<TrendyolLeafCategory>();

            string qNorm = NormalizeTr(query ?? "");
            if (string.IsNullOrWhiteSpace(qNorm)) return leaves.Take(limit).ToList();

            var qTokens = TokenizeTr(qNorm);
            return leaves
                .Select(leaf => new { Leaf = leaf, Score = ScoreLeafForSearch(leaf, qNorm, qTokens) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Take(limit)
                .Select(x => x.Leaf)
                .ToList();
        }

        private static int ScoreLeafForSearch(TrendyolLeafCategory leaf, string qNorm, HashSet<string> qTokens)
        {
            string nName = NormalizeTr(leaf.Name);
            string nPath = NormalizeTr(leaf.FullPath);
            int score = 0;
            if (nName == qNorm) score += 200;
            else if (nName.StartsWith(qNorm)) score += 120;
            else if (nName.Contains(qNorm)) score += 80;
            else if (nPath.Contains(qNorm)) score += 35;

            foreach (var t in qTokens)
            {
                if (nName.Contains(t)) score += 25;
                else if (nPath.Contains(t)) score += 8;
            }
            return score;
        }

        public static TrendyolCategoryMatchResult ResolveBestCategory(
            MarketplaceConfig cfg,
            string baseName,
            string title,
            string prompt,
            string coarseCategory,
            int preferredCategoryId = 0)
        {
            var result = new TrendyolCategoryMatchResult();
            var headers = cfg != null ? MarketplaceConnectors.BuildTrendyolHeaders(cfg) : null;

            string treeErr;
            var leaves = EnsureCategoryTreeLoaded(cfg, false, out treeErr);

            // STAGE 0: If user already selected a specific valid Trendyol leaf category ID, honor & verify it!
            if (preferredCategoryId > 0)
            {
                var foundInTree = leaves != null ? leaves.Find(x => x.Id == preferredCategoryId) : null;
                if (foundInTree != null)
                {
                    result.Success = true;
                    result.CategoryId = foundInTree.Id;
                    result.CategoryName = foundInTree.Name;
                    result.FullPath = foundInTree.FullPath;
                    result.Source = "Seçili Trendyol İç Kategorisi";
                    result.Alternatives = GetTopAlternatives(leaves, baseName, title, prompt, coarseCategory, foundInTree.Name, 6);
                    return result;
                }
                if (headers != null && VerifyLeafCategoryViaApi(headers, preferredCategoryId))
                {
                    result.Success = true;
                    result.CategoryId = preferredCategoryId;
                    result.CategoryName = "Trendyol Kategori #" + preferredCategoryId;
                    result.FullPath = result.CategoryName;
                    result.Source = "Doğrulanmış Trendyol Kategorisi";
                    return result;
                }
            }

            // STAGE 1: Live Trendyol Competitor Analysis — find the exact categoryId top-selling competitors use!
            string compHintName = "";
            if (headers != null)
            {
                var compCat = TryFindCompetitorCategoryOnTrendyol(headers, baseName, prompt, leaves, out compHintName);
                if (compCat != null && compCat.Id > 0)
                {
                    result.Success = true;
                    result.CategoryId = compCat.Id;
                    result.CategoryName = compCat.Name;
                    result.FullPath = compCat.FullPath;
                    result.Source = "Canlı Trendyol Rakip Eşleştirmesi";
                    if (leaves != null)
                        result.Alternatives = GetTopAlternatives(leaves, baseName, title, prompt, coarseCategory, compCat.Name, 6);
                    return result;
                }
            }

            // STAGE 2: Official Trendyol Leaf Category Tree Deep Turkish Semantic Matcher
            if (leaves != null && leaves.Count > 0)
            {
                var ranked = RankLeafCategories(leaves, baseName, title, prompt, coarseCategory, compHintName);
                if (ranked.Count > 0)
                {
                    var top = ranked[0];
                    result.Success = true;
                    result.CategoryId = top.Id;
                    result.CategoryName = top.Name;
                    result.FullPath = top.FullPath;
                    result.Source = "Trendyol Resmi Kategori Ağacı (" + leaves.Count + " yaprak kategori)";
                    result.Alternatives = ranked.Take(6).ToList();
                    return result;
                }
            }

            // STAGE 3: Seller's own existing Trendyol store products category lookup (if tree temporarily unreachable)
            if (cfg != null && headers != null)
            {
                var storeCat = TryFindFromSellerOwnProducts(cfg, headers, baseName, prompt);
                if (storeCat != null && storeCat.Id > 0)
                {
                    result.Success = true;
                    result.CategoryId = storeCat.Id;
                    result.CategoryName = storeCat.Name;
                    result.FullPath = storeCat.FullPath;
                    result.Source = "Trendyol Mağaza Geçmiş Kategorisi";
                    return result;
                }
            }

            result.Success = false;
            result.Error = string.IsNullOrWhiteSpace(treeErr)
                ? "Trendyol iç kategori ağacı alınamadı."
                : treeErr;
            return result;
        }

        private static TrendyolLeafCategory TryFindCompetitorCategoryOnTrendyol(
            Dictionary<string, string> sellerHeaders,
            string baseName,
            string prompt,
            List<TrendyolLeafCategory> leaves,
            out string categoryHint)
        {
            categoryHint = "";
            try
            {
                string query = CleanSearchQuery(baseName, prompt);
                if (string.IsNullOrWhiteSpace(query)) return null;

                string[] urls =
                {
                    "https://public.trendyol.com/discovery-web-searchgw-service/v2/api/infinite-scroll/sr?q=" + Uri.EscapeDataString(query) + "&pi=1&culture=tr-TR&userGenderId=1&pId=0&scoringAlgorithmId=2&categoryRelevancyEnabled=true",
                    "https://apigw.trendyol.com/discovery-web-searchgw-service/v2/api/infinite-scroll/sr?q=" + Uri.EscapeDataString(query) + "&pi=1&culture=tr-TR"
                };

                string json = "";
                var pubHeaders = new Dictionary<string, string>
                {
                    { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36" },
                    { "Accept", "application/json" }
                };

                foreach (var u in urls)
                {
                    try
                    {
                        json = HttpGet(u, pubHeaders, 6000);
                        if (!string.IsNullOrWhiteSpace(json) && json.Contains("products")) break;
                    }
                    catch { }
                }

                if (string.IsNullOrWhiteSpace(json)) return null;

                var root = Storage.Deserialize<Dictionary<string, object>>(json);
                var resObj = root != null && root.ContainsKey("result") ? root["result"] as Dictionary<string, object> : root;
                var prods = resObj != null && resObj.ContainsKey("products") ? ToObjectList(resObj["products"]) : null;
                if (prods == null || prods.Count == 0) return null;

                var votes = new Dictionary<int, int>();
                var names = new Dictionary<int, string>();
                var paths = new Dictionary<int, string>();

                int count = 0;
                foreach (var o in prods)
                {
                    if (count >= 15) break;
                    var pd = o as Dictionary<string, object>;
                    if (pd == null || !pd.ContainsKey("categoryId")) continue;
                    int cid = 0;
                    try { cid = Convert.ToInt32(pd["categoryId"]); } catch { continue; }
                    if (cid <= 0) continue;

                    count++;
                    int w = count <= 5 ? 3 : 1;
                    votes[cid] = (votes.ContainsKey(cid) ? votes[cid] : 0) + w;
                    if (pd.ContainsKey("categoryName") && pd["categoryName"] != null) names[cid] = Convert.ToString(pd["categoryName"]);
                    if (pd.ContainsKey("categoryHierarchy") && pd["categoryHierarchy"] != null) paths[cid] = Convert.ToString(pd["categoryHierarchy"]);
                }

                foreach (var kv in votes.OrderByDescending(x => x.Value).Take(4))
                {
                    int cid = kv.Key;
                    string cname = names.ContainsKey(cid) ? names[cid] : ("Kategori #" + cid);
                    if (string.IsNullOrWhiteSpace(categoryHint)) categoryHint = cname;

                    // Fast check against official leaf tree first, then live Seller API attribute verification
                    if (leaves != null)
                    {
                        var exactLeaf = leaves.Find(l => l.Id == cid);
                        if (exactLeaf != null) return exactLeaf;
                    }
                    if (VerifyLeafCategoryViaApi(sellerHeaders, cid))
                    {
                        string path = paths.ContainsKey(cid) && !string.IsNullOrWhiteSpace(paths[cid]) ? paths[cid] : cname;
                        return new TrendyolLeafCategory { Id = cid, Name = cname, FullPath = path };
                    }
                }
            }
            catch { }
            return null;
        }

        private static TrendyolLeafCategory TryFindFromSellerOwnProducts(MarketplaceConfig cfg, Dictionary<string, string> headers, string baseName, string prompt)
        {
            try
            {
                string supplierId = Uri.EscapeDataString((cfg.Credentials["supplierId"] ?? "").Trim());
                string url = "https://apigw.trendyol.com/integration/product/sellers/" + supplierId + "/products?page=0&size=50";
                string raw = HttpGet(url, headers, 10000);
                var root = Storage.Deserialize<Dictionary<string, object>>(raw);
                var content = root != null && root.ContainsKey("content") ? ToObjectList(root["content"]) : null;
                if (content == null || content.Count == 0) return null;

                var ownCats = new Dictionary<int, TrendyolLeafCategory>();
                foreach (var o in content)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null || !d.ContainsKey("pimCategoryId")) continue;
                    int cid = Convert.ToInt32(d["pimCategoryId"]);
                    string cname = d.ContainsKey("categoryName") ? Convert.ToString(d["categoryName"]) : ("Kategori #" + cid);
                    if (cid > 0 && !ownCats.ContainsKey(cid))
                        ownCats[cid] = new TrendyolLeafCategory { Id = cid, Name = cname, FullPath = cname };
                }

                if (ownCats.Count == 0) return null;
                var ranked = RankLeafCategories(ownCats.Values.ToList(), baseName, baseName, prompt, "", "");
                return ranked.Count > 0 ? ranked[0] : null;
            }
            catch { return null; }
        }

        public static bool VerifyLeafCategoryViaApi(Dictionary<string, string> headers, int categoryId)
        {
            try
            {
                string url = "https://apigw.trendyol.com/integration/product/categories/" + categoryId + "/attributes";
                string raw = HttpGet(url, headers, 10000);
                return !string.IsNullOrWhiteSpace(raw) && raw.Contains("categoryAttributes");
            }
            catch { return false; }
        }

        public static List<Dictionary<string, object>> FetchRawCategoryAttributes(MarketplaceConfig cfg, int categoryId)
        {
            lock (_catLock)
            {
                if (_attrCache.ContainsKey(categoryId)) return _attrCache[categoryId];
            }
            var result = new List<Dictionary<string, object>>();
            var headers = MarketplaceConnectors.BuildTrendyolHeaders(cfg);
            // Try bundled product-categories/{id}/attributes first (includes inline attributeValues when available), then V2 categories/{id}/attributes
            string[] urls =
            {
                "https://apigw.trendyol.com/integration/product/product-categories/" + categoryId + "/attributes",
                "https://apigw.trendyol.com/integration/product/categories/" + categoryId + "/attributes"
            };

            foreach (var url in urls)
            {
                try
                {
                    string raw = HttpGet(url, headers, 15000);
                    var root = Storage.Deserialize<Dictionary<string, object>>(raw);
                    var catAttrs = root != null && root.ContainsKey("categoryAttributes") ? ToObjectList(root["categoryAttributes"]) : null;
                    if (catAttrs != null && catAttrs.Count > 0)
                    {
                        foreach (var o in catAttrs)
                        {
                            var d = o as Dictionary<string, object>;
                            if (d != null) result.Add(d);
                        }
                        break;
                    }
                }
                catch { }
            }

            if (result.Count > 0)
            {
                lock (_catLock) { _attrCache[categoryId] = result; }
            }
            return result;
        }

        public static List<KeyValuePair<int, string>> FetchAllowedAttributeValues(MarketplaceConfig cfg, int categoryId, int attributeId, Dictionary<string, object> rawAttrNode)
        {
            string cacheKey = categoryId + ":" + attributeId;
            lock (_catLock)
            {
                if (_attrValuesCache.ContainsKey(cacheKey)) return _attrValuesCache[cacheKey];
            }

            var parsed = new List<KeyValuePair<int, string>>();

            // 1. Check if inline attributeValues already came in the categoryAttributes payload
            if (rawAttrNode != null && rawAttrNode.ContainsKey("attributeValues"))
            {
                var inlineList = ToObjectList(rawAttrNode["attributeValues"]);
                ExtractValuePairs(inlineList, parsed);
            }

            // 2. If inline was empty (Trendyol V2 decoupled design!), query the official V2 values endpoint!
            if (parsed.Count == 0 && cfg != null)
            {
                try
                {
                    var headers = MarketplaceConnectors.BuildTrendyolHeaders(cfg);
                    string url = "https://apigw.trendyol.com/integration/product/categories/" + categoryId + "/attributes/" + attributeId + "/values?page=0&size=500";
                    string raw = HttpGet(url, headers, 15000);
                    ExtractValuesFromV2Json(raw, parsed);
                }
                catch { }
            }

            if (parsed.Count > 0)
            {
                lock (_catLock) { _attrValuesCache[cacheKey] = parsed; }
            }
            return parsed;
        }

        public static void ExtractValuesFromV2Json(string rawJson, List<KeyValuePair<int, string>> target)
        {
            if (string.IsNullOrWhiteSpace(rawJson)) return;
            object parsed = Storage.Deserialize<object>(rawJson);
            var dict = parsed as Dictionary<string, object>;
            if (dict != null)
            {
                if (dict.ContainsKey("content")) ExtractValuePairs(ToObjectList(dict["content"]), target);
                else if (dict.ContainsKey("attributeValues")) ExtractValuePairs(ToObjectList(dict["attributeValues"]), target);
                else if (dict.ContainsKey("values")) ExtractValuePairs(ToObjectList(dict["values"]), target);
            }
            else
            {
                ExtractValuePairs(ToObjectList(parsed), target);
            }
        }

        private static void ExtractValuePairs(List<object> list, List<KeyValuePair<int, string>> target)
        {
            if (list == null) return;
            var seen = new HashSet<int>();
            foreach (var item in list)
            {
                var vd = item as Dictionary<string, object>;
                if (vd == null) continue;

                int id = 0;
                if (vd.ContainsKey("attributeValueId") && vd["attributeValueId"] != null)
                    int.TryParse(Convert.ToString(vd["attributeValueId"]), out id);
                else if (vd.ContainsKey("id") && vd["id"] != null)
                    int.TryParse(Convert.ToString(vd["id"]), out id);

                string name = "";
                if (vd.ContainsKey("attributeValue") && vd["attributeValue"] != null)
                    name = Convert.ToString(vd["attributeValue"]).Trim();
                else if (vd.ContainsKey("name") && vd["name"] != null)
                    name = Convert.ToString(vd["name"]).Trim();

                if (id > 0 && !seen.Contains(id))
                {
                    seen.Add(id);
                    target.Add(new KeyValuePair<int, string>(id, string.IsNullOrEmpty(name) ? ("#" + id) : name));
                }
            }
        }

        public static List<AttributeItem> BuildUiAttributesForCategory(MarketplaceConfig cfg, int categoryId, string baseName, string title, string prompt, List<AttributeItem> existingAttrs)
        {
            var rawAttrs = FetchRawCategoryAttributes(cfg, categoryId);
            var uiList = new List<AttributeItem>();
            string fullText = NormalizeTr((baseName ?? "") + " " + (title ?? "") + " " + (prompt ?? "") + " " + string.Join(" ", (existingAttrs ?? new List<AttributeItem>()).Select(a => a.Label + " " + a.Value)));
            var tokens = TokenizeTr(fullText);

            foreach (var ca in rawAttrs)
            {
                bool required = ca.ContainsKey("required") && Convert.ToBoolean(ca["required"]);
                bool varianter = ca.ContainsKey("varianter") && Convert.ToBoolean(ca["varianter"]);
                if (!required && !varianter) continue;

                var attrObj = ca.ContainsKey("attribute") ? ca["attribute"] as Dictionary<string, object> : null;
                if (attrObj == null || !attrObj.ContainsKey("id")) continue;
                int attrId = Convert.ToInt32(attrObj["id"]);
                string attrName = attrObj.ContainsKey("name") ? Convert.ToString(attrObj["name"]) : ("Özellik #" + attrId);
                bool allowCustom = ca.ContainsKey("allowCustom") && Convert.ToBoolean(ca["allowCustom"]);

                var allowedPairs = FetchAllowedAttributeValues(cfg, categoryId, attrId, ca);

                string chosenText = "";
                int chosenId = 0;
                string source = "default";
                var options = new List<string>();

                if (!allowCustom && allowedPairs.Count > 0)
                {
                    bool matchedFromPrompt;
                    PickBestValuePair(allowedPairs, fullText, tokens, attrName, out chosenId, out chosenText, out matchedFromPrompt);
                    source = matchedFromPrompt ? "prompt" : "competitor";
                    options = allowedPairs.Take(80).Select(x => x.Value).ToList();
                    if (!options.Contains(chosenText) && !string.IsNullOrEmpty(chosenText))
                        options.Insert(0, chosenText);
                }
                else
                {
                    chosenText = ResolveSmartCustomValue(attrName, fullText, existingAttrs);
                    source = "competitor";
                }

                uiList.Add(new AttributeItem
                {
                    Key = "ty_attr_" + attrId,
                    Label = attrName,
                    Value = chosenText,
                    ValueId = chosenId,
                    AllowCustom = allowCustom,
                    Required = true,
                    Source = source,
                    Options = options
                });
            }

            return uiList;
        }

        public static List<Dictionary<string, object>> BuildTrendyolApiAttributesPayload(MarketplaceConfig cfg, int categoryId, ProductRecord product, MarketplaceListing listing)
        {
            var outAttrs = new List<Dictionary<string, object>>();
            var rawAttrs = FetchRawCategoryAttributes(cfg, categoryId);
            if (rawAttrs.Count == 0) return outAttrs;

            string fullText = NormalizeTr(
                (product.BaseName ?? "") + " " +
                (listing.Title ?? "") + " " +
                (product.Prompt ?? "") + " " +
                string.Join(" ", listing.Attributes.Select(a => a.Label + " " + a.Value))
            );
            var textTokens = TokenizeTr(fullText);

            var updatedUiAttrs = new List<AttributeItem>();

            foreach (var ca in rawAttrs)
            {
                bool required = ca.ContainsKey("required") && Convert.ToBoolean(ca["required"]);
                bool varianter = ca.ContainsKey("varianter") && Convert.ToBoolean(ca["varianter"]);
                if (!required && !varianter) continue;

                var attrObj = ca.ContainsKey("attribute") ? ca["attribute"] as Dictionary<string, object> : null;
                if (attrObj == null || !attrObj.ContainsKey("id")) continue;
                int attrId = Convert.ToInt32(attrObj["id"]);
                string attrName = attrObj.ContainsKey("name") ? Convert.ToString(attrObj["name"]) : "";
                bool allowCustom = ca.ContainsKey("allowCustom") && Convert.ToBoolean(ca["allowCustom"]);

                var uiMatch = listing.Attributes.Find(a => a.Key == ("ty_attr_" + attrId) || NormalizeTr(a.Label) == NormalizeTr(attrName));
                string uiVal = uiMatch != null ? (uiMatch.Value ?? "").Trim() : "";

                if (allowCustom)
                {
                    string customVal = !string.IsNullOrWhiteSpace(uiVal) ? uiVal : ResolveSmartCustomValue(attrName, fullText, listing.Attributes);
                    outAttrs.Add(new Dictionary<string, object>
                    {
                        { "attributeId", attrId },
                        { "customAttributeValue", customVal }
                    });
                    updatedUiAttrs.Add(new AttributeItem { Key = "ty_attr_" + attrId, Label = attrName, Value = customVal, AllowCustom = true, Required = true, Source = "prompt" });
                }
                else
                {
                    var allowedPairs = FetchAllowedAttributeValues(cfg, categoryId, attrId, ca);
                    if (allowedPairs.Count == 0) continue; // Never send invalid customAttributeValue when allowCustom=false!

                    int bestId = 0;
                    string bestName = "";
                    if (!string.IsNullOrWhiteSpace(uiVal))
                    {
                        string normUi = NormalizeTr(uiVal);
                        var exact = allowedPairs.FirstOrDefault(p => NormalizeTr(p.Value) == normUi);
                        if (exact.Key > 0) { bestId = exact.Key; bestName = exact.Value; }
                    }
                    if (bestId <= 0)
                    {
                        bool matched;
                        PickBestValuePair(allowedPairs, fullText, textTokens, attrName, out bestId, out bestName, out matched);
                    }
                    if (bestId > 0)
                    {
                        outAttrs.Add(new Dictionary<string, object>
                        {
                            { "attributeId", attrId },
                            { "attributeValueId", bestId }
                        });
                        var opts = allowedPairs.Take(80).Select(x => x.Value).ToList();
                        if (!opts.Contains(bestName)) opts.Insert(0, bestName);
                        updatedUiAttrs.Add(new AttributeItem { Key = "ty_attr_" + attrId, Label = attrName, Value = bestName, ValueId = bestId, AllowCustom = false, Required = true, Source = "competitor", Options = opts });
                    }
                }
            }

            if (updatedUiAttrs.Count > 0) listing.Attributes = updatedUiAttrs;
            return outAttrs;
        }

        private static void PickBestValuePair(List<KeyValuePair<int, string>> values, string fullText, HashSet<string> textTokens, string attrName, out int bestId, out string bestName, out bool matchedPrompt)
        {
            bestId = 0;
            bestName = "";
            matchedPrompt = false;

            int bestScore = -1;
            int fallbackId = values[0].Key;
            string fallbackName = values[0].Value;
            int neutralId = 0;
            string neutralName = "";
            string normAttr = NormalizeTr(attrName ?? "");

            // Context-aware neutral defaults per Turkish attribute name
            var preferredNeutrals = new List<string> { "standart", "belirtilmemis", "tek ebat", "diger", "yok", "unisex" };
            if (normAttr.Contains("mensei")) preferredNeutrals.InsertRange(0, new[] { "tr", "turkiye" });
            else if (normAttr.Contains("parca")) preferredNeutrals.InsertRange(0, new[] { "1 parca", "2 parca", "1", "2" });
            else if (normAttr.Contains("garanti")) preferredNeutrals.InsertRange(0, new[] { "resmi distributor garantili", "ithalatci garantili", "garantili", "2 yil", "24 ay" });
            else if (normAttr.Contains("renk") || normAttr.Contains("color")) preferredNeutrals.InsertRange(0, new[] { "gri", "gumus", "metalik", "cok renkli", "siyah", "beyaz" });
            else if (normAttr.Contains("materyal")) preferredNeutrals.InsertRange(0, new[] { "celik", "paslanmaz celik", "granit", "pamuk" });

            foreach (var kv in values)
            {
                int id = kv.Key;
                string vName = kv.Value;
                string normV = NormalizeTr(vName);
                if (string.IsNullOrEmpty(normV)) continue;

                if (neutralId == 0)
                {
                    foreach (var n in preferredNeutrals)
                    {
                        if (normV == n || normV.Contains(n))
                        {
                            neutralId = id;
                            neutralName = vName;
                            break;
                        }
                    }
                }

                int score = 0;
                if (normV.Length >= 2 && fullText.Contains(normV)) score += 30 + normV.Length * 2;
                foreach (var tok in TokenizeTr(normV))
                {
                    if (tok.Length >= 2 && textTokens.Contains(tok)) score += 16;
                }
                if (score > bestScore && score > 0)
                {
                    bestScore = score;
                    bestId = id;
                    bestName = vName;
                    matchedPrompt = true;
                }
            }

            if (bestId > 0) return;
            if (neutralId > 0) { bestId = neutralId; bestName = neutralName; return; }
            bestId = fallbackId;
            bestName = fallbackName;
        }

        private static string ResolveSmartCustomValue(string attrName, string fullText, List<AttributeItem> existingAttrs)
        {
            string norm = NormalizeTr(attrName);
            if (existingAttrs != null)
            {
                foreach (var a in existingAttrs)
                {
                    if (!string.IsNullOrWhiteSpace(a.Value) && (NormalizeTr(a.Label).Contains(norm) || norm.Contains(NormalizeTr(a.Label))))
                        return a.Value;
                }
            }
            if (norm.Contains("mensei") || norm.Contains("ulke")) return "TR";
            if (norm.Contains("renk")) return fullText.Contains("celik") ? "Metalik / Gümüş" : "Standart";
            if (norm.Contains("materyal") || norm.Contains("malzeme")) return fullText.Contains("celik") ? "Paslanmaz Çelik" : "Standart";
            if (norm.Contains("parca")) return "1 Parça";
            if (norm.Contains("garanti")) return "24 Ay";
            if (norm.Contains("boyut") || norm.Contains("ebat") || norm.Contains("beden")) return "Standart";
            return "Standart";
        }

        public static List<TrendyolLeafCategory> GetTopAlternatives(List<TrendyolLeafCategory> leaves, string baseName, string title, string prompt, string coarseCategory, string hint, int count)
        {
            return RankLeafCategories(leaves, baseName, title, prompt, coarseCategory, hint).Take(count).ToList();
        }

        public static List<TrendyolLeafCategory> RankLeafCategories(List<TrendyolLeafCategory> leaves, string baseName, string title, string prompt, string coarseCategory, string competitorHint)
        {
            string baseNorm = NormalizeTr(baseName ?? "");
            string fullNorm = NormalizeTr((baseName ?? "") + " " + (title ?? "") + " " + (prompt ?? "") + " " + (coarseCategory ?? ""));
            string hintNorm = NormalizeTr(competitorHint ?? "");

            var baseWords = TokenizeTr(baseNorm);
            var fullTokens = TokenizeTr(fullNorm);
            var hintTokens = TokenizeTr(hintNorm);
            string[] accessoryTraps = { "kapak", "kapagi", "tutacak", "tutacagi", "sapi", "yedek", "parca", "oyuncak", "kilif", "kilifi", "aksesuar", "temizleyici", "standi", "askisi", "koruyucu", "cantasi", "kutu", "kutusu", "etiket", "kitap", "defter", "mama", "bebek" };

            var scored = new List<KeyValuePair<int, TrendyolLeafCategory>>(leaves.Count);
            foreach (var leaf in leaves)
            {
                string normName = NormalizeTr(leaf.Name);
                string normPath = NormalizeTr(leaf.FullPath);
                var nameTokens = TokenizeTr(normName);

                int score = 0;
                if (!string.IsNullOrEmpty(hintNorm))
                {
                    if (normName == hintNorm) score += 130;
                    else if (normName.Contains(hintNorm) || hintNorm.Contains(normName)) score += 70;
                    foreach (var ht in hintTokens)
                        if (ht.Length >= 3 && nameTokens.Contains(ht)) score += 30;
                }

                if (!string.IsNullOrEmpty(normName) && baseNorm.Contains(normName))
                    score += 85 + normName.Length * 2;
                else if (!string.IsNullOrEmpty(normName) && fullNorm.Contains(normName))
                    score += 45 + normName.Length;

                int matched = 0;
                foreach (var nt in nameTokens)
                {
                    if (nt.Length < 3) continue;
                    if (baseWords.Contains(nt)) { score += 38; matched++; }
                    else if (fullTokens.Contains(nt)) { score += 18; matched++; }
                    else if (fullNorm.Contains(nt)) { score += 9; matched++; }
                }

                if (nameTokens.Count > 0 && matched == nameTokens.Count) score += 45;
                else if (nameTokens.Count > matched) score -= (nameTokens.Count - matched) * 9;

                foreach (var qt in fullTokens)
                    if (qt.Length >= 3 && normPath.Contains(qt)) score += 4;

                foreach (var trap in accessoryTraps)
                {
                    if ((normName.Contains(trap) || normPath.Contains("oyuncak")) && !fullNorm.Contains(trap) && !fullNorm.Contains("oyuncak"))
                        score -= 75;
                }

                scored.Add(new KeyValuePair<int, TrendyolLeafCategory>(score, leaf));
            }

            return scored.OrderByDescending(x => x.Key).Select(x => x.Value).ToList();
        }

        private static string CleanSearchQuery(string baseName, string prompt)
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

        private static string HttpGet(string url, Dictionary<string, string> headers, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (headers != null)
            {
                foreach (var kv in headers)
                {
                    if (kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) req.UserAgent = kv.Value;
                    else if (kv.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)) req.Accept = kv.Value;
                    else req.Headers[kv.Key] = kv.Value;
                }
            }
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return r.ReadToEnd();
        }

        private static string ReadErr(WebException wex)
        {
            if (wex.Response != null)
            {
                try
                {
                    using (var r = new StreamReader(wex.Response.GetResponseStream(), Encoding.UTF8))
                    {
                        var h = wex.Response as HttpWebResponse;
                        string c = h != null ? ((int)h.StatusCode).ToString() + " " : "";
                        string txt = Security.RedactSensitiveText(r.ReadToEnd());
                        return c + (txt.Length > 200 ? txt.Substring(0, 200) : txt);
                    }
                }
                catch { }
            }
            return Security.RedactSensitiveText(wex.Message);
        }

        private static List<object> ToObjectList(object obj)
        {
            if (obj == null || obj is string) return null;
            var l = obj as List<object>;
            if (l != null) return l;
            var en = obj as System.Collections.IEnumerable;
            if (en == null) return null;
            var res = new List<object>();
            foreach (var x in en) res.Add(x);
            return res;
        }

        private static string NormalizeTr(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.ToLower(new CultureInfo("tr-TR"))
                .Replace('ğ', 'g').Replace('ü', 'u').Replace('ş', 's')
                .Replace('ı', 'i').Replace('ö', 'o').Replace('ç', 'c')
                .Replace('İ', 'i');
        }

        private static HashSet<string> TokenizeTr(string normText)
        {
            var set = new HashSet<string>();
            foreach (Match m in Regex.Matches(normText ?? "", @"[a-z0-9]+"))
                if (m.Value.Length >= 2) set.Add(m.Value);
            return set;
        }
    }
}
