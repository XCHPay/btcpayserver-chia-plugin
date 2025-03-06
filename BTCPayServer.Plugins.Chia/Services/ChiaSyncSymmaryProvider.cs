using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Abstractions.Contracts;

namespace BTCPayServer.Plugins.Chia.Services;

public class ChiaSyncSummaryProvider(ChiaRpcProvider chiaRpcProvider) : ISyncSummaryProvider
{
    public bool AllAvailable()
    {
        return chiaRpcProvider.Summaries.All(pair => pair.Value.RpcAvailable);
    }

    public string Partial => "ChiaLike/ChiaSyncSummary";

    public IEnumerable<ISyncStatus> GetStatuses()
    {
        return chiaRpcProvider.Summaries.Select(pair => new ChiaSyncStatus
        {
            Summary = pair.Value,
            PaymentMethodId = pair.Key.ToString()
        });
    }
}