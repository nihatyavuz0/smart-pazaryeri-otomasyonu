using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartMarketplace
{
    /// <summary>
    /// Persistent multi-user authentication & stateless HMAC session service.
    /// Stores salted PBKDF2 password hashes in Vercel Private Blob (cloud) or data/users_auth.json (local).
    /// Guarantees every user's workspace, products, API keys, and customizations persist across all devices & browsers.
    /// </summary>
    public static class UserAuthService
    {
        public const string SessionCookieName = "smart_user_session";
        public const string DefaultAdminUsername = "nihatstark";
        private const string CloudAuthBlobPath = "smart/users_auth.json";

        private static readonly object _lock = new object();
        private static UserAuthStore _cachedStore;
        private static DateTime _lastLoadedUtc = DateTime.MinValue;
        private static string _localAuthPath;

        public static void Initialize()
        {
            lock (_lock)
            {
                _localAuthPath = Path.Combine(Storage.DataDir, "users_auth.json");
                var store = LoadStoreInternal(true);
                bool changed = EnsureDefaultAdmin(store);
                if (changed)
                {
                    SaveStoreInternal(store);
                }
                _cachedStore = store;
                _lastLoadedUtc = DateTime.UtcNow;
            }
        }

        public static string NormalizeUsername(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string clean = raw.Trim().ToLowerInvariant();
            clean = Regex.Replace(clean, @"[^a-z0-9._\-]", "");
            if (clean.Length > 32) clean = clean.Substring(0, 32);
            return clean;
        }

        public static bool IsValidUsername(string username)
        {
            return !string.IsNullOrWhiteSpace(username)
                && username.Length >= 3
                && username.Length <= 32
                && Regex.IsMatch(username, @"^[a-z0-9][a-z0-9._\-]{2,31}$");
        }

        public static List<string> ListAllUsernames()
        {
            lock (_lock)
            {
                var store = GetStore(false);
                return store.Users.Select(u => u.Username).ToList();
            }
        }

        public static List<object> ListPublicUsers()
        {
            lock (_lock)
            {
                var store = GetStore(false);
                return store.Users.Select(u => (object)new
                {
                    username = u.Username,
                    displayName = string.IsNullOrWhiteSpace(u.DisplayName) ? u.Username : u.DisplayName,
                    role = u.Role ?? "user",
                    createdAt = u.CreatedAt ?? "",
                    lastLoginAt = u.LastLoginAt ?? ""
                }).ToList();
            }
        }

        public static UserAccountRecord GetUser(string username, bool forceRefresh = false)
        {
            string norm = NormalizeUsername(username);
            if (string.IsNullOrEmpty(norm)) return null;
            lock (_lock)
            {
                var store = GetStore(forceRefresh);
                return store.Users.Find(u => string.Equals(u.Username, norm, StringComparison.OrdinalIgnoreCase));
            }
        }

        public static UserAccountRecord Authenticate(string rawUsername, string password, out string error)
        {
            error = "";
            string norm = NormalizeUsername(rawUsername);
            if (string.IsNullOrEmpty(norm) || string.IsNullOrEmpty(password) || password.Length > 256)
            {
                Security.VerifyDummyPassword(password);
                error = "Kullanıcı adı ve şifre gereklidir.";
                return null;
            }

            int lockRemain;
            if (Security.IsLoginLockedOut("acct:" + norm, out lockRemain))
            {
                int mins = Math.Max(1, (int)Math.Ceiling(lockRemain / 60.0));
                error = "Bu hesap art arda hatalı girişler nedeniyle geçici olarak kilitlendi. Lütfen yaklaşık " + mins + " dakika sonra tekrar deneyin.";
                return null;
            }

            lock (_lock)
            {
                var store = GetStore(true);
                EnsureDefaultAdmin(store);
                var user = store.Users.Find(u => string.Equals(u.Username, norm, StringComparison.OrdinalIgnoreCase));
                if (user == null)
                {
                    Security.VerifyDummyPassword(password);
                    Security.RecordFailedLogin("acct:" + norm, 6, 10);
                    error = "Kullanıcı adı veya şifre hatalı.";
                    return null;
                }
                if (!Security.VerifyPassword(password, user.PasswordHash))
                {
                    Security.RecordFailedLogin("acct:" + norm, 6, 10);
                    error = "Kullanıcı adı veya şifre hatalı.";
                    return null;
                }
                Security.ClearFailedLogins("acct:" + norm);
                user.LastLoginAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                SaveStoreInternal(store);
                return user;
            }
        }

        public static string BuildSessionCookie(UserAccountRecord user, bool remember, bool isHttps)
        {
            long days = remember ? 30 : 1;
            long expUnix = ToUnixTimeSeconds(DateTime.UtcNow.AddDays(days));
            string payload = user.Username + "." + user.SessionVersion + "." + expUnix;
            string sig = Security.SignUserSessionPayload(payload);
            string token = payload + "." + sig;
            string maxAge = remember ? "; Max-Age=2592000" : "";
            return SessionCookieName + "=" + token + "; Path=/; HttpOnly; SameSite=Lax" + maxAge + (isHttps ? "; Secure" : "");
        }

        public static string BuildClearSessionCookie(bool isHttps)
        {
            return SessionCookieName + "=; Path=/; HttpOnly; SameSite=Lax; Max-Age=0" + (isHttps ? "; Secure" : "");
        }

        public static UserAccountRecord ValidateSessionCookie(Dictionary<string, string> headers)
        {
            if (headers == null || !headers.ContainsKey("Cookie")) return null;
            string rawCookie = headers["Cookie"] ?? "";
            string token = null;
            foreach (var part in rawCookie.Split(';'))
            {
                string p = part.Trim();
                if (p.StartsWith(SessionCookieName + "=", StringComparison.OrdinalIgnoreCase))
                {
                    token = p.Substring(SessionCookieName.Length + 1).Trim().Trim('"');
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(token) || token.Length > 256) return null;

            var parts = token.Split('.');
            if (parts.Length != 4) return null;

            string username = NormalizeUsername(parts[0]);
            int sessVer;
            long expUnix;
            string providedSig = parts[3];
            if (!IsValidUsername(username) || !int.TryParse(parts[1], out sessVer) || !long.TryParse(parts[2], out expUnix))
                return null;

            if (expUnix < ToUnixTimeSeconds(DateTime.UtcNow))
                return null;

            string payload = parts[0] + "." + parts[1] + "." + parts[2];
            string expectedSig = Security.SignUserSessionPayload(payload);
            if (!Security.FixedTimeEquals(providedSig, expectedSig))
                return null;

            var user = GetUser(username, false);
            if (user == null || user.SessionVersion != sessVer)
            {
                // Force refresh once from cloud in case another instance updated user record
                user = GetUser(username, true);
                if (user == null || user.SessionVersion != sessVer) return null;
            }
            return user;
        }

        public static bool ChangePassword(string username, string currentPassword, string newPassword, out UserAccountRecord updatedUser, out string error)
        {
            updatedUser = null;
            error = "";
            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
            {
                error = "Güvenlik nedeniyle yeni şifre en az 8 karakter olmalıdır.";
                return false;
            }
            if (newPassword.Length > 128)
            {
                error = "Yeni şifre en fazla 128 karakter olabilir.";
                return false;
            }

            lock (_lock)
            {
                var store = GetStore(true);
                var user = store.Users.Find(u => string.Equals(u.Username, NormalizeUsername(username), StringComparison.OrdinalIgnoreCase));
                if (user == null)
                {
                    error = "Kullanıcı bulunamadı.";
                    return false;
                }
                if (!Security.VerifyPassword(currentPassword, user.PasswordHash))
                {
                    error = "Mevcut şifreniz hatalı.";
                    return false;
                }
                user.PasswordHash = Security.HashPassword(newPassword);
                user.SessionVersion = Math.Max(1, user.SessionVersion) + 1;
                SaveStoreInternal(store);
                updatedUser = user;
                return true;
            }
        }

        public static bool UpdateDisplayName(string username, string displayName)
        {
            lock (_lock)
            {
                var store = GetStore(true);
                var user = store.Users.Find(u => string.Equals(u.Username, NormalizeUsername(username), StringComparison.OrdinalIgnoreCase));
                if (user == null) return false;
                string clean = (displayName ?? "").Trim();
                if (!string.IsNullOrEmpty(clean))
                {
                    user.DisplayName = clean.Length > 60 ? clean.Substring(0, 60) : clean;
                    SaveStoreInternal(store);
                }
                return true;
            }
        }

        public static UserAccountRecord RotateSessions(string username)
        {
            lock (_lock)
            {
                var store = GetStore(true);
                var user = store.Users.Find(u => string.Equals(u.Username, NormalizeUsername(username), StringComparison.OrdinalIgnoreCase));
                if (user == null) return null;
                user.SessionVersion = Math.Max(1, user.SessionVersion) + 1;
                SaveStoreInternal(store);
                return user;
            }
        }

        public static bool CreateUser(string rawUsername, string password, string displayName, string role, out UserAccountRecord created, out string error)
        {
            created = null;
            error = "";
            string norm = NormalizeUsername(rawUsername);
            if (!IsValidUsername(norm))
            {
                error = "Kullanıcı adı 3-32 karakter olmalı ve yalnızca harf, rakam, nokta, tire veya alt çizgi içermelidir.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(password) || password.Length < 8 || password.Length > 128)
            {
                error = "Güvenlik nedeniyle şifre 8 ile 128 karakter arasında olmalıdır.";
                return false;
            }

            lock (_lock)
            {
                var store = GetStore(true);
                if (store.Users.Any(u => string.Equals(u.Username, norm, StringComparison.OrdinalIgnoreCase)))
                {
                    error = "Bu kullanıcı adı zaten kayıtlı.";
                    return false;
                }
                created = new UserAccountRecord
                {
                    Username = norm,
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? norm : displayName.Trim(),
                    PasswordHash = Security.HashPassword(password),
                    Role = role == "admin" ? "admin" : "user",
                    SessionVersion = 1,
                    CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    LastLoginAt = ""
                };
                store.Users.Add(created);
                SaveStoreInternal(store);
                return true;
            }
        }

        public static bool DeleteUser(string actingUsername, string targetUsername, out string error)
        {
            error = "";
            string act = NormalizeUsername(actingUsername);
            string tgt = NormalizeUsername(targetUsername);
            if (string.IsNullOrEmpty(tgt))
            {
                error = "Geçersiz kullanıcı adı.";
                return false;
            }
            if (string.Equals(act, tgt, StringComparison.OrdinalIgnoreCase))
            {
                error = "Kendi aktif hesabınızı silemezsiniz.";
                return false;
            }

            lock (_lock)
            {
                var store = GetStore(true);
                var existing = store.Users.Find(u => string.Equals(u.Username, tgt, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    error = "Kullanıcı bulunamadı.";
                    return false;
                }
                store.Users.Remove(existing);
                SaveStoreInternal(store);
                return true;
            }
        }

        private static UserAuthStore GetStore(bool forceRefresh)
        {
            if (!forceRefresh && _cachedStore != null && (DateTime.UtcNow - _lastLoadedUtc).TotalSeconds < 15)
            {
                return _cachedStore;
            }
            var store = LoadStoreInternal(forceRefresh);
            if (EnsureDefaultAdmin(store))
            {
                SaveStoreInternal(store);
            }
            _cachedStore = store;
            _lastLoadedUtc = DateTime.UtcNow;
            return store;
        }

        private static bool EnsureDefaultAdmin(UserAuthStore store)
        {
            if (store.Users == null) store.Users = new List<UserAccountRecord>();
            if (store.Users.Count > 0) return false;

            string adminUser = NormalizeUsername(Environment.GetEnvironmentVariable("SMART_ADMIN_USER"));
            if (!IsValidUsername(adminUser)) adminUser = DefaultAdminUsername;

            string adminPass = (Environment.GetEnvironmentVariable("SMART_ADMIN_PASSWORD") ?? "").Trim();
            bool generatedBootstrap = false;
            if (string.IsNullOrWhiteSpace(adminPass))
            {
                // Never hardcode a predictable default password in public source code — generate strong CSPRNG password
                adminPass = Security.GenerateSecureToken(10);
                generatedBootstrap = true;
            }

            store.Users.Add(new UserAccountRecord
            {
                Username = adminUser,
                DisplayName = adminUser == DefaultAdminUsername ? "Nihat Stark" : adminUser,
                PasswordHash = Security.HashPassword(adminPass),
                Role = "admin",
                SessionVersion = 1,
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                LastLoginAt = ""
            });
            if (generatedBootstrap && !Security.IsCloudMode)
            {
                Console.WriteLine("[Smart Güvenlik] İlk kurulum rastgele yönetici şifresi oluşturuldu (" + adminUser + "): " + adminPass);
                Console.WriteLine("[Smart Güvenlik] Giriş yaptıktan sonra Hesap & Profil bölümünden kendi şifrenizi belirleyebilirsiniz.");
            }
            else if (generatedBootstrap && Security.IsCloudMode)
            {
                Console.WriteLine("[Smart Güvenlik] UYARI: SMART_ADMIN_PASSWORD tanımlı değil; güvenli rastgele başlangıç şifresi üretildi. Vercel ortam değişkenlerinden SMART_ADMIN_PASSWORD ayarlayın.");
            }
            return true;
        }

        private static UserAuthStore LoadStoreInternal(bool bypassCache)
        {
            UserAuthStore store = null;
            if (CloudBlobStorage.IsConfigured)
            {
                string etag;
                string raw = CloudBlobStorage.GetText(CloudAuthBlobPath, bypassCache, out etag);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    try { store = Storage.Deserialize<UserAuthStore>(raw); } catch { }
                    if (store != null && !string.IsNullOrEmpty(_localAuthPath))
                    {
                        try { File.WriteAllText(_localAuthPath, raw, new UTF8Encoding(false)); } catch { }
                    }
                }
            }
            if (store == null && !string.IsNullOrEmpty(_localAuthPath) && File.Exists(_localAuthPath))
            {
                try
                {
                    string raw = File.ReadAllText(_localAuthPath, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(raw)) store = Storage.Deserialize<UserAuthStore>(raw);
                }
                catch { }
            }
            if (store == null) store = new UserAuthStore();
            if (store.Users == null) store.Users = new List<UserAccountRecord>();
            return store;
        }

        private static void SaveStoreInternal(UserAuthStore store)
        {
            string json = Storage.Serialize(store);
            if (!string.IsNullOrEmpty(_localAuthPath))
            {
                try
                {
                    string dir = Path.GetDirectoryName(_localAuthPath);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string tmp = _localAuthPath + ".tmp";
                    File.WriteAllText(tmp, json, new UTF8Encoding(false));
                    if (File.Exists(_localAuthPath)) File.Delete(_localAuthPath);
                    File.Move(tmp, _localAuthPath);
                }
                catch { }
            }
            if (CloudBlobStorage.IsConfigured)
            {
                string err;
                CloudBlobStorage.PutText(CloudAuthBlobPath, json, "application/json; charset=utf-8", out err);
            }
            _cachedStore = store;
            _lastLoadedUtc = DateTime.UtcNow;
        }

        private static long ToUnixTimeSeconds(DateTime utc)
        {
            return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }
    }
}
