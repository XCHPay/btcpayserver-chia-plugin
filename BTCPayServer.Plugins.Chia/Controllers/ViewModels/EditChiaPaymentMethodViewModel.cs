using BTCPayServer.Plugins.Chia.Configuration;

namespace BTCPayServer.Plugins.Chia.Controllers.ViewModels;

public class EditChiaPaymentMethodViewModel
{
    [ChiaMasterPublicKey]
    public string? MasterPublicKey { get; init; }
    public bool Enabled { get; init; }

    public EditChiaPaymentMethodAddressViewModel[] Addresses { get; init; } =
        [];

    public class EditChiaPaymentMethodAddressViewModel
    {
        public required string Value { get; init; }
        public bool Available { get; init; }
        public required string Balance { get; init; }
    }
}