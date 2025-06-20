## Overview:
The **Orangemart** plugin allows players on your Rust server to buy and sell in-game units and VIP status using Bitcoin payments through the Lightning Network. This plugin integrates LNBits into the game, enabling secure transactions for game items and services.

---

## Features

- **In-Game Currency Purchase:** Players can purchase in-game currency using Bitcoin payments.
- **Send In-Game Currency:** Players can send currency to others, facilitating peer-to-peer transactions.
- **VIP Status Purchase:** Players can purchase VIP status through Bitcoin payments, unlocking special privileges.
- **Configurable:** Server admins can set up command names, currency items, prices, and more through the configuration file.

---

## Commands

The following commands are available to players:

- **`/buyblood`**  
  Players can purchase in-game currency using Bitcoin. The amount purchased is configurable.

- **`/sendblood <amount> <targetPlayer>`**  
  Players can send a specified amount of in-game currency to another player.

- **`/buyvip`**  
  Players can purchase VIP status using Bitcoin. The VIP price and associated permission group are configurable.

---

# Orangemart Plugin Configuration

Below is a comprehensive list of key configuration variables that can be customized in the **Orangemart** plugin. 
These settings allow you to tailor the plugin's behavior to fit your server's requirements.

## Configuration

#### Commands

- **`BuyCurrencyCommandName`**
  - The command players use to purchase in-game currency. For example, `/buyblood`.

- **`SendCurrencyCommandName`**
  - The command players use to send in-game currency to other players. For example, `/sendblood`.

- **`BuyVipCommandName`**
  - The command players use to purchase VIP status. For example, `/buyvip`.

#### Currency Settings

- **`CurrencyItemID`**
  - The item ID used for in-game currency transactions.

- **`CurrencyName`**
  - The name of the in-game currency displayed to players.

- **`CurrencySkinID`**
  - The skin ID applied to the in-game currency items. Set to `0` for default skin.

- **`SatsPerCurrencyUnit`**
  - The conversion rate between satoshis and in-game currency units. Determines how many satoshis equal one unit of in-game currency.

- **`PricePerCurrencyUnit`**
  - The price (in satoshis) per unit of in-game currency.

- **`MaxPurchaseAmount`**
  - Maximum number of currency units a player can buy in one invoice. Set to 0 for no limit.

- **`MaxSendAmount`**
  - Maximum number of currency units a player can send in one transaction. Set to 0 for no limit.

- **`CommandCooldownSeconds`**
  - How many seconds a player must wait between repeat uses of any Orangemart command. Set to 0 to disable cooldowns.

- **`MaxPendingInvoicesPerPlayer`**
  - How many outstanding (unpaid) invoices a single player can have at once. Set to 0 for no limit.

#### Discord Integration

- **`DiscordChannelName`**
  - The name of the Discord channel where payment invoices will be posted.

- **`DiscordWebhookUrl`**
  - The Discord webhook URL used to send invoice notifications to the specified channel.

#### Invoice Settings

- **`BlacklistedDomains`**
  - A list of domains that are disallowed for use in Lightning addresses. Players cannot send currency to addresses from these domains.

- **`WhitelistedDomains`**
  - A list of domains that are exclusively allowed for use in Lightning addresses. If this list is populated, only addresses from these domains are permitted.

- **`CheckIntervalSeconds`**
  - The interval time (in seconds) for checking pending Bitcoin transactions.

- **`InvoiceTimeoutSeconds`**
  - The time (in seconds) after which an unpaid invoice expires.

- **`LNbitsApiKey`**
  - The API key for authenticating with your LNbits instance.

- **`LNbitsBaseUrl`**
  - The base URL of your LNbits instance.

- **`MaxRetries`**
  - The maximum number of retry attempts for checking invoice payments before considering them expired.

- **`UseWebSockets`**
  - Set true to enable WS-based invoice updates; false to always fall back to HTTP.

- **`WebSocketReconnectDelay`**
  - How many seconds to wait before retrying a failed WebSocket connection. 

#### VIP Settings

Configure the VIP status purchase and permissions.

- **`VipCommand`**
  - The Oxide console command template to grant VIP status.

- **`VipPrice`**
  - The price (in satoshis) for players to purchase VIP status.

---

### Example Configuration Snippet

Here's how the configuration might look in your `config.json`:

```
{
  "Commands": {
    "BuyCurrencyCommandName": "buyblood",
    "SendCurrencyCommandName": "sendblood",
    "BuyVipCommandName": "buyvip"
  },
  "CurrencySettings": {
    "CurrencyItemID": 1776460938,
    "CurrencyName": "blood",
    "CurrencySkinID": 0,
    "PricePerCurrencyUnit": 1,
    "SatsPerCurrencyUnit": 1,
    "MaxPurchaseAmount": 10000,
    "MaxSendAmount": 10000,
    "CommandCooldownSeconds": 0,
    "MaxPendingInvoicesPerPlayer": 1
  },
  "Discord": {
    "DiscordChannelName": "mart",
    "DiscordWebhookUrl": "https://discord.com/api/webhooks/your_webhook_url"
  },
  "InvoiceSettings": {
    "BlacklistedDomains": ["example.com", "blacklisted.net"],
    "WhitelistedDomains": [],
    "CheckIntervalSeconds": 10,
    "InvoiceTimeoutSeconds": 300,
    "LNbitsApiKey": "your-lnbits-admin-api-key",
    "LNbitsBaseUrl": "https://your-lnbits-instance.com",
    "MaxRetries": 25,
    "UseWebSockets": true,
    "WebSocketReconnectDelay": 5
  },
  "VIPSettings": {
    "VipCommand": "oxide.usergroup add {player} vip",
    "VipPrice": 1000
  }
}
```

**Note:**  
- Ensure that all URLs and API keys are correctly set to match your server and LNbits configurations.
- Adjust the `BlacklistedDomains` and `WhitelistedDomains` according to your server's policies regarding Lightning addresses.

---

## Installation

1. **Download the Plugin**  
   Place the `Orangemart.cs` file in your server's `oxide/plugins` folder.

2. **Configuration**  
   Modify the plugin’s configuration file to fit your server’s settings (currency item, prices, VIP group, etc.). The configuration file will be automatically generated upon running the plugin for the first time.

3. **Create VIP Group (Optional)**  
   Create a VIP group to assign permssions to.
   
4. **Reload the Plugin**  
   Once configured, reload the plugin using the command:  
   ```  
   oxide.reload Orangemart  
   ```

---

## Permissions

The plugin uses the following permissions:

- **`orangemart.buycurrency`**  
  Grants permission to players who are allowed to buy your currency item via Bitcoin.

- **`orangemart.sendcurrency`**  
  Grants permission to players who are allowed to send Bitcoin for your in-game currency unit.

- **`orangemart.buyvip`**  
  Grants permission to players to purchase VIP via Bitcoin.

---

## Logging and Troubleshooting

- **Logs:**  
  Transaction details, such as purchases and currency sends, are logged for auditing purposes. Logs can be found in the `oxide/data/Orangemart` directory.

- **Troubleshooting:**  
  If any issues arise, check the server logs for errors related to the plugin. Ensure that the configuration file is correctly set up and that Bitcoin payment services are running as expected.

---

## License

This plugin is licensed under the MIT License.
