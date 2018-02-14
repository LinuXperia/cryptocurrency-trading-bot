﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using mleader.tradingbot.Data;
using mleader.tradingbot.Data.Cex;
using Newtonsoft.Json.Linq;
using OElite;
using StackExchange.Redis;

namespace mleader.tradingbot.Engine.Cex
{
    public class CexTradingEngine : ITradingEngine
    {
        public ExchangeApiConfig ApiConfig { get; set; }
        public string ReserveCurrency { get; set; }
        public Dictionary<string, decimal> MinimumCurrencyOrderAmount { get; set; }

        public List<ITradeHistory> LatestPublicSaleHistory { get; set; }
        public List<ITradeHistory> LatestPublicPurchaseHistory { get; set; }
        public List<IOrder> LatestAccountSaleHistory { get; set; }
        public List<IOrder> LatestAccountPurchaseHistory { get; set; }
        public List<IOrder> AccountOpenOrders { get; set; }

        public IOrder AccountNextBuyOpenOrder => AccountOpenOrders?.Where(item => item.Type == OrderType.Buy)
            .OrderByDescending(item => item.Price)
            .FirstOrDefault();

        public IOrder AccountNextSellOpenOrder => AccountOpenOrders?.Where(item => item.Type == OrderType.Sell)
            .OrderBy(item => item.Price).FirstOrDefault();

        public IOrder AccountLastBuyOpenOrder => AccountOpenOrders?.Where(item => item.Type == OrderType.Buy)
            .OrderBy(item => item.Price).FirstOrDefault();

        public IOrder AccountLastSellOpenOrder => AccountOpenOrders?.Where(item => item.Type == OrderType.Sell)
            .OrderByDescending(item => item.Price).FirstOrDefault();


        public ITradingStrategy TradingStrategy { get; }

        private Orderbook CurrentOrderbook { get; set; }
        private string OperatingExchangeCurrency { get; }
        private string OperatingTargetCurrency { get; }
        private List<CurrencyLimit> CurrencyLimits { get; set; }
        private CurrencyLimit ExchangeCurrencyLimit { get; set; }
        private CurrencyLimit TargetCurrencyLimit { get; set; }
        private decimal InitialBuyingCapInTargetCurrency { get; set; }
        private decimal InitialSellingCapInExchangeCurrency { get; set; }
        private int InitialBatchCycles { get; set; }

        private System.Timers.Timer RequestTimer { get; set; }

        private System.Timers.Timer FeecheckTimer { get; set; }
        private int ApiRequestcrruedAllowance { get; set; }
        private int ApiRequestCounts { get; set; }
        private AccountBalance AccountBalance { get; set; }
        private bool _isActive = true;
        private bool SleepNeeded = false;
        private bool AutoExecution = false;
        private int InputTimeout = 5000;
        public DateTime LastTimeBuyOrderCancellation { get; set; }
        public DateTime LastTimeSellOrderCancellation { get; set; }

        public CexTradingEngine(ExchangeApiConfig apiConfig, string exchangeCurrency, string targetCurrency,
            ITradingStrategy strategy)
        {
            ApiConfig = apiConfig;
            OperatingExchangeCurrency = exchangeCurrency;
            OperatingTargetCurrency = targetCurrency;
            TradingStrategy = strategy ?? new TradingStrategy
            {
                MinutesOfAccountHistoryOrderForPurchaseDecision = 60,
                MinutesOfAccountHistoryOrderForSellDecision = 60,
                MinutesOfPublicHistoryOrderForPurchaseDecision = 120,
                MinutesOfPublicHistoryOrderForSellDecision = 120,
                MinimumReservePercentageAfterInitInTargetCurrency = 0.1m,
                MinimumReservePercentageAfterInitInExchangeCurrency = 0.1m,
                OrderCapPercentageAfterInit = 0.6m,
                OrderCapPercentageOnInit = 0.25m,
                AutoDecisionExecution = true,
                MarketChangeSensitivityRatio = 0.015m
            };

            AutoExecution = TradingStrategy.AutoDecisionExecution;

            Rest = new Rest("https://cex.io/api/",
                new RestConfig
                {
                    OperationMode = RestMode.HTTPRestClient,
                    UseRestConvertForCollectionSerialization = false
                },
                apiConfig?.Logger);


//            Console.WriteLine("Init Cex Trading Engine");
            RequestTimer = new System.Timers.Timer(1000) {Enabled = true, AutoReset = true};
            RequestTimer.Elapsed += (sender, args) =>
            {
                ApiRequestCounts++;

                if (ApiRequestCounts > ApiRequestcrruedAllowance)
                {
                    if (ApiRequestCounts - ApiRequestcrruedAllowance > 0)
                        SleepNeeded = true;

                    ApiRequestCounts = ApiRequestCounts - ApiRequestcrruedAllowance;
                    ApiRequestcrruedAllowance = 0;
                }
                else
                {
                    ApiRequestCounts = 0;
                    ApiRequestcrruedAllowance = ApiRequestcrruedAllowance - ApiRequestCounts;
                }
            };
            FeecheckTimer = new System.Timers.Timer(1000 * 60 * 3) {Enabled = true, AutoReset = true};
            FeecheckTimer.Elapsed += (sender, args) => RefreshAccountFeesAsync().Wait();

            FirstBatchPreparationAsync().Wait();
        }

        public async Task FirstBatchPreparationAsync()
        {
            await RefreshCexCurrencyLimitsAsync();
            if (!await InitBaseDataAsync())
            {
                Console.WriteLine("Init Data Failed. Program Terminated.");
                return;
            }

            var totalExchangeCurrencyBalance =
                (AccountBalance?.CurrencyBalances?.Where(item => item.Key == OperatingExchangeCurrency)
                    .Select(c => c.Value?.Total)
                    .FirstOrDefault()).GetValueOrDefault();
            var totalTargetCurrencyBalance = (AccountBalance?.CurrencyBalances
                ?.Where(item => item.Key == OperatingTargetCurrency)
                .Select(c => c.Value?.Total)
                .FirstOrDefault()).GetValueOrDefault();

            ExchangeCurrencyLimit =
                CurrencyLimits?.FirstOrDefault(item => item.ExchangeCurrency == OperatingExchangeCurrency);
            TargetCurrencyLimit =
                CurrencyLimits?.FirstOrDefault(item => item.ExchangeCurrency == OperatingTargetCurrency);

            InitialBuyingCapInTargetCurrency =
                (totalTargetCurrencyBalance + totalExchangeCurrencyBalance * PublicLastPurchasePrice) *
                TradingStrategy.OrderCapPercentageOnInit;
            InitialSellingCapInExchangeCurrency =
                (totalExchangeCurrencyBalance + totalTargetCurrencyBalance / PublicLastSellPrice) *
                TradingStrategy.OrderCapPercentageOnInit;

            if (ExchangeCurrencyLimit?.MaximumExchangeAmount * PublicLastPurchasePrice <
                InitialBuyingCapInTargetCurrency)
                InitialBuyingCapInTargetCurrency = (ExchangeCurrencyLimit.MaximumExchangeAmount == null
                                                       ? decimal.MaxValue
                                                       : ExchangeCurrencyLimit.MaximumExchangeAmount.GetValueOrDefault()
                                                   ) * PublicLastPurchasePrice;

            if (ExchangeCurrencyLimit?.MinimumExchangeAmount * PublicLastPurchasePrice >=
                InitialBuyingCapInTargetCurrency)
                InitialBuyingCapInTargetCurrency = (ExchangeCurrencyLimit.MinimumExchangeAmount == null
                                                       ? 0
                                                       : ExchangeCurrencyLimit.MinimumExchangeAmount.GetValueOrDefault()
                                                   ) * PublicLastPurchasePrice;

            if (TargetCurrencyLimit?.MaximumExchangeAmount < InitialSellingCapInExchangeCurrency)
                InitialSellingCapInExchangeCurrency = (TargetCurrencyLimit.MaximumExchangeAmount == null
                                                          ? decimal.MaxValue
                                                          : TargetCurrencyLimit.MaximumExchangeAmount
                                                              .GetValueOrDefault()) / PublicLastSellPrice;
            if (TargetCurrencyLimit?.MinimumExchangeAmount >= InitialSellingCapInExchangeCurrency)
                InitialSellingCapInExchangeCurrency = (TargetCurrencyLimit.MinimumExchangeAmount == null
                                                          ? 0
                                                          : TargetCurrencyLimit.MinimumExchangeAmount
                                                              .GetValueOrDefault()) / PublicLastSellPrice;

            if (InitialBuyingCapInTargetCurrency <= 0)
                InitialBuyingCapInTargetCurrency = totalTargetCurrencyBalance / PublicLastPurchasePrice;
            if (InitialSellingCapInExchangeCurrency <= 0)
                InitialSellingCapInExchangeCurrency = totalExchangeCurrencyBalance;


            InitialBatchCycles = (int) Math.Max(
                InitialBuyingCapInTargetCurrency > 0
                    ? totalTargetCurrencyBalance / InitialBuyingCapInTargetCurrency
                    : 0,
                InitialSellingCapInExchangeCurrency > 0
                    ? totalExchangeCurrencyBalance / InitialSellingCapInExchangeCurrency
                    : 0);
        }

        public Task StartAsync()
        {
            SendWebhookMessage("*Trading Engine Started* :smile:");
            while (_isActive)
            {
                if (SleepNeeded)
                {
                    SleepNeeded = false;
                    var count = ApiRequestCounts - ApiRequestcrruedAllowance;
                    count = count - ApiRequestcrruedAllowance;
                    ApiRequestCounts = 0;

                    ApiRequestcrruedAllowance = 0;
                    if (count > 0)
                    {
                        count = count > 5 ? 5 : count;
                        Thread.Sleep(count * 1000);
                    }
                }

                try
                {
                    MarkeDecisionsAsync().Wait();
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                }

                Thread.Sleep(1000);
                ApiRequestcrruedAllowance++;
            }

            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            _isActive = false;
            SendWebhookMessage("*Trading Engine Stopped* :end:");
            Thread.CurrentThread.Abort();
            return Task.CompletedTask;
        }

        #region Price Calculations        

        public async Task<bool> MarkeDecisionsAsync()
        {
            var error = !(await InitBaseDataAsync());
            try
            {
                await DrawDecisionUIsAsync();
            }
            catch (Exception ex)
            {
                Console.Clear();
            }
            finally
            {
                if (InitialBatchCycles > 0)
                    InitialBatchCycles--;
            }

            return !error;
        }

        private async Task<bool> InitBaseDataAsync()
        {
            #region Get Historical Trade Histories

            var latestThousandTradeHistories =
                await Rest.GetAsync<List<TradeHistory>>(
                    $"trade_history/{OperatingExchangeCurrency}/{OperatingTargetCurrency}");
            ApiRequestCounts++;
            var error = false;
            if (latestThousandTradeHistories?.Count > 0)
            {
                var lastXMinutes =
                    latestThousandTradeHistories.Where(item => item.Timestamp >= DateTime.UtcNow.AddMinutes(-1 * (
                                                                                                                TradingStrategy
                                                                                                                    .MinutesOfPublicHistoryOrderForPurchaseDecision >
                                                                                                                TradingStrategy
                                                                                                                    .MinutesOfPublicHistoryOrderForSellDecision
                                                                                                                    ? TradingStrategy
                                                                                                                        .MinutesOfPublicHistoryOrderForPurchaseDecision
                                                                                                                    : TradingStrategy
                                                                                                                        .MinutesOfPublicHistoryOrderForSellDecision
                                                                                                            )));


                LatestPublicPurchaseHistory = lastXMinutes
                    .Where(item => item.OrderType == OrderType.Buy && item.Timestamp >=
                                   DateTime.UtcNow.AddMinutes(
                                       -1 * TradingStrategy.MinutesOfPublicHistoryOrderForPurchaseDecision))
                    .Select(item => item as ITradeHistory).ToList();
                LatestPublicSaleHistory = lastXMinutes.Where(item =>
                        item.OrderType == OrderType.Sell && item.Timestamp >=
                        DateTime.UtcNow.AddMinutes(-1 * TradingStrategy.MinutesOfPublicHistoryOrderForSellDecision))
                    .Select(item => item as ITradeHistory).ToList();
            }
            else
            {
                LatestPublicPurchaseHistory = new List<ITradeHistory>();
                LatestPublicSaleHistory = new List<ITradeHistory>();
                error = true;
            }

            if (error) return !error;

//            Console.WriteLine(
//                $"Cex Exchange order executions in last " +
//                $"{(TradingStrategy.HoursOfPublicHistoryOrderForPurchaseDecision > TradingStrategy.HoursOfPublicHistoryOrderForSellDecision ? TradingStrategy.HoursOfPublicHistoryOrderForPurchaseDecision : TradingStrategy.HoursOfPublicHistoryOrderForSellDecision)} hours: " +
//                $"\t BUY: {LatestPublicPurchaseHistory?.Count}\t SELL: {LatestPublicSaleHistory?.Count}");

            #endregion

            #region Get Orderbook

            await RefreshPublicOrderbookAsync();

            #endregion


            #region Get Account Trade Histories

            if (!await RefreshAccountTradeHistory()) return false;

            #endregion

            #region Get Account Trading Fees

            await RefreshAccountFeesAsync();

            #endregion

            #region Get Account Balancees

            AccountBalance = await GetAccountBalanceAsync();
            if (AccountBalance == null)
            {
                Console.WriteLine("\n [Unable to receive account balance] - Carrying 3 seconds sleep...");

                Thread.Sleep(3000);
                ApiRequestcrruedAllowance = 0;
                ApiRequestCounts = 0;
                return false;
            }

            #endregion

            #region Get Account Open Orders

            AccountOpenOrders = await GetOpenOrdersAsync();
            if (AccountOpenOrders == null)
            {
                Console.WriteLine("\n [Unable to receive account open orders] - Carrying 3 seconds sleep...");

                Thread.Sleep(3000);
                ApiRequestcrruedAllowance = 0;
                ApiRequestCounts = 0;
                return false;
            }

            #endregion

            return !error;
        }

        private async Task<bool> RefreshAccountTradeHistory()
        {
            bool error;
            var nonce = GetNonce();
            var latestAccountTradeHistories = await Rest.PostAsync<List<FullOrder>>(
                $"archived_orders/{OperatingExchangeCurrency}/{OperatingTargetCurrency}", new
                {
                    Key = ApiConfig.ApiKey,
                    Signature = GetApiSignature(nonce),
                    Nonce = nonce,
                    DateFrom = (DateTime.UtcNow.AddMinutes(
                                    -1 * (TradingStrategy.MinutesOfAccountHistoryOrderForPurchaseDecision >
                                          TradingStrategy.MinutesOfAccountHistoryOrderForSellDecision
                                        ? TradingStrategy.MinutesOfAccountHistoryOrderForPurchaseDecision
                                        : TradingStrategy.MinutesOfAccountHistoryOrderForSellDecision)) -
                                new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds,
                    DateTo = (DateTime.UtcNow.AddMinutes(
                                  (TradingStrategy.MinutesOfAccountHistoryOrderForPurchaseDecision >
                                   TradingStrategy.MinutesOfAccountHistoryOrderForSellDecision
                                      ? TradingStrategy.MinutesOfAccountHistoryOrderForPurchaseDecision
                                      : TradingStrategy.MinutesOfAccountHistoryOrderForSellDecision)) -
                              new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds,
                });
            ApiRequestCounts++;

            if (latestAccountTradeHistories == null)
            {
                Console.WriteLine(await Rest.PostAsync<string>(
                    $"archived_orders/{OperatingExchangeCurrency}/{OperatingTargetCurrency}", new
                    {
                        Key = ApiConfig.ApiKey,
                        Signature = GetApiSignature(nonce),
                        Nonce = nonce,
                        DateFrom = (DateTime.UtcNow.AddMinutes(
                                        -1 * (TradingStrategy.MinutesOfAccountHistoryOrderForPurchaseDecision >
                                              TradingStrategy.MinutesOfAccountHistoryOrderForSellDecision
                                            ? TradingStrategy.MinutesOfAccountHistoryOrderForPurchaseDecision
                                            : TradingStrategy.MinutesOfAccountHistoryOrderForSellDecision)) -
                                    new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds,
                        DateTo = (DateTime.UtcNow.AddMinutes(
                                      (TradingStrategy.MinutesOfAccountHistoryOrderForPurchaseDecision >
                                       TradingStrategy.MinutesOfAccountHistoryOrderForSellDecision
                                          ? TradingStrategy.MinutesOfAccountHistoryOrderForPurchaseDecision
                                          : TradingStrategy.MinutesOfAccountHistoryOrderForSellDecision)) -
                                  new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds,
                    }));

                Console.WriteLine("\n [Unable to receive account records] - Carrying 3 seconds sleep...");

                Thread.Sleep(3000);
                ApiRequestcrruedAllowance = 0;
                ApiRequestCounts = 0;
                return false;
            }

            if (latestAccountTradeHistories?.Count > 0)
            {
                LatestAccountPurchaseHistory = latestAccountTradeHistories
                    .Where(item => item.Type == OrderType.Buy && item.Timestamp >=
                                   DateTime.UtcNow.AddMinutes(
                                       -1 * TradingStrategy.MinutesOfAccountHistoryOrderForPurchaseDecision))
                    .Select(item => item as IOrder).ToList();
                LatestAccountSaleHistory = latestAccountTradeHistories
                    .Where(item => item.Type == OrderType.Sell && item.Timestamp >=
                                   DateTime.UtcNow.AddMinutes(
                                       -1 * TradingStrategy.MinutesOfAccountHistoryOrderForSellDecision))
                    .Select(item => item as IOrder).ToList();
            }
            else
            {
                LatestAccountSaleHistory = new List<IOrder>();
                LatestAccountPurchaseHistory = new List<IOrder>();
                error = true;
            }

//            Console.WriteLine(
//                $"Account orders executions in last " +
//                $"{(TradingStrategy.HoursOfAccountHistoryOrderForPurchaseDecision > TradingStrategy.HoursOfAccountHistoryOrderForSellDecision ? TradingStrategy.HoursOfAccountHistoryOrderForPurchaseDecision : TradingStrategy.HoursOfAccountHistoryOrderForSellDecision)} hours: " +
//                $"\t BUY: {LatestAccountPurchaseHistory?.Count}\t SELL: {LatestAccountSaleHistory?.Count}");
            return true;
        }

        private async Task RefreshPublicOrderbookAsync()
        {
            CurrentOrderbook =
                await Rest.GetAsync<Orderbook>($"order_book/{OperatingExchangeCurrency}/{OperatingTargetCurrency}/");
            ApiRequestCounts++;

            if (CurrentOrderbook == null)
            {
                Console.WriteLine(
                    await Rest.GetAsync<string>($"order_book/{OperatingExchangeCurrency}/{OperatingTargetCurrency}/"));

                Console.WriteLine("\n [Unable to receive public orderbook] - Carrying 3 seconds sleep...");

                Thread.Sleep(3000);
                ApiRequestcrruedAllowance = 0;
                ApiRequestCounts = 0;
            }
        }

        private async Task RefreshCexCurrencyLimitsAsync()
        {
            var result = await Rest.GetAsync<JObject>("currency_limits");
            ApiRequestCounts++;
            var limits = result?["data"]?["pairs"]?.Children().Select(item => item.ToObject<CurrencyLimit>());

            CurrencyLimits = limits?.ToList() ?? new List<CurrencyLimit>();
        }

        private async Task RefreshAccountFeesAsync()
        {
            var nonce = GetNonce();
            var myFees = await Rest.PostAsync<JObject>("get_myfee", new
            {
                key = ApiConfig.ApiKey,
                signature = GetApiSignature(nonce),
                nonce
            });
            ApiRequestCounts++;
            SellingFeeInPercentage = (myFees?.GetValue("data")
                                         ?.Value<JToken>($"{OperatingExchangeCurrency}:{OperatingTargetCurrency}")
                                         ?.Value<decimal>("sell"))
                                     .GetValueOrDefault() / 100;
            SellingFeeInAmount = 0;
            BuyingFeeInPercentage = (myFees?.GetValue("data")
                                        ?.Value<JToken>($"{OperatingExchangeCurrency}:{OperatingTargetCurrency}")
                                        ?.Value<decimal>("buy"))
                                    .GetValueOrDefault() / 100;
            BuyingFeeInAmount = 0;
        }

        public Task<decimal> GetSellingPriceInPrincipleAsync() => Task.FromResult(Math.Ceiling(ProposedSellingPrice *
                                                                                               (1 +
                                                                                                BuyingFeeInPercentage) +
                                                                                               BuyingFeeInAmount));

        public Task<decimal> GetPurchasePriceInPrincipleAsync() => Task.FromResult(Math.Floor(ProposedPurchasePrice *
                                                                                              (1 -
                                                                                               BuyingFeeInPercentage) -
                                                                                              BuyingFeeInAmount));


        public async Task<AccountBalance> GetAccountBalanceAsync()
        {
            var nonce = GetNonce();
            AccountBalance = (await Rest.PostAsync<CexAccountBalance>("balance/", new
            {
                key = ApiConfig.ApiKey,
                signature = GetApiSignature(nonce),
                nonce
            }))?.ToAccountBalance();
            ApiRequestCounts++;
            return AccountBalance;
        }

        public AccountBalanceItem ExchangeCurrencyBalance =>
            AccountBalance?.CurrencyBalances?.Where(item => item.Key == OperatingExchangeCurrency)
                .Select(item => item.Value).FirstOrDefault();

        public AccountBalanceItem TargetCurrencyBalance => AccountBalance?.CurrencyBalances
            ?.Where(item => item.Key == OperatingTargetCurrency)
            .Select(item => item.Value).FirstOrDefault();

        private async Task DrawDecisionUIsAsync()
        {
            #region Validate and Calculate Selling/Buying Prices and Trade Amount

            var sellingPriceInPrinciple = await GetSellingPriceInPrincipleAsync();
            var buyingPriceInPrinciple = await GetPurchasePriceInPrincipleAsync();


            bool buyingAmountAvailable = true,
                sellingAmountAvailable = true,
                finalPortfolioValueDecreasedWhenBuying,
                finalPortfolioValueDecreasedWhenSelling;
            decimal buyingAmountInPrinciple, sellingAmountInPrinciple;
            if (InitialBatchCycles > 0)
            {
                buyingAmountInPrinciple = TradingStrategy.OrderCapPercentageOnInit *
                                          GetPortfolioValueInExchangeCurrency(
                                              ExchangeCurrencyBalance.Available + TargetCurrencyBalance.InOrders /
                                              PublicLastPurchasePrice,
                                              TargetCurrencyBalance.Available + ExchangeCurrencyBalance.InOrders *
                                              PublicLastSellPrice, buyingPriceInPrinciple) *
                                          (1 - BuyingFeeInPercentage) - BuyingFeeInAmount;
                sellingAmountInPrinciple = TradingStrategy.OrderCapPercentageOnInit *
                                           GetPortfolioValueInExchangeCurrency(
                                               ExchangeCurrencyBalance.Available + TargetCurrencyBalance.InOrders /
                                               PublicLastPurchasePrice,
                                               TargetCurrencyBalance.Available + ExchangeCurrencyBalance.InOrders *
                                               PublicLastSellPrice, sellingPriceInPrinciple) *
                                           (1 - SellingFeeInPercentage) - SellingFeeInAmount;

                buyingAmountInPrinciple = buyingAmountInPrinciple >
                                          InitialBuyingCapInTargetCurrency / PublicLastPurchasePrice
                    ? InitialBuyingCapInTargetCurrency / PublicLastPurchasePrice
                    : buyingAmountInPrinciple;
                sellingAmountInPrinciple = sellingAmountInPrinciple > InitialSellingCapInExchangeCurrency
                    ? InitialSellingCapInExchangeCurrency
                    : sellingAmountInPrinciple;
            }
            else
            {
                buyingAmountInPrinciple =
                    TradingStrategy.OrderCapPercentageAfterInit * GetPortfolioValueInExchangeCurrency(
                        ExchangeCurrencyBalance.Available,
                        TargetCurrencyBalance.Available, buyingPriceInPrinciple) *
                    (1 - BuyingFeeInPercentage) - BuyingFeeInAmount;

                sellingAmountInPrinciple = TradingStrategy.OrderCapPercentageAfterInit *
                                           GetPortfolioValueInExchangeCurrency(
                                               ExchangeCurrencyBalance.Available,
                                               TargetCurrencyBalance.Available, sellingPriceInPrinciple) *
                                           (1 - SellingFeeInPercentage) - SellingFeeInAmount;
            }

            var exchangeCurrencyLimit = ExchangeCurrencyLimit?.MinimumExchangeAmount > 0
                ? ExchangeCurrencyLimit.MinimumExchangeAmount
                : 0;
            var targetCurrencyLimit = TargetCurrencyLimit?.MinimumExchangeAmount > 0
                ? TargetCurrencyLimit.MinimumExchangeAmount
                : 0;

            if (exchangeCurrencyLimit > buyingAmountInPrinciple)
                buyingAmountInPrinciple = exchangeCurrencyLimit.GetValueOrDefault();
            if (exchangeCurrencyLimit > sellingAmountInPrinciple)
                sellingAmountInPrinciple = exchangeCurrencyLimit.GetValueOrDefault();

            buyingAmountAvailable = buyingAmountInPrinciple > 0 &&
                                    buyingAmountInPrinciple * buyingPriceInPrinciple <=
                                    TargetCurrencyBalance?.Available;
            sellingAmountAvailable = sellingAmountInPrinciple > 0 &&
                                     sellingAmountInPrinciple <= ExchangeCurrencyBalance?.Available;

            buyingAmountInPrinciple =
                buyingAmountAvailable || (TargetCurrencyBalance?.Available).GetValueOrDefault() <= 0
                    ? buyingAmountInPrinciple
                    : (TargetCurrencyBalance?.Available / PublicLastPurchasePrice).GetValueOrDefault() *
                      (1 - BuyingFeeInPercentage) - BuyingFeeInAmount;
            sellingAmountInPrinciple =
                sellingAmountAvailable || (ExchangeCurrencyBalance?.Available).GetValueOrDefault() <= 0
                    ? sellingAmountInPrinciple
                    : (ExchangeCurrencyBalance?.Available).GetValueOrDefault() *
                      (1 - SellingFeeInPercentage) - SellingFeeInAmount;


            var finalPortfolioValueWhenBuying =
                Math.Round(
                    ((ExchangeCurrencyBalance?.Total + buyingAmountInPrinciple +
                      TargetCurrencyBalance?.Total / buyingPriceInPrinciple) * PublicLastSellPrice)
                    .GetValueOrDefault(), 2);
            var originalPortfolioValueWhenBuying =
                Math.Round(
                    (ExchangeCurrencyBalance?.Total * PublicLastSellPrice + TargetCurrencyBalance?.Total)
                    .GetValueOrDefault(), 2);
            var finalPortfolioValueWhenSelling =
                Math.Round(
                    (ExchangeCurrencyBalance?.Total * sellingPriceInPrinciple + TargetCurrencyBalance?.Total)
                    .GetValueOrDefault(),
                    2);
            var originalPortfolioValueWhenSelling =
                Math.Round(
                    (ExchangeCurrencyBalance?.Total * PublicLastPurchasePrice + TargetCurrencyBalance?.Total)
                    .GetValueOrDefault(), 2);

            finalPortfolioValueDecreasedWhenBuying = finalPortfolioValueWhenBuying < originalPortfolioValueWhenBuying;
            finalPortfolioValueDecreasedWhenSelling =
                finalPortfolioValueWhenSelling < originalPortfolioValueWhenSelling;

            if (!IsBuyingReserveRequirementMatched(buyingAmountInPrinciple, buyingPriceInPrinciple))
            {
                //find how much we can buy]
                var maxAmount =
                    GetMaximumBuyableAmountBasedOnReserveRatio(buyingPriceInPrinciple);
                if (maxAmount < buyingAmountInPrinciple)
                    buyingAmountInPrinciple = maxAmount;
            }

            if (!IsSellingReserveRequirementMatched(sellingAmountInPrinciple, sellingPriceInPrinciple))
            {
                var maxAmount =
                    GetMaximumSellableAmountBasedOnReserveRatio(sellingPriceInPrinciple);
                if (maxAmount < sellingAmountInPrinciple)
                    sellingAmountInPrinciple = maxAmount;
            }

            buyingAmountInPrinciple = Math.Truncate(buyingAmountInPrinciple * 100000000) / 100000000;
            sellingAmountInPrinciple = Math.Truncate(sellingAmountInPrinciple * 100000000) / 100000000;

            buyingAmountAvailable = buyingAmountInPrinciple > 0 &&
                                    buyingAmountInPrinciple * buyingPriceInPrinciple <=
                                    TargetCurrencyBalance?.Available &&
                                    buyingAmountInPrinciple >= exchangeCurrencyLimit
                                    && buyingAmountInPrinciple * buyingPriceInPrinciple >= targetCurrencyLimit;

            sellingAmountAvailable = sellingAmountInPrinciple > 0 &&
                                     sellingAmountInPrinciple <= ExchangeCurrencyBalance?.Available &&
                                     sellingAmountInPrinciple >= exchangeCurrencyLimit &&
                                     sellingAmountInPrinciple * sellingPriceInPrinciple >= targetCurrencyLimit;


//            var IsBullMarketContinuable =
//                PublicWeightedAverageBestSellPrice * (1 + AverageTradingChangeRatio) > sellingPriceInPrinciple
//                ||
//                PublicWeightedAverageBestSellPrice > PublicWeightedAverageBestPurchasePrice
//                ||
//                Math.Abs(PublicWeightedAverageBestSellPrice - PublicWeightedAverageBestSellPrice) /
//                PublicWeightedAverageBestSellPrice > TradingStrategy.MarketChangeSensitivityRatio ||
//                Math.Abs(PublicWeightedAverageLowPurchasePrice * (1 + AverageTradingChangeRatio) -
//                         PublicLastPurchasePrice) /
//                PublicLastPurchasePrice > TradingStrategy.MarketChangeSensitivityRatio;
//            var IsBearMarketContinuable =
//                Math.Abs(PublicWeightedAverageBestPurchasePrice * (1 - AverageTradingChangeRatio) -
//                         PublicLastPurchasePrice) /
//                PublicLastPurchasePrice > TradingStrategy.MarketChangeSensitivityRatio ||
//                Math.Abs(PublicWeightedAverageLowSellPrice * (1 - AverageTradingChangeRatio) - PublicLastSellPrice) /
//                PublicLastSellPrice > TradingStrategy.MarketChangeSensitivityRatio;

            #endregion

            #region Draw the Graph

            var isBullMarket = IsBullMarket;
            var isBullMarketContinuable = IsBullMarketContinuable;
            var isBearMarketContinuable = IsBearMarketContinuable;

            Console.WriteLine("");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.Blue;
            //Console.BackgroundColor = ConsoleColor.White;
            Console.WriteLine("\n\t_____________________________________________________________________");
            Console.WriteLine("\n\t                         Account Balance                            ");
            Console.WriteLine("\t                       +++++++++++++++++++                          ");
            Console.ForegroundColor = ConsoleColor.DarkMagenta;
            Console.WriteLine(
                $"\n\t {ExchangeCurrencyBalance?.Currency}: {ExchangeCurrencyBalance?.Available}{(ExchangeCurrencyBalance?.InOrders > 0 ? " \t\t\t" + ExchangeCurrencyBalance?.InOrders + "\tIn Orders" : "")}" +
                $"\n\t {TargetCurrencyBalance?.Currency}: {Math.Round((TargetCurrencyBalance?.Available).GetValueOrDefault(), 2)}{(TargetCurrencyBalance?.InOrders > 0 ? " \t\t\t" + Math.Round((TargetCurrencyBalance?.InOrders).GetValueOrDefault(), 2) + "\tIn Orders" : "")}\t\t\t\t");
            Console.WriteLine($"\n\t Execution Time: {DateTime.Now}");
            Console.ForegroundColor = ConsoleColor.Blue;

            Console.WriteLine("\n\t===================Buy / Sell Price Recommendation===================\n");
            Console.WriteLine($"\t Buying\t\t\t\t\t  Selling  \t\t\t\t");
            Console.WriteLine($"\t ========\t\t\t\t  ========\t\t\t\t");
            Console.WriteLine($"\t CEX Latest:\t{PublicLastPurchasePrice}\t\t\t  {PublicLastSellPrice}\t\t\t\t");
            Console.WriteLine($"\t Last Executed:\t{AccountLastPurchasePrice}\t\t\t  {AccountLastSellPrice}\t\t\t\t");
            Console.WriteLine(
                $"\t Next Order:\t{(AccountNextBuyOpenOrder == null ? "N/A" : AccountNextBuyOpenOrder.Amount.ToString(CultureInfo.InvariantCulture) + AccountNextBuyOpenOrder.ExchangeCurrency)}{(AccountNextBuyOpenOrder != null ? "@" + AccountNextBuyOpenOrder.Price : "")}\t\t  " +
                $"{(AccountNextSellOpenOrder == null ? "N/A  " : AccountNextSellOpenOrder.Amount + AccountNextSellOpenOrder.ExchangeCurrency)}{(AccountNextSellOpenOrder != null ? "@" + AccountNextSellOpenOrder.Price : "")}");
            Console.WriteLine(
                $"\t Last Order:\t{(AccountLastBuyOpenOrder == null ? "N/A" : AccountLastBuyOpenOrder.Amount.ToString(CultureInfo.InvariantCulture) + AccountLastBuyOpenOrder.ExchangeCurrency)}{(AccountLastBuyOpenOrder != null ? "@" + AccountLastBuyOpenOrder.Price : "")}\t\t  " +
                $"{(AccountLastSellOpenOrder == null ? "N/A  " : AccountLastSellOpenOrder.Amount + AccountNextSellOpenOrder.ExchangeCurrency)}{(AccountLastSellOpenOrder != null ? "@" + AccountLastSellOpenOrder.Price : "")}");
            Console.Write("\t Market Status:\t ");
            if (isBullMarket)
            {
                Console.ForegroundColor = ConsoleColor.DarkGreen;
                Console.Write("Bull Market \t\t  ");
                if (!isBullMarketContinuable)
                {
                    Console.ForegroundColor = ConsoleColor.DarkRed;
                }

                Console.Write($"{(isBullMarketContinuable ? "Up" : "Down")}\t\t\t\t\n");
                Console.ForegroundColor = ConsoleColor.Blue;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.Write("Bear Market \t\t  ");
                if (!isBearMarketContinuable)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGreen;
                }

                Console.Write($"{(isBearMarketContinuable ? "Down" : "Up")}\t\t\t\t\n");
                Console.ForegroundColor = ConsoleColor.Blue;
            }

            Console.WriteLine("\n\t_____________________________________________________________________\n");
            Console.ForegroundColor = ConsoleColor.DarkGreen;
            Console.WriteLine("\n\t Buying Decision: \t\t\t  Selling Decision:");

            Console.WriteLine(
                $"\t Price:\t{buyingPriceInPrinciple} {TargetCurrencyBalance?.Currency}\t\t\t  {sellingPriceInPrinciple} {TargetCurrencyBalance?.Currency}\t\t\t\t");
            Console.Write($"\t ");

            #region Buying Decision

            Console.ForegroundColor = ConsoleColor.White;
            if (buyingAmountAvailable &&
                IsBuyingReserveRequirementMatched(buyingAmountInPrinciple, buyingPriceInPrinciple))
            {
                if (!finalPortfolioValueDecreasedWhenBuying)
                {
                    if (!isBullMarket || isBullMarket && isBullMarketContinuable)
                    {
                        Console.BackgroundColor = ConsoleColor.DarkGreen;
                        Console.Write(
                            $"BUY {buyingAmountInPrinciple} {ExchangeCurrencyBalance?.Currency} ({buyingAmountInPrinciple * buyingPriceInPrinciple:N2} {TargetCurrencyBalance?.Currency})");
                    }
                    else
                    {
                        Console.BackgroundColor = ConsoleColor.DarkGray;
                        Console.Write("Better Hold");
                    }
                }
                else
                {
                    Console.BackgroundColor = ConsoleColor.DarkRed;
                    Console.Write("Depreciation  ");
                }
            }
            else
            {
                Console.BackgroundColor = ConsoleColor.DarkRed;
                Console.Write(
                    $"{(!IsBuyingReserveRequirementMatched(buyingAmountInPrinciple, buyingPriceInPrinciple) || buyingAmountInPrinciple == GetMaximumBuyableAmountBasedOnReserveRatio(buyingPriceInPrinciple) ? $"Limited Reserve - {TargetCurrencyBalance.Available * TradingStrategy.MinimumReservePercentageAfterInitInTargetCurrency:N2} {OperatingTargetCurrency}" : buyingAmountInPrinciple > 0 ? $"Low Fund - Need {(buyingAmountInPrinciple > exchangeCurrencyLimit ? buyingAmountInPrinciple : exchangeCurrencyLimit) * buyingPriceInPrinciple:N2} {TargetCurrencyBalance.Currency}" : "Low Fund")}");
            }

            Console.ResetColor();
            Console.Write("\t\t  ");

            #endregion

            #region Selling Decision

            Console.ForegroundColor = ConsoleColor.White;
            if (sellingAmountAvailable &&
                IsSellingReserveRequirementMatched(sellingAmountInPrinciple, sellingPriceInPrinciple))
            {
                if (!finalPortfolioValueDecreasedWhenSelling)
                {
                    if (
                        (isBullMarket || !isBullMarket && !isBearMarketContinuable))
                    {
                        Console.BackgroundColor = ConsoleColor.DarkGreen;
                        Console.Write(
                            $"SELL {sellingAmountInPrinciple} {ExchangeCurrencyBalance?.Currency} ({Math.Round(sellingAmountInPrinciple * sellingPriceInPrinciple, 2)} {TargetCurrencyBalance?.Currency})");
                    }
                    else
                    {
                        Console.BackgroundColor = ConsoleColor.DarkGray;
                        Console.Write("Better Hold");
                    }
                }
                else
                {
                    Console.BackgroundColor = ConsoleColor.DarkRed;
                    Console.Write("Depreciation");
                }
            }
            else
            {
                Console.BackgroundColor = ConsoleColor.DarkRed;
                Console.Write(
                    $"{(!IsSellingReserveRequirementMatched(sellingAmountInPrinciple, sellingPriceInPrinciple) || sellingAmountInPrinciple == GetMaximumSellableAmountBasedOnReserveRatio(sellingPriceInPrinciple) ? $"Limited Reserve - {ExchangeCurrencyBalance.Available * TradingStrategy.MinimumReservePercentageAfterInitInExchangeCurrency:N4} {OperatingExchangeCurrency}" : sellingAmountInPrinciple > 0 ? $"Low Fund - Need {(sellingAmountInPrinciple > exchangeCurrencyLimit ? sellingAmountInPrinciple : exchangeCurrencyLimit):N4} {ExchangeCurrencyBalance.Currency}" : "Low Fund")}");
            }

            Console.ResetColor();
            Console.Write("\t\t\n");

            #endregion

            Console.ResetColor();
            Console.WriteLine("\n\n\t Portfolio Estimates (A.I.):");
            Console.WriteLine(
                $"\t Current:\t{originalPortfolioValueWhenBuying} {TargetCurrencyBalance?.Currency}\t\t  {originalPortfolioValueWhenSelling} {TargetCurrencyBalance?.Currency}\t\t\t\t");
            Console.WriteLine(
                $"\t After  :\t{finalPortfolioValueWhenBuying} {TargetCurrencyBalance?.Currency}\t\t  {finalPortfolioValueWhenSelling} {TargetCurrencyBalance?.Currency}\t\t\t\t");
            Console.WriteLine(
                $"\t Difference:\t{finalPortfolioValueWhenBuying - originalPortfolioValueWhenBuying} {TargetCurrencyBalance?.Currency}\t\t  {finalPortfolioValueWhenSelling - originalPortfolioValueWhenSelling} {TargetCurrencyBalance?.Currency} ");
            Console.ForegroundColor = ConsoleColor.DarkBlue;
            Console.WriteLine(
                $"\n\t Stop Line:\t{TradingStrategy.StopLine} {TargetCurrencyBalance.Currency}\t\t  ");

            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine("\n\t===============================****==================================\n");
            Console.ResetColor();
            Console.WriteLine("");

            #endregion

            #region Execute Buy Order

            if (buyingAmountAvailable &&
                IsBuyingReserveRequirementMatched(buyingAmountInPrinciple, buyingPriceInPrinciple) &&
                !finalPortfolioValueDecreasedWhenBuying &&
                finalPortfolioValueWhenBuying >= TradingStrategy.StopLine && !IsBearMarketContinuable)
            {
                if (buyingPriceInPrinciple > sellingPriceInPrinciple)
                {
                    Console.BackgroundColor = ConsoleColor.Black;
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine(
                        $"WARNING - Buying price ({buyingPriceInPrinciple}) higher than selling price ({sellingPriceInPrinciple}) - Skip current [BUY] order execution for lower risk.");
                    SendWebhookMessage(
                        $":warning:  Buying Higher than selling - BUY: {buyingPriceInPrinciple} / SELL: {sellingPriceInPrinciple} \n" +
                        $"Skipped Order Amount In {OperatingExchangeCurrency}: {buyingAmountInPrinciple} {OperatingExchangeCurrency}\n" +
                        $"Skipped Order Amount In {OperatingTargetCurrency}: {buyingAmountInPrinciple * buyingPriceInPrinciple:N2} {OperatingTargetCurrency}\n" +
                        $"Skkipped on: {DateTime.Now}");
                    Console.ResetColor();
                }
                else
                {
                    var immediateExecute = false;
                    var skip = false;
                    if (!AutoExecution)
                    {
                        Console.WriteLine(
                            $"Do you want to execute this buy order? (BUY {buyingAmountInPrinciple} {ExchangeCurrencyBalance?.Currency} at {buyingPriceInPrinciple} {TargetCurrencyBalance?.Currency})");
                        Console.ForegroundColor = ConsoleColor.DarkRed;
                        Console.ResetColor();
                        Console.WriteLine(
                            $"Press [Y] to continue execution, otherwise Press [S] or [N] to skip.");

                        try
                        {
                            var lineText = Console.ReadLine().Trim('\t');
                            if (lineText?.ToLower() == "y")
                            {
                                immediateExecute = true;
                                skip = false;
                            }
                            else if (lineText?.ToLower() == "s" || lineText?.ToLower() == "n")
                            {
                                immediateExecute = false;
                                skip = true;
                            }

                            while (!immediateExecute &&
                                   (lineText.IsNullOrEmpty() || lineText.IsNotNullOrEmpty() && !skip))
                            {
                                Console.WriteLine(
                                    $"Press [Y] to continue execution, otherwise Press [S] or [N] to skip.");

                                var read = Console.ReadLine().Trim('\t');
                                if (read?.ToLower() == "y")
                                {
                                    immediateExecute = true;
                                    skip = false;
                                    break;
                                }

                                if (read?.ToLower() != "s" && read?.ToLower() != "n") continue;

                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                        }
                    }


                    if (AutoExecution)
                    {
                        immediateExecute = true;
                        Console.WriteLine("Auto execution triggered.");
                    }

                    if (skip)
                    {
                        Console.WriteLine("Skipped. Refreshing...");
                    }


                    if (immediateExecute & !skip)
                    {
                        //execute buy order
                        var nonce = GetNonce();
                        var order = await Rest.PostAsync<ShortOrder>(
                            $"place_order/{OperatingExchangeCurrency}/{OperatingTargetCurrency}", new
                            {
                                signature = GetApiSignature(nonce),
                                key = ApiConfig.ApiKey,
                                nonce,
                                type = "buy",
                                amount = buyingAmountInPrinciple,
                                price = buyingPriceInPrinciple
                            });
                        ApiRequestCounts++;
                        if (order?.OrderId?.IsNotNullOrEmpty() == true)
                        {
                            Console.BackgroundColor = ConsoleColor.DarkGreen;
                            Console.ForegroundColor = ConsoleColor.White;
                            Console.WriteLine(
                                $" [BUY] Order {order.OrderId} Executed: {order.Amount} {OperatingExchangeCurrency} at {order.Price} per {OperatingExchangeCurrency}");
                            Console.ResetColor();
                            SendWebhookMessage(
                                $" :smile: *[BUY]* Order {order.OrderId} - {order.Timestamp}\n" +
                                $" *Executed:* {order.Amount} {OperatingExchangeCurrency} \n" +
                                $" *Price:* {order.Price} {OperatingTargetCurrency}\n" +
                                $" *Cost:* {order.Amount * order.Price} {OperatingTargetCurrency}\n" +
                                $" *Current Value in {OperatingTargetCurrency}:* {originalPortfolioValueWhenBuying} {OperatingTargetCurrency} \n" +
                                $" *Target Value in {OperatingTargetCurrency}:* {finalPortfolioValueWhenBuying} {OperatingTargetCurrency} \n" +
                                $" *Current Value in {OperatingExchangeCurrency}:* {originalPortfolioValueWhenBuying / PublicLastSellPrice} {OperatingExchangeCurrency} \n" +
                                $" *Target Value in {OperatingExchangeCurrency}:* {finalPortfolioValueWhenBuying / order.Price} {OperatingExchangeCurrency}"
                            );
                            Thread.Sleep(1000);
                            ApiRequestcrruedAllowance++;
                        }
                        else
                        {
                            nonce = GetNonce();
                            Console.WriteLine(await Rest.PostAsync<string>(
                                $"place_order/{OperatingExchangeCurrency}/{OperatingTargetCurrency}", new
                                {
                                    signature = GetApiSignature(nonce),
                                    key = ApiConfig.ApiKey,
                                    nonce,
                                    type = "buy",
                                    amount = buyingAmountInPrinciple,
                                    price = buyingPriceInPrinciple
                                }));
                            Console.BackgroundColor = ConsoleColor.DarkRed;
                            Console.ForegroundColor = ConsoleColor.White;
                            Console.WriteLine(
                                $" [FAILED] BUY Order FAILED: {buyingAmountInPrinciple} {OperatingExchangeCurrency} at {buyingPriceInPrinciple} per {OperatingExchangeCurrency}");
                            Console.ResetColor();

                            ApiRequestCounts++;
                        }
                    }
                }
            }

            #endregion

            #region Execute Sell Order

            if (sellingAmountAvailable &&
                IsSellingReserveRequirementMatched(sellingAmountInPrinciple, sellingPriceInPrinciple) &&
                !finalPortfolioValueDecreasedWhenSelling &&
                finalPortfolioValueWhenSelling >= TradingStrategy.StopLine && !IsBullMarketContinuable)
            {
                if (buyingPriceInPrinciple > sellingPriceInPrinciple)
                {
                    Console.BackgroundColor = ConsoleColor.Black;
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine(
                        "WARNING - Selling price lower than buying price - Skip current [SELL] order execution for lower risk.");
                    SendWebhookMessage(
                        $":warning:  Selling lower than buying - BUY: {buyingPriceInPrinciple} / SELL: {sellingPriceInPrinciple} \n" +
                        $"Skipped Order Amount In {OperatingExchangeCurrency}: {buyingAmountInPrinciple} {OperatingExchangeCurrency}\n" +
                        $"Skipped Order Amount In {OperatingTargetCurrency}: {buyingAmountInPrinciple * buyingPriceInPrinciple:N2} {OperatingTargetCurrency}\n" +
                        $"Skkipped on: {DateTime.Now}");
                    Console.ResetColor();
                }
                else
                {
                    var immediateExecute = false;
                    var skip = false;
                    if (!AutoExecution)
                    {
                        Console.WriteLine(
                            $"Do you want to execute this sell order? (SELL {buyingAmountInPrinciple} {ExchangeCurrencyBalance?.Currency} at {buyingPriceInPrinciple} {TargetCurrencyBalance?.Currency})");
                        Console.ForegroundColor = ConsoleColor.DarkRed;
                        Console.ResetColor();
                        Console.WriteLine(
                            $"Press [Y] to continue execution, otherwise Press [S] or [N] to skip.");

                        try
                        {
                            var lineText = Console.ReadLine().Trim('\t');
                            if (lineText?.ToLower() == "y")
                            {
                                immediateExecute = true;
                                skip = false;
                            }
                            else if (lineText?.ToLower() == "s" || lineText?.ToLower() == "n")
                            {
                                immediateExecute = false;
                                skip = true;
                            }

                            while (!immediateExecute &&
                                   (lineText.IsNullOrEmpty() || lineText.IsNotNullOrEmpty() && !skip))
                            {
                                Console.WriteLine(
                                    $"Press [Y] to continue execution, otherwise Press [S] or [N] to skip.");

                                var read = Console.ReadLine().Trim('\t');
                                if (read?.ToLower() == "y")
                                {
                                    immediateExecute = true;
                                    break;
                                }

                                if (read?.ToLower() != "s" && read?.ToLower() != "n") continue;

                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                        }
                    }

                    if (AutoExecution)
                    {
                        immediateExecute = true;
                        Console.WriteLine("Auto execution triggered.");
                    }

                    if (skip)
                    {
                        Console.WriteLine("Skipped. Refreshing...");
                    }


                    if (immediateExecute & !skip)
                    {
                        //execute buy order
                        var nonce = GetNonce();
                        var order = await Rest.PostAsync<ShortOrder>(
                            $"place_order/{OperatingExchangeCurrency}/{OperatingTargetCurrency}", new
                            {
                                signature = GetApiSignature(nonce),
                                key = ApiConfig.ApiKey,
                                nonce,
                                type = "sell",
                                amount = sellingAmountInPrinciple,
                                price = sellingPriceInPrinciple
                            });
                        ApiRequestCounts++;
                        if (order?.OrderId?.IsNotNullOrEmpty() == true)
                        {
                            Console.BackgroundColor = ConsoleColor.DarkGreen;
                            Console.ForegroundColor = ConsoleColor.White;
                            Console.WriteLine(
                                $" [SELL] Order {order.OrderId} Executed: {order.Amount} {OperatingExchangeCurrency} at {order.Price} per {OperatingExchangeCurrency}");
                            Console.ResetColor();

                            SendWebhookMessage(
                                $" :moneybag: *[SELL]* Order {order.OrderId}  - {order.Timestamp}\n" +
                                $" *Executed:* {order.Amount} {OperatingExchangeCurrency} \n" +
                                $" *Price:* {order.Price} {OperatingTargetCurrency}\n" +
                                $" *Cost:* {order.Amount * order.Price} {OperatingTargetCurrency}\n" +
                                $" *Estimated Current Value:* {originalPortfolioValueWhenSelling} {OperatingTargetCurrency} \n" +
                                $" *Estimated Target Value:* {finalPortfolioValueWhenSelling} {OperatingTargetCurrency} \n" +
                                $" *Current Value in {OperatingTargetCurrency}:* {originalPortfolioValueWhenSelling} {OperatingTargetCurrency} \n" +
                                $" *Target Value in {OperatingTargetCurrency}:* {finalPortfolioValueWhenSelling} {OperatingTargetCurrency} \n" +
                                $" *Current Value in {OperatingExchangeCurrency}:* {originalPortfolioValueWhenSelling / PublicLastSellPrice} {OperatingExchangeCurrency} \n" +
                                $" *Target Value in {OperatingExchangeCurrency}:* {finalPortfolioValueWhenSelling / order.Price} {OperatingExchangeCurrency}"
                            );
                            Thread.Sleep(1000);
                            ApiRequestcrruedAllowance++;
                        }
                        else
                        {
                            nonce = GetNonce();
                            Console.WriteLine(await Rest.PostAsync<string>(
                                $"place_order/{OperatingExchangeCurrency}/{OperatingTargetCurrency}", new
                                {
                                    signature = GetApiSignature(nonce),
                                    key = ApiConfig.ApiKey,
                                    nonce,
                                    type = "sell",
                                    amount = sellingAmountInPrinciple,
                                    price = sellingPriceInPrinciple
                                }));
                            Console.BackgroundColor = ConsoleColor.DarkRed;
                            Console.ForegroundColor = ConsoleColor.White;
                            Console.WriteLine(
                                $" [FAILED] SELL Order FAILED: {sellingAmountInPrinciple} {OperatingExchangeCurrency} at {sellingPriceInPrinciple} per {OperatingExchangeCurrency}");
                            Console.ResetColor();

                            ApiRequestCounts++;
                        }
                    }
                }
            }

            #endregion

            #region Execute Drop Order

            // execute only when orderbook is available and no trade transaction in the current period
            if (CurrentOrderbook?.BuyTotal > 0 && CurrentOrderbook?.SellTotal > 0)
            {
                //Test whether to drop last buy order when no historical buy transaction in the current period
                if (AccountLastBuyOpenOrder != null && AccountLastPurchasePrice <= 0 &&
                    CurrentOrderbook.BuyTotal <= CurrentOrderbook.SellTotal * PublicLastSellPrice)
                {
                    // only do it when changes are significant (i.e. can't easily purchase)
                    if (
                        //PublicWeightedAveragePurchasePrice
                        Math.Abs(AccountLastBuyOpenOrder.Price - PublicWeightedAveragePurchasePrice) /
                        Math.Min(AccountLastBuyOpenOrder.Price, PublicWeightedAveragePurchasePrice) >
                        TradingStrategy.MarketChangeSensitivityRatio
                        &&
                        //PublicWeightedAverageBestPurchasePrice
                        Math.Abs(AccountLastBuyOpenOrder.Price - PublicWeightedAverageBestPurchasePrice) /
                        Math.Min(AccountLastBuyOpenOrder.Price, PublicWeightedAverageBestPurchasePrice) >
                        TradingStrategy.MarketChangeSensitivityRatio
                        &&
                        //PublicWeightedAverageLowPurchasePrice
                        Math.Abs(AccountLastBuyOpenOrder.Price - PublicWeightedAverageLowPurchasePrice) /
                        Math.Min(AccountLastBuyOpenOrder.Price, PublicWeightedAverageLowPurchasePrice) >
                        TradingStrategy.MarketChangeSensitivityRatio
                        &&
                        //PublicLastPurchasePrice
                        Math.Abs(AccountLastBuyOpenOrder.Price - PublicLastPurchasePrice) /
                        Math.Min(AccountLastBuyOpenOrder.Price, PublicLastPurchasePrice) >
                        TradingStrategy.MarketChangeSensitivityRatio
                    )
                    {
                        var priorityBids =
                            CurrentOrderbook.Bids.Where(item => item[0] >= AccountLastBuyOpenOrder.Price);
                        var buyingWeightedAveragePrice =
                            (priorityBids?.Sum(item => item[0] * item[1]) /
                             priorityBids.Sum(item => item[1]))
                            .GetValueOrDefault();
                        // only do it when changes are significant based on future purchase demand
                        if (buyingWeightedAveragePrice > 0 &&
                            Math.Abs(AccountLastBuyOpenOrder.Price - buyingWeightedAveragePrice) /
                            Math.Min(AccountLastBuyOpenOrder.Price, buyingWeightedAveragePrice) >
                            TradingStrategy.MarketChangeSensitivityRatio
                        )
                        {
                            var portfolioValueAfterCancellation =
                                originalPortfolioValueWhenBuying -
                                AccountLastBuyOpenOrder.Price * AccountLastBuyOpenOrder.Amount +
                                PublicLastSellPrice * AccountLastBuyOpenOrder.Amount;
                            if (portfolioValueAfterCancellation > TradingStrategy.StopLine)
                                await CancelOrderAsync(AccountLastBuyOpenOrder);
                        }
                    }
                }

                //Test whether to drop last sell order when no historical sell transaction in the current period
                if (AccountLastSellOpenOrder != null && AccountLastSellPrice <= 0 && CurrentOrderbook.BuyTotal >=
                    CurrentOrderbook.SellTotal * PublicLastPurchasePrice * (1 - AverageTradingChangeRatio))
                {
                    // only do it when changes are significant (i.e. can't easily sell)
                    if (
                        //PublicWeightedAveragePurchasePrice
                        Math.Abs(AccountLastSellOpenOrder.Price - PublicWeightedAveragePurchasePrice) /
                        Math.Min(AccountLastSellOpenOrder.Price, PublicWeightedAveragePurchasePrice) >
                        TradingStrategy.MarketChangeSensitivityRatio
                        &&
                        //PublicWeightedAverageBestPurchasePrice
                        Math.Abs(AccountLastSellOpenOrder.Price - PublicWeightedAverageBestPurchasePrice) /
                        Math.Min(AccountLastSellOpenOrder.Price, PublicWeightedAverageBestPurchasePrice) >
                        TradingStrategy.MarketChangeSensitivityRatio
                        &&
                        //PublicWeightedAverageLowPurchasePrice
                        Math.Abs(AccountLastSellOpenOrder.Price - PublicWeightedAverageLowPurchasePrice) /
                        Math.Min(AccountLastSellOpenOrder.Price, PublicWeightedAverageLowPurchasePrice) >
                        TradingStrategy.MarketChangeSensitivityRatio
                        &&
                        //PublicLastPurchasePrice
                        Math.Abs(AccountLastSellOpenOrder.Price - PublicLastPurchasePrice) /
                        Math.Min(AccountLastSellOpenOrder.Price, PublicLastPurchasePrice) >
                        TradingStrategy.MarketChangeSensitivityRatio
                    )
                    {
                        var priorityBids =
                            CurrentOrderbook.Asks.Where(item => item[0] >= AccountLastSellOpenOrder.Price);

                        var sellingWeightedAveragePrice =
                            (priorityBids?.Sum(item => item[0] * item[1]) /
                             priorityBids?.Sum(item => item[1]))
                            .GetValueOrDefault();
                        // only do it when changes are significant based on future purchase demand
                        if (sellingWeightedAveragePrice > 0 &&
                            Math.Abs(AccountLastSellOpenOrder.Price - sellingWeightedAveragePrice) /
                            Math.Min(AccountLastSellOpenOrder.Price, sellingWeightedAveragePrice) >
                            TradingStrategy.MarketChangeSensitivityRatio
                        )
                        {
                            var portfolioValueAfterCancellation =
                                originalPortfolioValueWhenSelling -
                                AccountLastSellOpenOrder.Price * AccountLastSellOpenOrder.Amount +
                                PublicLastPurchasePrice * AccountLastSellOpenOrder.Amount;
                            if (portfolioValueAfterCancellation > TradingStrategy.StopLine)
                                await CancelOrderAsync(AccountLastSellOpenOrder);
                        }
                    }
                }
            }

            #endregion
        }


        private bool IsBuyingReserveRequirementMatched(decimal buyingAmountInPrinciple,
            decimal buyingPriceInPrinciple)
        {
            return buyingAmountInPrinciple <=
                   GetMaximumBuyableAmountBasedOnReserveRatio(buyingPriceInPrinciple);
        }

        private decimal GetMaximumBuyableAmountBasedOnReserveRatio(decimal buyingPriceInPrinciple)
        {
            var maxAmount = TargetCurrencyBalance.Available *
                            (1 - TradingStrategy.MinimumReservePercentageAfterInitInTargetCurrency) /
                            buyingPriceInPrinciple *
                            (1 - BuyingFeeInPercentage) - BuyingFeeInAmount;

            return Math.Truncate(maxAmount * 100000000) / 100000000;
        }

        private decimal GetMaximumSellableAmountBasedOnReserveRatio(decimal sellingPriceInPrinciple)

        {
            var maxAmount = ExchangeCurrencyBalance.Available *
                            (1 - TradingStrategy.MinimumReservePercentageAfterInitInExchangeCurrency) *
                            (1 - SellingFeeInPercentage) - SellingFeeInAmount;
            return maxAmount;
        }

        private bool IsSellingReserveRequirementMatched(decimal sellingAmountInPrinciple,
            decimal sellingPriceInPrinciple)
        {
            return sellingAmountInPrinciple <=
                   GetMaximumSellableAmountBasedOnReserveRatio(sellingPriceInPrinciple);
        }


        private decimal GetPortfolioValueInTargetCurrency(decimal exchangeCurrencyValue, decimal targetCurrencyValue,
            decimal exchangePrice, int decimalPlaces = 2)
        {
            var realValue = exchangeCurrencyValue * exchangePrice + targetCurrencyValue;
            var baseTens = 1;
            for (var i = 1; i <= decimalPlaces; i++)
            {
                baseTens = baseTens * 10;
            }

            return Math.Floor(Math.Truncate(realValue * baseTens) / baseTens);
        }

        private decimal GetPortfolioValueInExchangeCurrency(decimal exchangeCurrencyValue, decimal targetCurrencyValue,
            decimal exchangePrice, int decimalPlaces = 8)
        {
            var realValue = exchangeCurrencyValue + targetCurrencyValue / exchangePrice;
            var baseTens = 1;
            for (var i = 1; i <= decimalPlaces; i++)
            {
                baseTens = baseTens * 10;
            }

            return Math.Truncate((exchangeCurrencyValue + targetCurrencyValue / exchangePrice) * baseTens) / baseTens;
        }

        public async Task<bool> CancelOrderAsync(IOrder order)
        {
            if (order?.OrderId?.IsNullOrEmpty() == true) return false;

            var executable = order.Type == OrderType.Buy &&
                             (!LastTimeBuyOrderCancellation.IsValidSqlDateTime() ||
                              LastTimeBuyOrderCancellation.IsValidSqlDateTime() &&
                              LastTimeBuyOrderCancellation.AddMinutes(TradingStrategy
                                  .MinutesOfAccountHistoryOrderForPurchaseDecision) <= DateTime.Now) ||
                             order.Type == OrderType.Sell &&
                             (!LastTimeSellOrderCancellation.IsValidSqlDateTime() ||
                              LastTimeSellOrderCancellation.IsValidSqlDateTime() &&
                              LastTimeSellOrderCancellation.AddMinutes(TradingStrategy
                                  .MinutesOfAccountHistoryOrderForSellDecision) <=
                              DateTime.Now);
            if (!executable) return false;
            var nonce = GetNonce();
            var result = await Rest.PostAsync<string>(
                $"cancel_order/", new
                {
                    signature = GetApiSignature(nonce),
                    key = ApiConfig.ApiKey,
                    nonce,
                    id = order.OrderId
                });
            ApiRequestCounts++;

            if (BooleanUtils.GetBooleanValueFromObject(result))
            {
                if (order.Type == OrderType.Buy)
                    LastTimeBuyOrderCancellation = DateTime.Now;
                if (order.Type == OrderType.Sell)
                    LastTimeSellOrderCancellation = DateTime.Now
                        ;
                ;
                Console.BackgroundColor = ConsoleColor.DarkRed;
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine(
                    $" [CANCEL] Order {order.OrderId} Cancelled: {order.Amount} {OperatingExchangeCurrency} at {order.Price} per {OperatingExchangeCurrency}");
                Console.ResetColor();

                var currentValue = ExchangeCurrencyBalance?.Total * PublicLastSellPrice + TargetCurrencyBalance?.Total;

                var targetValue = order.Type == OrderType.Buy
                    ? (ExchangeCurrencyBalance?.Total + order.Amount) * order.Price + TargetCurrencyBalance?.Total -
                      (order.Amount * order.Price)
                    : (ExchangeCurrencyBalance?.Total - order.Amount) * order.Price + TargetCurrencyBalance?.Total +
                      (order.Amount * order.Price);


                SendWebhookMessage(
                    $" :cry: *[CANCELCATION]* Order {order.OrderId} - {order.Timestamp}\n" +
                    $" _*Executed:* {order.Amount} {OperatingExchangeCurrency}_ \n" +
                    $" _*Price:* {order.Price} {OperatingTargetCurrency}_\n" +
                    $" _*Current Price:* {(order.Type == OrderType.Buy ? PublicLastPurchasePrice : PublicLastSellPrice)} {OperatingTargetCurrency}_\n" +
                    $" *Current Value in {OperatingTargetCurrency}:* {currentValue} {OperatingTargetCurrency} \n" +
                    $" *Target Value in {OperatingTargetCurrency}:* {targetValue} {OperatingTargetCurrency} \n" +
                    $" *Current Value in {OperatingExchangeCurrency}:* {currentValue / PublicLastSellPrice} {OperatingExchangeCurrency} \n" +
                    $" *Target Value in {OperatingExchangeCurrency}:* {targetValue / order.Price} {OperatingExchangeCurrency}"
                );
                Thread.Sleep(1000);
                ApiRequestcrruedAllowance++;
            }

            return BooleanUtils.GetBooleanValueFromObject(result);
        }


        public async Task<List<IOrder>> GetOpenOrdersAsync()
        {
            var nonce = GetNonce();
            var orders = await Rest.PostAsync<List<ShortOrder>>(
                $"open_orders/{OperatingExchangeCurrency}/{OperatingTargetCurrency}", new
                {
                    signature = GetApiSignature(nonce),
                    key = ApiConfig.ApiKey,
                    nonce
                });
            ApiRequestCounts++;
            AccountOpenOrders = orders?.Select(item => item as IOrder).ToList() ?? new List<IOrder>();
            return AccountOpenOrders;
        }

        public void SendWebhookMessage(string message)
        {
            try
            {
                if (ApiConfig.SlackWebhook.IsNotNullOrEmpty() && message.IsNotNullOrEmpty())
                {
                    new Rest(ApiConfig.SlackWebhook).PostAsync<string>("", new
                    {
                        text = message,
                        username =
                            $"MLEADER's CEX.IO Trading Bot - {OperatingExchangeCurrency}/{OperatingTargetCurrency} "
                    }).Wait();
                }
            }
            catch (Exception ex)
            {
                Rest.LogDebug(ex.Message, ex);
            }
        }

        private bool HasAvailableAmountToPurchase(decimal buyingAmount, AccountBalanceItem balanceItem)
        {
            return false;
        }

        #region Private Members

        private Rest Rest { get; }

        private long GetNonce()
        {
            return Convert.ToInt64(Math.Truncate((DateTime.UtcNow - DateTime.MinValue).TotalMilliseconds));
        }

        private string GetApiSignature(long nonce)
        {
            // Validation first
            if (string.IsNullOrEmpty(ApiConfig?.ApiKey))
            {
                throw new ArgumentException("Parameter apiUsername is not set.");
            }

            if (string.IsNullOrEmpty(ApiConfig.ApiKey))
            {
                throw new ArgumentException("Parameter apiKey is not set");
            }

            if (string.IsNullOrEmpty(ApiConfig.ApiSecret))
            {
                throw new ArgumentException("Parameter apiSecret is not set");
            }

            // HMAC input is nonce + username + key
            var hashInput = string.Format(CultureInfo.InvariantCulture, "{0}{1}{2}", nonce, ApiConfig.ApiUsername,
                ApiConfig.ApiKey);
            var hashInputBytes = Encoding.UTF8.GetBytes(hashInput);

            var secretBytes = Encoding.UTF8.GetBytes(ApiConfig.ApiSecret);
            var hmac = new HMACSHA256(secretBytes);
            var signatureBytes = hmac.ComputeHash(hashInputBytes);
            var signature = BitConverter.ToString(signatureBytes).ToUpper().Replace("-", string.Empty);
            return signature;
        }

        #endregion


        #region Calculation of Key Factors

        #region Staging Calculations

        private decimal PublicUpLevelSell1 =>
            Math.Abs(PublicWeightedAverageBestSellPrice - PublicWeightedAverageSellPrice) /
            PublicWeightedAverageSellPrice;

        private decimal PublicUpLevelSell2 =>
            Math.Abs(PublicWeightedAverageLowSellPrice - PublicWeightedAverageSellPrice) /
            PublicWeightedAverageSellPrice;

        private decimal PublicUpLevelPurchase1 =>
            Math.Abs(PublicWeightedAverageBestPurchasePrice - PublicWeightedAveragePurchasePrice) /
            PublicWeightedAveragePurchasePrice;

        private decimal PublicUpLevelPurchase2 =>
            Math.Abs(PublicWeightedAverageLowPurchasePrice - PublicWeightedAveragePurchasePrice) /
            PublicWeightedAveragePurchasePrice;

        #endregion

        /// <summary>
        /// Is market price going up: buying amount > selling amount
        /// </summary>
        private bool IsBullMarket => LatestPublicPurchaseHistory?.Sum(item => item.Amount) >
                                     LatestPublicSaleHistory?.Sum(item => item.Amount) &&
                                     PublicWeightedAveragePurchasePrice > PublicWeightedAverageSellPrice &&
                                     PublicWeightedAverageLowPurchasePrice > PublicWeightedAverageBestSellPrice &&
                                     PublicWeightedAverageLowPurchasePrice > PublicWeightedAverageLowSellPrice &&
                                     (PublicLastPurchasePrice > PublicWeightedAveragePurchasePrice ||
                                      PublicLastSellPrice > PublicWeightedAverageSellPrice);

        private bool IsBullMarketContinuable => IsBullMarket &&
                                                CurrentOrderbook?.Bids?.Where(item =>
                                                        item[0] >= PublicWeightedAverageLowSellPrice *
                                                        (1 - AverageTradingChangeRatio))
                                                    .Sum(item => item[0] * item[1]) > CurrentOrderbook?.Asks
                                                    ?.Where(item =>
                                                        item[0] <= PublicWeightedAverageLowPurchasePrice *
                                                        (1 + AverageTradingChangeRatio))
                                                    .Sum(item => item[0] * item[1]);

        private bool IsBearMarketContinuable => !IsBullMarket &&
                                                CurrentOrderbook?.Bids?.Where(item =>
                                                        item[0] >= PublicWeightedAverageBestSellPrice *
                                                        (1 - AverageTradingChangeRatio))
                                                    .Sum(item => item[0] * item[1]) < CurrentOrderbook?.Asks
                                                    ?.Where(item =>
                                                        item[0] <= PublicWeightedAverageBestPurchasePrice *
                                                        (1 + AverageTradingChangeRatio))
                                                    .Sum(item => item[0] * item[1]);


        /// <summary>
        /// Find the last X records of public sale prices and do a weighted average
        /// </summary>
        private decimal PublicWeightedAverageSellPrice
        {
            get
            {
                if (!(LatestPublicSaleHistory?.Count > 0)) return 0;
                var totalAmount = LatestPublicSaleHistory.Sum(item => item.Amount);
                return totalAmount > 0
                    ? LatestPublicSaleHistory.Sum(item => item.Amount * item.Price) / totalAmount
                    : 0;
            }
        }

        /// <summary>
        /// Find the best weighted average selling price of the 1/3 best sellig prices
        /// </summary>
        /// <returns></returns>
        private decimal PublicWeightedAverageBestSellPrice
        {
            get
            {
                var bestFirstThirdPrices = LatestPublicSaleHistory?.OrderBy(item => item.Price)
                    .Take(LatestPublicSaleHistory.Count / 3);
                var totalAmount = (bestFirstThirdPrices?.Sum(item => item.Amount)).GetValueOrDefault();
                return totalAmount > 0
                    ? bestFirstThirdPrices.Sum(item => item.Amount * item.Price) / totalAmount
                    : 0;
            }
        }

        /// <summary>
        /// Find the poorest weighted average selling price of the 1/3 low selling prices
        /// </summary>
        /// <returns></returns>
        private decimal PublicWeightedAverageLowSellPrice
        {
            get
            {
                var bestLastThirdPrices = LatestPublicSaleHistory?.OrderByDescending(item => item.Price)
                    .Take(LatestPublicSaleHistory.Count / 3);
                var totalAmount = (bestLastThirdPrices?.Sum(item => item.Amount)).GetValueOrDefault();
                return totalAmount > 0
                    ? bestLastThirdPrices.Sum(item => item.Amount * item.Price) / totalAmount
                    : 0;
            }
        }

        /// <summary>
        /// Find the last public sale price
        /// </summary>
        /// <returns></returns>
        private decimal PublicLastSellPrice => (LatestPublicSaleHistory?.OrderByDescending(item => item.Timestamp)
            ?.Select(item => item.Price)?.FirstOrDefault()).GetValueOrDefault();

        private decimal AccountLastSellPrice => (LatestAccountSaleHistory?.OrderByDescending(item => item.Timestamp)
            ?.Select(item => item.Price)?.FirstOrDefault()).GetValueOrDefault();

        /// <summary>
        /// Find the last X records of public purchase prices and do a weighted average
        /// </summary>
        /// <returns></returns>
        private decimal PublicWeightedAveragePurchasePrice
        {
            get
            {
                var totalAmount = (LatestPublicPurchaseHistory?.Sum(item => item.Amount)).GetValueOrDefault();
                return totalAmount > 0
                    ? LatestPublicPurchaseHistory.Sum(item =>
                          item.Amount * item.Price) / totalAmount
                    : 0;
            }
        }

        /// <summary>
        /// Find the best weighted average purchase price of the 1/3 lowest purchase prices
        /// </summary>
        /// <returns></returns>
        private decimal PublicWeightedAverageBestPurchasePrice
        {
            get
            {
                var bestFirstThirdPrices = LatestPublicPurchaseHistory?.OrderBy(item => item.Price)
                    .Take(LatestPublicPurchaseHistory.Count / 3);
                var totalAmount = (bestFirstThirdPrices?.Sum(item => item.Amount)).GetValueOrDefault();
                return totalAmount > 0
                    ? bestFirstThirdPrices.Sum(item => item.Amount * item.Price) / totalAmount
                    : 0;
            }
        }

        /// <summary>
        /// Find the poorest weighted average purchase price of the 1/3 highest purchase prices
        /// </summary>
        /// <returns></returns>
        private decimal PublicWeightedAverageLowPurchasePrice
        {
            get
            {
                var bestLastThirdPrices = LatestPublicPurchaseHistory?.OrderByDescending(item => item.Price)
                    .Take(LatestPublicPurchaseHistory.Count / 3);
                var totalAmount = (bestLastThirdPrices?.Sum(item => item.Amount)).GetValueOrDefault();
                return totalAmount > 0
                    ? bestLastThirdPrices.Sum(item => item.Amount * item.Price) / totalAmount
                    : 0;
            }
        }

        /// <summary>
        /// Find the last public purchase price
        /// </summary>
        /// <returns></returns>
        private decimal PublicLastPurchasePrice => (LatestPublicPurchaseHistory
            ?.OrderByDescending(item => item.Timestamp)
            ?.Select(item => item.Price)?.FirstOrDefault()).GetValueOrDefault();

        public decimal AccountLastPurchasePrice => (LatestAccountPurchaseHistory
            ?.OrderByDescending(item => item.Timestamp)
            ?.Select(item => item.Price)?.FirstOrDefault()).GetValueOrDefault();

        /// <summary>
        /// Find the last X record of account purchase prices and do a weighted average (i.e. should sell higher than this)
        /// </summary>
        /// <returns></returns>
        private decimal AccountWeightedAveragePurchasePrice
        {
            get
            {
                var totalAmount = (LatestAccountPurchaseHistory?.Sum(item => item.Amount)).GetValueOrDefault();
                return totalAmount > 0
                    ? LatestAccountPurchaseHistory.Sum(item =>
                          item.Amount * item.Price) / totalAmount
                    : 0;
            }
        }

        /// <summary>
        /// Find the last Y records of account sale prices and do a weighted average
        /// </summary>
        /// <returns></returns>
        private decimal AccountWeightedAverageSellPrice
        {
            get
            {
                var totalAmount = (LatestAccountSaleHistory?.Sum(item => item.Amount)).GetValueOrDefault();
                return totalAmount > 0
                    ? LatestAccountSaleHistory.Sum(item =>
                          item.Amount * item.Price) / totalAmount
                    : 0;
            }
        }

        /// <summary>
        /// My trading fee calculated in percentage
        /// </summary>
        /// <returns></returns>
        private decimal BuyingFeeInPercentage { get; set; }

        private decimal SellingFeeInPercentage { get; set; }

        /// <summary>
        /// My trading fee calculated in fixed amount
        /// </summary>
        /// <returns></returns>
        private decimal BuyingFeeInAmount { get; set; }

        private decimal SellingFeeInAmount { get; set; }

        /// <summary>
        /// The avarage market trading change ratio based on both buying/selling's high/low
        /// [AverageTradingChangeRatio] = AVG([PublicUpLevelSell1] , [PublicUpLevelSell2], [PublicUpLevelPurchase1], [PublicUpLevelPurchase2])
        /// </summary>
        /// <returns></returns>
        private decimal AverageTradingChangeRatio => new[]
            {PublicUpLevelSell1, PublicUpLevelSell2, PublicUpLevelPurchase1, PublicUpLevelPurchase2}.Average();

        /// <summary>
        /// [ProposedSellingPrice] = MAX(AVG([PublicWeightedAverageSellPrice],[PublicLastSellPrice], [AccountWeightedAveragePurchasePrice], [AccountWeightedAverageSellPrice]),[PublicLastSellPrice])
        /// </summary>
        /// <returns></returns>
        private decimal ProposedSellingPrice
        {
            get
            {
                var orderbookPriorityAsks = CurrentOrderbook?.Asks?.Where(i => i[0] <= ReasonableAccountLastSellPrice);
                var orderbookValuatedPrice = (CurrentOrderbook?.Asks?.Min(i => i[0])).GetValueOrDefault();
                if (orderbookPriorityAsks?.Count() > 0)
                {
                    orderbookValuatedPrice = orderbookPriorityAsks.Sum(i => i[1] * i[0]) /
                                             orderbookPriorityAsks.Sum(i => i[1]);
                }

                if (orderbookValuatedPrice <= 0) orderbookValuatedPrice = ReasonableAccountLastSellPrice;

                var proposedSellingPrice = new[]
                {
                    new[]
                    {
                        PublicWeightedAverageSellPrice,
                        PublicLastSellPrice,
                        PublicWeightedAverageBestSellPrice,
                        orderbookValuatedPrice
                    }.Average(),
                    IsBullMarket
                        ? Math.Max(ReasonableAccountWeightedAverageSellPrice, PublicWeightedAverageSellPrice)
                        : new[] {ReasonableAccountWeightedAverageSellPrice, PublicWeightedAverageSellPrice}.Average(),
                    IsBullMarket ? PublicWeightedAverageBestSellPrice : PublicWeightedAverageLowSellPrice,
                    orderbookValuatedPrice
//                    (PublicLastSellPrice + ReasonableAccountLastSellPrice + ReasonableAccountLastPurchasePrice) / 3,
//                    (ReasonableAccountLastSellPrice + orderbookValuatedPrice) / 2
                }.Max();

                orderbookPriorityAsks = CurrentOrderbook?.Asks?.Where(i => i[0] <= proposedSellingPrice);
                var exchangeCurrencyBalance =
                    AccountBalance?.CurrencyBalances?.Where(item => item.Key == OperatingExchangeCurrency)
                        ?.Select(item => item.Value)?.FirstOrDefault();
                var targetCurrencyBalance =
                    AccountBalance?.CurrencyBalances?.Where(item => item.Key == OperatingTargetCurrency)
                        ?.Select(item => item.Value)?.FirstOrDefault();

                if (!(orderbookPriorityAsks?.Count() > 0) || exchangeCurrencyBalance == null ||
                    targetCurrencyBalance == null) return proposedSellingPrice;
                var currentPortfolioValue =
                    exchangeCurrencyBalance.Total * PublicLastSellPrice + targetCurrencyBalance.Total;

                foreach (var order in orderbookPriorityAsks)
                {
                    var portfolioValueBasedOnOrder =
                        exchangeCurrencyBalance.Total * Math.Ceiling(order[0] * (1 - SellingFeeInPercentage) -
                                                                     SellingFeeInAmount) + targetCurrencyBalance.Total;
                    //i.e. still make a profit
                    if (portfolioValueBasedOnOrder > currentPortfolioValue) return order[0];
                }

                proposedSellingPrice = proposedSellingPrice * (1 + (IsBullMarket
                                                                   ? (IsBullMarketContinuable
                                                                       ? Math.Max(AverageTradingChangeRatio,
                                                                           TradingStrategy.MarketChangeSensitivityRatio)
                                                                       : Math.Min(AverageTradingChangeRatio,
                                                                           TradingStrategy.MarketChangeSensitivityRatio)
                                                                   )
                                                                   : IsBearMarketContinuable
                                                                       ? 0
                                                                       : Math.Min(AverageTradingChangeRatio,
                                                                           TradingStrategy.MarketChangeSensitivityRatio)
                                                               ));

                return proposedSellingPrice;
            }
        }

        /// <summary>
        /// [ProposedPurchasePrice] = MIN(AVG([PublicWeightedAveragePurchasePrice],[PublicLastPurchasePrice], [PublicWeightedAverageBestPurchasePrice], [AccountWeightedAverageSellPrice]), [PublicLastPurchasePrice])
        /// </summary>
        /// <returns></returns>
        private decimal ProposedPurchasePrice
        {
            get
            {
                var orderbookPriorityBids =
                    CurrentOrderbook?.Bids?.Where(i => i[0] >= ReasonableAccountLastPurchasePrice);
                var orderbookValuatedPrice = (CurrentOrderbook?.Bids?.Max(i => i[0])).GetValueOrDefault();
                if (orderbookPriorityBids?.Count() > 0)
                {
                    orderbookValuatedPrice = orderbookPriorityBids.Sum(i => i[1] * i[0]) /
                                             orderbookPriorityBids.Sum(i => i[1]);
                }

                if (orderbookValuatedPrice <= 0) orderbookValuatedPrice = ReasonableAccountLastPurchasePrice;

                var proposedPurchasePrice = new[]
                {
                    new[]
                    {
                        PublicWeightedAveragePurchasePrice,
                        PublicLastPurchasePrice,
                        PublicWeightedAverageBestPurchasePrice,
                        orderbookValuatedPrice
                    }.Average(),
                    IsBullMarket
                        ? Math.Max(ReasonableAccountWeightedAveragePurchasePrice, PublicWeightedAveragePurchasePrice)
                        : new[] {ReasonableAccountWeightedAveragePurchasePrice, PublicWeightedAveragePurchasePrice}
                            .Average(),
                    IsBullMarket ? PublicWeightedAverageBestPurchasePrice : PublicWeightedAverageLowPurchasePrice,
                    orderbookValuatedPrice
//                    (PublicLastPurchasePrice + ReasonableAccountLastPurchasePrice + ReasonableAccountLastSellPrice +
//                     PublicLastSellPrice) / 4,
//                    (ReasonableAccountLastPurchasePrice + orderbookValuatedPrice) / 2
                }.Min();

                orderbookPriorityBids = CurrentOrderbook?.Bids?.Where(i => i[0] >= proposedPurchasePrice);
                var exchangeCurrencyBalance =
                    AccountBalance?.CurrencyBalances?.Where(item => item.Key == OperatingExchangeCurrency)
                        ?.Select(item => item.Value)?.FirstOrDefault();
                var targetCurrencyBalance =
                    AccountBalance?.CurrencyBalances?.Where(item => item.Key == OperatingTargetCurrency)
                        ?.Select(item => item.Value)?.FirstOrDefault();

                if (!(orderbookPriorityBids?.Count() > 0) || exchangeCurrencyBalance == null ||
                    targetCurrencyBalance == null) return proposedPurchasePrice;

                var currentPortfolioValue =
                    exchangeCurrencyBalance.Total * PublicLastPurchasePrice + targetCurrencyBalance.Total;

                foreach (var order in orderbookPriorityBids)
                {
                    var portfolioValueBasedOnOrder =
                        exchangeCurrencyBalance.Total *
                        Math.Floor(order[0] * (1 - BuyingFeeInPercentage) - BuyingFeeInAmount)
                        + targetCurrencyBalance.Total;
                    //i.e. still make a profit
                    if (portfolioValueBasedOnOrder > currentPortfolioValue)
                    {
                        proposedPurchasePrice = order[0];
                        break;
                    }

                    ;
                }

                proposedPurchasePrice = proposedPurchasePrice * (1 + (IsBullMarket
                                                                     ? (IsBullMarketContinuable
                                                                         ? Math.Max(AverageTradingChangeRatio,
                                                                             TradingStrategy
                                                                                 .MarketChangeSensitivityRatio)
                                                                         : Math.Min(AverageTradingChangeRatio,
                                                                             TradingStrategy
                                                                                 .MarketChangeSensitivityRatio)
                                                                     )
                                                                     : IsBearMarketContinuable
                                                                         ? 0
                                                                         : -1 * Math.Min(AverageTradingChangeRatio,
                                                                               TradingStrategy
                                                                                   .MarketChangeSensitivityRatio)
                                                                 ));
                return proposedPurchasePrice;
            }
        }

        private decimal ReasonableAccountLastPurchasePrice =>
            Math.Abs(AccountLastPurchasePrice - PublicLastPurchasePrice) /
            Math.Min(PublicLastPurchasePrice,
                AccountLastPurchasePrice > 0 ? AccountLastPurchasePrice : PublicLastPurchasePrice) >
            TradingStrategy.MarketChangeSensitivityRatio
                ? PublicLastPurchasePrice
                : AccountLastPurchasePrice;

        private decimal ReasonableAccountLastSellPrice =>
            Math.Abs(AccountLastSellPrice - PublicLastSellPrice) / Math.Min(PublicLastSellPrice,
                AccountLastSellPrice > 0 ? AccountLastSellPrice : PublicLastSellPrice) >
            TradingStrategy.MarketChangeSensitivityRatio
                ? PublicLastSellPrice
                : AccountLastSellPrice;

        private decimal ReasonableAccountWeightedAverageSellPrice =>
            Math.Abs(AccountWeightedAverageSellPrice - PublicLastSellPrice) /
            Math.Min(AccountWeightedAverageSellPrice > 0 ? AccountWeightedAverageSellPrice : PublicLastSellPrice,
                PublicLastSellPrice) >
            TradingStrategy.MarketChangeSensitivityRatio
                ? PublicLastSellPrice
                : AccountWeightedAverageSellPrice;

        private decimal ReasonableAccountWeightedAveragePurchasePrice =>
            Math.Abs(AccountWeightedAveragePurchasePrice - PublicLastPurchasePrice) /
            Math.Min(PublicLastPurchasePrice,
                AccountWeightedAveragePurchasePrice > 0
                    ? AccountWeightedAveragePurchasePrice
                    : PublicLastPurchasePrice) >
            TradingStrategy.MarketChangeSensitivityRatio
                ? PublicLastPurchasePrice
                : AccountWeightedAveragePurchasePrice;

        #endregion

        /// Automated AI logics:
        /// 1. Identify how much amount can be spent for the next order
        /// 2. Identify how much commission/fee (percentage) will be charged for the next order
        /// 3. Identify the correct amount to be spent for the next order (using historical order)
        /// 4. If Reserve amount after order is lower than the minimum reserve amount calculated based on percentage then drop the order, otherwise execute the order
        /// Price decision making logic:
        /// 1. fetch X number of historical orders to check their prices
        /// 2. setting the decision factors:
        ///         2.1  [PublicWeightedAverageSellPrice] Find the last X records of public sale prices and do a weighted average
        ///         2.2  [PublicWeightedAverageBestSellPrice] Find the best weighted average selling price of the 1/3 best sellig prices
        ///         2.3  [PublicWeightedAverageLowSellPrice]  Find the poorest weighted average selling price of the 1/3 low selling prices
        ///         2.4  [PublicLastSellPrice] Find the last public sale price
        ///         2.5  [PublicWeightedAveragePurchasePrice] Find the last X records of public purchase prices and do a weighted average
        ///         2.6  [PublicWeightedAverageBestPurchasePrice] Find the best weighted average purchase price of the 1/3 lowest purchase prices
        ///         2.7  [PublicWeightedAverageLowPurchasePrice] Find the poorest weighted average purchase price of the 1/3 highest purchase prices
        ///         2.8  [PublicLastPurchasePrice] Find the last public purchase price
        ///         2.9  [AccountWeightedAveragePurchasePrice] Find the last X record of account purchase prices and do a weighted average (i.e. should sell higher than this)
        ///         2.10 [AccountWeightedAverageSellPrice] Find the last Y records of account sale prices and do a weighted average
        ///         2.11 [BuyingFeeInPercentage] My trading buying fee calculated in percentage
        ///         2.12 [SellingFeeInPercentage] My selling fee calculated in percentage
        ///         2.13 [BuyingFeeInAmount] My buying fee calculated in fixed amount
        ///         2.14 [SellingFeeInAmount] My selling fee calculated in fixed amount
        /// 
        ///         LOGIC, Decide if the market is trending price up
        ///         [PublicUpLevelSell1] = ABS([PublicWeightedAverageBestSellPrice] - [PublicWeightedAverageSellPrice]) / [PublicWeightedAverageSellPrice]
        ///         [PublicUpLevelSell2] = ABS([PublicWeightedAverageLowSellPrice] - [PublicWeightedAverageSellPrice]) / [PublicWeightedAverageSellPrice]
        ///         [PublicUpLevelPurchase1] = ABS([PublicWeightedAverageBestPurchasePrice] - [PublicWeightedAveragePurchasePrice]) / [PublicWeightedAveragePurchasePrice]
        ///         [PublicUpLevelPurchase2] = ABS([PublicWeightedAverageLowPurchasePrice] - [PublicWeightedAveragePurchasePrice]) / [PublicWeightedAveragePurchasePrice] 
        ///        
        ///         [IsPublicUp] = [PublicUpLevelPurchase1] >= [PublicUpLevelSell1] && [PublicUpLevelPurchase2] <= [PublicUpLevelPurchase2]
        ///         [AverageTradingChangeRatio] = AVG([PublicUpLevelSell1] , [PublicUpLevelSell2], [PublicUpLevelPurchase1], [PublicUpLevelPurchase2])
        /// 
        /// 
        /// 3. when selling:
        ///         3.1 [ProposedSellingPrice] = MAX(AVG([PublicWeightedAverageSellPrice],[PublicLastSellPrice], [AccountWeightedAveragePurchasePrice], [AccountWeightedAverageSellPrice]), [PublicLastSellPrice])
        ///         3.2 [SellingPriceInPrinciple] = [ProposedSellingPrice] * (1+ [SellingFeeInPercentage] + [AverageTradingChangeRatio] * ([IsPublicUp]? 1: -1)) + [SellingFeeInAmount]
        /// 
        /// 4. when buying:
        ///         4.1 [ProposedPurchasePrice] = MIN(AVG([PublicWeightedAveragePurchasePrice],[PublicLastPurchasePrice], [PublicWeightedAverageBestPurchasePrice], [AccountWeightedAverageSellPrice]), [PublicLastPurchasePrice])
        ///         4.2 [PurchasePriceInPrinciple] = [ProposedPurchasePrice] * (1 - [BuyingFeeInPercentage] + [AverageTradingChangeRatio] * ([IsPublicUp] ? 1: -1)) + [BuyingFeeInAmount]
        /// Final Decision:
        /// 5. If final portfolio value is descreasing, do not buy/sell

        #endregion
    }
}