using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading.Tasks;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Chia.Configuration;
using BTCPayServer.Plugins.Chia.Services.Events;
using BTCPayServer.Services;
using chia.dotnet;
using NBitcoin;

namespace BTCPayServer.Plugins.Chia.Services;

public class ChiaRpcProvider
{
    private ImmutableDictionary<PaymentMethodId, FullNodeProxy> _fullNodeRpcClients = null!;
    private readonly ChiaPluginConfiguration _chiaPluginConfiguration;
    private readonly IEventAggregatorSubscription _eventAggregatorSubscription;
    private readonly EventAggregator _eventAggregator;
    private readonly SettingsRepository _settingsRepository;

    public ChiaRpcProvider(ChiaPluginConfiguration chiaPluginConfiguration,
        EventAggregator eventAggregator, SettingsRepository settingsRepository)
    {
        _chiaPluginConfiguration = chiaPluginConfiguration;
        _eventAggregator = eventAggregator;
        _settingsRepository = settingsRepository;

        _eventAggregatorSubscription =
            _eventAggregator.Subscribe<ChiaSettingsChanged>(_ => LoadClientsFromConfiguration());
        LoadClientsFromConfiguration();
    }

    public ConcurrentDictionary<PaymentMethodId, ChiaLikeSummary> Summaries { get; } = new();

    private void LoadClientsFromConfiguration()
    {
        lock (this)
        {
            _fullNodeRpcClients = _chiaPluginConfiguration.ChiaConfigurationItems.ToImmutableDictionary(
                pair => pair.Key,
                pair =>
                {
                    var rpcClient = new HttpRpcClient(pair.Value.FullNodeEndpoint,
                        new HttpClient() { BaseAddress = pair.Value.FullNodeEndpoint.Uri });
                    return new FullNodeProxy(rpcClient, "XCHPay");
                });
        }
    }

    public FullNodeProxy GetFullNodeRpcClient(PaymentMethodId pmi)
    {
        lock (this)
        {
            return _fullNodeRpcClients[pmi];
        }
    }

    public bool IsAvailable(PaymentMethodId pmi)
    {
        return Summaries.ContainsKey(pmi) && IsAvailable(Summaries[pmi]);
    }

    private static bool IsAvailable(ChiaLikeSummary summary)
    {
        return summary is { Synced: true, RpcAvailable: true };
    }

    public async Task<(string, decimal?)[]> GetBalances(PaymentMethodId pmi, IEnumerable<string> addresses)
    {
        var configuration = _chiaPluginConfiguration.ChiaConfigurationItems[pmi];
        var rpcClient = GetFullNodeRpcClient(pmi);
        var puzzleHashes = addresses.Select(ChiaAddressHelper.AddressToPuzzleHash);

        var addressBalances = new Dictionary<string, BigInteger>();

        var coinRecords = await rpcClient.GetCoinRecordsByPuzzleHashes(puzzleHashes, false, null, null);

        foreach (var coinRecord in coinRecords)
        {
            var address = ChiaAddressHelper.PuzzleHashToAddress(coinRecord.Coin.PuzzleHash);
            var amount = coinRecord.Coin.Amount;
            if (!addressBalances.TryAdd(address, amount))
            {
                addressBalances[address] += amount;
            }
        }

        List<(string, decimal?)> results = [];
        foreach (var address in addresses)
        {
            try
            {
                var amount = addressBalances[address];
                var divisor = BigInteger.Pow(10, configuration.Divisibility);
                var quotient = amount / divisor;
                var remainder = amount % divisor;
                var fractionalPart = (decimal)remainder / (decimal)divisor;
                results.Add((address, (decimal)quotient + fractionalPart));
            }
            catch (Exception)
            {
                results.Add((address, 0));
            }
        }

        return results.ToArray();
    }

    public async Task UpdateSummary(PaymentMethodId pmi)
    {
        if (!_fullNodeRpcClients.TryGetValue(pmi, out var fullNodeRpcClient)) return;

        var configuration = _chiaPluginConfiguration.ChiaConfigurationItems[pmi];
        var listenerState =
            await _settingsRepository.GetSettingAsync<ChiaListenerState>(ListenerStateSettingKey(configuration));
        if (listenerState == null) return;

        var summary = new ChiaLikeSummary();
        try
        {
            summary.LatestBlockScanned = listenerState.LastBlockHeight;

            var blockchainState = await fullNodeRpcClient.GetBlockchainState();
            if (blockchainState.Sync.Synced)
            {
                summary.LatestBlockOnNode = blockchainState.Peak!.Height;
                summary.HighestBlockOnChain = blockchainState.Peak!.Height;
                summary.Syncing = false;
            }
            else
            {
                summary.LatestBlockOnNode = blockchainState.Sync.SyncProgressHeight;
                summary.HighestBlockOnChain = blockchainState.Sync.SyncTipHeight;
                summary.Syncing = true;
            }

            summary.Synced = summary.HighestBlockOnChain - listenerState.LastBlockHeight < 10;

            summary.UpdatedAt = DateTime.UtcNow;
            summary.RpcAvailable = true;
        }
        catch
        {
            summary.RpcAvailable = false;
        }

        var changed = !Summaries.ContainsKey(pmi) || IsAvailable(pmi) != IsAvailable(summary);

        Summaries.AddOrReplace(pmi, summary);
        if (changed)
            _eventAggregator.Publish(new ChiaDaemonStateChanged { Summary = summary, PaymentMethodId = pmi });
    }

    public static string ListenerStateSettingKey(ChiaPluginConfigurationItem config)
    {
        return $"{config.GetSettingPrefix()}_LISTENER_STATE";
    }

    public class ChiaLikeSummary
    {
        public bool Synced { get; set; }
        public BigInteger LatestBlockOnNode { get; set; }
        public DateTime UpdatedAt { get; set; }
        public bool RpcAvailable { get; set; }
        public BigInteger HighestBlockOnChain { get; set; }
        public BigInteger LatestBlockScanned { get; set; }
        public bool Syncing { get; set; }
    }
}