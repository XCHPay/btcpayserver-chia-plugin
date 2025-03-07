using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Rating;
using BTCPayServer.Services.Rates;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Chia.Services;

public class ChiaRateProvider : IRateProvider
{
    private readonly HttpClient _httpClient;

    public ChiaRateProvider(HttpClient httpClient)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public RateSourceInfo RateSourceInfo =>  new RateSourceInfo("xchprice", "xchprice.info", "https://xchprice.info/api/coingecko/price");

    public async Task<PairRate[]> GetRatesAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync("https://xchprice.info/api/coingecko/price", cancellationToken);
        var jobj = await response.Content.ReadAsAsync<JObject>(cancellationToken);
        var value = jobj["price"].Value<decimal>();
        return new[] { new PairRate(new CurrencyPair("XCH", "USD"), new BidAsk(value)) };
    }
}