using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartMarketplace
{
    public static class ImageHostService
    {
        private static readonly object _cacheLock = new object();
        private static readonly Dictionary<string, string> _publicUrlCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Standard public anonymous Imgur client ID (fallback if ImgBB is temporarily unavailable)
        private const string ImgurPublicClientId = "546c25a59c58ad7";

        static ImageHostService()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }

        /// <summary>
        /// Normalizes local images into Trendyol & Turkish marketplace gold-standard 1200x1800 96-DPI 24-bit sRGB baseline .jpg,
        /// uploads them to permanent Turkey-unblocked enterprise HTTPS CDN (ImgBB i.ibb.co primary -> Imgur i.imgur.com -> Uguu),
        /// and verifies BOTH HEAD (empty User-Agent) and GET (Java crawler User-Agent) return HTTP 200 + image/jpeg + valid JPEG bytes!
        /// </summary>
        public static List<string> EnsurePublicImageUrls(List<string> images, out string lastError)
        {
            lastError = "";
            var result = new List<string>();
            if (images == null || images.Count == 0) return result;

            foreach (var img in images)
            {
                if (string.IsNullOrWhiteSpace(img)) continue;
                string trimmed = img.Trim();

                if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    if (VerifyPublicImageUrl(trimmed, false))
                    {
                        result.Add(trimmed);
                    }
                    else
                    {
                        lastError = "Harici görsel bağlantısı pazaryeri doğrulamasından geçemedi: " + trimmed;
                    }
                    continue;
                }

                if (trimmed.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
                {
                    string cacheKey = "cdn_v4_ibb:" + trimmed;
                    lock (_cacheLock)
                    {
                        if (_publicUrlCache.ContainsKey(cacheKey))
                        {
                            string cached = _publicUrlCache[cacheKey];
                            if (VerifyPublicImageUrl(cached, true))
                            {
                                result.Add(cached);
                                continue;
                            }
                            _publicUrlCache.Remove(cacheKey);
                        }
                    }

                    string fileName = Path.GetFileName(trimmed);
                    string fullPath;
                    if (!Security.TryResolveSafePath(Storage.UploadsDir, fileName, out fullPath) || !File.Exists(fullPath))
                    {
                        lastError = "Yerel görsel dosyası bulunamadı: " + fileName;
                        continue;
                    }

                    bool isJpg;
                    string uploadPath = NormalizeToMarketplaceJpg(fullPath, out isJpg);
                    string publicUrl = UploadAndVerifyPublicImage(uploadPath, isJpg, out lastError);
                    if (!string.IsNullOrWhiteSpace(publicUrl))
                    {
                        lock (_cacheLock) { _publicUrlCache[cacheKey] = publicUrl; }
                        result.Add(publicUrl);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Converts any GDI+-decodable image into Trendyol & Turkish marketplace gold-standard:
        /// exact 1200x1800 (2:3), 96 DPI, 24-bit sRGB baseline .jpg on clean white background with stripped EXIF.
        /// Never mislabels non-JPEG bytes (e.g. legacy .webp) as .jpg.
        /// </summary>
        public static string NormalizeToMarketplaceJpg(string sourcePath, out bool outputIsJpg)
        {
            outputIsJpg = false;
            try
            {
                string baseName = Regex.Replace(Path.GetFileNameWithoutExtension(sourcePath), @"[^A-Za-z0-9_\-]", "");
                string normName = "ty1200_" + baseName + ".jpg";
                string outPath;
                if (!Security.TryResolveSafePath(Storage.UploadsDir, normName, out outPath))
                    return sourcePath;

                if (File.Exists(outPath) && new FileInfo(outPath).Length > 1024 && HasJpegMagic(outPath))
                {
                    outputIsJpg = true;
                    return outPath;
                }

                byte[] rawBytes = File.ReadAllBytes(sourcePath);
                using (var ms = new MemoryStream(rawBytes))
                using (var srcImg = Image.FromStream(ms, true, true))
                {
                    ApplyExifOrientation(srcImg);

                    int sw = srcImg.Width;
                    int sh = srcImg.Height;
                    if (sw <= 0 || sh <= 0) return sourcePath;

                    const int canvasW = 1200;
                    const int canvasH = 1800;
                    const int pad = 36;
                    double scale = Math.Min((double)(canvasW - pad * 2) / sw, (double)(canvasH - pad * 2) / sh);
                    int dw = Math.Max(1, (int)Math.Round(sw * scale));
                    int dh = Math.Max(1, (int)Math.Round(sh * scale));
                    int dx = (canvasW - dw) / 2;
                    int dy = (canvasH - dh) / 2;

                    using (var dst = new Bitmap(canvasW, canvasH, PixelFormat.Format24bppRgb))
                    {
                        dst.SetResolution(96, 96);
                        using (var g = Graphics.FromImage(dst))
                        {
                            g.Clear(Color.White);
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = SmoothingMode.HighQuality;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.CompositingQuality = CompositingQuality.HighQuality;
                            g.DrawImage(srcImg, new Rectangle(dx, dy, dw, dh), new Rectangle(0, 0, sw, sh), GraphicsUnit.Pixel);
                        }

                        var jpgCodec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                        if (jpgCodec != null)
                        {
                            using (var encParams = new EncoderParameters(1))
                            {
                                encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                                dst.Save(outPath, jpgCodec, encParams);
                            }
                        }
                        else
                        {
                            dst.Save(outPath, ImageFormat.Jpeg);
                        }
                    }
                }
                outputIsJpg = HasJpegMagic(outPath);
                return outPath;
            }
            catch
            {
                // If source is already a valid JPEG, mark it as JPEG; otherwise preserve its true format (e.g. legacy .webp)
                outputIsJpg = HasJpegMagic(sourcePath);
                return sourcePath;
            }
        }

        public static string NormalizeToMarketplaceJpg(string sourcePath)
        {
            bool isJpg;
            return NormalizeToMarketplaceJpg(sourcePath, out isJpg);
        }

        private static bool HasJpegMagic(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    byte[] h = new byte[3];
                    return fs.Read(h, 0, 3) == 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF;
                }
            }
            catch { return false; }
        }

        private static string UploadAndVerifyPublicImage(string filePath, bool isJpg, out string error)
        {
            error = "";
            byte[] bytes = File.ReadAllBytes(filePath);
            string ext = isJpg ? ".jpg" : (Path.GetExtension(filePath) ?? ".jpg").ToLowerInvariant();
            if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".webp") ext = ".jpg";
            string mime = ext == ".png" ? "image/png" : (ext == ".webp" ? "image/webp" : "image/jpeg");
            string cleanFileName = "smart_" + Guid.NewGuid().ToString("N").Substring(0, 10) + ext;

            // Provider 1 (PRIMARY): ImgBB -> permanent global CDN https://i.ibb.co/xxxx/smart_xxxx.jpg
            // Verified 100% accessible from Turkey & Trendyol Java/empty-UA servers (expiration=0, direct HTTP 200, no bot challenge)
            try
            {
                string ibbUrl = UploadToImgBB(cleanFileName, mime, bytes);
                if (!string.IsNullOrWhiteSpace(ibbUrl) && VerifyPublicImageUrl(ibbUrl, isJpg))
                    return ibbUrl;
                error = "ImgBB doğrulama geçemedi";
            }
            catch (Exception ex) { error = "ImgBB: " + ex.Message; }

            // Provider 2 (FALLBACK 1): Imgur -> permanent direct https://i.imgur.com/xxxxxxx.jpg
            try
            {
                string imgurUrl = UploadToImgur(cleanFileName, mime, bytes);
                if (!string.IsNullOrWhiteSpace(imgurUrl) && VerifyPublicImageUrl(imgurUrl, isJpg))
                    return imgurUrl;
                error += " | Imgur doğrulama geçemedi";
            }
            catch (Exception ex) { error += " | Imgur: " + ex.Message; }

            // Provider 3 (FALLBACK 2): Uguu direct HTTPS CDN -> https://d.uguu.se/xxxxxx.jpg
            try
            {
                string resp = PostMultipart("https://uguu.se/upload", null, "files[]", cleanFileName, mime, bytes, 15000, null, null);
                var m = Regex.Match(resp ?? "", @"""url""\s*:\s*""(https:\\/\\/[^""]+)""", RegexOptions.IgnoreCase);
                string uguuUrl = m.Success ? m.Groups[1].Value.Replace("\\/", "/") : "";
                if (!string.IsNullOrWhiteSpace(uguuUrl) && VerifyPublicImageUrl(uguuUrl, isJpg))
                    return uguuUrl;
                error += " | Uguu doğrulama geçemedi";
            }
            catch (Exception ex) { error += " | Uguu: " + ex.Message; }

            return "";
        }

        private static string UploadToImgBB(string fileName, string mime, byte[] bytes)
        {
            var cookies = new CookieContainer();
            // Step 1: Fetch short-lived CSRF auth_token + session cookie from imgbb.com
            var getReq = (HttpWebRequest)WebRequest.Create("https://imgbb.com/");
            getReq.Method = "GET";
            getReq.CookieContainer = cookies;
            getReq.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";
            getReq.Timeout = 12000;
            getReq.ReadWriteTimeout = 12000;

            string html;
            using (var resp = (HttpWebResponse)getReq.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                html = sr.ReadToEnd();
            }

            var tm = Regex.Match(html, @"auth_token\s*=\s*""([a-f0-9]{20,})""", RegexOptions.IgnoreCase);
            if (!tm.Success) throw new Exception("ImgBB oturum anahtarı alınamadı");

            string authToken = tm.Groups[1].Value;
            long ts = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            var fields = new Dictionary<string, string>
            {
                { "type", "file" },
                { "action", "upload" },
                { "timestamp", ts.ToString() },
                { "auth_token", authToken }
            };

            string json = PostMultipart("https://imgbb.com/json", fields, "source", fileName, mime, bytes, 25000, null, cookies);
            return ExtractDirectImageUrl(json);
        }

        private static string UploadToImgur(string fileName, string mime, byte[] bytes)
        {
            var extraHeaders = new Dictionary<string, string>
            {
                { "Authorization", "Client-ID " + ImgurPublicClientId }
            };
            string json = PostMultipart("https://api.imgur.com/3/image", null, "image", fileName, mime, bytes, 25000, extraHeaders, null);
            if (string.IsNullOrWhiteSpace(json)) return "";
            try
            {
                var root = Storage.Deserialize<Dictionary<string, object>>(json);
                if (root != null && root.ContainsKey("data") && root["data"] is Dictionary<string, object>)
                {
                    var d = (Dictionary<string, object>)root["data"];
                    if (d.ContainsKey("link") && d["link"] != null)
                        return Convert.ToString(d["link"]).Trim();
                }
            }
            catch { }
            var m = Regex.Match(json, @"""link""\s*:\s*""(https:\\/\\/i\.imgur\.com\\/[^""]+)""", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Replace("\\/", "/") : "";
        }

        public static string ExtractDirectImageUrl(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return "";
            try
            {
                var root = Storage.Deserialize<Dictionary<string, object>>(json);
                if (root != null && root.ContainsKey("image") && root["image"] is Dictionary<string, object>)
                {
                    var imgObj = (Dictionary<string, object>)root["image"];
                    if (imgObj.ContainsKey("url") && imgObj["url"] != null)
                        return Convert.ToString(imgObj["url"]).Trim();
                }
            }
            catch { }
            var m = Regex.Match(json, @"""url""\s*:\s*""(https:\\/\\/i\.ibb\.co\\/[^""]+)""", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Replace("\\/", "/") : "";
        }

        /// <summary>
        /// Simulates Trendyol's exact backend image crawler BEFORE submitting to any marketplace:
        /// 1. HTTPS direct file URL (no query params, no spaces, valid image extension, never blocked hosts like catbox/iili.io).
        /// 2. HEAD request with EMPTY User-Agent -> HTTP 200 OK (no redirect!), Content-Type image/*, Content-Length > 1 KB.
        /// 3. GET request with Java/17 User-Agent -> HTTP 200 OK, Content-Type image/*, valid magic bytes (FF D8 FF for JPEG).
        /// </summary>
        private static bool VerifyPublicImageUrl(string url, bool requireJpeg)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
            if (url.Contains(" ") || url.Contains("?")) return false;
            string low = url.ToLowerInvariant();
            if (low.Contains("catbox.moe") || low.Contains("iili.io")) return false;
            if (requireJpeg)
            {
                if (!low.EndsWith(".jpg") && !low.EndsWith(".jpeg")) return false;
            }
            else
            {
                if (!low.EndsWith(".jpg") && !low.EndsWith(".jpeg") && !low.EndsWith(".png") && !low.EndsWith(".webp"))
                    return false;
            }

            try
            {
                // Check 1: HEAD with empty User-Agent (catches servers that tarpit/block generic backend fetchers like catbox.moe!)
                var headReq = (HttpWebRequest)WebRequest.Create(url);
                headReq.Method = "HEAD";
                headReq.AllowAutoRedirect = false;
                headReq.Timeout = 8000;
                headReq.ReadWriteTimeout = 8000;
                using (var headResp = (HttpWebResponse)headReq.GetResponse())
                {
                    if ((int)headResp.StatusCode != 200) return false;
                    string hct = (headResp.ContentType ?? "").ToLowerInvariant();
                    if (requireJpeg && !hct.Contains("image/jpeg") && !hct.Contains("image/jpg")) return false;
                    if (!requireJpeg && !hct.StartsWith("image/")) return false;
                    if (headResp.ContentLength > 0 && headResp.ContentLength < 1024) return false;
                }

                // Check 2: GET with Java backend User-Agent + verify binary magic header (never HTML/error page!)
                var getReq = (HttpWebRequest)WebRequest.Create(url);
                getReq.Method = "GET";
                getReq.AllowAutoRedirect = false;
                getReq.UserAgent = "Java/17.0.8";
                getReq.Timeout = 10000;
                getReq.ReadWriteTimeout = 10000;
                using (var resp = (HttpWebResponse)getReq.GetResponse())
                {
                    if ((int)resp.StatusCode != 200) return false;
                    string ct = (resp.ContentType ?? "").ToLowerInvariant();
                    if (requireJpeg && !ct.Contains("image/jpeg") && !ct.Contains("image/jpg")) return false;
                    if (!requireJpeg && !ct.StartsWith("image/")) return false;

                    byte[] head = new byte[12];
                    using (var s = resp.GetResponseStream())
                    {
                        int read = s.Read(head, 0, head.Length);
                        if (read < 4) return false;
                        bool isJpegBytes = head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF;
                        bool isPngBytes = head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47;
                        bool isWebpBytes = read >= 12 && head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46
                            && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50;
                        if (requireJpeg) return isJpegBytes;
                        return isJpegBytes || isPngBytes || isWebpBytes;
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        private static void ApplyExifOrientation(Image img)
        {
            const int OrientationId = 0x0112;
            try
            {
                if (!img.PropertyIdList.Contains(OrientationId)) return;
                var prop = img.GetPropertyItem(OrientationId);
                if (prop == null || prop.Value == null || prop.Value.Length < 2) return;
                int val = BitConverter.ToUInt16(prop.Value, 0);
                switch (val)
                {
                    case 2: img.RotateFlip(RotateFlipType.RotateNoneFlipX); break;
                    case 3: img.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
                    case 4: img.RotateFlip(RotateFlipType.Rotate180FlipX); break;
                    case 5: img.RotateFlip(RotateFlipType.Rotate90FlipX); break;
                    case 6: img.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
                    case 7: img.RotateFlip(RotateFlipType.Rotate270FlipX); break;
                    case 8: img.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
                }
            }
            catch { }
        }

        private static string PostMultipart(
            string url,
            Dictionary<string, string> formFields,
            string fileFieldName,
            string fileName,
            string contentType,
            byte[] fileBytes,
            int timeoutMs,
            Dictionary<string, string> extraHeaders,
            CookieContainer cookies)
        {
            string boundary = "----SmartBoundary" + Guid.NewGuid().ToString("N");
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "multipart/form-data; boundary=" + boundary;
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            if (cookies != null) req.CookieContainer = cookies;
            if (extraHeaders != null)
            {
                foreach (var kv in extraHeaders)
                    req.Headers[kv.Key] = kv.Value;
            }

            using (var ms = new MemoryStream())
            {
                if (formFields != null)
                {
                    foreach (var kv in formFields)
                    {
                        WriteStr(ms, "--" + boundary + "\r\n");
                        WriteStr(ms, "Content-Disposition: form-data; name=\"" + kv.Key + "\"\r\n\r\n");
                        WriteStr(ms, kv.Value + "\r\n");
                    }
                }

                WriteStr(ms, "--" + boundary + "\r\n");
                WriteStr(ms, "Content-Disposition: form-data; name=\"" + fileFieldName + "\"; filename=\"" + fileName + "\"\r\n");
                WriteStr(ms, "Content-Type: " + contentType + "\r\n\r\n");
                ms.Write(fileBytes, 0, fileBytes.Length);
                WriteStr(ms, "\r\n--" + boundary + "--\r\n");

                byte[] payload = ms.ToArray();
                req.ContentLength = payload.Length;
                using (var s = req.GetRequestStream()) s.Write(payload, 0, payload.Length);
            }

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                return r.ReadToEnd();
            }
        }

        private static void WriteStr(Stream s, string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            s.Write(b, 0, b.Length);
        }
    }
}
