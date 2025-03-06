using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.Chia.Controllers.ViewModels;

public class ViewChiaStoreOptionItemViewModel
{
    public required string DisplayName { get; init; }
    public bool Enabled { get; init; }
    public required PaymentMethodId PaymentMethodId { get; init; }
    public required string MasterPublicKey { get; init; }
}