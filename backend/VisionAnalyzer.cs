using System;
using System.Collections.Generic;
#if NET8_0_OR_GREATER
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
#else
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
#endif
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartMarketplace
{
    public static class VisionAnalyzer
    {
        static VisionAnalyzer()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }

        public static VisionReport AnalyzeProductImages(List<string> images, List<string> originalFileNames, string userPrompt, AiSettings aiCfg)
        {
            var report = new VisionReport { Analyzed = false };
            if (images == null || images.Count == 0) return report;

            var localPaths = ResolveLocalImagePaths(images);
            if (localPaths.Count == 0) return report;

            // Step 1: Always run fast local pixel analysis (extracts true center color hex + Turkish color name + surface cues)
            string pixelColorName, pixelColorHex, surfaceMaterialHint;
            var pixelFeatures = AnalyzePixels(localPaths[0], out pixelColorName, out pixelColorHex, out surfaceMaterialHint);
            report.DominantColor = pixelColorName;
            report.DominantColorHex = pixelColorHex;
            report.VisualFeatures.AddRange(pixelFeatures);

            // Step 2: Try Multimodal AI Vision (Gemini Vision -> OpenAI Vision) if API keys are available
            if (aiCfg != null && aiCfg.Provider != "builtin")
            {
                var base64Images = PrepareCompactBase64Images(localPaths, 2);
                if (base64Images.Count > 0)
                {
                    if ((aiCfg.Provider == "gemini" || aiCfg.Provider == "auto") && !string.IsNullOrWhiteSpace(aiCfg.GeminiApiKey))
                    {
                        var gem = TryGeminiVision(base64Images, userPrompt, aiCfg);
                        if (gem != null)
                        {
                            ApplyAiVisionResult(report, gem, "Google Gemini Vision (" + (aiCfg.GeminiModel ?? "gemini-2.0-flash") + ")");
                            return report;
                        }
                    }
                    if ((aiCfg.Provider == "openai" || aiCfg.Provider == "auto") && !string.IsNullOrWhiteSpace(aiCfg.OpenAiApiKey))
                    {
                        var oai = TryOpenAiVision(base64Images, userPrompt, aiCfg);
                        if (oai != null)
                        {
                            ApplyAiVisionResult(report, oai, "OpenAI Vision (" + (aiCfg.OpenAiModel ?? "gpt-4o-mini") + ")");
                            return report;
                        }
                    }
                }
            }

            // Step 3: Built-in Vision + Filename + Prompt synthesis (works 100% offline / zero API key!)
            report.Analyzed = true;
            report.EngineUsed = "Smart Görsel Piksel & Desen Analizi";
            report.Confidence = "Orta";

            string fileHint = ExtractCleanNameFromFiles(originalFileNames);
            string combinedHint = (!string.IsNullOrWhiteSpace(userPrompt) ? userPrompt.Trim() : "")
                + (!string.IsNullOrWhiteSpace(fileHint) ? (" " + fileHint) : "");

            if (!string.IsNullOrWhiteSpace(fileHint) && string.IsNullOrWhiteSpace(userPrompt))
            {
                report.DetectedProductName = CompetitorAnalyzer.ToTitleCaseTr(fileHint);
                report.SearchQuery = fileHint;
            }
            else if (!string.IsNullOrWhiteSpace(userPrompt))
            {
                var parsed = CompetitorAnalyzer.ParsePrompt(new AnalyzeRequest { Prompt = userPrompt });
                report.DetectedProductName = parsed.CleanTitle;
                report.SearchQuery = parsed.CleanTitle;
            }
            else
            {
                report.DetectedProductName = pixelColorName + " " + (string.IsNullOrWhiteSpace(surfaceMaterialHint) ? "Modern Ürün" : surfaceMaterialHint + " Ürün");
                report.SearchQuery = report.DetectedProductName;
            }

            report.DetectedMaterial = !string.IsNullOrWhiteSpace(surfaceMaterialHint) ? surfaceMaterialHint : "";
            report.DetectedCategory = DetectCategoryFromText(combinedHint + " " + surfaceMaterialHint);
            report.Summary = string.Format(
                "{0} adet görsel piksel düzeyinde tarandı. Baskın renk: {1} ({2}){3}. Daha derin görsel ürün tanıma için API Bağlantıları'na opsiyonel Gemini veya OpenAI anahtarı ekleyebilirsiniz.",
                localPaths.Count,
                report.DominantColor,
                report.DominantColorHex,
                string.IsNullOrWhiteSpace(report.DetectedMaterial) ? "" : (", yüzey/materyal ipucu: " + report.DetectedMaterial)
            );

            return report;
        }

        private static void ApplyAiVisionResult(VisionReport report, Dictionary<string, object> ai, string engineName)
        {
            report.Analyzed = true;
            report.EngineUsed = engineName;
            report.Confidence = GetStr(ai, "confidence", "Yüksek");
            string pName = GetStr(ai, "productName", "");
            if (!string.IsNullOrWhiteSpace(pName)) report.DetectedProductName = pName;
            string cat = GetStr(ai, "category", "");
            if (!string.IsNullOrWhiteSpace(cat)) report.DetectedCategory = cat;
            string col = GetStr(ai, "dominantColor", "");
            if (!string.IsNullOrWhiteSpace(col)) report.DominantColor = col;
            string mat = GetStr(ai, "material", "");
            if (!string.IsNullOrWhiteSpace(mat)) report.DetectedMaterial = mat;
            string sq = GetStr(ai, "searchQuery", "");
            report.SearchQuery = !string.IsNullOrWhiteSpace(sq) ? sq : report.DetectedProductName;

            decimal minP = GetDec(ai, "estimatedMinPrice");
            decimal maxP = GetDec(ai, "estimatedMaxPrice");
            if (minP > 0) report.EstimatedMinPrice = minP;
            if (maxP > 0) report.EstimatedMaxPrice = maxP;

            if (ai.ContainsKey("features") && ai["features"] is System.Collections.IEnumerable && !(ai["features"] is string))
            {
                var aiFeats = new List<string>();
                foreach (var x in (System.Collections.IEnumerable)ai["features"])
                {
                    string s = Convert.ToString(x);
                    if (!string.IsNullOrWhiteSpace(s)) aiFeats.Add(s.Trim());
                }
                if (aiFeats.Count > 0) report.VisualFeatures = aiFeats;
            }

            report.Summary = string.Format(
                "Görsel {0} ile analiz edildi: '{1}' tespit edildi (Renk: {2}, Materyal: {3}). Bu görsel tespiti temel alınarak her pazaryeri için ayrı rakip ve fiyat araştırması yapıldı.",
                engineName,
                report.DetectedProductName,
                report.DominantColor,
                string.IsNullOrWhiteSpace(report.DetectedMaterial) ? "Standart" : report.DetectedMaterial
            );
        }

        private static Dictionary<string, object> TryGeminiVision(List<string> base64Jpegs, string userPrompt, AiSettings cfg)
        {
            try
            {
                string model = string.IsNullOrWhiteSpace(cfg.GeminiModel) ? "gemini-2.0-flash" : cfg.GeminiModel.Trim();
                string url = "https://generativelanguage.googleapis.com/v1beta/models/" + Uri.EscapeDataString(model) + ":generateContent?key=" + Uri.EscapeDataString(cfg.GeminiApiKey.Trim());

                var parts = new List<object>
                {
                    new Dictionary<string, object> { { "text", BuildVisionInstruction(userPrompt) } }
                };
                foreach (var b64 in base64Jpegs)
                {
                    parts.Add(new Dictionary<string, object>
                    {
                        { "inline_data", new Dictionary<string, object> { { "mime_type", "image/jpeg" }, { "data", b64 } } }
                    });
                }

                var payload = new Dictionary<string, object>
                {
                    { "contents", new object[] { new Dictionary<string, object> { { "parts", parts.ToArray() } } } },
                    { "generationConfig", new Dictionary<string, object> { { "temperature", 0.2 }, { "responseMimeType", "application/json" } } }
                };

                string resp = PostJson(url, Storage.Serialize(payload), null, 22000);
                var root = Storage.Deserialize<Dictionary<string, object>>(resp);
                var cands = root != null && root.ContainsKey("candidates") ? root["candidates"] as System.Collections.IList : null;
                if (cands != null && cands.Count > 0)
                {
                    var c0 = cands[0] as Dictionary<string, object>;
                    var content = c0["content"] as Dictionary<string, object>;
                    var rparts = content["parts"] as System.Collections.IList;
                    var p0 = rparts[0] as Dictionary<string, object>;
                    string text = StripFence(Convert.ToString(p0["text"]));
                    return Storage.Deserialize<Dictionary<string, object>>(text);
                }
            }
            catch { }
            return null;
        }

        private static Dictionary<string, object> TryOpenAiVision(List<string> base64Jpegs, string userPrompt, AiSettings cfg)
        {
            try
            {
                string model = string.IsNullOrWhiteSpace(cfg.OpenAiModel) ? "gpt-4o-mini" : cfg.OpenAiModel.Trim();
                var contentParts = new List<object>
                {
                    new Dictionary<string, object> { { "type", "text" }, { "text", BuildVisionInstruction(userPrompt) } }
                };
                foreach (var b64 in base64Jpegs)
                {
                    contentParts.Add(new Dictionary<string, object>
                    {
                        { "type", "image_url" },
                        { "image_url", new Dictionary<string, object> { { "url", "data:image/jpeg;base64," + b64 }, { "detail", "low" } } }
                    });
                }

                var payload = new Dictionary<string, object>
                {
                    { "model", model },
                    { "response_format", new Dictionary<string, object> { { "type", "json_object" } } },
                    {
                        "messages", new object[]
                        {
                            new Dictionary<string, object> { { "role", "user" }, { "content", contentParts.ToArray() } }
                        }
                    }
                };

                var headers = new Dictionary<string, string> { { "Authorization", "Bearer " + cfg.OpenAiApiKey.Trim() } };
                string resp = PostJson("https://api.openai.com/v1/chat/completions", Storage.Serialize(payload), headers, 22000);
                var root = Storage.Deserialize<Dictionary<string, object>>(resp);
                var choices = root != null && root.ContainsKey("choices") ? root["choices"] as System.Collections.IList : null;
                if (choices != null && choices.Count > 0)
                {
                    var ch = choices[0] as Dictionary<string, object>;
                    var msg = ch["message"] as Dictionary<string, object>;
                    string text = StripFence(Convert.ToString(msg["content"]));
                    return Storage.Deserialize<Dictionary<string, object>>(text);
                }
            }
            catch { }
            return null;
        }

        private static string BuildVisionInstruction(string userPrompt)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Bu e-ticaret ürün fotoğraf(lar)ını detaylı incele ve Türkiye pazaryerleri (Trendyol, Hepsiburada, N11, Amazon TR) için ürünü tanımla.");
            if (!string.IsNullOrWhiteSpace(userPrompt))
                sb.AppendLine("Satıcının ek notu: " + userPrompt);
            sb.AppendLine("SADECE aşağıdaki geçerli JSON formatında Türkçe yanıt ver (yasaklı kelime veya marka uydurma):");
            sb.AppendLine("{");
            sb.AppendLine("  \"productName\": \"Ürünün net ve profesyonel Türkçe adı (ör: Paslanmaz Çelik Derin Tencere)\",");
            sb.AppendLine("  \"category\": \"Ana kategori (Giyim & Tekstil | Elektronik & Teknoloji | Ev & Yaşam | Kozmetik & Kişisel Bakım | Ayakkabı & Çanta | Genel / Çok Amaçlı)\",");
            sb.AppendLine("  \"dominantColor\": \"Ürünün baskın rengi (ör: Metalik / Gümüş, Haki, Siyah)\",");
            sb.AppendLine("  \"material\": \"Görselden tespit edilen ana malzeme/kumaş (ör: Paslanmaz Çelik, %100 Pamuk, Seramik)\",");
            sb.AppendLine("  \"searchQuery\": \"Pazaryerlerinde rakipleri bulmak için en iyi 2-4 kelimelik Türkçe arama sorgusu\",");
            sb.AppendLine("  \"estimatedMinPrice\": 0,");
            sb.AppendLine("  \"estimatedMaxPrice\": 0,");
            sb.AppendLine("  \"confidence\": \"Yüksek\",");
            sb.AppendLine("  \"features\": [\"Görselde net görülen 3-5 somut ürün özelliği\"]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static List<string> AnalyzePixels(string filePath, out string colorName, out string colorHex, out string surfaceHint)
        {
            colorName = "Standart";
            colorHex = "#7A8270";
            surfaceHint = "";
            var features = new List<string>();

            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
#if NET8_0_OR_GREATER
                using (var src = Image.Load<Rgb24>(bytes))
                {
                    src.Mutate(x => x.AutoOrient());
                    int origW = src.Width;
                    int origH = src.Height;
                    using (var small = src.Clone(x => x.Resize(96, 96, KnownResamplers.Triangle)))
                    {
                        // Sample border average to detect background color (white/light studio bg)
                        long bgR = 0, bgG = 0, bgB = 0; int bgN = 0;
                        for (int i = 0; i < 96; i += 2)
                        {
                            var coords = new[] { (i, 2), (i, 93), (2, i), (93, i) };
                            foreach (var pt in coords)
                            {
                                var c = small[pt.Item1, pt.Item2];
                                bgR += c.R; bgG += c.G; bgB += c.B; bgN++;
                            }
                        }
                        int avgBgR = bgN > 0 ? (int)(bgR / bgN) : 250;
                        int avgBgG = bgN > 0 ? (int)(bgG / bgN) : 250;
                        int avgBgB = bgN > 0 ? (int)(bgB / bgN) : 250;

                        // Sample center 60% foreground pixels
                        long fgR = 0, fgG = 0, fgB = 0; int fgN = 0;
                        int brightHighlights = 0;
                        int lowSatPixels = 0;
                        int darkPixels = 0;

                        for (int y = 18; y < 78; y += 2)
                        {
                            for (int x = 18; x < 78; x += 2)
                            {
                                var c = small[x, y];
                                int diffBg = Math.Abs(c.R - avgBgR) + Math.Abs(c.G - avgBgG) + Math.Abs(c.B - avgBgB);
                                if (diffBg < 32 && (c.R > 230 && c.G > 230 && c.B > 230)) continue;

                                fgR += c.R; fgG += c.G; fgB += c.B; fgN++;
                                float hue, sat, bri;
                                RgbToHsl(c.R, c.G, c.B, out hue, out sat, out bri);
                                if (sat < 0.16f) lowSatPixels++;
                                if (bri > 0.80f && sat < 0.18f) brightHighlights++;
                                if (bri < 0.22f) darkPixels++;
                            }
                        }

                        if (fgN == 0) { fgR = avgBgR; fgG = avgBgG; fgB = avgBgB; fgN = 1; }
                        int r = (int)(fgR / fgN);
                        int gr = (int)(fgG / fgN);
                        int b = (int)(fgB / fgN);
                        colorHex = string.Format("#{0:X2}{1:X2}{2:X2}", r, gr, b);
                        colorName = ClassifyTurkishColor(r, gr, b);

                        double lowSatRatio = (double)lowSatPixels / fgN;
                        double highlightRatio = (double)brightHighlights / fgN;
                        double darkRatio = (double)darkPixels / fgN;

                        if (lowSatRatio > 0.55 && highlightRatio > 0.08 && darkRatio > 0.08)
                        {
                            surfaceHint = "Paslanmaz Çelik / Metalik";
                            features.Add("Parlak metalik yüzey yansıması tespit edildi");
                        }
                        else if (lowSatRatio > 0.70 && highlightRatio < 0.05)
                        {
                            features.Add("Mat ve sade yüzey dokusu tespit edildi");
                        }

                        features.Add("Görsel baskın renk tonu: " + colorName);
                        features.Add(string.Format("Orijinal görsel çözünürlüğü: {0}×{1} px", origW, origH));
                    }
                }
#else
                using (var ms = new MemoryStream(bytes))
                using (var src = Image.FromStream(ms, true, true))
                using (var small = new Bitmap(96, 96, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.DrawImage(src, 0, 0, 96, 96);
                    }

                    // Sample border average to detect background color (white/light studio bg)
                    long bgR = 0, bgG = 0, bgB = 0; int bgN = 0;
                    for (int i = 0; i < 96; i += 2)
                    {
                        foreach (var pt in new[] { new Point(i, 2), new Point(i, 93), new Point(2, i), new Point(93, i) })
                        {
                            var c = small.GetPixel(pt.X, pt.Y);
                            bgR += c.R; bgG += c.G; bgB += c.B; bgN++;
                        }
                    }
                    int avgBgR = bgN > 0 ? (int)(bgR / bgN) : 250;
                    int avgBgG = bgN > 0 ? (int)(bgG / bgN) : 250;
                    int avgBgB = bgN > 0 ? (int)(bgB / bgN) : 250;

                    // Sample center 60% foreground pixels
                    long fgR = 0, fgG = 0, fgB = 0; int fgN = 0;
                    int brightHighlights = 0;
                    int lowSatPixels = 0;
                    int darkPixels = 0;

                    for (int y = 18; y < 78; y += 2)
                    {
                        for (int x = 18; x < 78; x += 2)
                        {
                            var c = small.GetPixel(x, y);
                            int diffBg = Math.Abs(c.R - avgBgR) + Math.Abs(c.G - avgBgG) + Math.Abs(c.B - avgBgB);
                            if (diffBg < 32 && (c.R > 230 && c.G > 230 && c.B > 230)) continue; // skip pure white backdrop

                            fgR += c.R; fgG += c.G; fgB += c.B; fgN++;
                            float sat = c.GetSaturation();
                            float bri = c.GetBrightness();
                            if (sat < 0.16f) lowSatPixels++;
                            if (bri > 0.80f && sat < 0.18f) brightHighlights++;
                            if (bri < 0.22f) darkPixels++;
                        }
                    }

                    if (fgN == 0) { fgR = avgBgR; fgG = avgBgG; fgB = avgBgB; fgN = 1; }
                    int r = (int)(fgR / fgN);
                    int gr = (int)(fgG / fgN);
                    int b = (int)(fgB / fgN);
                    colorHex = string.Format("#{0:X2}{1:X2}{2:X2}", r, gr, b);
                    colorName = ClassifyTurkishColor(r, gr, b);

                    double lowSatRatio = (double)lowSatPixels / fgN;
                    double highlightRatio = (double)brightHighlights / fgN;
                    double darkRatio = (double)darkPixels / fgN;

                    if (lowSatRatio > 0.55 && highlightRatio > 0.08 && darkRatio > 0.08)
                    {
                        surfaceHint = "Paslanmaz Çelik / Metalik";
                        features.Add("Parlak metalik yüzey yansıması tespit edildi");
                    }
                    else if (lowSatRatio > 0.70 && highlightRatio < 0.05)
                    {
                        features.Add("Mat ve sade yüzey dokusu tespit edildi");
                    }

                    features.Add("Görsel baskın renk tonu: " + colorName);
                    features.Add(string.Format("Orijinal görsel çözünürlüğü: {0}×{1} px", src.Width, src.Height));
                }
#endif
            }
            catch { }
            return features;
        }

        private static void RgbToHsl(int r, int g, int b, out float h, out float s, out float l)
        {
            float rf = r / 255f;
            float gf = g / 255f;
            float bf = b / 255f;
            float max = Math.Max(rf, Math.Max(gf, bf));
            float min = Math.Min(rf, Math.Min(gf, bf));
            float delta = max - min;
            l = (max + min) / 2f;
            if (delta < 0.00001f)
            {
                h = 0f;
                s = 0f;
                return;
            }
            s = l <= 0.5f ? (delta / (max + min)) : (delta / (2f - max - min));
            if (max == rf) h = ((gf - bf) / delta) % 6f;
            else if (max == gf) h = ((bf - rf) / delta) + 2f;
            else h = ((rf - gf) / delta) + 4f;
            h *= 60f;
            if (h < 0f) h += 360f;
        }

        private static string ClassifyTurkishColor(int r, int g, int b)
        {
            float h, s, l;
            RgbToHsl(r, g, b, out h, out s, out l);

            if (l < 0.17f) return "Siyah";
            if (l > 0.86f && s < 0.15f) return "Beyaz";
            if (s < 0.13f)
            {
                if (l > 0.62f) return "Metalik / Gümüş";
                if (l > 0.36f) return "Gri";
                return "Antrasit";
            }

            if (h >= 55 && h <= 105 && s < 0.45f && l >= 0.25f && l <= 0.62f) return "Haki";
            if (h >= 25 && h < 55)
            {
                if (l > 0.68f && s < 0.45f) return "Bej";
                if (l < 0.38f) return "Kahverengi";
                return s > 0.55f ? "Sarı" : "Taba";
            }
            if (h < 18 || h >= 340) return l < 0.35f ? "Bordo" : (l > 0.68f ? "Pembe" : "Kırmızı");
            if (h >= 18 && h < 25) return "Turuncu";
            if (h >= 105 && h < 170) return "Yeşil";
            if (h >= 170 && h < 250) return l < 0.32f ? "Lacivert" : "Mavi";
            if (h >= 250 && h < 310) return l > 0.65f ? "Lila" : "Mor";
            return "Pembe";
        }

        private static List<string> ResolveLocalImagePaths(List<string> images)
        {
            var paths = new List<string>();
            foreach (var img in images)
            {
                if (string.IsNullOrWhiteSpace(img) || !img.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase)) continue;
                string fullPath;
                if (Storage.EnsureUploadFileLocal(Path.GetFileName(img), out fullPath))
                    paths.Add(fullPath);
            }
            return paths;
        }

        private static List<string> PrepareCompactBase64Images(List<string> paths, int maxCount)
        {
            var list = new List<string>();
            foreach (var p in paths.Take(maxCount))
            {
                try
                {
                    byte[] raw = File.ReadAllBytes(p);
#if NET8_0_OR_GREATER
                    using (var img = Image.Load<Rgb24>(raw))
                    {
                        img.Mutate(x => x.AutoOrient());
                        int maxDim = Math.Max(img.Width, img.Height);
                        double scale = maxDim > 768 ? 768.0 / maxDim : 1.0;
                        int w = Math.Max(1, (int)Math.Round(img.Width * scale));
                        int h = Math.Max(1, (int)Math.Round(img.Height * scale));
                        img.Mutate(x => x.Resize(w, h, KnownResamplers.Bicubic));
                        using (var bmp = new Image<Rgb24>(w, h, Color.White))
                        {
                            bmp.Mutate(x => x.DrawImage(img, new Point(0, 0), 1f));
                            using (var outMs = new MemoryStream())
                            {
                                bmp.Save(outMs, new JpegEncoder { Quality = 85 });
                                list.Add(Convert.ToBase64String(outMs.ToArray()));
                            }
                        }
                    }
#else
                    using (var ms = new MemoryStream(raw))
                    using (var img = Image.FromStream(ms, true, true))
                    {
                        int maxDim = Math.Max(img.Width, img.Height);
                        double scale = maxDim > 768 ? 768.0 / maxDim : 1.0;
                        int w = Math.Max(1, (int)Math.Round(img.Width * scale));
                        int h = Math.Max(1, (int)Math.Round(img.Height * scale));
                        using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.Clear(Color.White);
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(img, 0, 0, w, h);
                            using (var outMs = new MemoryStream())
                            {
                                bmp.Save(outMs, ImageFormat.Jpeg);
                                list.Add(Convert.ToBase64String(outMs.ToArray()));
                            }
                        }
                    }
#endif
                }
                catch { }
            }
            return list;
        }

        private static string ExtractCleanNameFromFiles(List<string> originalFileNames)
        {
            if (originalFileNames == null || originalFileNames.Count == 0) return "";
            foreach (var f in originalFileNames)
            {
                string n = Path.GetFileNameWithoutExtension(f ?? "");
                if (string.IsNullOrWhiteSpace(n)) continue;
                // Ignore generic camera filenames like IMG_20261125, WhatsApp Image, DSC0123, screenshot
                if (Regex.IsMatch(n, @"^(img|dsc|pxl|whatsapp|screenshot|ekran|photo|image|media)[\s_\-0-9]*$", RegexOptions.IgnoreCase))
                    continue;
                n = Regex.Replace(n, @"[_\-]+", " ");
                n = Regex.Replace(n, @"\s+", " ").Trim();
                if (n.Length >= 4 && Regex.IsMatch(n, @"[\p{L}]{3,}"))
                    return n;
            }
            return "";
        }

        private static string DetectCategoryFromText(string text)
        {
            string low = (text ?? "").ToLower(new CultureInfo("tr-TR"));
            if (low.Contains("tencere") || low.Contains("tava") || low.Contains("fincan") || low.Contains("kupa") || low.Contains("mutfak") || low.Contains("çelik"))
                return "Ev & Yaşam";
            if (low.Contains("sweatshirt") || low.Contains("tişört") || low.Contains("elbise") || low.Contains("gömlek") || low.Contains("pantolon"))
                return "Giyim & Tekstil";
            if (low.Contains("kulaklık") || low.Contains("telefon") || low.Contains("saat") || low.Contains("bluetooth"))
                return "Elektronik & Teknoloji";
            if (low.Contains("krem") || low.Contains("serum") || low.Contains("parfüm"))
                return "Kozmetik & Kişisel Bakım";
            if (low.Contains("ayakkabı") || low.Contains("çanta") || low.Contains("sneaker"))
                return "Ayakkabı & Çanta";
            return "Genel / Çok Amaçlı";
        }

        private static string PostJson(string url, string body, Dictionary<string, string> headers, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json; charset=utf-8";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            if (headers != null)
            {
                foreach (var kv in headers) req.Headers[kv.Key] = kv.Value;
            }
            byte[] data = Encoding.UTF8.GetBytes(body);
            req.ContentLength = data.Length;
            using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return r.ReadToEnd();
        }

        private static string StripFence(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Trim();
            if (s.StartsWith("```"))
            {
                int nl = s.IndexOf('\n');
                if (nl >= 0) s = s.Substring(nl + 1);
                if (s.EndsWith("```")) s = s.Substring(0, s.Length - 3);
            }
            return s.Trim();
        }

        private static string GetStr(Dictionary<string, object> d, string key, string def)
        {
            if (d != null && d.ContainsKey(key) && d[key] != null)
                return Convert.ToString(d[key]).Trim();
            return def;
        }

        private static decimal GetDec(Dictionary<string, object> d, string key)
        {
            if (d != null && d.ContainsKey(key) && d[key] != null)
            {
                decimal v;
                if (decimal.TryParse(Convert.ToString(d[key]), NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                    return v;
            }
            return 0m;
        }
    }
}
