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
        // Official public FreeImage.host API key (permanent direct https://iili.io/xxxx.jpg Cloudflare CDN, expiration=0)
        private const string FreeImagePublicKey = "6d207e02198a847aa98d0a2a901485a5";

        static ImageHostService()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }

        /// <summary>
        /// Normalizes local images into Trendyol gold-standard 1200x1800 96-DPI 24-bit sRGB baseline .jpg,
        /// uploads them to permanent direct HTTPS CDN (iili.io primary), and verifies HTTP 200 + image/jpeg + JPEG magic bytes!
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
                    result.Add(trimmed);
                    continue;
                }

                if (trimmed.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
                {
                    string cacheKey = "iili_verified_v3:" + trimmed;
                    lock (_cacheLock)
                    {
                        if (_publicUrlCache.ContainsKey(cacheKey))
                        {
                            result.Add(_publicUrlCache[cacheKey]);
                            continue;
                        }
                    }

                    string fileName = Path.GetFileName(trimmed);
                    string fullPath;
                    if (!Security.TryResolveSafePath(Storage.UploadsDir, fileName, out fullPath) || !File.Exists(fullPath))
                    {
                        lastError = "Yerel görsel dosyası bulunamadı: " + fileName;
                        continue;
                    }

                    string normalizedJpgPath = NormalizeToMarketplaceJpg(fullPath);
                    string uploadPath = !string.IsNullOrEmpty(normalizedJpgPath) && File.Exists(normalizedJpgPath) ? normalizedJpgPath : fullPath;

                    string publicUrl = UploadAndVerifyPublicJpg(uploadPath, out lastError);
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
        /// Converts any image into Trendyol & Turkish marketplace gold-standard:
        /// exact 1200x1800 (2:3), 96 DPI, 24-bit sRGB baseline .jpg on clean white background with stripped EXIF.
        /// </summary>
        public static string NormalizeToMarketplaceJpg(string sourcePath)
        {
            try
            {
                string baseName = Regex.Replace(Path.GetFileNameWithoutExtension(sourcePath), @"[^A-Za-z0-9_\-]", "");
                string normName = "ty1200_" + baseName + ".jpg";
                string outPath;
                if (!Security.TryResolveSafePath(Storage.UploadsDir, normName, out outPath))
                    return sourcePath;

                if (File.Exists(outPath) && new FileInfo(outPath).Length > 1024)
                    return outPath;

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
                return outPath;
            }
            catch
            {
                return sourcePath;
            }
        }

        private static string UploadAndVerifyPublicJpg(string filePath, out string error)
        {
            error = "";
            byte[] bytes = File.ReadAllBytes(filePath);
            string cleanFileName = "product_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".jpg";

            // Provider 1 (PRIMARY): FreeImage.host -> permanent global Cloudflare CDN https://iili.io/xxxxx.jpg (expiration=0, direct JPEG)
            try
            {
                var fields = new Dictionary<string, string>
                {
                    { "key", FreeImagePublicKey },
                    { "action", "upload" },
                    { "format", "json" }
                };
                string resp = PostMultipart("https://freeimage.host/api/1/upload", fields, "source", cleanFileName, "image/jpeg", bytes, 20000);
                string url = ExtractDirectImageUrl(resp);
                if (!string.IsNullOrWhiteSpace(url) && VerifyPublicJpgUrl(url))
                    return url;
                error = "FreeImage doğrulama geçemedi";
            }
            catch (Exception ex) { error = "FreeImage: " + ex.Message; }

            // Provider 2 (FALLBACK): Catbox.moe -> permanent direct https://files.catbox.moe/xxxx.jpg (only accepted if live check passes!)
            try
            {
                var fields = new Dictionary<string, string> { { "reqtype", "fileupload" } };
                string resp = PostMultipart("https://catbox.moe/user/api.php", fields, "fileToUpload", cleanFileName, "image/jpeg", bytes, 15000);
                string url = (resp ?? "").Trim();
                if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && VerifyPublicJpgUrl(url))
                    return url;
                error += " | Catbox doğrulama geçemedi";
            }
            catch (Exception ex) { error += " | Catbox: " + ex.Message; }

            // Provider 3 (LAST RESORT): Litterbox 72h direct .jpg (only accepted if live check passes!)
            try
            {
                var fields = new Dictionary<string, string> { { "reqtype", "fileupload" }, { "time", "72h" } };
                string resp = PostMultipart("https://litterbox.catbox.moe/resources/internals/api.php", fields, "fileToUpload", cleanFileName, "image/jpeg", bytes, 15000);
                string url = (resp ?? "").Trim();
                if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && VerifyPublicJpgUrl(url))
                    return url;
            }
            catch (Exception ex) { error += " | Litterbox: " + ex.Message; }

            return "";
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
            var m = Regex.Match(json, @"""url""\s*:\s*""(https:\\/\\/iili\.io\\/[^""]+\.jpg)""", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Replace("\\/", "/") : "";
        }

        /// <summary>
        /// Verifies the uploaded image URL exactly as Trendyol's approval crawler does:
        /// HTTPS, ends with .jpg, no query/spaces, HTTP 200 OK (no HTML redirect), Content-Type image/jpeg, valid JPEG FF D8 FF magic bytes!
        /// </summary>
        private static bool VerifyPublicJpgUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
            if (url.Contains(" ") || url.Contains("?")) return false;
            string low = url.ToLowerInvariant();
            if (!low.EndsWith(".jpg") && !low.EndsWith(".jpeg")) return false;

            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.AllowAutoRedirect = false; // Must be a direct image file, NOT a redirect to an HTML page!
                req.UserAgent = "Mozilla/5.0 (compatible; TrendyolImageBot/1.0)";
                req.Timeout = 10000;
                req.ReadWriteTimeout = 10000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if ((int)resp.StatusCode != 200) return false;
                    string ct = (resp.ContentType ?? "").ToLowerInvariant();
                    if (!ct.Contains("image/jpeg") && !ct.Contains("image/jpg")) return false;

                    byte[] head = new byte[4];
                    using (var s = resp.GetResponseStream())
                    {
                        int read = s.Read(head, 0, 4);
                        if (read < 3) return false;
                        return head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF;
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

        private static string PostMultipart(string url, Dictionary<string, string> formFields, string fileFieldName, string fileName, string contentType, byte[] fileBytes, int timeoutMs)
        {
            string boundary = "----SmartBoundary" + Guid.NewGuid().ToString("N");
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "multipart/form-data; boundary=" + boundary;
            req.UserAgent = "SmartOtomasyon/3.3";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;

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
