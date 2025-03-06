using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.Chia.Services.Payments;

public class ChiaPaymentMethodConfig
{
    public string MasterPublicKey { get; set; } = String.Empty;

    public int DerivationIndex { get; set; } = 0;
    
    public string[] Addresses { get; set; } = [];

    public async Task<string?> GetOneNotReservedAddress(PaymentMethodId paymentMethodId,
        InvoiceRepository invoiceRepository)
    {
        var allReservedAddresses = await GetReservedAddresses(paymentMethodId, invoiceRepository);
        return Addresses.Except(allReservedAddresses).FirstOrDefault();
    }

    public static async Task<string[]> GetReservedAddresses(PaymentMethodId paymentMethodId,
        InvoiceRepository invoiceRepository)
    {
        var pendingInvoices = (await invoiceRepository.GetMonitoredInvoices(paymentMethodId, true))
            .Where(i => ChiaListener.StatusToTrack.Contains(i.Status));
        return pendingInvoices
            .Select(i => i.GetPaymentPrompt(paymentMethodId)?.Destination)
            .Where(s => s is not null)
            .Select(s => s!).ToArray();
    }
}