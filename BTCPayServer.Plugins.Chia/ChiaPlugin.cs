using System;
using System.Collections.Generic;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Hosting;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Chia.Configuration;
using BTCPayServer.Plugins.Chia.Services;
using BTCPayServer.Plugins.Chia.Services.Payments;
using BTCPayServer.Services.Rates;
using chia.dotnet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.Chia;

public class ChiaPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.0.0" }
    };
    
    public override void Execute(IServiceCollection serviceCollection)
    {
        RegisterServices(serviceCollection);
        base.Execute(serviceCollection);
    }

    private void RegisterServices(IServiceCollection services)
    {
        var networkProvider = ((PluginServiceCollection)services).BootstrapServices.GetRequiredService<NBXplorerNetworkProvider>();
        var configuration = ((PluginServiceCollection)services).BootstrapServices.GetRequiredService<IConfiguration>();

        var chiaConfiguration = GetChiaDefaultConfigurationItem(networkProvider, configuration);
        services.AddSingleton(new ChiaPluginConfiguration
        {
            ChiaConfigurationItems = new Dictionary<PaymentMethodId, ChiaPluginConfigurationItem>
            {
                { chiaConfiguration.GetPaymentMethodId(), chiaConfiguration }
            }
        });

        services.AddCurrencyData(new CurrencyData
        {
            Code = Constants.XchCurrency,
            Name = Constants.XchCurrencyDisplayName,
            Divisibility = 12,
            Symbol = Constants.XchCurrencyDisplayName,
            Crypto = true
        });

        var chiaPaymentMethodId = chiaConfiguration.GetPaymentMethodId();
        services.AddTransactionLinkProvider(chiaPaymentMethodId, new ChiaTransactionLinkProvider(chiaConfiguration.BlockExplorerLink));
        services.AddSingleton<ChiaRpcProvider>();
        services.AddHostedService<ChiaLikeSummaryUpdaterHostedService>();
        services.AddHostedService<ChiaListener>();

        services.AddSingleton(new DefaultRules(chiaConfiguration.DefaultRateRules));

        services.AddSingleton(provider => (IPaymentMethodHandler)ActivatorUtilities.CreateInstance(provider, typeof(ChiaLikePaymentMethodHandler),
            chiaConfiguration));
        services.AddSingleton<IPaymentLinkExtension>(provider =>
            (IPaymentLinkExtension)ActivatorUtilities.CreateInstance(provider, typeof(ChiaPaymentLinkExtension), chiaPaymentMethodId));
        services.AddSingleton(provider =>
            (ICheckoutModelExtension)ActivatorUtilities.CreateInstance(provider, typeof(ChiaCheckoutModelExtension),
                chiaConfiguration));
        
        services.AddDefaultPrettyName(chiaPaymentMethodId, chiaConfiguration.DisplayName);

        // For future usages (multiple CATs)
        //services.AddSingleton<IUIExtension>(new UIExtension("Chia/StoreNavChiaExtension", "store-integrations-nav"));
        services.AddUIExtension("store-wallets-nav", "ChiaLike/StoreWalletsNavChiaExtension");
        services.AddUIExtension("store-invoices-payments", "ChiaLike/ViewChiaLikePaymentData");
        services.AddUIExtension("checkout-payment-method", "ChiaLike/EmptyCheckoutPaymentMethodExtension");
        services.AddSingleton<ISyncSummaryProvider, ChiaSyncSummaryProvider>();
    }


    private static ChiaPluginConfigurationItem GetChiaHardcodedConfig(ChainName chainName)
    {
        return chainName switch
        {
            _ when chainName == ChainName.Mainnet => new ChiaPluginConfigurationItem
            {
                Currency = Constants.XchCurrency,
                CurrencyDisplayName = Constants.XchCurrencyDisplayName,
                DisplayName = $"{Constants.XchCurrencyDisplayName} on {Constants.ChiaChainName}",
                CryptoImagePath = "Resources/assets/chia.png",

                DefaultRateRules =
                [
                    $"{Constants.XchCurrency}_USD = huobi({Constants.XchCurrency}_USDT)",
                    $"{Constants.XchCurrency}_X = {Constants.XchCurrency}_USD * USD_X"
                ],

                Divisibility = 12,
                FullNodeEndpoint = new EndpointInfo { Uri = new Uri("https://api.coinset.org")},
                BlockExplorerLink = "https://www.spacescan.io/coin/{0}"
            },
            _ when chainName == ChainName.Testnet || chainName == ChainName.Regtest => new ChiaPluginConfigurationItem
            {
                Currency = Constants.XchCurrency,
                CurrencyDisplayName = Constants.XchCurrencyDisplayName,
                DisplayName = $"{Constants.XchCurrency} on {Constants.ChiaChainName} Testnet",
                CryptoImagePath = "Resources/assets/chia.png",

                DefaultRateRules =
                [
                    $"{Constants.XchCurrency}_USD = 0.01",
                    $"{Constants.XchCurrency}_X = {Constants.XchCurrency}_USD * USD_X"
                ],

                Divisibility = 12,
                FullNodeEndpoint = new EndpointInfo { Uri = new Uri("https://testnet11.api.coinset.org")},
                BlockExplorerLink = "https://testnet11.spacescan.io/coin/{0}"
            },

            _ => throw new NotSupportedException()
        };
    }

    public static ChiaPluginConfigurationItem GetChiaDefaultConfigurationItem(NBXplorerNetworkProvider networkProvider, IConfiguration configuration)
    {
        return GetChiaHardcodedConfig(networkProvider.NetworkType);
    }
}
