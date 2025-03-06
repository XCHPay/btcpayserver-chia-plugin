using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;

namespace BTCPayServer.Plugins.Chia.Services;

public class ChiaSyncStatus : SyncStatus, ISyncStatus
{
    public required ChiaRpcProvider.ChiaLikeSummary Summary { get; init; }

    public override bool Available => Summary?.RpcAvailable ?? false;
}