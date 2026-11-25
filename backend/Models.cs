using System;
using System.Collections.Generic;

namespace SmartMarketplace
{
    public class AppDatabase
    {
        public AiSettings AiConfig { get; set; }
        public SyncSettings SyncConfig { get; set; }
        public RemoteAccessSettings RemoteConfig { get; set; }
        public UserProfileSettings UserProfile { get; set; }
        public DraftWorkspace Draft { get; set; }
        public List<MarketplaceConfig> Marketplaces { get; set; }
        public List<ProductRecord> Products { get; set; }
        public List<SyncedOrderRecord> Orders { get; set; }
        public List<string> ProcessedOrderKeys { get; set; }
        public List<ActivityLog> Logs { get; set; }

        public AppDatabase()
        {
            AiConfig = new AiSettings();
            SyncConfig = new SyncSettings();
            RemoteConfig = new RemoteAccessSettings();
            UserProfile = new UserProfileSettings();
            Draft = new DraftWorkspace();
            Marketplaces = new List<MarketplaceConfig>();
            Products = new List<ProductRecord>();
            Orders = new List<SyncedOrderRecord>();
            ProcessedOrderKeys = new List<string>();
            Logs = new List<ActivityLog>();
        }
    }

    public class UserProfileSettings
    {
        public string DisplayName { get; set; }
        public string JobTitle { get; set; }
        public string StoreName { get; set; }
        public string PanelTitle { get; set; }
        public string Email { get; set; }
        public string Bio { get; set; }
        public string AvatarUrl { get; set; }
        public string AvatarColor { get; set; } // "khaki", "slate", "forest", "amber", "plum", "terracotta"
        public string AvatarInitials { get; set; }
        public string Theme { get; set; }       // "khaki", "slate", "forest", "sand", "dark"
        public string Density { get; set; }     // "comfortable", "compact"
        public string DefaultView { get; set; } // "dashboard", "add-product", "inventory", "apis"
        public string UpdatedAt { get; set; }

        public UserProfileSettings()
        {
            DisplayName = "Nihat Stark";
            JobTitle = "Mağaza Yöneticisi";
            StoreName = "Pazaryeri Otomasyonu";
            PanelTitle = "Smart";
            Email = "";
            Bio = "";
            AvatarUrl = "";
            AvatarColor = "khaki";
            AvatarInitials = "NS";
            Theme = "khaki";
            Density = "comfortable";
            DefaultView = "dashboard";
            UpdatedAt = "";
        }
    }

    public class UserAccountRecord
    {
        public string Username { get; set; }
        public string DisplayName { get; set; }
        public string PasswordHash { get; set; }
        public string Role { get; set; } // "admin", "user"
        public int SessionVersion { get; set; }
        public string CreatedAt { get; set; }
        public string LastLoginAt { get; set; }

        public UserAccountRecord()
        {
            Role = "user";
            SessionVersion = 1;
        }
    }

    public class UserAuthStore
    {
        public List<UserAccountRecord> Users { get; set; }

        public UserAuthStore()
        {
            Users = new List<UserAccountRecord>();
        }
    }

    public class RemoteAccessSettings
    {
        public bool AutoStartTunnel { get; set; }
        public string AccessPin { get; set; }

        public RemoteAccessSettings()
        {
            AutoStartTunnel = false;
            AccessPin = "";
        }
    }

    public class DraftWorkspace
    {
        public string ActiveView { get; set; }
        public string Prompt { get; set; }
        public string OptPrice { get; set; }
        public string OptStock { get; set; }
        public string OptBrand { get; set; }
        public string OptBarcode { get; set; }
        public List<string> UploadedImages { get; set; }
        public List<string> SelectedTargetMps { get; set; }
        public string ActiveListingTab { get; set; }
        public string EngineUsed { get; set; }
        public ProductRecord GeneratedDraft { get; set; }
        public string UpdatedAt { get; set; }

        public DraftWorkspace()
        {
            ActiveView = "dashboard";
            Prompt = "";
            OptPrice = "";
            OptStock = "";
            OptBrand = "";
            OptBarcode = "";
            UploadedImages = new List<string>();
            SelectedTargetMps = new List<string>();
            ActiveListingTab = "trendyol";
            EngineUsed = "";
            UpdatedAt = "";
        }
    }

    public class AiSettings
    {
        public string Provider { get; set; } // "auto", "gemini", "openai", "builtin"
        public string GeminiApiKey { get; set; }
        public string GeminiModel { get; set; }
        public string OpenAiApiKey { get; set; }
        public string OpenAiModel { get; set; }
        public bool EnableWebCompetitorAnalysis { get; set; }
        public string Tone { get; set; } // "professional", "persuasive", "minimal"

        public AiSettings()
        {
            Provider = "auto";
            GeminiApiKey = "";
            GeminiModel = "gemini-2.0-flash";
            OpenAiApiKey = "";
            OpenAiModel = "gpt-4o-mini";
            EnableWebCompetitorAnalysis = true;
            Tone = "professional";
        }
    }

    public class SyncSettings
    {
        public bool AutoSyncEnabled { get; set; }
        public bool AutoPullOrders { get; set; }
        public bool RestoreStockOnCancel { get; set; }
        public int IntervalMinutes { get; set; }
        public int LowStockThreshold { get; set; }
        public string LastSyncAt { get; set; }
        public string LastOrderPullAt { get; set; }
        public string WebhookToken { get; set; }

        public SyncSettings()
        {
            AutoSyncEnabled = true;
            AutoPullOrders = true;
            RestoreStockOnCancel = true;
            IntervalMinutes = 5;
            LowStockThreshold = 5;
            LastSyncAt = "";
            LastOrderPullAt = "";
            WebhookToken = "";
        }
    }

    public class SyncedOrderRecord
    {
        public string Id { get; set; }
        public string OrderKey { get; set; }
        public string OrderNumber { get; set; }
        public string MarketplaceId { get; set; }
        public string MarketplaceName { get; set; }
        public string ProductId { get; set; }
        public string ProductName { get; set; }
        public string Sku { get; set; }
        public string Barcode { get; set; }
        public int Quantity { get; set; }
        public string Status { get; set; }
        public int StockBefore { get; set; }
        public int StockAfter { get; set; }
        public int SyncedChannels { get; set; }
        public string Source { get; set; } // "Canlı API", "Webhook", "Simülasyon"
        public string ProcessedAt { get; set; }
        public bool Restored { get; set; }
    }

    public class MarketplaceConfig
    {
        public string Id { get; set; }           // trendyol, hepsiburada, n11, idefix, pazarama, pttavm, amazon
        public string Name { get; set; }
        public string Endpoint { get; set; }
        public string AuthType { get; set; }
        public bool Enabled { get; set; }
        public bool Connected { get; set; }
        public bool SimulationMode { get; set; } // true until live credentials are verified or user toggles live
        public string StatusMessage { get; set; }
        public string LastTestedAt { get; set; }
        public Dictionary<string, string> Credentials { get; set; }

        public MarketplaceConfig()
        {
            Credentials = new Dictionary<string, string>();
            StatusMessage = "Hazır (Test Modu)";
            SimulationMode = true;
        }
    }

    public class ProductRecord
    {
        public string Id { get; set; }
        public string Sku { get; set; }
        public string Barcode { get; set; }
        public string BaseName { get; set; }
        public string Brand { get; set; }
        public string Category { get; set; }
        public string Prompt { get; set; }
        public decimal Price { get; set; }
        public decimal ListPrice { get; set; }
        public int Stock { get; set; }
        public int VatRate { get; set; }
        public double Desi { get; set; }
        public List<string> Images { get; set; }
        public VisionReport VisionData { get; set; }
        public CompetitorReport CompetitorData { get; set; }
        public Dictionary<string, MarketplaceListing> Listings { get; set; }
        public string CreatedAt { get; set; }
        public string UpdatedAt { get; set; }

        public ProductRecord()
        {
            Images = new List<string>();
            Listings = new Dictionary<string, MarketplaceListing>();
            VatRate = 20;
            Desi = 1.0;
        }
    }

    public class MarketplaceListing
    {
        public string MarketplaceId { get; set; }
        public string MarketplaceName { get; set; }
        public bool Selected { get; set; }
        public string Title { get; set; }
        public int MaxTitleLength { get; set; }
        public string Description { get; set; }
        public List<string> BulletPoints { get; set; }
        public string CategoryId { get; set; }
        public string CategoryName { get; set; }
        public decimal Price { get; set; }
        public int SyncedStock { get; set; }
        public string Status { get; set; } // "draft", "published", "simulated", "error", "syncing"
        public string RemoteId { get; set; }
        public string StatusMessage { get; set; }
        public string LastSyncAt { get; set; }
        public ChannelResearch Research { get; set; }
        public List<AttributeItem> Attributes { get; set; }

        public MarketplaceListing()
        {
            BulletPoints = new List<string>();
            Attributes = new List<AttributeItem>();
            Status = "draft";
        }
    }

    public class VisionReport
    {
        public bool Analyzed { get; set; }
        public string EngineUsed { get; set; } // "Google Gemini Vision", "OpenAI Vision", "Smart Görsel Piksel Analizi"
        public string DetectedProductName { get; set; }
        public string DetectedCategory { get; set; }
        public string DominantColor { get; set; }
        public string DominantColorHex { get; set; }
        public string DetectedMaterial { get; set; }
        public string SearchQuery { get; set; }
        public string Confidence { get; set; }
        public decimal EstimatedMinPrice { get; set; }
        public decimal EstimatedMaxPrice { get; set; }
        public List<string> VisualFeatures { get; set; }
        public string Summary { get; set; }

        public VisionReport()
        {
            VisualFeatures = new List<string>();
        }
    }

    public class ChannelResearch
    {
        public string MarketplaceId { get; set; }
        public string MarketplaceName { get; set; }
        public string SearchQuery { get; set; }
        public bool LiveDataFound { get; set; }
        public decimal MinPrice { get; set; }
        public decimal AvgPrice { get; set; }
        public decimal MaxPrice { get; set; }
        public decimal SuggestedPrice { get; set; }
        public string MatchedCategoryId { get; set; }
        public string MatchedCategoryName { get; set; }
        public string CategorySource { get; set; }
        public string IsolationStatus { get; set; }
        public string Summary { get; set; }
        public List<string> ChannelKeywords { get; set; }
        public List<CompetitorSample> Samples { get; set; }

        public ChannelResearch()
        {
            ChannelKeywords = new List<string>();
            Samples = new List<CompetitorSample>();
        }
    }

    public class AttributeItem
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public string Value { get; set; }
        public int ValueId { get; set; }
        public bool AllowCustom { get; set; }
        public bool Required { get; set; }
        public string Source { get; set; } // "prompt", "competitor", "default", "ai"
        public List<string> Options { get; set; }

        public AttributeItem()
        {
            AllowCustom = true;
            Options = new List<string>();
        }
    }

    public class CompetitorReport
    {
        public string DetectedCategory { get; set; }
        public string SearchQuery { get; set; }
        public bool WebSearchUsed { get; set; }
        public string Summary { get; set; }
        public decimal AvgPrice { get; set; }
        public decimal MinPrice { get; set; }
        public decimal MaxPrice { get; set; }
        public List<string> TrendingKeywords { get; set; }
        public List<string> MissingFieldsDetected { get; set; }
        public List<string> AutoFilledHighlights { get; set; }
        public List<CompetitorSample> Samples { get; set; }

        public CompetitorReport()
        {
            TrendingKeywords = new List<string>();
            MissingFieldsDetected = new List<string>();
            AutoFilledHighlights = new List<string>();
            Samples = new List<CompetitorSample>();
        }
    }

    public class CompetitorSample
    {
        public string Title { get; set; }
        public string Source { get; set; }
        public string Url { get; set; }
        public string PriceHint { get; set; }
    }

    public class ActivityLog
    {
        public string Id { get; set; }
        public string Timestamp { get; set; }
        public string Type { get; set; }    // "publish", "sync", "analysis", "api", "stock", "system"
        public string Level { get; set; }   // "success", "info", "warning", "error"
        public string Marketplace { get; set; }
        public string Message { get; set; }
        public string Details { get; set; }
    }

    public class AnalyzeRequest
    {
        public string Prompt { get; set; }
        public List<string> Images { get; set; }
        public List<string> OriginalFileNames { get; set; }
        public List<string> TargetMarketplaces { get; set; }
        public decimal? Price { get; set; }
        public int? Stock { get; set; }
        public string Brand { get; set; }
        public string Barcode { get; set; }
        public string Sku { get; set; }
    }

    public class StockUpdateRequest
    {
        public string ProductId { get; set; }
        public int NewStock { get; set; }
        public string Source { get; set; }
        public string Note { get; set; }
    }
}
