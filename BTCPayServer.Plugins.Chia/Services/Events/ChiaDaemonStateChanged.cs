using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.Chia.Services.Events;

public class ChiaDaemonStateChanged
{
    public required PaymentMethodId PaymentMethodId { get; set; }
    public required ChiaRpcProvider.ChiaLikeSummary Summary { get; set; }
}