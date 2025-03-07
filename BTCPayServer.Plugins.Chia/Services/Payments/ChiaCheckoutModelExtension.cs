using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Chia.Configuration;

namespace BTCPayServer.Plugins.Chia.Services.Payments;

public class ChiaCheckoutModelExtension(
    ChiaPluginConfigurationItem configurationItem,
    IEnumerable<IPaymentLinkExtension> paymentLinkExtensions) : ICheckoutModelExtension
{
    private readonly IPaymentLinkExtension _paymentLinkExtension =
        paymentLinkExtensions.Single(p => p.PaymentMethodId == configurationItem.GetPaymentMethodId());

    public PaymentMethodId PaymentMethodId { get; } = configurationItem.GetPaymentMethodId();
    public string Image => configurationItem.CryptoImagePath;
    public string Badge => "";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context is not { Handler: ChiaLikePaymentMethodHandler handler })
            return;

        context.Model.CheckoutBodyComponentName = BitcoinCheckoutModelExtension.CheckoutBodyComponentName;
        
        // TODO Enable when Chia has a standard for payment links

        context.Model.InvoiceBitcoinUrl = _paymentLinkExtension.GetPaymentLink(context.Prompt, context.UrlHelper);
        context.Model.InvoiceBitcoinUrlQR = "";
        
        context.Model.ShowPayInWalletButton = false;
        context.Model.PaymentMethodCurrency = configurationItem.CurrencyDisplayName;
    }
}