using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartMarketplace
{
    public static class Security
    {
        private static readonly byte[] _dpapiEntropy = Encoding.UTF8.GetBytes("SmartMarketplace-DPAPI-v1-Salt-2026");
        private static readonly object _rateLock = new object();
        private static readonly Dictionary<string, List<DateTime>> _rateBuckets = new Dictionary<string, List<DateTime>>();

        public const string EncryptedPrefix = "dpapi:v1:";
        public const string MaskedTokenPrefix = "••••••••";
        public static string SessionToken { get; private set; }

        static Security()
        {
            SessionToken = GenerateSecureToken(32);
        }

        public static string GenerateSecureToken(int numBytes)
        {
            var bytes = new byte[numBytes];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(bytes);
            }
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        public static bool FixedTimeEquals(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            byte[] ba = Encoding.UTF8.GetBytes(a);
            byte[] bb = Encoding.UTF8.GetBytes(b);
            if (ba.Length != bb.Length) return false;
            int diff = 0;
            for (int i = 0; i < ba.Length; i++) diff |= (ba[i] ^ bb[i]);
            return diff == 0;
        }

        // ==================== DPAPI ENCRYPTION AT REST ====================
        public static bool IsSecretKeyName(string keyName)
        {
            if (string.IsNullOrWhiteSpace(keyName)) return false;
            string k = keyName.ToLowerInvariant();
            return k.Contains("secret") || k.Contains("password") || k.Contains("token") || k.Contains("apikey") || k.Contains("servicekey") || k.Contains("appkey") || k.Contains("key");
        }

        public static bool IsMaskedPlaceholder(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return value.StartsWith(MaskedTokenPrefix);
        }

        public static string MaskSecret(string plain)
        {
            if (string.IsNullOrWhiteSpace(plain)) return "";
            string trimmed = plain.Trim();
            if (trimmed.Length <= 4) return MaskedTokenPrefix;
            return MaskedTokenPrefix + trimmed.Substring(trimmed.Length - 4);
        }

        public static string EncryptSecret(string plain)
        {
            if (string.IsNullOrWhiteSpace(plain)) return "";
            if (plain.StartsWith(EncryptedPrefix)) return plain;
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(plain);
                byte[] protectedBytes = ProtectedData.Protect(data, _dpapiEntropy, DataProtectionScope.CurrentUser);
                return EncryptedPrefix + Convert.ToBase64String(protectedBytes);
            }
            catch
            {
                // Fallback if DPAPI unavailable in restricted profile
                return plain;
            }
        }

        public static string DecryptSecret(string stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return "";
            if (!stored.StartsWith(EncryptedPrefix)) return stored;
            try
            {
                byte[] protectedBytes = Convert.FromBase64String(stored.Substring(EncryptedPrefix.Length));
                byte[] plainBytes = ProtectedData.Unprotect(protectedBytes, _dpapiEntropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                return "";
            }
        }

        public static void EncryptDatabaseSecrets(AppDatabase db)
        {
            if (db == null) return;
            if (db.AiConfig != null)
            {
                db.AiConfig.GeminiApiKey = EncryptSecret(db.AiConfig.GeminiApiKey);
                db.AiConfig.OpenAiApiKey = EncryptSecret(db.AiConfig.OpenAiApiKey);
            }
            if (db.Marketplaces != null)
            {
                foreach (var mp in db.Marketplaces)
                {
                    if (mp.Credentials == null) continue;
                    var keys = mp.Credentials.Keys.ToList();
                    foreach (var k in keys)
                    {
                        if (IsSecretKeyName(k))
                            mp.Credentials[k] = EncryptSecret(mp.Credentials[k]);
                    }
                }
            }
        }

        public static void DecryptDatabaseSecrets(AppDatabase db)
        {
            if (db == null) return;
            if (db.AiConfig != null)
            {
                db.AiConfig.GeminiApiKey = DecryptSecret(db.AiConfig.GeminiApiKey);
                db.AiConfig.OpenAiApiKey = DecryptSecret(db.AiConfig.OpenAiApiKey);
            }
            if (db.Marketplaces != null)
            {
                foreach (var mp in db.Marketplaces)
                {
                    if (mp.Credentials == null) continue;
                    var keys = mp.Credentials.Keys.ToList();
                    foreach (var k in keys)
                    {
                        if (IsSecretKeyName(k))
                            mp.Credentials[k] = DecryptSecret(mp.Credentials[k]);
                    }
                }
            }
        }

        public static AppDatabase CloneWithMaskedSecrets(AppDatabase db, bool stripCompletely)
        {
            string raw = Storage.Serialize(db);
            var clone = Storage.Deserialize<AppDatabase>(raw);
            if (clone.AiConfig != null)
            {
                clone.AiConfig.GeminiApiKey = stripCompletely ? "" : MaskSecret(clone.AiConfig.GeminiApiKey);
                clone.AiConfig.OpenAiApiKey = stripCompletely ? "" : MaskSecret(clone.AiConfig.OpenAiApiKey);
            }
            if (clone.Marketplaces != null)
            {
                foreach (var mp in clone.Marketplaces)
                {
                    if (mp.Credentials == null) continue;
                    var keys = mp.Credentials.Keys.ToList();
                    foreach (var k in keys)
                    {
                        if (IsSecretKeyName(k))
                            mp.Credentials[k] = stripCompletely ? "" : MaskSecret(mp.Credentials[k]);
                    }
                }
            }
            return clone;
        }

        // ==================== HTTP & NETWORK GUARDS ====================
        public static List<string> GetLocalLanIpv4Addresses()
        {
            var ips = new List<string>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && IsPrivateLanIp(ua.Address))
                        {
                            string ipStr = ua.Address.ToString();
                            if (!ips.Contains(ipStr)) ips.Add(ipStr);
                        }
                    }
                }
            }
            catch { }
            return ips;
        }

        public static bool IsPrivateLanIp(System.Net.IPAddress ip)
        {
            if (ip == null) return false;
            if (System.Net.IPAddress.IsLoopback(ip)) return true;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
            byte[] b = ip.GetAddressBytes();
            // RFC 1918 private LAN ranges only: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            return false;
        }

        public static bool IsValidHostHeader(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            string h = host.Trim().ToLowerInvariant();
            if (h == ("localhost:" + port)
                || h == ("127.0.0.1:" + port)
                || h == "[::1]:" + port
                || (port == 80 && (h == "localhost" || h == "127.0.0.1")))
                return true;

            foreach (var lanIp in GetLocalLanIpv4Addresses())
            {
                if (h == (lanIp + ":" + port) || (port == 80 && h == lanIp))
                    return true;
            }
            return false;
        }

        public static bool IsValidOriginHeader(Dictionary<string, string> headers, int port)
        {
            if (headers == null) return true;
            if (headers.ContainsKey("Origin"))
            {
                string origin = (headers["Origin"] ?? "").Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(origin))
                {
                    bool allowed = origin == ("http://localhost:" + port)
                        || origin == ("http://127.0.0.1:" + port);
                    if (!allowed)
                    {
                        foreach (var lanIp in GetLocalLanIpv4Addresses())
                        {
                            if (origin == ("http://" + lanIp + ":" + port))
                            {
                                allowed = true;
                                break;
                            }
                        }
                    }
                    if (!allowed) return false;
                }
            }
            if (headers.ContainsKey("Sec-Fetch-Site"))
            {
                string sfs = (headers["Sec-Fetch-Site"] ?? "").Trim().ToLowerInvariant();
                if (sfs == "cross-site") return false;
            }
            return true;
        }

        public static bool IsValidSessionToken(Dictionary<string, string> headers)
        {
            if (headers == null || !headers.ContainsKey("X-Smart-Token")) return false;
            return FixedTimeEquals(headers["X-Smart-Token"].Trim(), SessionToken);
        }

        public static bool CheckRateLimit(string bucket, int maxPerMinute)
        {
            lock (_rateLock)
            {
                DateTime now = DateTime.UtcNow;
                DateTime cutoff = now.AddMinutes(-1);
                if (!_rateBuckets.ContainsKey(bucket)) _rateBuckets[bucket] = new List<DateTime>();
                var list = _rateBuckets[bucket];
                list.RemoveAll(t => t < cutoff);
                if (list.Count >= maxPerMinute) return false;
                list.Add(now);
                return true;
            }
        }

        // ==================== PATH TRAVERSAL & UPLOAD SAFETY ====================
        public static bool TryResolveSafePath(string allowedRoot, string relativePath, out string fullPath)
        {
            fullPath = null;
            if (string.IsNullOrWhiteSpace(allowedRoot) || relativePath == null) return false;
            if (relativePath.Contains("\0") || relativePath.Contains("..") || relativePath.Contains(":")) return false;

            try
            {
                string rootFull = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string cleaned = relativePath.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
                string candidate = Path.GetFullPath(Path.Combine(rootFull, cleaned));
                if (!candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return false;
                fullPath = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool ValidateImageBytes(byte[] bytes, out string safeExtension, out string error)
        {
            safeExtension = null;
            error = null;
            if (bytes == null || bytes.Length < 12)
            {
                error = "Geçersiz veya bozuk görsel dosyası.";
                return false;
            }
            if (bytes.Length > 5 * 1024 * 1024)
            {
                error = "Görsel boyutu 5 MB sınırını aşıyor.";
                return false;
            }

            // JPEG: FF D8 FF
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                safeExtension = ".jpg";
                return true;
            }
            // PNG: 89 50 4E 47 0D 0A 1A 0A
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
                && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            {
                safeExtension = ".png";
                return true;
            }
            // WEBP: RIFF....WEBP
            if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
                && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            {
                safeExtension = ".webp";
                return true;
            }

            error = "Güvenlik nedeniyle yalnızca geçerli JPG, PNG veya WEBP görselleri kabul edilir.";
            return false;
        }

        // ==================== SANITIZATION & LOG REDACTION ====================
        public static string SanitizeId(string raw, string prefix)
        {
            string clean = Regex.Replace(raw ?? "", @"[^A-Za-z0-9_\-]", "");
            if (string.IsNullOrWhiteSpace(clean))
                clean = prefix + Guid.NewGuid().ToString("N").Substring(0, 10);
            if (clean.Length > 48) clean = clean.Substring(0, 48);
            return clean;
        }

        public static bool IsSafeImageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (url.StartsWith("/uploads/") && !url.Contains("..") && !url.Contains("\\") && !url.Contains("\"") && !url.Contains("'"))
                return true;
            Uri parsed;
            if (Uri.TryCreate(url, UriKind.Absolute, out parsed))
                return parsed.Scheme == Uri.UriSchemeHttps;
            return false;
        }

        public static string RedactSensitiveText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text;
            s = Regex.Replace(s, @"(Authorization\s*:\s*(?:Basic|Bearer)\s+)[^\s""']+", "$1[GİZLENDİ]", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"((?:api[_-]?key|api[_-]?secret|access[_-]?token|refresh[_-]?token|password|client[_-]?secret)[""']?\s*[:=]\s*[""']?)[^""'\s,&}]+", "$1[GİZLENDİ]", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"(AIza[0-9A-Za-z\-_]{20,})", "[GİZLENDİ]");
            s = Regex.Replace(s, @"(sk-[0-9A-Za-z\-_]{20,})", "[GİZLENDİ]");
            return s;
        }

        public static void SanitizeImportedDatabase(AppDatabase db)
        {
            if (db == null) return;
            if (db.Products != null)
            {
                foreach (var p in db.Products)
                {
                    p.Id = SanitizeId(p.Id, "prd_");
                    p.Stock = Math.Max(0, Math.Min(1000000, p.Stock));
                    p.Price = Math.Max(0, Math.Min(10000000, p.Price));
                    p.ListPrice = Math.Max(0, Math.Min(10000000, p.ListPrice));
                    p.VatRate = Math.Max(0, Math.Min(100, p.VatRate));
                    p.Images = (p.Images ?? new List<string>()).Where(IsSafeImageUrl).Take(12).ToList();
                }
            }
            if (db.Draft != null && db.Draft.UploadedImages != null)
            {
                db.Draft.UploadedImages = db.Draft.UploadedImages.Where(IsSafeImageUrl).Take(12).ToList();
            }
            if (db.SyncConfig != null)
            {
                db.SyncConfig.IntervalMinutes = Math.Max(1, Math.Min(1440, db.SyncConfig.IntervalMinutes));
                db.SyncConfig.LowStockThreshold = Math.Max(0, Math.Min(100000, db.SyncConfig.LowStockThreshold));
            }
        }
    }
}
