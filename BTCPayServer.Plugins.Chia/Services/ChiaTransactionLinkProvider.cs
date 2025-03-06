using System.Globalization;
using BTCPayServer.Services;

namespace BTCPayServer.Plugins.Chia.Services;

internal class ChiaTransactionLinkProvider(string blockExplorerLink) : DefaultTransactionLinkProvider(blockExplorerLink)
{
    public override string? GetTransactionLink(string paymentId)
    {
        if (string.IsNullOrEmpty(BlockExplorerLink))
            return null;
        return string.Format(CultureInfo.InvariantCulture, BlockExplorerLink, paymentId);
    }
}