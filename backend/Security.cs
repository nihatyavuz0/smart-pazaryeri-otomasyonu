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
        private static readonly Dictionary<string, List<DateTime>> _rateBuckets = new Dictionary<string, List<DateTime>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, DateTime> _lockoutUntil = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static DateTime _lastRateSweepUtc = DateTime.UtcNow;
        private const int MaxRateBuckets = 10000;
        private static readonly string _dummyPbkdf2Hash;

        public const string EncryptedPrefix = "dpapi:v1:";
        public const string AesEncryptedPrefix = "aesgcm:v1:";
        public const string MaskedTokenPrefix = "••••••••";
        public static bool IsCloudMode { get; private set; }
        public static string SessionToken { get; private set; }
        public static string RemoteAuthCookieToken { get; private set; }
        public static volatile string ActiveTunnelHost = "";
        private static readonly byte[] _cloudKeyBytes;
        private static readonly byte[] _cloudSessionKeyBytes;
        private static readonly byte[] _csrfKeyBytes;

        static Security()
        {
            string vercelEnv = Environment.GetEnvironmentVariable("VERCEL") ?? "";
            string cloudFlag = Environment.GetEnvironmentVariable("SMART_CLOUD_MODE") ?? "";
            IsCloudMode = vercelEnv == "1"
                || cloudFlag == "1"
                || cloudFlag.Equals("true", StringComparison.OrdinalIgnoreCase)
                || CloudBlobStorage.IsConfigured;

            string encSeed = (Environment.GetEnvironmentVariable("SMART_ENCRYPTION_KEY") ?? "").Trim();
            string sessSeed = (Environment.GetEnvironmentVariable("SMART_SESSION_SECRET") ?? "").Trim();
            string blobSeed = (Environment.GetEnvironmentVariable("BLOB_READ_WRITE_TOKEN") ?? "").Trim();

            using (var sha = SHA256.Create())
            {
                string keyMat = !string.IsNullOrEmpty(encSeed)
                    ? encSeed
                    : (!string.IsNullOrEmpty(sessSeed)
                        ? sessSeed
                        : (!string.IsNullOrEmpty(blobSeed) ? blobSeed : GenerateSecureToken(32)));
                _cloudKeyBytes = sha.ComputeHash(Encoding.UTF8.GetBytes("enc:" + keyMat));

                string sessMat = !string.IsNullOrEmpty(sessSeed)
                    ? sessSeed
                    : (!string.IsNullOrEmpty(encSeed)
                        ? encSeed
                        : (!string.IsNullOrEmpty(blobSeed) ? blobSeed : ""));
                _cloudSessionKeyBytes = !string.IsNullOrEmpty(sessMat) ? sha.ComputeHash(Encoding.UTF8.GetBytes("sess:" + sessMat)) : null;

                byte[] csrfSeed = _cloudSessionKeyBytes ?? sha.ComputeHash(Encoding.UTF8.GetBytes("local-csrf:" + GenerateSecureToken(32)));
                _csrfKeyBytes = sha.ComputeHash(Encoding.UTF8.GetBytes("csrf:" + Convert.ToBase64String(csrfSeed)));
            }

            if (IsCloudMode && _cloudSessionKeyBytes != null)
            {
                SessionToken = ComputeHmacHex(_cloudSessionKeyBytes, "smart-session-token-v1");
                RemoteAuthCookieToken = ComputeHmacHex(_cloudSessionKeyBytes, "smart-remote-cookie-default-v1");
            }
            else
            {
                SessionToken = GenerateSecureToken(32);
                RemoteAuthCookieToken = GenerateSecureToken(32);
            }

            _dummyPbkdf2Hash = HashPassword("dummy-timing-shield-" + GenerateSecureToken(12));
        }

        public static void VerifyDummyPassword(string password)
        {
            // Constant-work timing shield so attackers cannot enumerate valid usernames by latency
            VerifyPassword(string.IsNullOrEmpty(password) ? "x" : password, _dummyPbkdf2Hash);
        }

        public static string GetUserCsrfToken(string username, int sessionVersion)
        {
            string norm = UserAuthService.NormalizeUsername(username);
            if (string.IsNullOrEmpty(norm)) return SessionToken;
            return ComputeHmacHex(_csrfKeyBytes, "smart-csrf-v2:" + norm + ":" + Math.Max(1, sessionVersion));
        }

        public static bool IsValidUserSessionToken(Dictionary<string, string> headers, UserAccountRecord user)
        {
            if (headers == null || !headers.ContainsKey("X-Smart-Token")) return false;
            string provided = (headers["X-Smart-Token"] ?? "").Trim();
            if (string.IsNullOrEmpty(provided)) return false;
            if (user != null)
            {
                string expectedUserToken = GetUserCsrfToken(user.Username, user.SessionVersion);
                if (FixedTimeEquals(provided, expectedUserToken)) return true;
            }
            return false;
        }

        private static string ComputeHmacHex(byte[] key, string message)
        {
            using (var hmac = new HMACSHA256(key))
            {
                byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message ?? ""));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        public static string SignUserSessionPayload(string payload)
        {
            byte[] key = _cloudSessionKeyBytes ?? _cloudKeyBytes;
            return ComputeHmacHex(key, "smart-user-sess-v1:" + (payload ?? ""));
        }

        public static string HashPassword(string password)
        {
            const int iterations = 100000;
            byte[] salt = new byte[16];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(salt);
            }
            byte[] hash;
#if NET8_0_OR_GREATER
            using (var pbkdf2 = new Rfc2898DeriveBytes(password ?? "", salt, iterations, HashAlgorithmName.SHA256))
            {
                hash = pbkdf2.GetBytes(32);
            }
#else
            using (var pbkdf2 = new Rfc2898DeriveBytes(password ?? "", salt, iterations))
            {
                hash = pbkdf2.GetBytes(32);
            }
#endif
            string algo =
#if NET8_0_OR_GREATER
                "pbkdf2-sha256";
#else
                "pbkdf2-sha1";
#endif
            return algo + ":" + iterations + ":" + Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash);
        }

        public static bool VerifyPassword(string password, string storedHash)
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHash)) return false;
            try
            {
                var parts = storedHash.Split(':');
                if (parts.Length != 4) return false;
                string algo = parts[0];
                int iterations;
                if (!int.TryParse(parts[1], out iterations) || iterations < 10000 || iterations > 500000) return false;
                byte[] salt = Convert.FromBase64String(parts[2]);
                byte[] expected = Convert.FromBase64String(parts[3]);
                byte[] actual;
#if NET8_0_OR_GREATER
                var hashAlg = algo == "pbkdf2-sha1" ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256;
                using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, hashAlg))
                {
                    actual = pbkdf2.GetBytes(expected.Length);
                }
#else
                if (algo == "pbkdf2-sha256")
                {
                    actual = Pbkdf2Sha256Fallback(password, salt, iterations, expected.Length);
                }
                else
                {
                    using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations))
                    {
                        actual = pbkdf2.GetBytes(expected.Length);
                    }
                }
#endif
                if (actual.Length != expected.Length) return false;
                int diff = 0;
                for (int i = 0; i < actual.Length; i++) diff |= (actual[i] ^ expected[i]);
                return diff == 0;
            }
            catch
            {
                return false;
            }
        }

#if !NET8_0_OR_GREATER
        private static byte[] Pbkdf2Sha256Fallback(string password, byte[] salt, int iterations, int outputBytes)
        {
            byte[] pwdBytes = Encoding.UTF8.GetBytes(password);
            using (var hmac = new HMACSHA256(pwdBytes))
            {
                int hashLen = 32;
                int blockCount = (outputBytes + hashLen - 1) / hashLen;
                byte[] result = new byte[outputBytes];
                int offset = 0;
                for (int block = 1; block <= blockCount; block++)
                {
                    byte[] blockSalt = new byte[salt.Length + 4];
                    Buffer.BlockCopy(salt, 0, blockSalt, 0, salt.Length);
                    blockSalt[salt.Length] = (byte)(block >> 24);
                    blockSalt[salt.Length + 1] = (byte)(block >> 16);
                    blockSalt[salt.Length + 2] = (byte)(block >> 8);
                    blockSalt[salt.Length + 3] = (byte)(block);
                    byte[] u = hmac.ComputeHash(blockSalt);
                    byte[] t = (byte[])u.Clone();
                    for (int i = 1; i < iterations; i++)
                    {
                        u = hmac.ComputeHash(u);
                        for (int j = 0; j < hashLen; j++) t[j] ^= u[j];
                    }
                    int copyLen = Math.Min(hashLen, outputBytes - offset);
                    Buffer.BlockCopy(t, 0, result, offset, copyLen);
                    offset += copyLen;
                }
                return result;
            }
        }
#endif

        public static string GetExpectedRemoteCookieToken(string currentPin)
        {
            if (IsCloudMode && _cloudSessionKeyBytes != null && !string.IsNullOrWhiteSpace(currentPin))
            {
                return ComputeHmacHex(_cloudSessionKeyBytes, "pin-auth-v1:" + currentPin.Trim());
            }
            return RemoteAuthCookieToken;
        }

        public static void RotateRemoteCookieToken()
        {
            RemoteAuthCookieToken = GenerateSecureToken(32);
        }

        public static string Generate6DigitPin()
        {
            var bytes = new byte[4];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            uint val = BitConverter.ToUInt32(bytes, 0) % 900000 + 100000;
            return val.ToString();
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
            if (plain.StartsWith(EncryptedPrefix) || plain.StartsWith(AesEncryptedPrefix)) return plain;
#if NET8_0_OR_GREATER
            try
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(plain);
                byte[] nonce = new byte[12];
                RandomNumberGenerator.Fill(nonce);
                byte[] cipher = new byte[plainBytes.Length];
                byte[] tag = new byte[16];
                using (var aes = new AesGcm(_cloudKeyBytes, 16))
                {
                    aes.Encrypt(nonce, plainBytes, cipher, tag);
                }
                byte[] combined = new byte[nonce.Length + tag.Length + cipher.Length];
                Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
                Buffer.BlockCopy(tag, 0, combined, nonce.Length, tag.Length);
                Buffer.BlockCopy(cipher, 0, combined, nonce.Length + tag.Length, cipher.Length);
                return AesEncryptedPrefix + Convert.ToBase64String(combined);
            }
            catch (Exception ex)
            {
                if (IsCloudMode) throw new InvalidOperationException("Bulut şifreleme hatası: gizli anahtar düz metin olarak saklanamaz.", ex);
                return plain;
            }
#else
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
#endif
        }

        public static string DecryptSecret(string stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return "";
            if (stored.StartsWith(AesEncryptedPrefix))
            {
#if NET8_0_OR_GREATER
                try
                {
                    byte[] combined = Convert.FromBase64String(stored.Substring(AesEncryptedPrefix.Length));
                    if (combined.Length < 28) return "";
                    byte[] nonce = new byte[12];
                    byte[] tag = new byte[16];
                    byte[] cipher = new byte[combined.Length - 28];
                    Buffer.BlockCopy(combined, 0, nonce, 0, 12);
                    Buffer.BlockCopy(combined, 12, tag, 0, 16);
                    Buffer.BlockCopy(combined, 28, cipher, 0, cipher.Length);
                    byte[] plainBytes = new byte[cipher.Length];
                    using (var aes = new AesGcm(_cloudKeyBytes, 16))
                    {
                        aes.Decrypt(nonce, cipher, tag, plainBytes);
                    }
                    return Encoding.UTF8.GetString(plainBytes);
                }
                catch
                {
                    return "";
                }
#else
                return "";
#endif
            }
            if (!stored.StartsWith(EncryptedPrefix)) return stored;
#if NET8_0_OR_GREATER
            return "";
#else
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
#endif
        }

        public static void EncryptDatabaseSecrets(AppDatabase db)
        {
            if (db == null) return;
            if (db.AiConfig != null)
            {
                db.AiConfig.GeminiApiKey = EncryptSecret(db.AiConfig.GeminiApiKey);
                db.AiConfig.OpenAiApiKey = EncryptSecret(db.AiConfig.OpenAiApiKey);
            }
            if (db.RemoteConfig != null)
            {
                db.RemoteConfig.AccessPin = EncryptSecret(db.RemoteConfig.AccessPin);
            }
            if (db.SyncConfig != null && !string.IsNullOrWhiteSpace(db.SyncConfig.WebhookToken))
            {
                db.SyncConfig.WebhookToken = EncryptSecret(db.SyncConfig.WebhookToken);
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
            if (db.RemoteConfig != null)
            {
                db.RemoteConfig.AccessPin = DecryptSecret(db.RemoteConfig.AccessPin);
            }
            if (db.SyncConfig != null && !string.IsNullOrWhiteSpace(db.SyncConfig.WebhookToken))
            {
                db.SyncConfig.WebhookToken = DecryptSecret(db.SyncConfig.WebhookToken);
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
            if (clone.SyncConfig != null && stripCompletely)
            {
                clone.SyncConfig.WebhookToken = "";
            }
            if (clone.RemoteConfig != null && stripCompletely)
            {
                clone.RemoteConfig.AccessPin = "";
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

        public static bool IsTunnelRequest(Dictionary<string, string> headers)
        {
            if (IsCloudMode) return true;
            if (headers == null) return false;
            if (headers.ContainsKey("CF-Connecting-IP") || headers.ContainsKey("Cf-Ray")) return true;
            string activeHost = ActiveTunnelHost;
            if (!string.IsNullOrEmpty(activeHost) && headers.ContainsKey("Host"))
            {
                string h = (headers["Host"] ?? "").Trim().ToLowerInvariant();
                if (h == activeHost.ToLowerInvariant()) return true;
            }
            return false;
        }

        public static bool HasValidRemoteCookie(Dictionary<string, string> headers, string currentPin = null)
        {
            if (headers == null || !headers.ContainsKey("Cookie")) return false;
            string expected = GetExpectedRemoteCookieToken(currentPin);
            string raw = headers["Cookie"] ?? "";
            foreach (var part in raw.Split(';'))
            {
                string p = part.Trim();
                if (p.StartsWith("smart_remote_auth=", StringComparison.OrdinalIgnoreCase))
                {
                    string val = p.Substring("smart_remote_auth=".Length).Trim().Trim('"');
                    if (FixedTimeEquals(val, expected)) return true;
                }
            }
            return false;
        }

        public static bool IsAllowedCloudHost(string hostOnly)
        {
            if (string.IsNullOrWhiteSpace(hostOnly)) return false;
            string h = hostOnly.Trim().ToLowerInvariant();
            int colon = h.IndexOf(':');
            if (colon > 0) h = h.Substring(0, colon);

            if (h == "localhost" || h == "127.0.0.1") return true;
            // Strictly allow ONLY this project's Vercel production/preview hostnames — NEVER arbitrary *.vercel.app!
            if (h == "smart-pazaryeri-otomasyonu.vercel.app"
                || (h.StartsWith("smart-pazaryeri-otomasyonu-") && h.EndsWith(".vercel.app")))
                return true;

            string[] envHosts =
            {
                Environment.GetEnvironmentVariable("VERCEL_URL") ?? "",
                Environment.GetEnvironmentVariable("VERCEL_BRANCH_URL") ?? "",
                Environment.GetEnvironmentVariable("VERCEL_PROJECT_PRODUCTION_URL") ?? ""
            };
            foreach (var eh in envHosts)
            {
                string clean = eh.Trim().ToLowerInvariant().Replace("https://", "").Replace("http://", "").TrimEnd('/');
                if (!string.IsNullOrEmpty(clean) && h == clean) return true;
            }

            string extra = Environment.GetEnvironmentVariable("SMART_ALLOWED_HOSTS") ?? "";
            if (!string.IsNullOrWhiteSpace(extra))
            {
                foreach (var item in extra.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string clean = item.Trim().ToLowerInvariant().Replace("https://", "").Replace("http://", "").TrimEnd('/');
                    if (!string.IsNullOrEmpty(clean) && h == clean) return true;
                }
            }
            return false;
        }

        public static bool IsValidHostHeader(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            string h = host.Trim().ToLowerInvariant();
            if (IsCloudMode && IsAllowedCloudHost(h)) return true;

            if (h == ("localhost:" + port)
                || h == ("127.0.0.1:" + port)
                || h == "[::1]:" + port
                || (port == 80 && (h == "localhost" || h == "127.0.0.1")))
                return true;

            string tunnelHost = ActiveTunnelHost;
            if (!string.IsNullOrEmpty(tunnelHost))
            {
                string th = tunnelHost.ToLowerInvariant();
                if (h == th || h == (th + ":443") || h == (th + ":80"))
                    return true;
            }

            foreach (var lanIp in GetLocalLanIpv4Addresses())
            {
                if (h == (lanIp + ":" + port) || (port == 80 && h == lanIp))
                    return true;
            }
            return false;
        }

        public static bool IsValidOriginHeader(Dictionary<string, string> headers, int port, string method = "GET")
        {
            if (headers == null) return true;
            bool isSafeRead = string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
                || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
                || string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase);

            if (headers.ContainsKey("Origin"))
            {
                string origin = (headers["Origin"] ?? "").Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(origin))
                {
                    // Reject Origin: null on all state-changing requests (blocks sandboxed iframe CSRF)
                    if (origin == "null")
                    {
                        if (!isSafeRead) return false;
                    }
                    else
                    {
                        bool allowed = origin == ("http://localhost:" + port)
                            || origin == ("http://127.0.0.1:" + port);

                        if (!allowed && IsCloudMode)
                        {
                            Uri parsedOrigin;
                            if (Uri.TryCreate(origin, UriKind.Absolute, out parsedOrigin)
                                && (parsedOrigin.Scheme == Uri.UriSchemeHttps || (parsedOrigin.Scheme == Uri.UriSchemeHttp && (parsedOrigin.Host == "localhost" || parsedOrigin.Host == "127.0.0.1")))
                                && IsAllowedCloudHost(parsedOrigin.Host))
                            {
                                allowed = true;
                            }
                        }

                        string tunnelHost = ActiveTunnelHost;
                        if (!allowed && !string.IsNullOrEmpty(tunnelHost))
                        {
                            string th = tunnelHost.ToLowerInvariant();
                            if (origin == ("https://" + th) || origin == ("https://" + th + ":443"))
                                allowed = true;
                        }
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
            }
            if (headers.ContainsKey("Sec-Fetch-Site"))
            {
                string sfs = (headers["Sec-Fetch-Site"] ?? "").Trim().ToLowerInvariant();
                if (sfs == "cross-site" && !isSafeRead) return false;
            }
            return true;
        }

        public static bool IsValidSessionToken(Dictionary<string, string> headers)
        {
            if (headers == null || !headers.ContainsKey("X-Smart-Token")) return false;
            return FixedTimeEquals(headers["X-Smart-Token"].Trim(), SessionToken);
        }

        public static bool CheckRateLimit(string bucket, int maxPerWindow, int windowSeconds = 60)
        {
            lock (_rateLock)
            {
                DateTime now = DateTime.UtcNow;
                SweepRateBucketsIfNeeded(now);
                DateTime cutoff = now.AddSeconds(-Math.Max(1, windowSeconds));
                if (!_rateBuckets.ContainsKey(bucket))
                {
                    if (_rateBuckets.Count >= MaxRateBuckets)
                    {
                        // Evict oldest empty/stale buckets before inserting
                        var toRemove = _rateBuckets.Where(kv => kv.Value.Count == 0 || kv.Value[kv.Value.Count - 1] < now.AddMinutes(-10)).Select(kv => kv.Key).Take(2000).ToList();
                        foreach (var rk in toRemove) _rateBuckets.Remove(rk);
                        if (_rateBuckets.Count >= MaxRateBuckets)
                        {
                            var oldestKey = _rateBuckets.Keys.FirstOrDefault();
                            if (oldestKey != null) _rateBuckets.Remove(oldestKey);
                        }
                    }
                    _rateBuckets[bucket] = new List<DateTime>();
                }
                var list = _rateBuckets[bucket];
                list.RemoveAll(t => t < cutoff);
                if (list.Count >= maxPerWindow) return false;
                list.Add(now);
                return true;
            }
        }

        public static bool IsLoginLockedOut(string key, out int remainingSeconds)
        {
            remainingSeconds = 0;
            if (string.IsNullOrWhiteSpace(key)) return false;
            lock (_rateLock)
            {
                DateTime until;
                if (_lockoutUntil.TryGetValue(key, out until))
                {
                    DateTime now = DateTime.UtcNow;
                    if (until > now)
                    {
                        remainingSeconds = (int)Math.Ceiling((until - now).TotalSeconds);
                        return true;
                    }
                    _lockoutUntil.Remove(key);
                }
                return false;
            }
        }

        public static void RecordFailedLogin(string key, int maxAttempts = 6, int lockoutMinutes = 10)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            string bucket = "fail_login:" + key.ToLowerInvariant();
            lock (_rateLock)
            {
                DateTime now = DateTime.UtcNow;
                DateTime cutoff = now.AddMinutes(-lockoutMinutes);
                if (!_rateBuckets.ContainsKey(bucket)) _rateBuckets[bucket] = new List<DateTime>();
                var list = _rateBuckets[bucket];
                list.RemoveAll(t => t < cutoff);
                list.Add(now);
                if (list.Count >= maxAttempts)
                {
                    _lockoutUntil[key] = now.AddMinutes(lockoutMinutes);
                    list.Clear();
                }
            }
        }

        public static void ClearFailedLogins(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            lock (_rateLock)
            {
                _lockoutUntil.Remove(key);
                _rateBuckets.Remove("fail_login:" + key.ToLowerInvariant());
            }
        }

        private static void SweepRateBucketsIfNeeded(DateTime now)
        {
            if ((now - _lastRateSweepUtc).TotalMinutes < 5) return;
            _lastRateSweepUtc = now;
            DateTime staleCutoff = now.AddMinutes(-15);
            var deadKeys = new List<string>();
            foreach (var kv in _rateBuckets)
            {
                kv.Value.RemoveAll(t => t < staleCutoff);
                if (kv.Value.Count == 0) deadKeys.Add(kv.Key);
            }
            foreach (var k in deadKeys) _rateBuckets.Remove(k);

            var expiredLocks = _lockoutUntil.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
            foreach (var lk in expiredLocks) _lockoutUntil.Remove(lk);
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

        public static bool IsValidUploadFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return false;
            return Regex.IsMatch(fileName, @"^[A-Za-z0-9_\-]{3,120}\.(jpg|jpeg|png|webp)$", RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// DNS-resolved SSRF Guard: allows ONLY public HTTPS URLs on port 443.
        /// Blocks localhost, RFC1918 private LANs, cloud metadata (169.254.169.254), CGNAT, link-local, multicast, and IPv6 ULA!
        /// </summary>
        public static bool IsSafePublicExternalUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            Uri parsed;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out parsed)) return false;
            if (parsed.Scheme != Uri.UriSchemeHttps) return false;
            if (!string.IsNullOrEmpty(parsed.UserInfo)) return false;
            if (parsed.Port != 443) return false;

            string host = (parsed.Host ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(host) || host == "localhost" || host.EndsWith(".local") || host.EndsWith(".internal") || host.EndsWith(".localhost"))
                return false;

            // Block raw IP literals (prevents direct metadata/LAN probing)
            System.Net.IPAddress literalIp;
            if (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out literalIp))
                return false;

            try
            {
                var addrs = System.Net.Dns.GetHostAddresses(host);
                if (addrs == null || addrs.Length == 0) return false;
                foreach (var addr in addrs)
                {
                    if (IsDisallowedSsrfIp(addr)) return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsDisallowedSsrfIp(System.Net.IPAddress ip)
        {
            if (ip == null) return true;
            if (System.Net.IPAddress.IsLoopback(ip)) return true;
            if (ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any) || ip.Equals(System.Net.IPAddress.Broadcast))
                return true;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                byte[] b = ip.GetAddressBytes();
                if (b[0] == 0) return true;                                  // 0.0.0.0/8
                if (b[0] == 10) return true;                                 // 10.0.0.0/8
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;   // 100.64.0.0/10 (CGNAT)
                if (b[0] == 127) return true;                                // 127.0.0.0/8
                if (b[0] == 169 && b[1] == 254) return true;                 // 169.254.0.0/16 (AWS/GCP/Azure Metadata!)
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;    // 172.16.0.0/12
                if (b[0] == 192 && b[1] == 0 && b[2] == 0) return true;      // 192.0.0.0/24
                if (b[0] == 192 && b[1] == 0 && b[2] == 2) return true;      // TEST-NET-1
                if (b[0] == 192 && b[1] == 168) return true;                 // 192.168.0.0/16
                if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;  // 198.18.0.0/15
                if (b[0] >= 224) return true;                                // Multicast & reserved
                return false;
            }
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal || ip.IsIPv6Teredo) return true;
                byte[] b = ip.GetAddressBytes();
                if ((b[0] & 0xFE) == 0xFC) return true; // fc00::/7 Unique Local
                return false;
            }
            return true;
        }

        public static bool IsSafeImageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            string s = url.Trim();
            if (s.Contains("\r") || s.Contains("\n") || s.Contains("\"") || s.Contains("'") || s.Contains("<") || s.Contains(">") || s.Contains(" "))
                return false;
            if (s.StartsWith("/uploads/"))
            {
                string fn = s.Substring("/uploads/".Length);
                return IsValidUploadFileName(fn);
            }
            Uri parsed;
            if (Uri.TryCreate(s, UriKind.Absolute, out parsed))
            {
                if (parsed.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(parsed.UserInfo) || parsed.Port != 443)
                    return false;
                string h = (parsed.Host ?? "").ToLowerInvariant();
                if (string.IsNullOrEmpty(h) || h == "localhost" || h.EndsWith(".local") || h.EndsWith(".internal"))
                    return false;
                System.Net.IPAddress ip;
                if (System.Net.IPAddress.TryParse(h.Trim('[', ']'), out ip)) return false;
                return true;
            }
            return false;
        }

        public static string RedactSensitiveText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text;
            s = Regex.Replace(s, @"(Authorization\s*:\s*(?:Basic|Bearer)\s+)[^\s""']+", "$1[GİZLENDİ]", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"((?:api[_-]?key|api[_-]?secret|access[_-]?token|refresh[_-]?token|webhook[_-]?token|password|client[_-]?secret|appsecret|servicekey|x-api-key|x-smart-token|x-smart-webhook-token)[""']?\s*[:=]\s*[""']?)[^""'\s,&}]+", "$1[GİZLENDİ]", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"((?:smart_user_session|smart_remote_auth|token)=)[^;\s&""']+", "$1[GİZLENDİ]", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"(vercel_blob_rw_[A-Za-z0-9_\-]+)", "[GİZLENDİ]", RegexOptions.IgnoreCase);
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
            if (db.UserProfile != null)
            {
                if (!string.IsNullOrWhiteSpace(db.UserProfile.AvatarUrl) && !IsSafeImageUrl(db.UserProfile.AvatarUrl))
                    db.UserProfile.AvatarUrl = "";
                if (!string.IsNullOrEmpty(db.UserProfile.DisplayName) && db.UserProfile.DisplayName.Length > 60)
                    db.UserProfile.DisplayName = db.UserProfile.DisplayName.Substring(0, 60);
                if (!string.IsNullOrEmpty(db.UserProfile.Bio) && db.UserProfile.Bio.Length > 240)
                    db.UserProfile.Bio = db.UserProfile.Bio.Substring(0, 240);
                if (!string.IsNullOrEmpty(db.UserProfile.Email) && db.UserProfile.Email.Length > 100)
                    db.UserProfile.Email = db.UserProfile.Email.Substring(0, 100);
            }
            if (db.SyncConfig != null)
            {
                db.SyncConfig.IntervalMinutes = Math.Max(1, Math.Min(1440, db.SyncConfig.IntervalMinutes));
                db.SyncConfig.LowStockThreshold = Math.Max(0, Math.Min(100000, db.SyncConfig.LowStockThreshold));
            }
        }
    }
}
