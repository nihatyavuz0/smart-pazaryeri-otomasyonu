using System;
using System.Collections.Generic;

namespace SmartMarketplace
{
    public class MarketplaceAttributeDef
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public bool Required { get; set; }
        public string DefaultValue { get; set; }
    }

    public static class MarketplaceSchema
    {
        public static List<MarketplaceConfig> GetDefaultConfigs()
        {
            return new List<MarketplaceConfig>
            {
                new MarketplaceConfig
                {
                    Id = "trendyol",
                    Name = "Trendyol",
                    Endpoint = "https://apigw.trendyol.com/integration",
                    AuthType = "Basic Auth (API Key : API Secret)",
                    Enabled = true,
                    Connected = false,
                    SimulationMode = true,
                    StatusMessage = "Test Modu Aktif — Canlı için API bilgilerini girin",
                    Credentials = new Dictionary<string, string>
                    {
                        { "supplierId", "" },
                        { "apiKey", "" },
                        { "apiSecret", "" },
                        { "userAgent", "SmartOtomasyon - SelfIntegration" }
                    }
                },
                new MarketplaceConfig
                {
                    Id = "hepsiburada",
                    Name = "Hepsiburada",
                    Endpoint = "https://mpop.hepsiburada.com",
                    AuthType = "Basic Auth (Merchant ID : Service Key)",
                    Enabled = true,
                    Connected = false,
                    SimulationMode = true,
                    StatusMessage = "Test Modu Aktif — Canlı için API bilgilerini girin",
                    Credentials = new Dictionary<string, string>
                    {
                        { "merchantId", "" },
                        { "username", "" },
                        { "serviceKey", "" }
                    }
                },
                new MarketplaceConfig
                {
                    Id = "n11",
                    Name = "N11",
                    Endpoint = "https://api.n11.com/ws",
                    AuthType = "App Key & App Secret (REST / SOAP)",
                    Enabled = true,
                    Connected = false,
                    SimulationMode = true,
                    StatusMessage = "Test Modu Aktif — Canlı için API bilgilerini girin",
                    Credentials = new Dictionary<string, string>
                    {
                        { "appKey", "" },
                        { "appSecret", "" },
                        { "shipmentTemplate", "Standart Kargo" }
                    }
                },
                new MarketplaceConfig
                {
                    Id = "idefix",
                    Name = "İdefix",
                    Endpoint = "https://merchantapi.idefix.com",
                    AuthType = "X-API-KEY Base64(ApiKey:ApiSecret)",
                    Enabled = true,
                    Connected = false,
                    SimulationMode = true,
                    StatusMessage = "Test Modu Aktif — Canlı için API bilgilerini girin",
                    Credentials = new Dictionary<string, string>
                    {
                        { "vendorId", "" },
                        { "apiKey", "" },
                        { "apiSecret", "" }
                    }
                },
                new MarketplaceConfig
                {
                    Id = "pazarama",
                    Name = "Pazarama",
                    Endpoint = "https://isortagimapi.pazarama.com",
                    AuthType = "OAuth 2.0 Client Credentials",
                    Enabled = true,
                    Connected = false,
                    SimulationMode = true,
                    StatusMessage = "Test Modu Aktif — Canlı için API bilgilerini girin",
                    Credentials = new Dictionary<string, string>
                    {
                        { "clientId", "" },
                        { "clientSecret", "" }
                    }
                },
                new MarketplaceConfig
                {
                    Id = "pttavm",
                    Name = "PttAVM",
                    Endpoint = "https://ws.pttavm.com:93/service.svc",
                    AuthType = "WS-Security / API Key & Shop ID",
                    Enabled = true,
                    Connected = false,
                    SimulationMode = true,
                    StatusMessage = "Test Modu Aktif — Canlı için API bilgilerini girin",
                    Credentials = new Dictionary<string, string>
                    {
                        { "apiKey", "" },
                        { "username", "" },
                        { "password", "" },
                        { "shopId", "" }
                    }
                },
                new MarketplaceConfig
                {
                    Id = "amazon",
                    Name = "Amazon TR",
                    Endpoint = "https://sellingpartnerapi-eu.amazon.com",
                    AuthType = "SP-API LWA OAuth2 + Refresh Token",
                    Enabled = true,
                    Connected = false,
                    SimulationMode = true,
                    StatusMessage = "Test Modu Aktif — Canlı için SP-API bilgilerini girin",
                    Credentials = new Dictionary<string, string>
                    {
                        { "sellerId", "" },
                        { "marketplaceId", "A33AVAJ2PDY3EV" },
                        { "lwaClientId", "" },
                        { "lwaClientSecret", "" },
                        { "refreshToken", "" }
                    }
                }
            };
        }

        public static int GetMaxTitleLength(string marketplaceId)
        {
            switch (marketplaceId)
            {
                case "trendyol": return 100;
                case "hepsiburada": return 150;
                case "n11": return 65;
                case "idefix": return 120;
                case "pazarama": return 100;
                case "pttavm": return 100;
                case "amazon": return 200;
                default: return 100;
            }
        }

        public static string GetMarketplaceName(string marketplaceId)
        {
            switch (marketplaceId)
            {
                case "trendyol": return "Trendyol";
                case "hepsiburada": return "Hepsiburada";
                case "n11": return "N11";
                case "idefix": return "İdefix";
                case "pazarama": return "Pazarama";
                case "pttavm": return "PttAVM";
                case "amazon": return "Amazon TR";
                default: return marketplaceId;
            }
        }

        public static string GetCategoryIdForMarketplace(string marketplaceId, string category)
        {
            string c = (category ?? "").ToLowerInvariant();
            if (c.Contains("giyim") || c.Contains("tekstil") || c.Contains("sweatshirt") || c.Contains("tişört") || c.Contains("elbise"))
            {
                switch (marketplaceId)
                {
                    case "trendyol": return "1014";
                    case "hepsiburada": return "60001028";
                    case "n11": return "1000284";
                    case "idefix": return "3412";
                    case "pazarama": return "a4c2-giyim-01";
                    case "pttavm": return "412";
                    case "amazon": return "APPAREL";
                }
            }
            if (c.Contains("elektronik") || c.Contains("kulaklık") || c.Contains("telefon") || c.Contains("bilgisayar") || c.Contains("saat"))
            {
                switch (marketplaceId)
                {
                    case "trendyol": return "1040";
                    case "hepsiburada": return "60000101";
                    case "n11": return "1000221";
                    case "idefix": return "2105";
                    case "pazarama": return "b9e1-elektronik-02";
                    case "pttavm": return "208";
                    case "amazon": return "CONSUMER_ELECTRONICS";
                }
            }
            if (c.Contains("kozmetik") || c.Contains("bakım") || c.Contains("parfüm") || c.Contains("krem"))
            {
                switch (marketplaceId)
                {
                    case "trendyol": return "1092";
                    case "hepsiburada": return "60001540";
                    case "n11": return "1000145";
                    case "idefix": return "4820";
                    case "pazarama": return "c7d4-kozmetik-03";
                    case "pttavm": return "615";
                    case "amazon": return "BEAUTY";
                }
            }
            if (c.Contains("ev") || c.Contains("mutfak") || c.Contains("dekor") || c.Contains("mobilya"))
            {
                switch (marketplaceId)
                {
                    case "trendyol": return "1075";
                    case "hepsiburada": return "60002010";
                    case "n11": return "1000310";
                    case "idefix": return "5201";
                    case "pazarama": return "d3f8-evyasam-04";
                    case "pttavm": return "710";
                    case "amazon": return "HOME";
                }
            }
            if (c.Contains("ayakkabı") || c.Contains("çanta"))
            {
                switch (marketplaceId)
                {
                    case "trendyol": return "1021";
                    case "hepsiburada": return "60001105";
                    case "n11": return "1000290";
                    case "idefix": return "3510";
                    case "pazarama": return "e1a5-ayakkabi-05";
                    case "pttavm": return "455";
                    case "amazon": return "SHOES";
                }
            }
            switch (marketplaceId)
            {
                case "trendyol": return "1001";
                case "hepsiburada": return "60000001";
                case "n11": return "1000001";
                case "idefix": return "1001";
                case "pazarama": return "genel-00";
                case "pttavm": return "100";
                case "amazon": return "PRODUCT";
                default: return "1000";
            }
        }

        public static List<MarketplaceAttributeDef> GetMandatoryAttributes(string marketplaceId, string category)
        {
            var list = new List<MarketplaceAttributeDef>();
            string c = (category ?? "").ToLowerInvariant();
            bool isApparel = c.Contains("giyim") || c.Contains("tekstil") || c.Contains("sweatshirt") || c.Contains("tişört") || c.Contains("elbise") || c.Contains("ayakkabı");
            bool isElectronics = c.Contains("elektronik") || c.Contains("kulaklık") || c.Contains("telefon") || c.Contains("bilgisayar") || c.Contains("saat");
            bool isCosmetics = c.Contains("kozmetik") || c.Contains("bakım") || c.Contains("parfüm") || c.Contains("krem");

            switch (marketplaceId)
            {
                case "trendyol":
                    list.Add(new MarketplaceAttributeDef { Key = "brand", Label = "Marka", Required = true, DefaultValue = "Genel Markalar" });
                    list.Add(new MarketplaceAttributeDef { Key = "color", Label = "Renk", Required = true, DefaultValue = "Standart" });
                    list.Add(new MarketplaceAttributeDef { Key = "origin", Label = "Menşei", Required = true, DefaultValue = "Türkiye (TR)" });
                    list.Add(new MarketplaceAttributeDef { Key = "vatRate", Label = "KDV Oranı (%)", Required = true, DefaultValue = isApparel ? "10" : "20" });
                    list.Add(new MarketplaceAttributeDef { Key = "dimensionalWeight", Label = "Desi", Required = true, DefaultValue = "1" });
                    list.Add(new MarketplaceAttributeDef { Key = "cargoCompany", Label = "Kargo Firması", Required = true, DefaultValue = "Trendyol Express" });
                    if (isApparel)
                    {
                        list.Add(new MarketplaceAttributeDef { Key = "size", Label = "Beden", Required = true, DefaultValue = "M" });
                        list.Add(new MarketplaceAttributeDef { Key = "fabric", Label = "Materyal / Kumaş", Required = true, DefaultValue = "%100 Pamuk" });
                        list.Add(new MarketplaceAttributeDef { Key = "pattern", Label = "Kalıp", Required = true, DefaultValue = "Regular" });
                        list.Add(new MarketplaceAttributeDef { Key = "gender", Label = "Cinsiyet", Required = true, DefaultValue = "Unisex" });
                    }
                    else if (isElectronics)
                    {
                        list.Add(new MarketplaceAttributeDef { Key = "warrantyType", Label = "Garanti Tipi", Required = true, DefaultValue = "Resmi Distribütör Garantili" });
                        list.Add(new MarketplaceAttributeDef { Key = "warrantyDuration", Label = "Garanti Süresi", Required = true, DefaultValue = "24 Ay" });
                        list.Add(new MarketplaceAttributeDef { Key = "connectivity", Label = "Bağlantı Tipi", Required = true, DefaultValue = "Kablosuz / Bluetooth" });
                    }
                    else if (isCosmetics)
                    {
                        list.Add(new MarketplaceAttributeDef { Key = "skinType", Label = "Cilt Tipi", Required = true, DefaultValue = "Tüm Cilt Tipleri" });
                        list.Add(new MarketplaceAttributeDef { Key = "volume", Label = "Hacim / Miktar", Required = true, DefaultValue = "100 ml" });
                    }
                    else
                    {
                        list.Add(new MarketplaceAttributeDef { Key = "material", Label = "Materyal", Required = true, DefaultValue = "Birinci Sınıf Malzeme" });
                        list.Add(new MarketplaceAttributeDef { Key = "packageContent", Label = "Parça Sayısı", Required = true, DefaultValue = "1 Parça" });
                    }
                    break;

                case "hepsiburada":
                    list.Add(new MarketplaceAttributeDef { Key = "Brand", Label = "Marka", Required = true, DefaultValue = "Genel Markalar" });
                    list.Add(new MarketplaceAttributeDef { Key = "Renk", Label = "Renk", Required = true, DefaultValue = "Standart" });
                    list.Add(new MarketplaceAttributeDef { Key = "GarantiSuresi", Label = "Garanti Süresi (Ay)", Required = true, DefaultValue = isElectronics ? "24" : "0" });
                    list.Add(new MarketplaceAttributeDef { Key = "Tax", Label = "KDV (%)", Required = true, DefaultValue = isApparel ? "10" : "20" });
                    list.Add(new MarketplaceAttributeDef { Key = "kg", Label = "Desi / Ağırlık", Required = true, DefaultValue = "1" });
                    list.Add(new MarketplaceAttributeDef { Key = "Mensei", Label = "Menşei", Required = true, DefaultValue = "Türkiye" });
                    if (isApparel)
                    {
                        list.Add(new MarketplaceAttributeDef { Key = "Beden", Label = "Beden", Required = true, DefaultValue = "M" });
                        list.Add(new MarketplaceAttributeDef { Key = "KumasTipi", Label = "Kumaş Tipi", Required = true, DefaultValue = "Pamuklu" });
                        list.Add(new MarketplaceAttributeDef { Key = "Cinsiyet", Label = "Cinsiyet", Required = true, DefaultValue = "Unisex" });
                    }
                    else if (isElectronics)
                    {
                        list.Add(new MarketplaceAttributeDef { Key = "GarantiTipi", Label = "Garanti Tipi", Required = true, DefaultValue = "İthalatçı / Distribütör Garantili" });
                        list.Add(new MarketplaceAttributeDef { Key = "Baglanti", Label = "Bağlantı", Required = true, DefaultValue = "Bluetooth 5.3" });
                    }
                    break;

                case "n11":
                    list.Add(new MarketplaceAttributeDef { Key = "Marka", Label = "Marka", Required = true, DefaultValue = "Genel Markalar" });
                    list.Add(new MarketplaceAttributeDef { Key = "Renk", Label = "Renk", Required = true, DefaultValue = "Standart" });
                    list.Add(new MarketplaceAttributeDef { Key = "preparingDay", Label = "Hazırlama Süresi (Gün)", Required = true, DefaultValue = "2" });
                    list.Add(new MarketplaceAttributeDef { Key = "shipmentTemplate", Label = "Kargo Şablonu", Required = true, DefaultValue = "Standart Kargo" });
                    list.Add(new MarketplaceAttributeDef { Key = "domestic", Label = "Yerli Üretim", Required = true, DefaultValue = "Evet" });
                    list.Add(new MarketplaceAttributeDef { Key = "productCondition", Label = "Ürün Durumu", Required = true, DefaultValue = "Yeni" });
                    if (isApparel)
                    {
                        list.Add(new MarketplaceAttributeDef { Key = "Beden", Label = "Beden", Required = true, DefaultValue = "M" });
                        list.Add(new MarketplaceAttributeDef { Key = "Materyal", Label = "Materyal", Required = true, DefaultValue = "Pamuk" });
                    }
                    break;

                case "idefix":
                    list.Add(new MarketplaceAttributeDef { Key = "brandName", Label = "Marka", Required = true, DefaultValue = "Genel Markalar" });
                    list.Add(new MarketplaceAttributeDef { Key = "color", Label = "Renk", Required = true, DefaultValue = "Standart" });
                    list.Add(new MarketplaceAttributeDef { Key = "vatRate", Label = "KDV Oranı (%)", Required = true, DefaultValue = isApparel ? "10" : "20" });
                    list.Add(new MarketplaceAttributeDef { Key = "deliveryDuration", Label = "Kargo Teslim Süresi (Gün)", Required = true, DefaultValue = "2" });
                    list.Add(new MarketplaceAttributeDef { Key = "desi", Label = "Desi", Required = true, DefaultValue = "1" });
                    list.Add(new MarketplaceAttributeDef { Key = "originCountry", Label = "Menşei", Required = true, DefaultValue = "TR" });
                    if (isApparel)
                        list.Add(new MarketplaceAttributeDef { Key = "size", Label = "Beden", Required = true, DefaultValue = "M" });
                    break;

                case "pazarama":
                    list.Add(new MarketplaceAttributeDef { Key = "brand", Label = "Marka", Required = true, DefaultValue = "Genel Markalar" });
                    list.Add(new MarketplaceAttributeDef { Key = "color", Label = "Renk", Required = true, DefaultValue = "Standart" });
                    list.Add(new MarketplaceAttributeDef { Key = "vatRate", Label = "KDV (%)", Required = true, DefaultValue = isApparel ? "10" : "20" });
                    list.Add(new MarketplaceAttributeDef { Key = "desi", Label = "Desi", Required = true, DefaultValue = "1" });
                    list.Add(new MarketplaceAttributeDef { Key = "stockCode", Label = "Stok Kodu", Required = true, DefaultValue = "OTOMATIK" });
                    if (isApparel)
                    {
                        list.Add(new MarketplaceAttributeDef { Key = "size", Label = "Beden", Required = true, DefaultValue = "M" });
                        list.Add(new MarketplaceAttributeDef { Key = "material", Label = "Materyal", Required = true, DefaultValue = "Pamuklu" });
                    }
                    break;

                case "pttavm":
                    list.Add(new MarketplaceAttributeDef { Key = "Marka", Label = "Marka", Required = true, DefaultValue = "Genel Markalar" });
                    list.Add(new MarketplaceAttributeDef { Key = "Renk", Label = "Renk", Required = true, DefaultValue = "Standart" });
                    list.Add(new MarketplaceAttributeDef { Key = "KDVOran", Label = "KDV Oranı (%)", Required = true, DefaultValue = isApparel ? "10" : "20" });
                    list.Add(new MarketplaceAttributeDef { Key = "Desi", Label = "Desi", Required = true, DefaultValue = "1" });
                    list.Add(new MarketplaceAttributeDef { Key = "GarantiSuresi", Label = "Garanti Süresi (Ay)", Required = true, DefaultValue = isElectronics ? "24" : "12" });
                    list.Add(new MarketplaceAttributeDef { Key = "GarantiVerenFirma", Label = "Garanti Sağlayıcı", Required = true, DefaultValue = "Üretici / Distribütör" });
                    if (isApparel)
                        list.Add(new MarketplaceAttributeDef { Key = "Beden", Label = "Beden", Required = true, DefaultValue = "M" });
                    break;

                case "amazon":
                    list.Add(new MarketplaceAttributeDef { Key = "brand", Label = "Marka (Brand)", Required = true, DefaultValue = "Genel Markalar" });
                    list.Add(new MarketplaceAttributeDef { Key = "color_name", Label = "Renk (Color)", Required = true, DefaultValue = "Standart" });
                    list.Add(new MarketplaceAttributeDef { Key = "country_of_origin", Label = "Menşei (Country of Origin)", Required = true, DefaultValue = "TR" });
                    list.Add(new MarketplaceAttributeDef { Key = "fulfillment_channel", Label = "Gönderim Kanalı", Required = true, DefaultValue = "FBM (Satıcı Gönderimi)" });
                    list.Add(new MarketplaceAttributeDef { Key = "condition_type", Label = "Ürün Durumu", Required = true, DefaultValue = "new_new (Yeni)" });
                    list.Add(new MarketplaceAttributeDef { Key = "item_type_keyword", Label = "Arama Tipi (Item Type)", Required = true, DefaultValue = isApparel ? "apparel-turkiye" : (isElectronics ? "electronics-turkiye" : "general-turkiye") });
                    if (isApparel)
                    {
                        list.Add(new MarketplaceAttributeDef { Key = "size_name", Label = "Beden (Size)", Required = true, DefaultValue = "M" });
                        list.Add(new MarketplaceAttributeDef { Key = "material_type", Label = "Materyal (Material)", Required = true, DefaultValue = "Pamuk" });
                        list.Add(new MarketplaceAttributeDef { Key = "department_name", Label = "Departman", Required = true, DefaultValue = "Unisex Yetişkin" });
                    }
                    break;
            }

            return list;
        }
    }
}
