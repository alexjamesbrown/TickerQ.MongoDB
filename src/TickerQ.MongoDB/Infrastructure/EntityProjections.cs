using System.Collections.Generic;
using TickerQ.Utilities.Entities;

namespace TickerQ.MongoDB.Infrastructure
{
    /// <summary>
    /// Materialises entities in the shape the dispatcher expects:
    ///   - cron-ticker summaries for the scheduler's expression cache
    ///   - root-with-children time tickers for queued execution
    ///
    /// Unlike the EF provider's MappingExtensions (which returns Expression&lt;Func&lt;..&gt;&gt;
    /// for IQueryable translation), Mongo materialises eagerly, so plain methods suffice.
    /// Internal-set properties (ParentId, UpdatedAt) are written via <see cref="InternalSetters{T}"/>.
    /// </summary>
    internal static class EntityProjections
    {
        public static CronTickerEntity ToCronTickerExpression<TCronTicker>(TCronTicker e)
            where TCronTicker : CronTickerEntity, new()
            => new()
            {
                Id = e.Id,
                Expression = e.Expression,
                Function = e.Function,
                RetryIntervals = e.RetryIntervals,
                Retries = e.Retries,
                IsEnabled = e.IsEnabled
            };

        public static TimeTickerEntity ToQueueTimeTicker<TTimeTicker>(TTimeTicker e)
            where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        {
            var root = new TimeTickerEntity
            {
                Id = e.Id,
                Function = e.Function,
                Retries = e.Retries,
                RetryIntervals = e.RetryIntervals,
                ExecutionTime = e.ExecutionTime,
            };
            InternalSetters<TimeTickerEntity>.Set(root, nameof(TimeTickerEntity.UpdatedAt), e.UpdatedAt);
            InternalSetters<TimeTickerEntity>.Set(root, nameof(TimeTickerEntity.ParentId), e.ParentId);

            var children = new List<TimeTickerEntity>();
            if (e.Children != null)
            {
                foreach (var ch in e.Children)
                {
                    var child = new TimeTickerEntity
                    {
                        Id = ch.Id,
                        Function = ch.Function,
                        Retries = ch.Retries,
                        RetryIntervals = ch.RetryIntervals,
                        RunCondition = ch.RunCondition,
                    };

                    var grandchildren = new List<TimeTickerEntity>();
                    if (ch.Children != null)
                    {
                        foreach (var gch in ch.Children)
                        {
                            grandchildren.Add(new TimeTickerEntity
                            {
                                Id = gch.Id,
                                Function = gch.Function,
                                Retries = gch.Retries,
                                RetryIntervals = gch.RetryIntervals,
                                RunCondition = gch.RunCondition,
                            });
                        }
                    }

                    child.Children = grandchildren;
                    children.Add(child);
                }
            }

            root.Children = children;
            return root;
        }
    }
}
