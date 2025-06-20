using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Libraries;

namespace Oxide.Plugins
{
    [Info("Orangemart", "RustySats", "0.4.0")]
    [Description("Allows players to buy and sell in-game units and VIP status using Bitcoin Lightning Network payments via LNbits with WebSocket support and comprehensive protection features")]
    public class Orangemart : CovalencePlugin
    {
        // Configuration sections and keys
        private static class ConfigSections
        {
            public const string Commands = "Commands";
            public const string CurrencySettings = "CurrencySettings";
            public const string Discord = "Discord";
            public const string InvoiceSettings = "InvoiceSettings";
            public const string VIPSettings = "VIPSettings";
        }

        private static class ConfigKeys
        {
            // Commands
            public const string BuyCurrencyCommandName = "BuyCurrencyCommandName";
            public const string SendCurrencyCommandName = "SendCurrencyCommandName";
            public const string BuyVipCommandName = "BuyVipCommandName";

            // CurrencySettings
            public const string CurrencyItemID = "CurrencyItemID";
            public const string CurrencyName = "CurrencyName";
            public const string CurrencySkinID = "CurrencySkinID";
            public const string PricePerCurrencyUnit = "PricePerCurrencyUnit";
            public const string SatsPerCurrencyUnit = "SatsPerCurrencyUnit";
            
            // NEW: Protection Settings
            public const string MaxPurchaseAmount = "MaxPurchaseAmount";
            public const string MaxSendAmount = "MaxSendAmount";
            public const string CommandCooldownSeconds = "CommandCooldownSeconds";
            public const string MaxPendingInvoicesPerPlayer = "MaxPendingInvoicesPerPlayer";

            // Discord
            public const string DiscordChannelName = "DiscordChannelName";
            public const string DiscordWebhookUrl = "DiscordWebhookUrl";

            // InvoiceSettings
            public const string BlacklistedDomains = "BlacklistedDomains";
            public const string WhitelistedDomains = "WhitelistedDomains";
            public const string CheckIntervalSeconds = "CheckIntervalSeconds";
            public const string InvoiceTimeoutSeconds = "InvoiceTimeoutSeconds";
            public const string LNbitsApiKey = "LNbitsApiKey";
            public const string LNbitsBaseUrl = "LNbitsBaseUrl";
            public const string MaxRetries = "MaxRetries";
            public const string UseWebSockets = "UseWebSockets";
            public const string WebSocketReconnectDelay = "WebSocketReconnectDelay";

            // VIPSettings
            public const string VipPrice = "VipPrice";
            public const string VipCommand = "VipCommand";
        }

        // Configuration variables
        private int currencyItemID;
        private string buyCurrencyCommandName;
        private string sendCurrencyCommandName;
        private string buyVipCommandName;
        private int vipPrice;
        private string vipCommand;
        private string currencyName;
        private int satsPerCurrencyUnit;
        private int pricePerCurrencyUnit;
        private string discordChannelName;
        private ulong currencySkinID;
        private int checkIntervalSeconds;
        private int invoiceTimeoutSeconds;
        private int maxRetries;
        private bool useWebSockets;
        private int webSocketReconnectDelay;
        private List<string> blacklistedDomains = new List<string>();
        private List<string> whitelistedDomains = new List<string>();
        
        // NEW: Protection and rate limiting variables
        private int maxPurchaseAmount;
        private int maxSendAmount;
        private int commandCooldownSeconds;
        private int maxPendingInvoicesPerPlayer;
        private Dictionary<string, DateTime> lastCommandTime = new Dictionary<string, DateTime>();

        private const string SellLogFile = "Orangemart/send_bitcoin.json";
        private const string BuyInvoiceLogFile = "Orangemart/buy_invoices.json";
        private LNbitsConfig config;
        private List<PendingInvoice> pendingInvoices = new List<PendingInvoice>();
        private Dictionary<string, int> retryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        
        // WebSocket tracking
        private Dictionary<string, WebSocketConnection> activeWebSockets = new Dictionary<string, WebSocketConnection>();
        private readonly object webSocketLock = new object();

        // Transaction status constants
        private static class TransactionStatus
        {
            public const string INITIATED = "INITIATED";
            public const string PROCESSING = "PROCESSING";
            public const string COMPLETED = "COMPLETED";
            public const string FAILED = "FAILED";
            public const string EXPIRED = "EXPIRED";
            public const string REFUNDED = "REFUNDED";
        }

        // WebSocket connection wrapper
        private class WebSocketConnection
        {
            public ClientWebSocket WebSocket { get; set; }
            public CancellationTokenSource CancellationTokenSource { get; set; }
            public string InvoiceKey { get; set; }
            public PendingInvoice Invoice { get; set; }
            public DateTime ConnectedAt { get; set; }
            public int ReconnectAttempts { get; set; }
            public Task ListenTask { get; set; }
        }

        // WebSocket response structure
        private class WebSocketPaymentUpdate
        {
            [JsonProperty("balance")]
            public long Balance { get; set; }
            
            [JsonProperty("payment")]
            public WebSocketPayment Payment { get; set; }
        }

        private class WebSocketPayment
        {
            [JsonProperty("checking_id")]
            public string CheckingId { get; set; }
            
            [JsonProperty("pending")]
            public bool Pending { get; set; }
            
            [JsonProperty("amount")]
            public long Amount { get; set; }
            
            [JsonProperty("fee")]
            public long Fee { get; set; }
            
            [JsonProperty("memo")]
            public string Memo { get; set; }
            
            [JsonProperty("time")]
            public long Time { get; set; }
            
            [JsonProperty("bolt11")]
            public string Bolt11 { get; set; }
            
            [JsonProperty("preimage")]
            public string Preimage { get; set; }
            
            [JsonProperty("payment_hash")]
            public string PaymentHash { get; set; }
            
            [JsonProperty("expiry")]
            public long Expiry { get; set; }
            
            [JsonProperty("extra")]
            public Dictionary<string, object> Extra { get; set; }
        }

        // LNbits Configuration
        private class LNbitsConfig
        {
            public string BaseUrl { get; set; }
            public string ApiKey { get; set; }
            public string DiscordWebhookUrl { get; set; }
            public string WebSocketUrl { get; set; }

            public static LNbitsConfig ParseLNbitsConnection(string baseUrl, string apiKey, string discordWebhookUrl)
            {
                var trimmedBaseUrl = baseUrl.TrimEnd('/');
                if (!Uri.IsWellFormedUriString(trimmedBaseUrl, UriKind.Absolute))
                    throw new Exception("Invalid base URL in connection string.");

                // Convert HTTP URL to WebSocket URL
                var wsUrl = trimmedBaseUrl.Replace("https://", "wss://").Replace("http://", "ws://");

                return new LNbitsConfig
                {
                    BaseUrl = trimmedBaseUrl,
                    ApiKey = apiKey,
                    DiscordWebhookUrl = discordWebhookUrl,
                    WebSocketUrl = wsUrl
                };
            }
        }

        // Invoice and Payment Classes
        private class InvoiceResponse
        {
            [JsonProperty("bolt11")]
            public string PaymentRequest { get; set; }

            [JsonProperty("payment_hash")]
            public string PaymentHash { get; set; }
        }

        // Wrapper class for LNbits v1 responses
        private class InvoiceResponseWrapper
        {
            [JsonProperty("data")]
            public InvoiceResponse Data { get; set; }
        }

        // Enhanced SellInvoiceLogEntry with status tracking
        private class SellInvoiceLogEntry
        {
            public string TransactionId { get; set; }
            public string SteamID { get; set; }
            public string LightningAddress { get; set; }
            public string Status { get; set; }
            public bool Success { get; set; }
            public int SatsAmount { get; set; }
            public string PaymentHash { get; set; }
            public bool CurrencyReturned { get; set; }
            public DateTime Timestamp { get; set; }
            public DateTime? CompletedTimestamp { get; set; }
            public int RetryCount { get; set; }
            public string FailureReason { get; set; }
        }

        // Enhanced BuyInvoiceLogEntry with status tracking
        private class BuyInvoiceLogEntry
        {
            public string TransactionId { get; set; }
            public string SteamID { get; set; }
            public string InvoiceID { get; set; }
            public string Status { get; set; }
            public bool IsPaid { get; set; }
            public DateTime Timestamp { get; set; }
            public DateTime? CompletedTimestamp { get; set; }
            public int Amount { get; set; }
            public bool CurrencyGiven { get; set; }
            public bool VipGranted { get; set; }
            public int RetryCount { get; set; }
            public string PurchaseType { get; set; }
        }

        private class PendingInvoice
        {
            public string TransactionId { get; set; }
            public string RHash { get; set; }
            public IPlayer Player { get; set; }
            public int Amount { get; set; }
            public string Memo { get; set; }
            public DateTime CreatedAt { get; set; }
            public PurchaseType Type { get; set; }
        }

        private enum PurchaseType
        {
            Currency,
            Vip,
            SendBitcoin
        }

        private class PaymentStatusResponse
        {
            [JsonProperty("paid")]
            public bool Paid { get; set; }

            [JsonProperty("preimage")]
            public string Preimage { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                bool configChanged = false;

                // Parse LNbits connection settings
                config = LNbitsConfig.ParseLNbitsConnection(
                    GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.LNbitsBaseUrl, "https://your-lnbits-instance.com", ref configChanged),
                    GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.LNbitsApiKey, "your-lnbits-admin-api-key", ref configChanged),
                    GetConfigValue(ConfigSections.Discord, ConfigKeys.DiscordWebhookUrl, "https://discord.com/api/webhooks/your_webhook_url", ref configChanged)
                );

                // Parse Currency Settings
                currencyItemID = GetConfigValue(ConfigSections.CurrencySettings, ConfigKeys.CurrencyItemID, 1776460938, ref configChanged);
                currencyName = GetConfigValue(ConfigSections.CurrencySettings, ConfigKeys.CurrencyName, "blood", ref configChanged);
                satsPerCurrencyUnit = GetConfigValue(ConfigSections.CurrencySettings, ConfigKeys.SatsPerCurrencyUnit, 1, ref configChanged);
                pricePerCurrencyUnit = GetConfigValue(ConfigSections.CurrencySettings, ConfigKeys.PricePerCurrencyUnit, 1, ref configChanged);
                currencySkinID = GetConfigValue(ConfigSections.CurrencySettings, ConfigKeys.CurrencySkinID, 0UL, ref configChanged);

                // NEW: Parse Protection Settings
                maxPurchaseAmount = GetConfigValue(ConfigSections.CurrencySettings, ConfigKeys.MaxPurchaseAmount, 10000, ref configChanged);
                maxSendAmount = GetConfigValue(ConfigSections.CurrencySettings, ConfigKeys.MaxSendAmount, 10000, ref configChanged);
                commandCooldownSeconds = GetConfigValue(ConfigSections.CurrencySettings, ConfigKeys.CommandCooldownSeconds, 0, ref configChanged);
                maxPendingInvoicesPerPlayer = GetConfigValue(ConfigSections.CurrencySettings, ConfigKeys.MaxPendingInvoicesPerPlayer, 1, ref configChanged);

                // Ensure non-negative values
                if (maxPurchaseAmount < 0) maxPurchaseAmount = 0;
                if (maxSendAmount < 0) maxSendAmount = 0;
                if (commandCooldownSeconds < 0) commandCooldownSeconds = 0;
                if (maxPendingInvoicesPerPlayer < 0) maxPendingInvoicesPerPlayer = 0;

                // Parse Command Names
                buyCurrencyCommandName = GetConfigValue(ConfigSections.Commands, ConfigKeys.BuyCurrencyCommandName, "buyblood", ref configChanged);
                sendCurrencyCommandName = GetConfigValue(ConfigSections.Commands, ConfigKeys.SendCurrencyCommandName, "sendblood", ref configChanged);
                buyVipCommandName = GetConfigValue(ConfigSections.Commands, ConfigKeys.BuyVipCommandName, "buyvip", ref configChanged);

                // Parse VIP Settings
                vipPrice = GetConfigValue(ConfigSections.VIPSettings, ConfigKeys.VipPrice, 1000, ref configChanged);
                vipCommand = GetConfigValue(ConfigSections.VIPSettings, ConfigKeys.VipCommand, "oxide.usergroup add {player} vip", ref configChanged);

                // Parse Discord Settings
                discordChannelName = GetConfigValue(ConfigSections.Discord, ConfigKeys.DiscordChannelName, "mart", ref configChanged);

                // Parse Invoice Settings
                checkIntervalSeconds = GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.CheckIntervalSeconds, 10, ref configChanged);
                invoiceTimeoutSeconds = GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.InvoiceTimeoutSeconds, 300, ref configChanged);
                maxRetries = GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.MaxRetries, 25, ref configChanged);
                useWebSockets = GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.UseWebSockets, true, ref configChanged);
                webSocketReconnectDelay = GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.WebSocketReconnectDelay, 5, ref configChanged);

                blacklistedDomains = GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.BlacklistedDomains, new List<string> { "example.com", "blacklisted.net" }, ref configChanged)
                    .Select(d => d.ToLower()).ToList();

                whitelistedDomains = GetConfigValue(ConfigSections.InvoiceSettings, ConfigKeys.WhitelistedDomains, new List<string>(), ref configChanged)
                    .Select(d => d.ToLower()).ToList();

                MigrateConfig();

                if (configChanged)
                {
                    SaveConfig();
                }

                // Log protection settings
                Puts($"Protection Settings: MaxPurchase={maxPurchaseAmount}, MaxSend={maxSendAmount}, Cooldown={commandCooldownSeconds}s, MaxPending={maxPendingInvoicesPerPlayer}");
            }
            catch (Exception ex)
            {
                PrintError($"Failed to load configuration: {ex.Message}");
            }
        }

        private void MigrateConfig()
        {
            bool configChanged = false;

            if (!(Config[ConfigSections.InvoiceSettings] is Dictionary<string, object> invoiceSettings))
            {
                invoiceSettings = new Dictionary<string, object>();
                Config[ConfigSections.InvoiceSettings] = invoiceSettings;
                configChanged = true;
            }

            if (!invoiceSettings.ContainsKey(ConfigKeys.WhitelistedDomains))
            {
                invoiceSettings[ConfigKeys.WhitelistedDomains] = new List<string>();
                configChanged = true;
            }

            if (!invoiceSettings.ContainsKey(ConfigKeys.UseWebSockets))
            {
                invoiceSettings[ConfigKeys.UseWebSockets] = true;
                configChanged = true;
            }

            if (!invoiceSettings.ContainsKey(ConfigKeys.WebSocketReconnectDelay))
            {
                invoiceSettings[ConfigKeys.WebSocketReconnectDelay] = 5;
                configChanged = true;
            }

            // Migrate VIP settings from old format to new format
            if (!(Config[ConfigSections.VIPSettings] is Dictionary<string, object> vipSettings))
            {
                vipSettings = new Dictionary<string, object>();
                Config[ConfigSections.VIPSettings] = vipSettings;
                configChanged = true;
            }

            // Check if old VipPermissionGroup exists and migrate to VipCommand
            if (vipSettings.ContainsKey("VipPermissionGroup") && !vipSettings.ContainsKey(ConfigKeys.VipCommand))
            {
                string oldGroup = vipSettings["VipPermissionGroup"].ToString();
                vipSettings[ConfigKeys.VipCommand] = $"oxide.usergroup add {{player}} {oldGroup}";
                vipSettings.Remove("VipPermissionGroup");
                configChanged = true;
                Puts($"[Migration] Converted VipPermissionGroup '{oldGroup}' to VipCommand");
            }

            // Ensure VipCommand exists with default value
            if (!vipSettings.ContainsKey(ConfigKeys.VipCommand))
            {
                vipSettings[ConfigKeys.VipCommand] = "oxide.usergroup add {player} vip";
                configChanged = true;
            }

            // NEW: Migrate protection settings
            if (!(Config[ConfigSections.CurrencySettings] is Dictionary<string, object> currencySettings))
            {
                currencySettings = new Dictionary<string, object>();
                Config[ConfigSections.CurrencySettings] = currencySettings;
                configChanged = true;
            }

            // Add new protection settings if missing
            if (!currencySettings.ContainsKey(ConfigKeys.MaxPurchaseAmount))
            {
                currencySettings[ConfigKeys.MaxPurchaseAmount] = 10000;
                configChanged = true;
                Puts("[Migration] Added MaxPurchaseAmount = 10000");
            }

            if (!currencySettings.ContainsKey(ConfigKeys.MaxSendAmount))
            {
                currencySettings[ConfigKeys.MaxSendAmount] = 10000;
                configChanged = true;
                Puts("[Migration] Added MaxSendAmount = 10000");
            }

            if (!currencySettings.ContainsKey(ConfigKeys.CommandCooldownSeconds))
            {
                currencySettings[ConfigKeys.CommandCooldownSeconds] = 0;
                configChanged = true;
                Puts("[Migration] Added CommandCooldownSeconds = 0 (disabled)");
            }

            if (!currencySettings.ContainsKey(ConfigKeys.MaxPendingInvoicesPerPlayer))
            {
                currencySettings[ConfigKeys.MaxPendingInvoicesPerPlayer] = 1;
                configChanged = true;
                Puts("[Migration] Added MaxPendingInvoicesPerPlayer = 1");
            }

            if (configChanged)
            {
                SaveConfig();
                Puts("[Migration] Configuration updated with protection settings");
            }
        }

        private T GetConfigValue<T>(string section, string key, T defaultValue, ref bool configChanged)
        {
            if (!(Config[section] is Dictionary<string, object> data))
            {
                data = new Dictionary<string, object>();
                Config[section] = data;
                configChanged = true;
            }

            if (!data.TryGetValue(key, out var value))
            {
                value = defaultValue;
                data[key] = value;
                configChanged = true;
            }

            try
            {
                if (value is T tValue)
                {
                    return tValue;
                }
                else if (typeof(T) == typeof(List<string>))
                {
                    if (value is IEnumerable<object> enumerable)
                    {
                        return (T)(object)enumerable.Select(item => item.ToString()).ToList();
                    }
                    else if (value is string singleString)
                    {
                        return (T)(object)new List<string> { singleString };
                    }
                    else
                    {
                        PrintError($"Unexpected type for [{section}][{key}]. Using default value.");
                        data[key] = defaultValue;
                        configChanged = true;
                        return defaultValue;
                    }
                }
                else if (typeof(T) == typeof(ulong))
                {
                    if (value is long longVal)
                    {
                        return (T)(object)(ulong)longVal;
                    }
                    else if (value is ulong ulongVal)
                    {
                        return (T)(object)ulongVal;
                    }
                    else
                    {
                        return (T)Convert.ChangeType(value, typeof(T));
                    }
                }
                else
                {
                    return (T)Convert.ChangeType(value, typeof(T));
                }
            }
            catch (Exception ex)
            {
                PrintError($"Error converting config value for [{section}][{key}]: {ex.Message}. Using default value.");
                data[key] = defaultValue;
                configChanged = true;
                return defaultValue;
            }
        }

        protected override void LoadDefaultConfig()
        {
            Config[ConfigSections.Commands] = new Dictionary<string, object>
            {
                [ConfigKeys.BuyCurrencyCommandName] = "buyblood",
                [ConfigKeys.BuyVipCommandName] = "buyvip",
                [ConfigKeys.SendCurrencyCommandName] = "sendblood"
            };

            Config[ConfigSections.CurrencySettings] = new Dictionary<string, object>
            {
                [ConfigKeys.CurrencyItemID] = 1776460938,
                [ConfigKeys.CurrencyName] = "blood",
                [ConfigKeys.CurrencySkinID] = 0UL,
                [ConfigKeys.PricePerCurrencyUnit] = 1,
                [ConfigKeys.SatsPerCurrencyUnit] = 1,
                // NEW: Protection settings
                [ConfigKeys.MaxPurchaseAmount] = 10000,
                [ConfigKeys.MaxSendAmount] = 10000,
                [ConfigKeys.CommandCooldownSeconds] = 0,
                [ConfigKeys.MaxPendingInvoicesPerPlayer] = 1
            };

            Config[ConfigSections.Discord] = new Dictionary<string, object>
            {
                [ConfigKeys.DiscordChannelName] = "mart",
                [ConfigKeys.DiscordWebhookUrl] = "https://discord.com/api/webhooks/your_webhook_url"
            };

            Config[ConfigSections.InvoiceSettings] = new Dictionary<string, object>
            {
                [ConfigKeys.BlacklistedDomains] = new List<string> { "example.com", "blacklisted.net" },
                [ConfigKeys.WhitelistedDomains] = new List<string>(),
                [ConfigKeys.CheckIntervalSeconds] = 10,
                [ConfigKeys.InvoiceTimeoutSeconds] = 300,
                [ConfigKeys.LNbitsApiKey] = "your-lnbits-admin-api-key",
                [ConfigKeys.LNbitsBaseUrl] = "https://your-lnbits-instance.com",
                [ConfigKeys.MaxRetries] = 25,
                [ConfigKeys.UseWebSockets] = true,
                [ConfigKeys.WebSocketReconnectDelay] = 5
            };

            Config[ConfigSections.VIPSettings] = new Dictionary<string, object>
            {
                [ConfigKeys.VipCommand] = "oxide.usergroup add {steamid} vip",
                [ConfigKeys.VipPrice] = 1000
            };
        }

        private void Init()
        {
            // Register permissions
            permission.RegisterPermission("orangemart.buycurrency", this);
            permission.RegisterPermission("orangemart.sendcurrency", this);
            permission.RegisterPermission("orangemart.buyvip", this);
        }

        private void OnServerInitialized()
        {
            if (config == null)
            {
                PrintError("Plugin configuration is not properly set up. Please check your configuration file.");
                return;
            }

            // Register commands
            AddCovalenceCommand(buyCurrencyCommandName, nameof(CmdBuyCurrency), "orangemart.buycurrency");
            AddCovalenceCommand(sendCurrencyCommandName, nameof(CmdSendCurrency), "orangemart.sendcurrency");
            AddCovalenceCommand(buyVipCommandName, nameof(CmdBuyVip), "orangemart.buyvip");

            // Recover interrupted transactions
            RecoverInterruptedTransactions();

            // Start a timer to check pending invoices periodically (fallback for WebSocket failures)
            if (!useWebSockets || checkIntervalSeconds > 0)
            {
                timer.Every(checkIntervalSeconds, CheckPendingInvoices);
            }

            // Cleanup old cooldown entries every 5 minutes
            timer.Every(300f, CleanupOldCooldowns);

            Puts($"Orangemart initialized. WebSockets: {(useWebSockets ? "Enabled" : "Disabled")}");
        }

        private void Unload()
        {
            // Clean up all WebSocket connections
            CleanupAllWebSockets();
            
            pendingInvoices.Clear();
            retryCounts.Clear();
            lastCommandTime.Clear();
        }

        // NEW: Protection Methods

        // Rate limiting system
        private bool IsOnCooldown(IPlayer player, string commandType)
        {
            if (commandCooldownSeconds <= 0) return false; // Cooldown disabled
            
            string key = $"{GetPlayerId(player)}:{commandType}";
            
            if (lastCommandTime.TryGetValue(key, out DateTime lastTime))
            {
                double secondsSince = (DateTime.UtcNow - lastTime).TotalSeconds;
                if (secondsSince < commandCooldownSeconds)
                {
                    double remaining = commandCooldownSeconds - secondsSince;
                    player.Reply(Lang("CommandOnCooldown", player.Id, commandType, Math.Ceiling(remaining)));
                    return true;
                }
            }
            
            lastCommandTime[key] = DateTime.UtcNow;
            return false;
        }

        // Pending invoice limit check
        private bool HasTooManyPendingInvoices(IPlayer player)
        {
            // 0 = no limit
            if (maxPendingInvoicesPerPlayer == 0) return false;
            
            string playerId = GetPlayerId(player);
            int pendingCount = pendingInvoices.Count(inv => GetPlayerId(inv.Player) == playerId);
            
            if (pendingCount >= maxPendingInvoicesPerPlayer)
            {
                player.Reply(Lang("TooManyPendingInvoices", player.Id, pendingCount, maxPendingInvoicesPerPlayer));
                return true;
            }
            
            return false;
        }

        // Amount validation with overflow protection
        private bool ValidatePurchaseAmount(IPlayer player, int amount, out int safeSats)
        {
            safeSats = 0;
            
            // Basic validation
            if (amount <= 0)
            {
                player.Reply(Lang("InvalidAmount", player.Id));
                return false;
            }
            
            // Maximum amount check (0 = no limit)
            if (maxPurchaseAmount > 0 && amount > maxPurchaseAmount)
            {
                player.Reply(Lang("AmountTooLarge", player.Id, amount, maxPurchaseAmount, currencyName));
                return false;
            }
            
            // Integer overflow protection
            long amountSatsLong = (long)amount * pricePerCurrencyUnit;
            if (amountSatsLong > int.MaxValue)
            {
                player.Reply(Lang("AmountCausesOverflow", player.Id));
                return false;
            }
            
            safeSats = (int)amountSatsLong;
            return true;
        }

        // Send amount validation
        private bool ValidateSendAmount(IPlayer player, int amount, out int safeSats)
        {
            safeSats = 0;
            
            // Basic validation
            if (amount <= 0)
            {
                player.Reply(Lang("InvalidAmount", player.Id));
                return false;
            }
            
            // Maximum amount check (0 = no limit)
            if (maxSendAmount > 0 && amount > maxSendAmount)
            {
                player.Reply(Lang("SendAmountTooLarge", player.Id, amount, maxSendAmount, currencyName));
                return false;
            }
            
            // Integer overflow protection
            long amountSatsLong = (long)amount * satsPerCurrencyUnit;
            if (amountSatsLong > int.MaxValue)
            {
                player.Reply(Lang("AmountCausesOverflow", player.Id));
                return false;
            }
            
            safeSats = (int)amountSatsLong;
            return true;
        }

        // VIP price validation
        private bool ValidateVipPrice(IPlayer player, out int safeSats)
        {
            safeSats = 0;
            
            // Check if VIP price would cause overflow
            if (vipPrice > int.MaxValue)
            {
                player.Reply(Lang("VipPriceTooHigh", player.Id));
                PrintError($"VIP price {vipPrice} exceeds int.MaxValue");
                return false;
            }
            
            safeSats = vipPrice;
            return true;
        }

        private void CleanupOldCooldowns()
        {
            var expiredKeys = lastCommandTime
                .Where(kvp => (DateTime.UtcNow - kvp.Value).TotalSeconds > commandCooldownSeconds * 2)
                .Select(kvp => kvp.Key)
                .ToList();
            
            foreach (var key in expiredKeys)
            {
                lastCommandTime.Remove(key);
            }
            
            if (expiredKeys.Count > 0)
            {
                Puts($"Cleaned up {expiredKeys.Count} expired cooldown entries.");
            }
        }

        private void CleanupAllWebSockets()
        {
            lock (webSocketLock)
            {
                foreach (var kvp in activeWebSockets)
                {
                    try
                    {
                        kvp.Value.CancellationTokenSource?.Cancel();
                        kvp.Value.WebSocket?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        PrintError($"Error cleaning up WebSocket for {kvp.Key}: {ex.Message}");
                    }
                }
                activeWebSockets.Clear();
            }
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                // Existing messages
                ["UsageSendCurrency"] = "Usage: /{0} <amount> <lightning_address>",
                ["NeedMoreCurrency"] = "You need more {0}. You currently have {1}.",
                ["FailedToReserveCurrency"] = "Failed to reserve currency. Please try again.",
                ["FailedToQueryLightningAddress"] = "Failed to query Lightning address for an invoice.",
                ["FailedToAuthenticate"] = "Failed to authenticate with LNbits.",
                ["InvoiceCreatedCheckDiscord"] = "Invoice created! Please check the #{0} channel on Discord to complete your payment.",
                ["FailedToCreateInvoice"] = "Failed to create an invoice. Please try again later.",
                ["FailedToProcessPayment"] = "Failed to process payment. Please try again later.",
                ["CurrencySentSuccess"] = "You have successfully sent {0} {1}!",
                ["PurchaseSuccess"] = "You have successfully purchased {0} {1}!",
                ["PurchaseVipSuccess"] = "You have successfully purchased VIP status!",
                ["InvalidCommandUsage"] = "Usage: /{0} <amount>",
                ["NoPermission"] = "You do not have permission to use this command.",
                ["FailedToFindBasePlayer"] = "Failed to find base player object for player {0}.",
                ["FailedToCreateCurrencyItem"] = "Failed to create {0} item for player {1}.",
                ["AddedToVipGroup"] = "Player {0} added to VIP group '{1}'.",
                ["InvoiceExpired"] = "Your invoice for {0} sats has expired. Please try again.",
                ["BlacklistedDomain"] = "The domain '{0}' is currently blacklisted. Please use a different Lightning address.",
                ["NotWhitelistedDomain"] = "The domain '{0}' is not whitelisted. Please use a Lightning address from the following domains: {1}.",
                ["InvalidLightningAddress"] = "The Lightning Address provided is invalid or cannot be resolved.",
                ["PaymentProcessing"] = "Your payment is being processed. You will receive a confirmation once it's complete.",
                ["TransactionInitiated"] = "Transaction initiated. Processing your payment...",
                
                // NEW: Protection and validation messages
                ["InvalidAmount"] = "Invalid amount. Please enter a positive number.",
                ["AmountTooLarge"] = "Amount {0} exceeds maximum limit of {1} {2}. Please use a smaller amount.",
                ["SendAmountTooLarge"] = "Send amount {0} exceeds maximum limit of {1} {2}. Please use a smaller amount.",
                ["AmountCausesOverflow"] = "Amount too large and would cause calculation errors. Please use a smaller amount.",
                ["CommandOnCooldown"] = "Command '{0}' is on cooldown. Please wait {1} more seconds.",
                ["TooManyPendingInvoices"] = "You have {0} pending invoices (max: {1}). Please complete or wait for them to expire.",
                ["VipPriceTooHigh"] = "VIP price is configured too high. Please contact an administrator.",
                ["ProtectionLimits"] = "Orangemart Limits: Purchase max {0}, Send max {1}, Cooldown {2}s"
            }, this);
        }

        private string Lang(string key, string userId = null, params object[] args)
        {
            return string.Format(lang.GetMessage(key, this, userId), args);
        }

        // Helper method to generate unique transaction IDs
        private string GenerateTransactionId()
        {
            return $"{DateTime.UtcNow.Ticks}-{Guid.NewGuid().ToString("N").Substring(0, 8)}";
        }

        // WebSocket connection management
        private async Task ConnectWebSocket(PendingInvoice invoice)
        {
            if (!useWebSockets)
            {
                Puts($"WebSockets disabled, using HTTP polling for invoice {invoice.RHash}");
                return;
            }

            var wsConnection = new WebSocketConnection
            {
                WebSocket = new ClientWebSocket(),
                CancellationTokenSource = new CancellationTokenSource(),
                InvoiceKey = invoice.RHash,
                Invoice = invoice,
                ConnectedAt = DateTime.UtcNow,
                ReconnectAttempts = 0
            };

            wsConnection.WebSocket.Options.SetRequestHeader("X-Api-Key", config.ApiKey);

            lock (webSocketLock)
            {
                if (activeWebSockets.ContainsKey(invoice.RHash))
                {
                    var existing = activeWebSockets[invoice.RHash];
                    existing.CancellationTokenSource?.Cancel();
                    existing.WebSocket?.Dispose();
                }
                activeWebSockets[invoice.RHash] = wsConnection;
            }

            try
            {
                // Try the payment hash endpoint first
                var wsUrl = $"{config.WebSocketUrl}/api/v1/ws/{invoice.RHash}";
                Puts($"[WebSocket] Attempting to connect to: {wsUrl}");
                
                await wsConnection.WebSocket.ConnectAsync(new Uri(wsUrl), wsConnection.CancellationTokenSource.Token);
                
                Puts($"WebSocket connected for invoice {invoice.RHash}");
                wsConnection.ListenTask = Task.Run(async () => await ListenToWebSocket(wsConnection), wsConnection.CancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                PrintError($"Failed to connect WebSocket for invoice {invoice.RHash}: {ex.Message}");
                
                lock (webSocketLock)
                {
                    activeWebSockets.Remove(invoice.RHash);
                }
                
                // Enable HTTP polling as fallback immediately
                Puts($"[WebSocket] Falling back to HTTP polling for {invoice.RHash}");
                
                if (wsConnection.ReconnectAttempts < 3)
                {
                    timer.Once(webSocketReconnectDelay, () =>
                    {
                        if (pendingInvoices.Contains(invoice))
                        {
                            wsConnection.ReconnectAttempts++;
                            Task.Run(async () => await ConnectWebSocket(invoice));
                        }
                    });
                }
            }
        }

        private async Task ListenToWebSocket(WebSocketConnection connection)
        {
            var buffer = new ArraySegment<byte>(new byte[4096]);
            var messageBuilder = new StringBuilder();

            try
            {
                while (connection.WebSocket.State == WebSocketState.Open && !connection.CancellationTokenSource.Token.IsCancellationRequested)
                {
                    WebSocketReceiveResult result;
                    messageBuilder.Clear();

                    do
                    {
                        result = await connection.WebSocket.ReceiveAsync(buffer, connection.CancellationTokenSource.Token);
                        
                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            messageBuilder.Append(Encoding.UTF8.GetString(buffer.Array, 0, result.Count));
                        }
                        else if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await connection.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                            break;
                        }
                    }
                    while (!result.EndOfMessage);

                    if (messageBuilder.Length > 0)
                    {
                        var message = messageBuilder.ToString();
                        ProcessWebSocketMessage(connection, message);
                    }
                }
            }
            catch (WebSocketException wsEx)
            {
                PrintError($"WebSocket error for invoice {connection.InvoiceKey}: {wsEx.Message}");
            }
            catch (TaskCanceledException)
            {
                // Expected when cancellation is requested
            }
            catch (Exception ex)
            {
                PrintError($"Unexpected error in WebSocket listener for invoice {connection.InvoiceKey}: {ex.Message}");
            }
            finally
            {
                // Clean up
                lock (webSocketLock)
                {
                    if (activeWebSockets.ContainsKey(connection.InvoiceKey))
                    {
                        activeWebSockets.Remove(connection.InvoiceKey);
                    }
                }

                if (connection.WebSocket?.State == WebSocketState.Open)
                {
                    try
                    {
                        await connection.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    }
                    catch { }
                }

                connection.WebSocket?.Dispose();
            }
        }

        private void ProcessWebSocketMessage(WebSocketConnection connection, string message)
        {
            try
            {
                // Log all WebSocket messages for debugging
                Puts($"[WebSocket] Raw message for {connection.InvoiceKey}: {message}");
                
                // Try to parse as the simple format first: {"pending": false, "status": "success"}
                try
                {
                    var simpleUpdate = JsonConvert.DeserializeObject<Dictionary<string, object>>(message);
                    
                    if (simpleUpdate != null && simpleUpdate.ContainsKey("pending") && simpleUpdate.ContainsKey("status"))
                    {
                        bool isPending = Convert.ToBoolean(simpleUpdate["pending"]);
                        string status = simpleUpdate["status"]?.ToString();
                        
                        Puts($"[WebSocket] Simple format - Pending: {isPending}, Status: {status}");
                        
                        if (!isPending && status == "success")
                        {
                            Puts($"[WebSocket] Payment confirmed via simple format for {connection.InvoiceKey}");
                            ProcessPaymentConfirmation(connection.Invoice);
                            connection.CancellationTokenSource?.Cancel();
                            return;
                        }
                    }
                }
                catch (Exception)
                {
                    // Fall through to try the complex format
                }
                
                // Try to parse as the complex format: {"payment": {...}}
                try
                {
                    var update = JsonConvert.DeserializeObject<WebSocketPaymentUpdate>(message);
                    
                    if (update?.Payment != null)
                    {
                        Puts($"[WebSocket] Complex format - Hash: {update.Payment.PaymentHash}, Pending: {update.Payment.Pending}, Preimage: {update.Payment.Preimage}");
                        
                        // Check if payment is confirmed (not pending)
                        if (!update.Payment.Pending && !string.IsNullOrEmpty(update.Payment.Preimage))
                        {
                            Puts($"[WebSocket] Payment confirmed via complex format (preimage) for {connection.InvoiceKey}");
                            ProcessPaymentConfirmation(connection.Invoice);
                            connection.CancellationTokenSource?.Cancel();
                            return;
                        }
                        // Alternative check: if payment hash matches and not pending
                        else if (!update.Payment.Pending && update.Payment.PaymentHash?.ToLower() == connection.InvoiceKey.ToLower())
                        {
                            Puts($"[WebSocket] Payment confirmed via complex format (hash match) for {connection.InvoiceKey}");
                            ProcessPaymentConfirmation(connection.Invoice);
                            connection.CancellationTokenSource?.Cancel();
                            return;
                        }
                    }
                }
                catch (Exception)
                {
                    // Neither format worked
                }
                
                Puts($"[WebSocket] Message did not indicate payment completion for {connection.InvoiceKey}");
            }
            catch (Exception ex)
            {
                PrintError($"Error processing WebSocket message for invoice {connection.InvoiceKey}: {ex.Message}");
                Puts($"[WebSocket] Problematic message: {message}");
            }
        }

        private void ProcessPaymentConfirmation(PendingInvoice invoice)
        {
            // Check if this invoice was already processed to prevent duplicates
            if (!pendingInvoices.Contains(invoice))
            {
                Puts($"[ProcessPayment] Invoice {invoice.RHash} already processed, skipping");
                return;
            }

            // Remove from pending list immediately to prevent duplicate processing
            pendingInvoices.Remove(invoice);
            
            Puts($"[ProcessPayment] Processing payment confirmation for {invoice.RHash}, Type: {invoice.Type}");

            // Process based on type
            switch (invoice.Type)
            {
                case PurchaseType.Currency:
                    RewardPlayer(invoice.Player, invoice.Amount);
                    UpdateBuyTransactionStatus(invoice.TransactionId, TransactionStatus.COMPLETED, true);
                    break;
                case PurchaseType.Vip:
                    GrantVip(invoice.Player);
                    UpdateBuyTransactionStatus(invoice.TransactionId, TransactionStatus.COMPLETED, true);
                    break;
                case PurchaseType.SendBitcoin:
                    invoice.Player.Reply(Lang("CurrencySentSuccess", invoice.Player.Id, invoice.Amount / satsPerCurrencyUnit, currencyName));
                    UpdateSellTransactionStatus(invoice.TransactionId, TransactionStatus.COMPLETED, true);
                    break;
            }

            // Clean up
            retryCounts.Remove(invoice.RHash);
            
            // Close WebSocket
            lock (webSocketLock)
            {
                if (activeWebSockets.ContainsKey(invoice.RHash))
                {
                    var ws = activeWebSockets[invoice.RHash];
                    ws.CancellationTokenSource?.Cancel();
                    activeWebSockets.Remove(invoice.RHash);
                }
            }

            Puts($"Payment confirmed for invoice {invoice.RHash}, TransactionId: {invoice.TransactionId}");
        }

        // Recovery logic for interrupted transactions
        private void RecoverInterruptedTransactions()
        {
            Puts("Checking for interrupted transactions...");

            // Recover sell transactions
            var sellLogs = LoadSellLogData();
            var interruptedSells = sellLogs.Where(l => 
                l.Status == TransactionStatus.INITIATED || 
                l.Status == TransactionStatus.PROCESSING).ToList();

            foreach (var log in interruptedSells)
            {
                Puts($"Found interrupted sell transaction: {log.TransactionId} for player {log.SteamID}");
                
                // Mark as failed and refund if payment hash exists
                if (!string.IsNullOrEmpty(log.PaymentHash))
                {
                    // Check if payment was actually completed
                    CheckInvoicePaid(log.PaymentHash, isPaid =>
                    {
                        if (isPaid)
                        {
                            // Payment was completed, update status
                            UpdateSellTransactionStatus(log.TransactionId, TransactionStatus.COMPLETED, true);
                            Puts($"Recovered completed sell transaction: {log.TransactionId}");
                        }
                        else
                        {
                            // Payment failed, mark as failed
                            UpdateSellTransactionStatus(log.TransactionId, TransactionStatus.FAILED, false, "Server interrupted");
                            Puts($"Marked interrupted sell transaction as failed: {log.TransactionId}");
                        }
                    });
                }
                else
                {
                    // No payment hash, mark as failed
                    UpdateSellTransactionStatus(log.TransactionId, TransactionStatus.FAILED, false, "Server interrupted before payment initiation");
                }
            }

            // Recover buy transactions
            var buyLogs = LoadBuyLogData();
            var interruptedBuys = buyLogs.Where(l => 
                l.Status == TransactionStatus.INITIATED || 
                l.Status == TransactionStatus.PROCESSING).ToList();

            foreach (var log in interruptedBuys)
            {
                Puts($"Found interrupted buy transaction: {log.TransactionId} for player {log.SteamID}");
                
                // Check if invoice was paid
                if (!string.IsNullOrEmpty(log.InvoiceID))
                {
                    CheckInvoicePaid(log.InvoiceID, isPaid =>
                    {
                        if (isPaid)
                        {
                            // Payment was completed, update status
                            UpdateBuyTransactionStatus(log.TransactionId, TransactionStatus.COMPLETED, true);
                            Puts($"Recovered completed buy transaction: {log.TransactionId}");
                        }
                        else
                        {
                            // Payment failed, mark as expired
                            UpdateBuyTransactionStatus(log.TransactionId, TransactionStatus.EXPIRED, false);
                            Puts($"Marked interrupted buy transaction as expired: {log.TransactionId}");
                        }
                    });
                }
            }
        }

        // PROTECTED COMMAND METHODS

        // Protected CmdBuyCurrency method
        private void CmdBuyCurrency(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission("orangemart.buycurrency"))
            {
                player.Reply(Lang("NoPermission", player.Id));
                return;
            }

            // Rate limiting check
            if (IsOnCooldown(player, "buy")) return;

            // Pending invoice limit check
            if (HasTooManyPendingInvoices(player)) return;

            if (args.Length != 1 || !int.TryParse(args[0], out int amount))
            {
                player.Reply(Lang("InvalidCommandUsage", player.Id, buyCurrencyCommandName));
                return;
            }

            // Amount validation with overflow protection
            if (!ValidatePurchaseAmount(player, amount, out int amountSats)) return;

            string transactionId = GenerateTransactionId();

            // Log transaction initiation
            var initialLogEntry = new BuyInvoiceLogEntry
            {
                TransactionId = transactionId,
                SteamID = GetPlayerId(player),
                InvoiceID = null,
                Status = TransactionStatus.INITIATED,
                IsPaid = false,
                Timestamp = DateTime.UtcNow,
                CompletedTimestamp = null,
                Amount = amountSats,
                CurrencyGiven = false,
                VipGranted = false,
                RetryCount = 0,
                PurchaseType = "Currency"
            };
            LogBuyInvoice(initialLogEntry);

            CreateInvoice(amountSats, $"Buying {amount} {currencyName}", invoiceResponse =>
            {
                if (invoiceResponse != null)
                {
                    // Update log entry with invoice ID
                    UpdateBuyTransactionInvoiceId(transactionId, invoiceResponse.PaymentHash);

                    SendInvoiceToDiscord(player, invoiceResponse.PaymentRequest, amountSats, $"Buying {amount} {currencyName}");

                    player.Reply(Lang("InvoiceCreatedCheckDiscord", player.Id, discordChannelName));

                    var pendingInvoice = new PendingInvoice
                    {
                        TransactionId = transactionId,
                        RHash = invoiceResponse.PaymentHash.ToLower(),
                        Player = player,
                        Amount = amount,
                        Memo = $"Buying {amount} {currencyName}",
                        CreatedAt = DateTime.UtcNow,
                        Type = PurchaseType.Currency
                    };
                    pendingInvoices.Add(pendingInvoice);

                    // Connect WebSocket for monitoring
                    Task.Run(async () => await ConnectWebSocket(pendingInvoice));

                    ScheduleInvoiceExpiry(pendingInvoice);
                }
                else
                {
                    player.Reply(Lang("FailedToCreateInvoice", player.Id));
                    
                    // Update transaction as failed
                    UpdateBuyTransactionStatus(transactionId, TransactionStatus.FAILED, false);
                }
            });
        }

        // Protected CmdSendCurrency method
        private void CmdSendCurrency(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission("orangemart.sendcurrency"))
            {
                player.Reply(Lang("NoPermission", player.Id));
                return;
            }

            // Rate limiting check
            if (IsOnCooldown(player, "send")) return;

            // Pending invoice limit check
            if (HasTooManyPendingInvoices(player)) return;

            if (args.Length != 2 || !int.TryParse(args[0], out int amount))
            {
                player.Reply(Lang("UsageSendCurrency", player.Id, sendCurrencyCommandName));
                return;
            }

            // Amount validation with overflow protection
            if (!ValidateSendAmount(player, amount, out int satsAmount)) return;

            string lightningAddress = args[1];

            if (!IsLightningAddressAllowed(lightningAddress))
            {
                string domain = GetDomainFromLightningAddress(lightningAddress);
                if (whitelistedDomains.Any())
                {
                    string whitelist = string.Join(", ", whitelistedDomains);
                    player.Reply(Lang("NotWhitelistedDomain", player.Id, domain, whitelist));
                }
                else
                {
                    player.Reply(Lang("BlacklistedDomain", player.Id, domain));
                }
                return;
            }

            var basePlayer = player.Object as BasePlayer;
            if (basePlayer == null)
            {
                player.Reply(Lang("FailedToFindBasePlayer", player.Id));
                return;
            }

            int currencyAmount = GetAllInventoryItems(basePlayer).Where(IsCurrencyItem).Sum(item => item.amount);

            if (currencyAmount < amount)
            {
                player.Reply(Lang("NeedMoreCurrency", player.Id, currencyName, currencyAmount));
                return;
            }

            if (!TryReserveCurrency(basePlayer, amount))
            {
                player.Reply(Lang("FailedToReserveCurrency", player.Id));
                return;
            }

            // Generate transaction ID and log initiation immediately
            string transactionId = GenerateTransactionId();

            // Log transaction initiation
            var initialLogEntry = new SellInvoiceLogEntry
            {
                TransactionId = transactionId,
                SteamID = GetPlayerId(player),
                LightningAddress = lightningAddress,
                Status = TransactionStatus.INITIATED,
                Success = false,
                SatsAmount = satsAmount,
                PaymentHash = null,
                CurrencyReturned = false,
                Timestamp = DateTime.UtcNow,
                CompletedTimestamp = null,
                RetryCount = 0,
                FailureReason = null
            };
            LogSellTransaction(initialLogEntry);

            player.Reply(Lang("TransactionInitiated", player.Id));

            SendBitcoin(lightningAddress, satsAmount, (success, paymentHash) =>
            {
                if (success && !string.IsNullOrEmpty(paymentHash))
                {
                    // Update log entry with payment hash
                    UpdateSellTransactionPaymentHash(transactionId, paymentHash);

                    var pendingInvoice = new PendingInvoice
                    {
                        TransactionId = transactionId,
                        RHash = paymentHash.ToLower(),
                        Player = player,
                        Amount = satsAmount,
                        Memo = $"Sending {amount} {currencyName} to {lightningAddress}",
                        CreatedAt = DateTime.UtcNow,
                        Type = PurchaseType.SendBitcoin
                    };
                    pendingInvoices.Add(pendingInvoice);

                    // Connect WebSocket for monitoring
                    Task.Run(async () => await ConnectWebSocket(pendingInvoice));

                    Puts($"Outbound payment to {lightningAddress} initiated. PaymentHash: {paymentHash}, TransactionId: {transactionId}");
                }
                else
                {
                    player.Reply(Lang("FailedToProcessPayment", player.Id));

                    // Update transaction as failed
                    UpdateSellTransactionStatus(transactionId, TransactionStatus.FAILED, false, "Failed to initiate payment", true);

                    Puts($"Outbound payment to {lightningAddress} failed to initiate. TransactionId: {transactionId}");

                    ReturnCurrency(basePlayer, amount);
                    Puts($"Returned {amount} {currencyName} to player {basePlayer.UserIDString} due to failed payment.");
                }
            });
        }

        // Protected CmdBuyVip method
        private void CmdBuyVip(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission("orangemart.buyvip"))
            {
                player.Reply(Lang("NoPermission", player.Id));
                return;
            }

            // Rate limiting check
            if (IsOnCooldown(player, "vip")) return;

            // Pending invoice limit check
            if (HasTooManyPendingInvoices(player)) return;

            // VIP price validation
            if (!ValidateVipPrice(player, out int amountSats)) return;

            string transactionId = GenerateTransactionId();

            // Log transaction initiation
            var initialLogEntry = new BuyInvoiceLogEntry
            {
                TransactionId = transactionId,
                SteamID = GetPlayerId(player),
                InvoiceID = null,
                Status = TransactionStatus.INITIATED,
                IsPaid = false,
                Timestamp = DateTime.UtcNow,
                CompletedTimestamp = null,
                Amount = amountSats,
                CurrencyGiven = false,
                VipGranted = false,
                RetryCount = 0,
                PurchaseType = "VIP"
            };
            LogBuyInvoice(initialLogEntry);

            CreateInvoice(amountSats, "Buying VIP Status", invoiceResponse =>
            {
                if (invoiceResponse != null)
                {
                    // Update log entry with invoice ID
                    UpdateBuyTransactionInvoiceId(transactionId, invoiceResponse.PaymentHash);

                    SendInvoiceToDiscord(player, invoiceResponse.PaymentRequest, amountSats, "Buying VIP Status");

                    player.Reply(Lang("InvoiceCreatedCheckDiscord", player.Id, discordChannelName));

                    var pendingInvoice = new PendingInvoice
                    {
                        TransactionId = transactionId,
                        RHash = invoiceResponse.PaymentHash.ToLower(),
                        Player = player,
                        Amount = amountSats,
                        Memo = "Buying VIP Status",
                        CreatedAt = DateTime.UtcNow,
                        Type = PurchaseType.Vip
                    };
                    pendingInvoices.Add(pendingInvoice);

                    // Connect WebSocket for monitoring
                    Task.Run(async () => await ConnectWebSocket(pendingInvoice));

                    ScheduleInvoiceExpiry(pendingInvoice);
                }
                else
                {
                    player.Reply(Lang("FailedToCreateInvoice", player.Id));
                    
                    // Update transaction as failed
                    UpdateBuyTransactionStatus(transactionId, TransactionStatus.FAILED, false);
                }
            });
        }

        // TEMPORARILY COMMENTED OUT - ADMIN COMMANDS
        // Uncomment these once the core plugin is working
        /*
        [ConsoleCommand("orangemart.limits")]
        private void CmdShowLimits(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.connection?.player as BasePlayer;
            if (player != null && !player.IsAdmin) return;
                
            arg.ReplyWith("=== Orangemart Protection Limits ===");
            arg.ReplyWith($"Max Purchase Amount: {maxPurchaseAmount} {currencyName}");
            arg.ReplyWith($"Max Send Amount: {maxSendAmount} {currencyName}");
            arg.ReplyWith($"Command Cooldown: {commandCooldownSeconds} seconds");
            arg.ReplyWith($"Max Pending Invoices: {maxPendingInvoicesPerPlayer} per player");
            arg.ReplyWith($"Active Pending Invoices: {pendingInvoices.Count}");
            arg.ReplyWith($"Players on Cooldown: {lastCommandTime.Count(kvp => (DateTime.UtcNow - kvp.Value).TotalSeconds < commandCooldownSeconds)}");
        }

        [ConsoleCommand("orangemart.clearcooldowns")]
        private void CmdClearCooldowns(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.connection?.player as BasePlayer;
            if (player != null && !player.IsAdmin) return;
                
            int cleared = lastCommandTime.Count;
            lastCommandTime.Clear();
            arg.ReplyWith($"Cleared {cleared} command cooldowns.");
            Puts($"Admin {player?.displayName ?? "Console"} cleared all command cooldowns.");
        }

        [ConsoleCommand("orangemart.clearcooldown")]
        private void CmdClearPlayerCooldown(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.connection?.player as BasePlayer;
            if (player != null && !player.IsAdmin) return;
                
            if (arg.Args == null || arg.Args.Length == 0)
            {
                arg.ReplyWith("Usage: orangemart.clearcooldown <steamid>");
                return;
            }
            
            string steamId = arg.Args[0];
            int cleared = 0;
            
            var keysToRemove = lastCommandTime.Keys.Where(k => k.StartsWith(steamId + ":")).ToList();
            foreach (var key in keysToRemove)
            {
                lastCommandTime.Remove(key);
                cleared++;
            }
            
            arg.ReplyWith($"Cleared {cleared} cooldowns for player {steamId}.");
            Puts($"Admin {player?.displayName ?? "Console"} cleared cooldowns for player {steamId}.");
        }

        [ConsoleCommand("orangemart.playerstats")]
        private void CmdPlayerStats(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.connection?.player as BasePlayer;
            if (player != null && !player.IsAdmin) return;
                
            if (arg.Args == null || arg.Args.Length == 0)
            {
                arg.ReplyWith("Usage: orangemart.playerstats <steamid>");
                return;
            }
            
            string steamId = arg.Args[0];
            int pendingCount = pendingInvoices.Count(inv => GetPlayerId(inv.Player) == steamId);
            
            bool onCooldown = false;
            DateTime lastTime = DateTime.MinValue;
            foreach (var kvp in lastCommandTime)
            {
                if (kvp.Key.StartsWith(steamId + ":"))
                {
                    if (kvp.Value > lastTime)
                    {
                        lastTime = kvp.Value;
                        onCooldown = (DateTime.UtcNow - kvp.Value).TotalSeconds < commandCooldownSeconds;
                    }
                }
            }
            
            arg.ReplyWith($"Player {steamId} stats:");
            arg.ReplyWith($"- Pending invoices: {pendingCount}/{maxPendingInvoicesPerPlayer}");
            arg.ReplyWith($"- On cooldown: {onCooldown}");
            arg.ReplyWith($"- Last command: {(lastTime == DateTime.MinValue ? "Never" : lastTime.ToString())}");
        }
        */

        [ChatCommand("orangelimits")]
        private void CmdPlayerLimits(BasePlayer player, string command, string[] args)
        {
            var covalencePlayer = players.FindPlayerById(player.UserIDString);
            if (covalencePlayer == null ||
                (!covalencePlayer.HasPermission("orangemart.buycurrency") && 
                !covalencePlayer.HasPermission("orangemart.sendcurrency") && 
                !covalencePlayer.HasPermission("orangemart.buyvip")))
            {
                player.ChatMessage("You do not have permission to use Orangemart commands.");
                return;
            }
            
            player.ChatMessage(Lang("ProtectionLimits", player.UserIDString, maxPurchaseAmount, maxSendAmount, commandCooldownSeconds));
            
            // Show player's current status
            string playerId = player.UserIDString;
            int pendingCount = pendingInvoices.Count(inv => GetPlayerId(inv.Player) == playerId);
            
            bool onCooldown = false;
            string cooldownCommands = "";
            foreach (var kvp in lastCommandTime)
            {
                if (kvp.Key.StartsWith(playerId + ":"))
                {
                    double remaining = commandCooldownSeconds - (DateTime.UtcNow - kvp.Value).TotalSeconds;
                    if (remaining > 0)
                    {
                        onCooldown = true;
                        string cmd = kvp.Key.Split(':')[1];
                        cooldownCommands += $"{cmd}({Math.Ceiling(remaining)}s) ";
                    }
                }
            }
            
            player.ChatMessage($"Your status: {pendingCount}/{maxPendingInvoicesPerPlayer} pending invoices");
            if (onCooldown)
            {
                player.ChatMessage($"Cooldowns: {cooldownCommands.Trim()}");
            }
        }

        // HELPER METHODS

        private List<Item> GetAllInventoryItems(BasePlayer player)
        {
            List<Item> allItems = new List<Item>();

            // Main Inventory
            if (player.inventory.containerMain != null)
                allItems.AddRange(player.inventory.containerMain.itemList);

            // Belt (Hotbar)
            if (player.inventory.containerBelt != null)
                allItems.AddRange(player.inventory.containerBelt.itemList);

            // Wear (Clothing)
            if (player.inventory.containerWear != null)
                allItems.AddRange(player.inventory.containerWear.itemList);

            return allItems;
        }

        private bool IsCurrencyItem(Item item)
        {
            return item.info.itemid == currencyItemID && (currencySkinID == 0 || item.skin == currencySkinID);
        }

        private bool TryReserveCurrency(BasePlayer player, int amount)
        {
            var items = GetAllInventoryItems(player).Where(IsCurrencyItem).ToList();
            int totalCurrency = items.Sum(item => item.amount);

            if (totalCurrency < amount)
            {
                return false;
            }

            int remaining = amount;

            foreach (var item in items)
            {
                if (item.amount > remaining)
                {
                    item.UseItem(remaining);
                    break;
                }
                else
                {
                    remaining -= item.amount;
                    item.Remove();
                }

                if (remaining <= 0)
                {
                    break;
                }
            }

            return true;
        }

        // Fallback HTTP polling for when WebSockets are disabled or fail
        private void CheckPendingInvoices()
        {
            foreach (var invoice in pendingInvoices.ToList())
            {
                string localPaymentHash = invoice.RHash;
                
                // Always check via HTTP as a fallback, but log differently for WebSocket vs HTTP-only
                bool hasActiveWebSocket = false;
                lock (webSocketLock)
                {
                    hasActiveWebSocket = useWebSockets && activeWebSockets.ContainsKey(invoice.RHash);
                }
                
                string checkType = hasActiveWebSocket ? "HTTP Fallback" : "HTTP Polling";
                Puts($"[{checkType}] Checking payment status for {localPaymentHash}");
                
                CheckInvoicePaid(localPaymentHash, isPaid =>
                {
                    if (isPaid)
                    {
                        Puts($"[{checkType}] Payment confirmed for {localPaymentHash}");
                        ProcessPaymentConfirmation(invoice);
                    }
                    else
                    {
                        if (!retryCounts.ContainsKey(localPaymentHash))
                        {
                            retryCounts[localPaymentHash] = 0;
                            Puts($"Initialized retry count for paymentHash: {localPaymentHash}");
                        }

                        retryCounts[localPaymentHash]++;
                        
                        if (retryCounts[localPaymentHash] == 1)
                        {
                            if (invoice.Type == PurchaseType.SendBitcoin)
                            {
                                UpdateSellTransactionStatus(invoice.TransactionId, TransactionStatus.PROCESSING, false);
                            }
                            else
                            {
                                UpdateBuyTransactionStatus(invoice.TransactionId, TransactionStatus.PROCESSING, false);
                            }
                        }

                        Puts($"[{checkType}] retry count for paymentHash {localPaymentHash}: {retryCounts[localPaymentHash]} of {maxRetries}");

                        if (retryCounts[localPaymentHash] >= maxRetries)
                        {
                            pendingInvoices.Remove(invoice);
                            int finalRetryCount = retryCounts[localPaymentHash];
                            retryCounts.Remove(localPaymentHash);
                            PrintWarning($"Invoice for player {GetPlayerId(invoice.Player)} expired (amount: {invoice.Amount} sats).");

                            invoice.Player.Reply(Lang("InvoiceExpired", invoice.Player.Id, invoice.Amount));

                            if (invoice.Type == PurchaseType.SendBitcoin)
                            {
                                var basePlayer = invoice.Player.Object as BasePlayer;
                                if (basePlayer != null)
                                {
                                    ReturnCurrency(basePlayer, invoice.Amount / satsPerCurrencyUnit);
                                    Puts($"Refunded {invoice.Amount / satsPerCurrencyUnit} {currencyName} to player {basePlayer.UserIDString} due to failed payment.");
                                }
                                else
                                {
                                    PrintError($"Failed to find base player object for player {invoice.Player.Id} to refund currency.");
                                }

                                UpdateSellTransactionStatus(invoice.TransactionId, TransactionStatus.EXPIRED, false, "Payment timeout", true);
                            }
                            else
                            {
                                UpdateBuyTransactionStatus(invoice.TransactionId, TransactionStatus.EXPIRED, false);
                            }
                        }
                    }
                });
            }
        }

        private void CheckInvoicePaid(string paymentHash, Action<bool> callback)
        {
            string normalizedPaymentHash = paymentHash.ToLower();
            string url = $"{config.BaseUrl}/api/v1/payments/{normalizedPaymentHash}";

            var headers = new Dictionary<string, string>
            {
                { "Content-Type", "application/json" },
                { "X-Api-Key", config.ApiKey }
            };

            MakeWebRequest(url, null, (code, response) =>
            {
                if (code != 200 || string.IsNullOrEmpty(response))
                {
                    PrintError($"Error checking invoice status: HTTP {code}");
                    callback(false);
                    return;
                }

                try
                {
                    var paymentStatus = JsonConvert.DeserializeObject<PaymentStatusResponse>(response);
                    callback(paymentStatus != null && paymentStatus.Paid);
                }
                catch (Exception ex)
                {
                    PrintError($"Failed to parse invoice status response: {ex.Message}");
                    callback(false);
                }
            }, RequestMethod.GET, headers);
        }

        private bool IsLightningAddressAllowed(string lightningAddress)
        {
            string domain = GetDomainFromLightningAddress(lightningAddress);
            if (string.IsNullOrEmpty(domain))
                return false;

            if (whitelistedDomains.Any())
            {
                return whitelistedDomains.Contains(domain.ToLower());
            }
            else
            {
                return !blacklistedDomains.Contains(domain.ToLower());
            }
        }

        private string GetDomainFromLightningAddress(string lightningAddress)
        {
            if (string.IsNullOrEmpty(lightningAddress))
                return null;

            var parts = lightningAddress.Split('@');
            return parts.Length == 2 ? parts[1].ToLower() : null;
        }

        private void SendBitcoin(string lightningAddress, int satsAmount, Action<bool, string> callback)
        {
            ResolveLightningAddress(lightningAddress, satsAmount, bolt11 =>
            {
                if (string.IsNullOrEmpty(bolt11))
                {
                    PrintError($"Failed to resolve Lightning Address: {lightningAddress}");
                    callback(false, null);
                    return;
                }

                SendPayment(bolt11, satsAmount, (success, paymentHash) =>
                {
                    if (success && !string.IsNullOrEmpty(paymentHash))
                    {
                        // Return success immediately - payment status will be tracked by WebSocket/polling
                        callback(true, paymentHash);
                    }
                    else
                    {
                        callback(false, null);
                    }
                });
            });
        }

        private void ScheduleInvoiceExpiry(PendingInvoice pendingInvoice)
        {
            timer.Once(invoiceTimeoutSeconds, () =>
            {
                if (pendingInvoices.Contains(pendingInvoice))
                {
                    pendingInvoices.Remove(pendingInvoice);
                    PrintWarning($"Invoice for player {GetPlayerId(pendingInvoice.Player)} expired (amount: {pendingInvoice.Amount} sats).");

                    // Clean up WebSocket if exists
                    lock (webSocketLock)
                    {
                        if (activeWebSockets.ContainsKey(pendingInvoice.RHash))
                        {
                            var ws = activeWebSockets[pendingInvoice.RHash];
                            ws.CancellationTokenSource?.Cancel();
                            activeWebSockets.Remove(pendingInvoice.RHash);
                        }
                    }

                    int finalRetryCount = retryCounts.ContainsKey(pendingInvoice.RHash) ? retryCounts[pendingInvoice.RHash] : 0;

                    if (pendingInvoice.Type == PurchaseType.SendBitcoin)
                    {
                        var basePlayer = pendingInvoice.Player.Object as BasePlayer;
                        if (basePlayer != null)
                        {
                            ReturnCurrency(basePlayer, pendingInvoice.Amount / satsPerCurrencyUnit);
                            Puts($"Refunded {pendingInvoice.Amount / satsPerCurrencyUnit} {currencyName} to player {basePlayer.UserIDString} due to failed payment.");
                        }
                        else
                        {
                            PrintError($"Failed to find base player object for player {pendingInvoice.Player.Id} to refund currency.");
                        }

                        UpdateSellTransactionStatus(pendingInvoice.TransactionId, TransactionStatus.EXPIRED, false, "Invoice timeout", true);
                    }
                    else
                    {
                        UpdateBuyTransactionStatus(pendingInvoice.TransactionId, TransactionStatus.EXPIRED, false);
                    }
                }
            });
        }

        // SendPayment now properly handles the wrapper class
        private void SendPayment(string bolt11, int satsAmount, Action<bool, string> callback)
        {
            // For outbound payments, LNbits expects only "out" and "bolt11"
            string url = $"{config.BaseUrl}/api/v1/payments";
            var requestBody = new
            {
                @out = true,
                bolt11 = bolt11
            };
            string jsonBody = JsonConvert.SerializeObject(requestBody);

            var headers = new Dictionary<string, string>
            {
                { "X-Api-Key", config.ApiKey },
                { "Content-Type", "application/json" }
            };

            MakeWebRequest(url, jsonBody, (code, response) =>
            {
                if (code != 200 && code != 201)
                {
                    PrintError($"Error processing payment: HTTP {code}");
                    callback(false, null);
                    return;
                }

                try
                {
                    InvoiceResponse invoiceResponse = null;
                    // First, attempt to deserialize using the wrapper (if present)
                    try
                    {
                        var wrapper = JsonConvert.DeserializeObject<InvoiceResponseWrapper>(response);
                        invoiceResponse = wrapper?.Data;
                    }
                    catch { }

                    // Fallback: try direct deserialization
                    if (invoiceResponse == null)
                    {
                        invoiceResponse = JsonConvert.DeserializeObject<InvoiceResponse>(response);
                    }

                    string paymentHash = invoiceResponse != null ? invoiceResponse.PaymentHash : null;

                    if (!string.IsNullOrEmpty(paymentHash))
                    {
                        callback(true, paymentHash);
                    }
                    else
                    {
                        PrintError("Payment hash (rhash) is missing or invalid in the response.");
                        PrintWarning($"[SendPayment] Raw response: {response}");
                        callback(false, null);
                    }
                }
                catch (Exception ex)
                {
                    PrintError($"Exception occurred while parsing payment response: {ex.Message}");
                    callback(false, null);
                }
            }, RequestMethod.POST, headers);
        }

        // CreateInvoice now properly handles the wrapper class
        private void CreateInvoice(int amountSats, string memo, Action<InvoiceResponse> callback)
        {
            string url = $"{config.BaseUrl}/api/v1/payments";

            var requestBody = new
            {
                @out = false,
                amount = amountSats,
                memo = memo
            };
            string jsonBody = JsonConvert.SerializeObject(requestBody);

            var headers = new Dictionary<string, string>
            {
                { "X-Api-Key", config.ApiKey },
                { "Content-Type", "application/json" }
            };

            MakeWebRequest(url, jsonBody, (code, response) =>
            {
                if (code != 200 && code != 201)
                {
                    PrintError($"Error creating invoice: HTTP {code}");
                    callback(null);
                    return;
                }

                if (string.IsNullOrEmpty(response))
                {
                    PrintError("Empty response received when creating invoice.");
                    callback(null);
                    return;
                }

                // Log the raw response for debugging purposes.
                PrintWarning($"[CreateInvoice] Raw response: {response}");

                try
                {
                    var invoiceResponse = JsonConvert.DeserializeObject<InvoiceResponse>(response);
                    callback(invoiceResponse != null && !string.IsNullOrEmpty(invoiceResponse.PaymentHash) ? invoiceResponse : null);
                }
                catch (Exception ex)
                {
                    PrintError($"Failed to deserialize invoice response: {ex.Message}");
                    callback(null);
                }
            }, RequestMethod.POST, headers);
        }

        private string GetPlayerId(IPlayer player)
        {
            var basePlayer = player.Object as BasePlayer;
            return basePlayer != null ? basePlayer.UserIDString : player.Id;
        }

        private void MakeWebRequest(string url, string jsonData, Action<int, string> callback, RequestMethod method = RequestMethod.GET, Dictionary<string, string> headers = null)
        {
            webrequest.Enqueue(url, jsonData, (code, response) =>
            {
                if (string.IsNullOrEmpty(response) && (code < 200 || code >= 300))
                {
                    PrintError($"Web request to {url} returned empty response or HTTP {code}");
                    callback(code, null);
                }
                else
                {
                    callback(code, response);
                }
            }, this, method, headers ?? new Dictionary<string, string> { { "Content-Type", "application/json" } });
        }

        private void ResolveLightningAddress(string lightningAddress, int amountSats, Action<string> callback)
        {
            var parts = lightningAddress.Split('@');
            if (parts.Length != 2)
            {
                PrintError($"Invalid Lightning Address format: {lightningAddress}");
                callback(null);
                return;
            }

            string user = parts[0];
            string domain = parts[1];

            string lnurlEndpoint = $"https://{domain}/.well-known/lnurlp/{user}";

            var headers = new Dictionary<string, string>
            {
                { "Content-Type", "application/json" }
            };

            MakeWebRequest(lnurlEndpoint, null, (code, response) =>
            {
                if (code != 200 || string.IsNullOrEmpty(response))
                {
                    PrintError($"Failed to fetch LNURL for {lightningAddress}: HTTP {code}");
                    callback(null);
                    return;
                }

                try
                {
                    var lnurlResponse = JsonConvert.DeserializeObject<LNURLResponse>(response);
                    if (lnurlResponse == null || string.IsNullOrEmpty(lnurlResponse.Callback))
                    {
                        PrintError($"Invalid LNURL response for {lightningAddress}");
                        callback(null);
                        return;
                    }

                    long amountMsat = (long)amountSats * 1000;

                    string callbackUrl = lnurlResponse.Callback;

                    string callbackUrlWithAmount = $"{callbackUrl}?amount={amountMsat}";

                    MakeWebRequest(callbackUrlWithAmount, null, (payCode, payResponse) =>
                    {
                        if (payCode != 200 || string.IsNullOrEmpty(payResponse))
                        {
                            PrintError($"Failed to perform LNURL Pay for {lightningAddress}: HTTP {payCode}");
                            callback(null);
                            return;
                        }

                        try
                        {
                            var payAction = JsonConvert.DeserializeObject<LNURLPayResponse>(payResponse);
                            if (payAction == null || string.IsNullOrEmpty(payAction.Pr))
                            {
                                PrintError($"Invalid LNURL Pay response for {lightningAddress}");
                                callback(null);
                                return;
                            }

                            callback(payAction.Pr);
                        }
                        catch (Exception ex)
                        {
                            PrintError($"Error parsing LNURL Pay response: {ex.Message}");
                            callback(null);
                        }
                    }, RequestMethod.GET, headers);
                }
                catch (Exception ex)
                {
                    PrintError($"Error parsing LNURL response: {ex.Message}");
                    callback(null);
                }
            }, RequestMethod.GET, headers);
        }

        private class LNURLResponse
        {
            [JsonProperty("tag")]
            public string Tag { get; set; }

            [JsonProperty("callback")]
            public string Callback { get; set; }

            [JsonProperty("minSendable")]
            public long MinSendable { get; set; }

            [JsonProperty("maxSendable")]
            public long MaxSendable { get; set; }

            [JsonProperty("metadata")]
            public string Metadata { get; set; }

            [JsonProperty("commentAllowed")]
            public int CommentAllowed { get; set; }

            [JsonProperty("allowsNostr")]
            public bool AllowsNostr { get; set; }

            [JsonProperty("nostrPubkey")]
            public string NostrPubkey { get; set; }
        }

        private class LNURLPayResponse
        {
            [JsonProperty("pr")]
            public string Pr { get; set; }

            [JsonProperty("routes")]
            public List<object> Routes { get; set; }
        }

        private string ExtractLightningAddress(string memo)
        {
            // Expected format: "Sending {amount} {currency} to {lightning_address}"
            var parts = memo.Split(" to ");
            return parts.Length == 2 ? parts[1] : "unknown@unknown.com";
        }

        // RewardPlayer method to grant currency items to the player
        private void RewardPlayer(IPlayer player, int amount)
        {
            var basePlayer = player.Object as BasePlayer;
            if (basePlayer == null)
            {
                PrintError($"Failed to find base player object for player {player.Id}.");
                return;
            }

            var currencyItem = ItemManager.CreateByItemID(currencyItemID, amount);
            if (currencyItem != null)
            {
                if (currencySkinID > 0)
                {
                    currencyItem.skin = currencySkinID;
                }
                
                // Check if player has inventory space first
                if (HasInventorySpace(basePlayer, amount))
                {
                    // Give the item to the player
                    basePlayer.GiveItem(currencyItem);
                    player.Reply($"You have successfully purchased {amount} {currencyName}!");
                    Puts($"Gave {amount} {currencyName} (skinID: {currencySkinID}) to player {basePlayer.UserIDString}.");
                }
                else
                {
                    // Drop on ground if no inventory space
                    var dropPosition = basePlayer.transform.position + new UnityEngine.Vector3(0f, 1.5f, 0f);
                    var droppedItemEntity = currencyItem.CreateWorldObject(dropPosition);
                    
                    if (droppedItemEntity != null)
                    {
                        player.Reply($"Your inventory was full! {amount} {currencyName} dropped on the ground near you.");
                        Puts($"Dropped {amount} {currencyName} on ground for player {basePlayer.UserIDString} (inventory full).");
                    }
                    else
                    {
                        currencyItem.Remove();
                        PrintError($"Failed to drop {currencyName} item for player {basePlayer.UserIDString}.");
                        player.Reply($"Your inventory was full and we couldn't drop the items. Please contact an administrator.");
                    }
                }
            }
            else
            {
                PrintError($"Failed to create {currencyName} item for player {basePlayer.UserIDString}.");
            }
        }

        // GrantVip method to add the VIP permission group to the player
        private void GrantVip(IPlayer player)
        {
            player.Reply("You have successfully purchased VIP status!");
            
            var basePlayer = player.Object as BasePlayer;
            if (basePlayer == null)
            {
                PrintError($"Failed to find base player object for player {player.Id} to grant VIP.");
                return;
            }
            
            // Replace placeholders in the command
            string commandToExecute = vipCommand
                .Replace("{player}", player.Name)
                .Replace("{steamid}", GetPlayerId(player))
                .Replace("{userid}", GetPlayerId(player))
                .Replace("{id}", GetPlayerId(player));
            
            Puts($"[VIP] Executing command for player {player.Name} ({GetPlayerId(player)}): {commandToExecute}");
            
            try
            {
                // Execute the command on the server using the correct Covalence method
                server.Command(commandToExecute);
                Puts($"[VIP] Successfully executed VIP command for player {player.Name}");
            }
            catch (Exception ex)
            {
                PrintError($"[VIP] Failed to execute VIP command for player {player.Name}: {ex.Message}");
                PrintError($"[VIP] Command was: {commandToExecute}");
            }
        }

        // Fixed ReturnCurrency method with inventory space checking
        private void ReturnCurrency(BasePlayer player, int amount)
        {
            var returnedCurrency = ItemManager.CreateByItemID(currencyItemID, amount);
            if (returnedCurrency != null)
            {
                if (currencySkinID > 0)
                {
                    returnedCurrency.skin = currencySkinID;
                }
                
                // Check if player has inventory space first
                if (HasInventorySpace(player, amount))
                {
                    // Move to main container
                    returnedCurrency.MoveToContainer(player.inventory.containerMain);
                    Puts($"Returned {amount} {currencyName} to player {player.UserIDString}.");
                }
                else
                {
                    // Drop on ground if no inventory space
                    var dropPosition = player.transform.position + new UnityEngine.Vector3(0f, 1.5f, 0f);
                    var droppedItemEntity = returnedCurrency.CreateWorldObject(dropPosition);
                    
                    if (droppedItemEntity != null)
                    {
                        Puts($"Dropped {amount} {currencyName} on ground for player {player.UserIDString} (inventory full).");
                    }
                    else
                    {
                        returnedCurrency.Remove();
                        PrintError($"Failed to return or drop {amount} {currencyName} for player {player.UserIDString}.");
                    }
                }
            }
            else
            {
                PrintError($"Failed to create {currencyName} item to return to player {player.UserIDString}.");
            }
        }

        // Helper method to check if player has inventory space
        private bool HasInventorySpace(BasePlayer player, int amount)
        {
            if (player?.inventory?.containerMain == null)
                return false;

            int availableSpace = 0;
            var container = player.inventory.containerMain;
            
            // Check each slot in main inventory
            for (int i = 0; i < container.capacity; i++)
            {
                var slot = container.GetSlot(i);
                if (slot == null)
                {
                    // Empty slot - can fit full stack
                    var itemDefinition = ItemManager.FindItemDefinition(currencyItemID);
                    if (itemDefinition != null)
                    {
                        availableSpace += itemDefinition.stackable;
                    }
                }
                else if (IsCurrencyItem(slot))
                {
                    int maxStack = slot.info.stackable;
                    if (slot.amount < maxStack)
                    {
                        // Existing currency stack with room
                        availableSpace += maxStack - slot.amount;
                    }
                }
                
                // If we have enough space, no need to check further
                if (availableSpace >= amount)
                    return true;
            }
            
            return availableSpace >= amount;
        }

        // LOGGING METHODS

        // Enhanced LogSellTransaction helper
        private void LogSellTransaction(SellInvoiceLogEntry logEntry)
        {
            var logs = LoadSellLogData();
            
            // Check if this is an update to existing transaction
            var existingIndex = logs.FindIndex(l => l.TransactionId == logEntry.TransactionId);
            if (existingIndex >= 0)
            {
                // Update existing entry
                logs[existingIndex] = logEntry;
                Puts($"[Orangemart] Updated sell transaction: {logEntry.TransactionId}");
            }
            else
            {
                // Add new entry
                logs.Add(logEntry);
                Puts($"[Orangemart] Logged new sell transaction: {logEntry.TransactionId}");
            }
            
            SaveSellLogData(logs);
        }

        // Update sell transaction status
        private void UpdateSellTransactionStatus(string transactionId, string status, bool success, string failureReason = null, bool currencyReturned = false)
        {
            var logs = LoadSellLogData();
            var entry = logs.FirstOrDefault(l => l.TransactionId == transactionId);
            
            if (entry != null)
            {
                entry.Status = status;
                entry.Success = success;
                entry.CompletedTimestamp = DateTime.UtcNow;
                entry.CurrencyReturned = currencyReturned;
                if (!string.IsNullOrEmpty(failureReason))
                {
                    entry.FailureReason = failureReason;
                }
                
                SaveSellLogData(logs);
                Puts($"[Orangemart] Updated sell transaction status: {transactionId} -> {status}");
            }
            else
            {
                PrintWarning($"[Orangemart] Could not find sell transaction to update: {transactionId}");
            }
        }

        // Update sell transaction with payment hash
        private void UpdateSellTransactionPaymentHash(string transactionId, string paymentHash)
        {
            var logs = LoadSellLogData();
            var entry = logs.FirstOrDefault(l => l.TransactionId == transactionId);
            
            if (entry != null)
            {
                entry.PaymentHash = paymentHash;
                SaveSellLogData(logs);
                Puts($"[Orangemart] Updated sell transaction with payment hash: {transactionId}");
            }
        }

        private List<SellInvoiceLogEntry> LoadSellLogData()
        {
            var path = Path.Combine(Interface.Oxide.DataDirectory, SellLogFile);
            return File.Exists(path)
                ? JsonConvert.DeserializeObject<List<SellInvoiceLogEntry>>(File.ReadAllText(path))
                : new List<SellInvoiceLogEntry>();
        }

        private void SaveSellLogData(List<SellInvoiceLogEntry> data)
        {
            var path = Path.Combine(Interface.Oxide.DataDirectory, SellLogFile);
            var directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented));
        }

        // Enhanced LogBuyInvoice helper
        private void LogBuyInvoice(BuyInvoiceLogEntry logEntry)
        {
            var logs = LoadBuyLogData();
            
            // Check if this is an update to existing transaction
            var existingIndex = logs.FindIndex(l => l.TransactionId == logEntry.TransactionId);
            if (existingIndex >= 0)
            {
                // Update existing entry
                logs[existingIndex] = logEntry;
                Puts($"[Orangemart] Updated buy transaction: {logEntry.TransactionId}");
            }
            else
            {
                // Add new entry
                logs.Add(logEntry);
                Puts($"[Orangemart] Logged new buy transaction: {logEntry.TransactionId}");
            }
            
            SaveBuyLogData(logs);
        }

        // Update buy transaction status
        private void UpdateBuyTransactionStatus(string transactionId, string status, bool isPaid)
        {
            var logs = LoadBuyLogData();
            var entry = logs.FirstOrDefault(l => l.TransactionId == transactionId);
            
            if (entry != null)
            {
                entry.Status = status;
                entry.IsPaid = isPaid;
                entry.CompletedTimestamp = DateTime.UtcNow;
                
                if (isPaid)
                {
                    if (entry.PurchaseType == "Currency")
                    {
                        entry.CurrencyGiven = true;
                    }
                    else if (entry.PurchaseType == "VIP")
                    {
                        entry.VipGranted = true;
                    }
                }
                
                SaveBuyLogData(logs);
                Puts($"[Orangemart] Updated buy transaction status: {transactionId} -> {status}");
            }
            else
            {
                PrintWarning($"[Orangemart] Could not find buy transaction to update: {transactionId}");
            }
        }

        // Update buy transaction with invoice ID
        private void UpdateBuyTransactionInvoiceId(string transactionId, string invoiceId)
        {
            var logs = LoadBuyLogData();
            var entry = logs.FirstOrDefault(l => l.TransactionId == transactionId);
            
            if (entry != null)
            {
                entry.InvoiceID = invoiceId;
                SaveBuyLogData(logs);
                Puts($"[Orangemart] Updated buy transaction with invoice ID: {transactionId}");
            }
        }

        private List<BuyInvoiceLogEntry> LoadBuyLogData()
        {
            var path = Path.Combine(Interface.Oxide.DataDirectory, BuyInvoiceLogFile);
            return File.Exists(path)
                ? JsonConvert.DeserializeObject<List<BuyInvoiceLogEntry>>(File.ReadAllText(path)) ?? new List<BuyInvoiceLogEntry>()
                : new List<BuyInvoiceLogEntry>();
        }

        private void SaveBuyLogData(List<BuyInvoiceLogEntry> data)
        {
            var path = Path.Combine(Interface.Oxide.DataDirectory, BuyInvoiceLogFile);
            var directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented));
        }

        private BuyInvoiceLogEntry CreateBuyInvoiceLogEntry(IPlayer player, string invoiceID, bool isPaid, int amount, PurchaseType type, int retryCount)
        {
            return new BuyInvoiceLogEntry
            {
                TransactionId = GenerateTransactionId(),
                SteamID = GetPlayerId(player),
                InvoiceID = invoiceID,
                Status = isPaid ? TransactionStatus.COMPLETED : TransactionStatus.FAILED,
                IsPaid = isPaid,
                Timestamp = DateTime.UtcNow,
                CompletedTimestamp = DateTime.UtcNow,
                Amount = amount,
                CurrencyGiven = isPaid && type == PurchaseType.Currency,
                VipGranted = isPaid && type == PurchaseType.Vip,
                RetryCount = retryCount,
                PurchaseType = type == PurchaseType.Currency ? "Currency" : "VIP"
            };
        }

        private void SendInvoiceToDiscord(IPlayer player, string invoice, int amountSats, string memo)
        {
            if (string.IsNullOrEmpty(config.DiscordWebhookUrl))
            {
                PrintError("Discord webhook URL is not configured.");
                return;
            }

            string qrCodeUrl = $"https://api.qrserver.com/v1/create-qr-code/?data={Uri.EscapeDataString(invoice)}&size=200x200";

            var webhookPayload = new
            {
                content = $"**{player.Name}**, please pay **{amountSats} sats** using the Lightning Network.",
                embeds = new[]
                {
                    new
                    {
                        title = "Payment Invoice",
                        description = $"{memo}\n\nPlease pay the following Lightning invoice to complete your purchase:\n\n```\n{invoice}\n```",
                        image = new
                        {
                            url = qrCodeUrl
                        },
                        fields = new[]
                        {
                            new { name = "Amount", value = $"{amountSats} sats", inline = true },
                            new { name = "Steam ID", value = GetPlayerId(player), inline = true }
                        }
                    }
                }
            };

            string jsonPayload = JsonConvert.SerializeObject(webhookPayload);

            MakeWebRequest(config.DiscordWebhookUrl, jsonPayload, (code, response) =>
            {
                if (code != 204)
                {
                    PrintError($"Failed to send invoice to Discord webhook: HTTP {code}");
                }
                else
                {
                    Puts($"Invoice sent to Discord for player {GetPlayerId(player)}.");
                }
            }, RequestMethod.POST, new Dictionary<string, string> { { "Content-Type", "application/json" } });
        }
    }
}