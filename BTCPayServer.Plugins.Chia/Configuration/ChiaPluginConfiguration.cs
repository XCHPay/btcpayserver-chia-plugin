using System.Collections.Generic;
using BTCPayServer.Payments;
using chia.dotnet;

namespace BTCPayServer.Plugins.Chia.Configuration;

public class ChiaPluginConfiguration
    {
        public Dictionary<PaymentMethodId, ChiaPluginConfigurationItem> ChiaConfigurationItems { get; init; } = [];
    }

    public record ChiaPluginConfigurationItem
    {
        public string Chain => Constants.ChiaChainName;
        
        public required EndpointInfo FullNodeEndpoint { get; init; }
        
        public required string Currency { get; init; }
        public required string DisplayName { get; init; }
        public required int Divisibility { get; init; }
        public required string CryptoImagePath { get; init; }
        public required string BlockExplorerLink { get; init; }
        public required string[] DefaultRateRules { get; init; }
        public required string CurrencyDisplayName { get; init; }
    
        public ChainRef ChainRef => Chain;
        public CurrencyRef CurrencyRef => Currency;

        public PaymentMethodId GetPaymentMethodId() => new($"{CurrencyRef}-{ChainRef}");
        public string GetSettingPrefix() => $"{CurrencyRef}_{ChainRef}";
    }
