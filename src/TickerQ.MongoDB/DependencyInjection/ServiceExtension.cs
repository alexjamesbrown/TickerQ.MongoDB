using System;
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using TickerQ.Utilities;
using TickerQ.Utilities.Entities;

namespace TickerQ.MongoDB.DependencyInjection
{
    public static class ServiceExtension
    {
        /// <summary>
        /// Registers the MongoDB persistence provider for TickerQ. Call this from
        /// inside <c>AddTickerQ(options =&gt; ...)</c>.
        /// </summary>
        public static TickerOptionsBuilder<TimeTickerEntity, CronTickerEntity> AddOperationalStore(
            this TickerOptionsBuilder<TimeTickerEntity, CronTickerEntity> tickerConfiguration,
            Action<TickerQMongoOptionBuilder<TimeTickerEntity, CronTickerEntity>> mongoConfiguration = null)
            => AddOperationalStore<TimeTickerEntity, CronTickerEntity>(tickerConfiguration, mongoConfiguration);

        public static TickerOptionsBuilder<TTimeTicker, TCronTicker> AddOperationalStore<TTimeTicker, TCronTicker>(
            this TickerOptionsBuilder<TTimeTicker, TCronTicker> tickerConfiguration,
            Action<TickerQMongoOptionBuilder<TTimeTicker, TCronTicker>> mongoConfiguration = null)
            where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
            where TCronTicker : CronTickerEntity, new()
        {
            var optionBuilder = new TickerQMongoOptionBuilder<TTimeTicker, TCronTicker>();
            mongoConfiguration?.Invoke(optionBuilder);

            if (optionBuilder.ConfigureServices is null)
                throw new InvalidOperationException(
                    "TickerQ.MongoDB: you must call UseTickerQMongoClient(...) or UseExistingMongoClient(...) on the option builder.");

            // ExternalProviderConfigServiceAction is internal on TickerOptionsBuilder<,> and
            // TickerQ.Utilities does not (yet) grant InternalsVisibleTo("TickerQ.MongoDB"),
            // so we go through reflection. Runs once at DI registration — cost is negligible.
            // Drop this helper once upstream lands the visibility grant.
            ExternalProviderConfig.Append(tickerConfiguration, optionBuilder.ConfigureServices);
            return tickerConfiguration;
        }

        private static class ExternalProviderConfig
        {
            private static readonly ConcurrentDictionary<Type, PropertyInfo> PropertyCache = new();

            public static void Append<TTimeTicker, TCronTicker>(
                TickerOptionsBuilder<TTimeTicker, TCronTicker> builder,
                Action<IServiceCollection> contribution)
                where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
                where TCronTicker : CronTickerEntity, new()
            {
                var prop = PropertyCache.GetOrAdd(typeof(TickerOptionsBuilder<TTimeTicker, TCronTicker>),
                    t => t.GetProperty(
                        "ExternalProviderConfigServiceAction",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? throw new InvalidOperationException(
                            "ExternalProviderConfigServiceAction not found on TickerOptionsBuilder. " +
                            "The TickerQ.Utilities version may be incompatible with this provider."));

                var current = (Action<IServiceCollection>)prop.GetValue(builder);
                var combined = current is null
                    ? contribution
                    : (Action<IServiceCollection>)Delegate.Combine(current, contribution);
                prop.SetValue(builder, combined);
            }
        }
    }
}
