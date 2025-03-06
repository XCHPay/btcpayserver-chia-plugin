using System;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Chia.Configuration;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Stores;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Chia.Services.Payments;

public class ChiaLikePaymentMethodHandler(
    ChiaPluginConfigurationItem configurationItem,
    ChiaRpcProvider chiaRpcProvider,
    CurrencyNameTable currencyNameTable,
    InvoiceRepository invoiceRepository) : IPaymentMethodHandler
{
    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;

    public PaymentMethodId PaymentMethodId { get; } = configurationItem.GetPaymentMethodId();

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = configurationItem.Currency;
        context.Prompt.Divisibility = configurationItem.Divisibility;
        context.Prompt.RateDivisibility = currencyNameTable.GetCurrencyData(context.Prompt.Currency, false).Divisibility;
        return Task.CompletedTask;
    }

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        if (!chiaRpcProvider.IsAvailable(configurationItem.GetPaymentMethodId()))
            throw new PaymentMethodUnavailableException("Node or wallet not available");

        var details = new ChiaLikeOnChainPaymentMethodDetails();
        var availableAddress = await ParsePaymentMethodConfig(context.PaymentMethodConfig)
                                   .GetOneNotReservedAddress(context.PaymentMethodId, invoiceRepository) ??
                               throw new PaymentMethodUnavailableException("All your Chia addresses are currently waiting payment");
        context.Prompt.Destination = availableAddress;
        context.Prompt.PaymentMethodFee = 0; 
        context.Prompt.Details = JObject.FromObject(details, Serializer);
    }

    object IPaymentMethodHandler.ParsePaymentMethodConfig(JToken config)
    {
        return ParsePaymentMethodConfig(config);
    }

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details)
    {
        return ParsePaymentPromptDetails(details)!;
    }

    object IPaymentMethodHandler.ParsePaymentDetails(JToken details)
    {
        return ParsePaymentDetails(details);
    }

    private ChiaPaymentMethodConfig ParsePaymentMethodConfig(JToken config)
    {
        return config.ToObject<ChiaPaymentMethodConfig>(Serializer) ??
               throw new FormatException($"Invalid {nameof(ChiaLikePaymentMethodHandler)}");
    }

    public ChiaLikeOnChainPaymentMethodDetails? ParsePaymentPromptDetails(JToken details)
    {
        return details.ToObject<ChiaLikeOnChainPaymentMethodDetails>(Serializer);
    }

    public ChiaLikePaymentData ParsePaymentDetails(JToken details)
    {
        return details.ToObject<ChiaLikePaymentData>(Serializer) ??
               throw new FormatException($"Invalid {nameof(ChiaLikePaymentMethodHandler)}");
    }
}