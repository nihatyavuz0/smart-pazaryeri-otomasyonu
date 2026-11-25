using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
#if NET8_0_OR_GREATER
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
#else
using System.Web.Script.Serialization;
#endif

namespace SmartMarketplace
{
    public static class Storage
    {
        private const string LegacyCloudDbBlobPath = "smart/smart_db.json";
        private const string CloudUploadsPrefix = "smart/uploads/";

        private static readonly object _lock = new object();
#if NET8_0_OR_GREATER
        private static readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Include,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore
        };
#else
        private static readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 };
#endif
        [ThreadStatic]
        private static string _currentUser;

        private static readonly Dictionary<string, long> _userVersions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _userCloudEtags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> _userEtagCheckedUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private static string _cachedAccessPin = "";
        private static DateTime _lastPinRefreshUtc = DateTime.MinValue;

        public static string DataDir { get; private set; }
        public static string UploadsDir { get; private set; }
        public static string DbPath { get; private set; }
        public static string BackupPath { get; private set; }

        public static string CurrentUser
        {
            get
            {
                string u = UserAuthService.NormalizeUsername(_currentUser);
                return string.IsNullOrEmpty(u) ? UserAuthService.DefaultAdminUsername : u;
            }
            set
            {
                _currentUser = UserAuthService.NormalizeUsername(value);
            }
        }

        private static string GetCloudDbBlobPath(string username)
        {
            string u = UserAuthService.NormalizeUsername(username);
            if (string.IsNullOrEmpty(u)) u = UserAuthService.DefaultAdminUsername;
            return "smart/users/" + u + "/smart_db.json";
        }

        private static string GetLocalUserDbPath(string username)
        {
            string u = UserAuthService.NormalizeUsername(username);
            if (string.IsNullOrEmpty(u)) u = UserAuthService.DefaultAdminUsername;
            string dir = Path.Combine(DataDir, "users", u);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return Path.Combine(dir, "smart_db.json");
        }

        private static string GetLocalUserBackupPath(string username)
        {
            return GetLocalUserDbPath(username) + ".backup.json";
        }

        public static string GetDataVersion()
        {
            string u = CurrentUser;
            if (CloudBlobStorage.IsConfigured)
            {
                lock (_lock)
                {
                    DateTime lastChk = _userEtagCheckedUtc.ContainsKey(u) ? _userEtagCheckedUtc[u] : DateTime.MinValue;
                    if ((DateTime.UtcNow - lastChk).TotalSeconds >= 2.0)
                    {
                        _userEtagCheckedUtc[u] = DateTime.UtcNow;
                        string etag = CloudBlobStorage.HeadEtag(GetCloudDbBlobPath(u));
                        if (!string.IsNullOrEmpty(etag)) _userCloudEtags[u] = etag;
                    }
                    if (_userCloudEtags.ContainsKey(u) && !string.IsNullOrEmpty(_userCloudEtags[u]))
                        return _userCloudEtags[u].Trim('"');
                }
            }
            lock (_lock)
            {
                if (!_userVersions.ContainsKey(u)) _userVersions[u] = DateTime.UtcNow.Ticks;
                return _userVersions[u].ToString("x");
            }
        }

        public static string GetCachedAccessPin(bool forceRefresh = false)
        {
            lock (_lock)
            {
                if (!forceRefresh && !string.IsNullOrEmpty(_cachedAccessPin) && (DateTime.UtcNow - _lastPinRefreshUtc).TotalSeconds < 15)
                {
                    return _cachedAccessPin;
                }
            }
            var db = Load();
            return db.RemoteConfig != null ? (db.RemoteConfig.AccessPin ?? "") : "";
        }

        public static void Initialize(string rootDir)
        {
            if (Security.IsCloudMode)
            {
                DataDir = Path.Combine(Path.GetTempPath(), "smart_data");
            }
            else
            {
                DataDir = Path.Combine(rootDir, "data");
            }
            UploadsDir = Path.Combine(DataDir, "uploads");
            DbPath = Path.Combine(DataDir, "smart_db.json");
            BackupPath = Path.Combine(DataDir, "smart_db.backup.json");

            if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);
            if (!Directory.Exists(UploadsDir)) Directory.CreateDirectory(UploadsDir);

            // Initialize user accounts (ensures default admin "nihatstark" exists securely)
            UserAuthService.Initialize();

            // Pre-load default admin database & migrate legacy smart_db.json seamlessly
            string prevUser = _currentUser;
            try
            {
                CurrentUser = UserAuthService.DefaultAdminUsername;
                var existing = Load();
                bool changed = false;
                foreach (var mp in existing.Marketplaces)
                {
                    if (mp.SimulationMode && MarketplaceConnectors.HasRequiredCredentials(mp))
                    {
                        mp.SimulationMode = false;
                        mp.StatusMessage = "Canlı API Modu otomatik etkinleştirildi — Gerçek hesabınıza yayınlamaya hazır";
                        AddLog(existing, "api", "info", mp.Name, mp.Name + " API anahtarları algılandı ve Canlı API Modu otomatik aktif edildi.");
                        changed = true;
                    }
                }
                if (changed) Save(existing);
            }
            finally
            {
                _currentUser = prevUser;
            }
        }

        public static AppDatabase Load()
        {
            string u = CurrentUser;
            lock (_lock)
            {
                AppDatabase db = null;
                string userCloudPath = GetCloudDbBlobPath(u);
                string userLocalPath = GetLocalUserDbPath(u);
                string userBackupPath = GetLocalUserBackupPath(u);
                bool isDefaultAdmin = string.Equals(u, UserAuthService.DefaultAdminUsername, StringComparison.OrdinalIgnoreCase);

                if (CloudBlobStorage.IsConfigured)
                {
                    string etag;
                    string cloudRaw = CloudBlobStorage.GetText(userCloudPath, true, out etag);
                    if (string.IsNullOrWhiteSpace(cloudRaw) && isDefaultAdmin)
                    {
                        // Seamless one-time migration from legacy single-user smart/smart_db.json
                        string legacyEtag;
                        string legacyRaw = CloudBlobStorage.GetText(LegacyCloudDbBlobPath, true, out legacyEtag);
                        if (!string.IsNullOrWhiteSpace(legacyRaw))
                        {
                            var migrated = TryDeserializeDb(legacyRaw);
                            if (migrated != null)
                            {
                                EnsureDefaults(migrated, u);
                                Security.DecryptDatabaseSecrets(migrated);
                                SaveInternal(migrated, u);
                                Console.WriteLine("[Smart] Mevcut bulut verileri '" + u + "' hesabına aktarıldı.");
                                return migrated;
                            }
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(cloudRaw))
                    {
                        db = TryDeserializeDb(cloudRaw);
                        if (db != null)
                        {
                            if (!string.IsNullOrEmpty(etag))
                            {
                                _userCloudEtags[u] = etag;
                                _userEtagCheckedUtc[u] = DateTime.UtcNow;
                            }
                            try { File.WriteAllText(userLocalPath, cloudRaw, new UTF8Encoding(false)); } catch { }
                        }
                    }
                }

                if (db == null) db = TryReadFile(userLocalPath);
                if (db == null && File.Exists(userBackupPath))
                {
                    db = TryReadFile(userBackupPath);
                    if (db != null) SaveInternal(db, u);
                }
                if (db == null && isDefaultAdmin && File.Exists(DbPath))
                {
                    // Seamless local migration from legacy data/smart_db.json
                    db = TryReadFile(DbPath);
                    if (db != null)
                    {
                        EnsureDefaults(db, u);
                        Security.DecryptDatabaseSecrets(db);
                        SaveInternal(db, u);
                        return db;
                    }
                }
                if (db == null)
                {
                    db = CreateInitialDatabase(u);
                    SaveInternal(db, u);
                }

                EnsureDefaults(db, u);
                Security.DecryptDatabaseSecrets(db);
                if (isDefaultAdmin)
                {
                    _cachedAccessPin = db.RemoteConfig.AccessPin;
                    _lastPinRefreshUtc = DateTime.UtcNow;
                }
                return db;
            }
        }

        private static AppDatabase TryDeserializeDb(string raw)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(raw)) return null;
                return Deserialize<AppDatabase>(raw);
            }
            catch
            {
                return null;
            }
        }

        private static AppDatabase TryReadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string raw = File.ReadAllText(path, Encoding.UTF8);
                return TryDeserializeDb(raw);
            }
            catch
            {
                return null;
            }
        }

        public static void Save(AppDatabase db)
        {
            string u = CurrentUser;
            lock (_lock)
            {
                EnsureDefaults(db, u);
                SaveInternal(db, u);
            }
        }

        public static string ExportSafeBackupJson()
        {
            lock (_lock)
            {
                var db = Load();
                // Strip raw secrets from exported backup JSON so sharing a backup never exposes API keys
                var safeClone = Security.CloneWithMaskedSecrets(db, true);
                return Serialize(safeClone);
            }
        }

        public static AppDatabase ImportRawJson(string rawJson)
        {
            string u = CurrentUser;
            lock (_lock)
            {
                var current = Load();
                var imported = Deserialize<AppDatabase>(rawJson);
                if (imported == null) throw new Exception("Geçersiz yedek dosyası formatı.");
                EnsureDefaults(imported, u);
                Security.SanitizeImportedDatabase(imported);

                // Preserve existing secrets if backup had stripped/masked secrets
                if (string.IsNullOrWhiteSpace(imported.AiConfig.GeminiApiKey) || Security.IsMaskedPlaceholder(imported.AiConfig.GeminiApiKey))
                    imported.AiConfig.GeminiApiKey = current.AiConfig.GeminiApiKey;
                if (string.IsNullOrWhiteSpace(imported.AiConfig.OpenAiApiKey) || Security.IsMaskedPlaceholder(imported.AiConfig.OpenAiApiKey))
                    imported.AiConfig.OpenAiApiKey = current.AiConfig.OpenAiApiKey;

                foreach (var impMp in imported.Marketplaces)
                {
                    var curMp = current.Marketplaces.Find(m => m.Id == impMp.Id);
                    if (curMp == null || impMp.Credentials == null || curMp.Credentials == null) continue;
                    var keys = new List<string>(impMp.Credentials.Keys);
                    foreach (var k in keys)
                    {
                        if (Security.IsSecretKeyName(k))
                        {
                            string v = impMp.Credentials[k];
                            if (string.IsNullOrWhiteSpace(v) || Security.IsMaskedPlaceholder(v))
                            {
                                if (curMp.Credentials.ContainsKey(k))
                                    impMp.Credentials[k] = curMp.Credentials[k];
                            }
                        }
                    }
                }

                AddLog(imported, "system", "success", "Smart", "Yedek dosyası doğrulandı ve güvenle geri yüklendi.");
                SaveInternal(imported, u);
                return imported;
            }
        }

        private static void SaveInternal(AppDatabase db, string username)
        {
            if (db.Logs != null && db.Logs.Count > 300)
            {
                db.Logs = db.Logs.GetRange(0, 300);
            }

            // Serialize an encrypted copy so in-memory object stays decrypted while disk/cloud is 100% encrypted
            var diskCopy = Deserialize<AppDatabase>(Serialize(db));
            Security.EncryptDatabaseSecrets(diskCopy);

            string json = Serialize(diskCopy);
            string userLocalPath = GetLocalUserDbPath(username);
            string userBackupPath = GetLocalUserBackupPath(username);
            string tmp = userLocalPath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));

            if (File.Exists(userLocalPath))
            {
                try { File.Copy(userLocalPath, userBackupPath, true); } catch { }
                File.Delete(userLocalPath);
            }
            File.Move(tmp, userLocalPath);

            if (string.Equals(username, UserAuthService.DefaultAdminUsername, StringComparison.OrdinalIgnoreCase))
            {
                _cachedAccessPin = db.RemoteConfig != null ? (db.RemoteConfig.AccessPin ?? "") : "";
                _lastPinRefreshUtc = DateTime.UtcNow;
            }

            if (CloudBlobStorage.IsConfigured)
            {
                string blobErr;
                if (CloudBlobStorage.PutText(GetCloudDbBlobPath(username), json, "application/json; charset=utf-8", out blobErr))
                {
                    _userCloudEtags[username] = DateTime.UtcNow.Ticks.ToString("x");
                    _userEtagCheckedUtc[username] = DateTime.UtcNow;
                }
            }

            long curVer = _userVersions.ContainsKey(username) ? _userVersions[username] : DateTime.UtcNow.Ticks;
            _userVersions[username] = curVer + 1;
        }

        public static void AddLog(AppDatabase db, string type, string level, string marketplace, string message, string details = "")
        {
            db.Logs.Insert(0, new ActivityLog
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 10),
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Type = type,
                Level = level,
                Marketplace = marketplace ?? "Smart",
                Message = Security.RedactSensitiveText(message ?? ""),
                Details = Security.RedactSensitiveText(details ?? "")
            });
            if (db.Logs.Count > 300)
            {
                db.Logs.RemoveRange(300, db.Logs.Count - 300);
            }
        }

        public static string SaveBase64Image(string fileName, string base64Data)
        {
            if (string.IsNullOrWhiteSpace(base64Data))
                throw new Exception("Görsel verisi boş.");
            if (base64Data.Length > 7 * 1024 * 1024)
                throw new Exception("Görsel boyutu izin verilen sınırı (5 MB) aşıyor.");

            string cleanBase64 = base64Data;
            if (cleanBase64.Contains(","))
            {
                cleanBase64 = cleanBase64.Substring(cleanBase64.IndexOf(",") + 1);
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(cleanBase64);
            }
            catch
            {
                throw new Exception("Geçersiz görsel verisi.");
            }

            string safeExt;
            string validationError;
            if (!Security.ValidateImageBytes(bytes, out safeExt, out validationError))
            {
                throw new Exception(validationError);
            }

            string safeName = "img_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Security.GenerateSecureToken(6) + safeExt;
            string fullPath;
            if (!Security.TryResolveSafePath(UploadsDir, safeName, out fullPath))
                throw new Exception("Güvenli dosya yolu oluşturulamadı.");

            File.WriteAllBytes(fullPath, bytes);

            if (CloudBlobStorage.IsConfigured)
            {
                string mime = safeExt == ".png" ? "image/png" : (safeExt == ".webp" ? "image/webp" : "image/jpeg");
                string blobErr;
                CloudBlobStorage.PutBytes(CloudUploadsPrefix + safeName, bytes, mime, 31536000, out blobErr);
            }

            return "/uploads/" + safeName;
        }

        /// <summary>
        /// Ensures an uploaded image file exists in UploadsDir locally.
        /// In Vercel cloud mode, transparently restores the file from Private Blob after cold starts.
        /// </summary>
        public static bool EnsureUploadFileLocal(string fileName, out string fullPath)
        {
            fullPath = null;
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains("/") || fileName.Contains("\\"))
                return false;
            if (!Security.TryResolveSafePath(UploadsDir, fileName, out fullPath))
                return false;
            if (File.Exists(fullPath))
                return true;

            if (CloudBlobStorage.IsConfigured)
            {
                string etag;
                byte[] bytes = CloudBlobStorage.GetBytes(CloudUploadsPrefix + fileName, false, out etag);
                if (bytes != null && bytes.Length > 0)
                {
                    try
                    {
                        if (!Directory.Exists(UploadsDir)) Directory.CreateDirectory(UploadsDir);
                        File.WriteAllBytes(fullPath, bytes);
                        return true;
                    }
                    catch { }
                }
            }
            return false;
        }

        public static string Serialize(object obj)
        {
#if NET8_0_OR_GREATER
            return JsonConvert.SerializeObject(obj, _jsonSettings);
#else
            return _json.Serialize(obj);
#endif
        }

        public static T Deserialize<T>(string json)
        {
#if NET8_0_OR_GREATER
            if (string.IsNullOrWhiteSpace(json)) return default(T);
            Type t = typeof(T);
            if (t == typeof(Dictionary<string, object>) || t == typeof(object) || t == typeof(object[]))
            {
                var token = JToken.Parse(json);
                object converted = ConvertJToken(token);
                if (converted == null) return default(T);
                return (T)converted;
            }
            return JsonConvert.DeserializeObject<T>(json, _jsonSettings);
#else
            return _json.Deserialize<T>(json);
#endif
        }

#if NET8_0_OR_GREATER
        private static object ConvertJToken(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                return null;
            if (token is JObject obj)
            {
                var dict = new Dictionary<string, object>();
                foreach (var prop in obj.Properties())
                {
                    dict[prop.Name] = ConvertJToken(prop.Value);
                }
                return dict;
            }
            if (token is JArray arr)
            {
                var list = new object[arr.Count];
                for (int i = 0; i < arr.Count; i++)
                {
                    list[i] = ConvertJToken(arr[i]);
                }
                return list;
            }
            if (token is JValue val)
            {
                object v = val.Value;
                if (v is long l && l >= int.MinValue && l <= int.MaxValue)
                    return (int)l;
                return v;
            }
            return token.ToString();
        }
#endif

        private static AppDatabase CreateInitialDatabase(string username)
        {
            var db = new AppDatabase();
            EnsureDefaults(db, username);
            string encMode = Security.IsCloudMode
                ? "API anahtarları AES-256-GCM ile şifrelenir; tüm ayarlarınız ve ürünleriniz Vercel Private Blob üzerinde hesabınıza özel kalıcı saklanır."
                : "API anahtarları Windows DPAPI ile şifrelenir; tüm ayarlarınız ve ürünleriniz hesabınıza özel kalıcı saklanır.";
            AddLog(db, "system", "info", "Smart", "Smart Pazaryeri Otomasyon Sistemi '" + username + "' hesabı için başlatıldı.", encMode);
            return db;
        }

        private static void EnsureDefaults(AppDatabase db, string username)
        {
            if (db.AiConfig == null) db.AiConfig = new AiSettings();
            if (db.SyncConfig == null) db.SyncConfig = new SyncSettings();
            if (string.IsNullOrWhiteSpace(db.SyncConfig.WebhookToken))
                db.SyncConfig.WebhookToken = Security.GenerateSecureToken(24);
            if (db.RemoteConfig == null) db.RemoteConfig = new RemoteAccessSettings();
            if (string.IsNullOrWhiteSpace(db.RemoteConfig.AccessPin))
            {
                string envPin = System.Text.RegularExpressions.Regex.Replace(Environment.GetEnvironmentVariable("SMART_ACCESS_PIN") ?? "", @"\D+", "");
                db.RemoteConfig.AccessPin = envPin.Length == 6 ? envPin : Security.Generate6DigitPin();
            }
            if (db.UserProfile == null)
            {
                var acct = UserAuthService.GetUser(username, false);
                string disp = acct != null && !string.IsNullOrWhiteSpace(acct.DisplayName) ? acct.DisplayName : username;
                db.UserProfile = new UserProfileSettings
                {
                    DisplayName = disp,
                    AvatarInitials = BuildInitials(disp)
                };
            }
            if (string.IsNullOrWhiteSpace(db.UserProfile.DisplayName))
                db.UserProfile.DisplayName = username;
            if (string.IsNullOrWhiteSpace(db.UserProfile.AvatarInitials))
                db.UserProfile.AvatarInitials = BuildInitials(db.UserProfile.DisplayName);
            if (string.IsNullOrWhiteSpace(db.UserProfile.PanelTitle))
                db.UserProfile.PanelTitle = "Smart";
            if (string.IsNullOrWhiteSpace(db.UserProfile.StoreName))
                db.UserProfile.StoreName = "Pazaryeri Otomasyonu";
            if (string.IsNullOrWhiteSpace(db.UserProfile.Theme))
                db.UserProfile.Theme = "khaki";
            if (string.IsNullOrWhiteSpace(db.UserProfile.AvatarColor))
                db.UserProfile.AvatarColor = "khaki";
            if (string.IsNullOrWhiteSpace(db.UserProfile.Density))
                db.UserProfile.Density = "comfortable";
            if (string.IsNullOrWhiteSpace(db.UserProfile.DefaultView))
                db.UserProfile.DefaultView = "dashboard";

            if (db.Draft == null) db.Draft = new DraftWorkspace();
            if (db.Draft.UploadedImages == null) db.Draft.UploadedImages = new List<string>();
            if (db.Draft.SelectedTargetMps == null) db.Draft.SelectedTargetMps = new List<string>();
            if (db.Products == null) db.Products = new List<ProductRecord>();
            if (db.Orders == null) db.Orders = new List<SyncedOrderRecord>();
            if (db.ProcessedOrderKeys == null) db.ProcessedOrderKeys = new List<string>();
            if (db.Logs == null) db.Logs = new List<ActivityLog>();
            EnsureAllMarketplaces(db);
        }

        public static string BuildInitials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "SM";
            var parts = name.Trim().Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return (parts[0].Substring(0, 1) + parts[1].Substring(0, 1)).ToUpperInvariant();
            string s = parts[0];
            return (s.Length >= 2 ? s.Substring(0, 2) : s).ToUpperInvariant();
        }

        private static void EnsureAllMarketplaces(AppDatabase db)
        {
            if (db.Marketplaces == null) db.Marketplaces = new List<MarketplaceConfig>();
            var defaults = MarketplaceSchema.GetDefaultConfigs();
            foreach (var def in defaults)
            {
                var existing = db.Marketplaces.Find(m => m.Id == def.Id);
                if (existing == null)
                {
                    db.Marketplaces.Add(def);
                }
                else
                {
                    if (existing.Credentials == null) existing.Credentials = new Dictionary<string, string>();
                    foreach (var kv in def.Credentials)
                    {
                        if (!existing.Credentials.ContainsKey(kv.Key))
                            existing.Credentials[kv.Key] = kv.Value;
                    }
                }
            }
        }
    }
}
