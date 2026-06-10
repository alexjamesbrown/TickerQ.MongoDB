using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace TickerQ.MongoDB.Infrastructure
{
    /// <summary>
    /// Bypasses C# accessibility for `{ get; internal set; }` properties on TickerQ entities.
    /// Several entity properties (Status, LockHolder, LockedAt, UpdatedAt, InitIdentifier, etc.)
    /// have internal setters; the EF provider can write them because TickerQ.Utilities marks
    /// EF as InternalsVisibleTo. We can't get that grant from outside the TickerQ repo, so we
    /// use Expression.Compile() to build setters that go through PropertyInfo.SetMethod —
    /// runtime reflection ignores the C# accessibility check, and the compiled delegate runs
    /// at native speed after the first call.
    ///
    /// When the upstream TickerQ.Utilities adds `InternalsVisibleTo("TickerQ.MongoDB")`, this
    /// whole file can be deleted and the direct property writes restored.
    /// </summary>
    internal static class InternalSetters<T>
    {
        private static readonly ConcurrentDictionary<string, Delegate> Cache = new();

        public static void Set<TValue>(T instance, string propertyName, TValue value)
        {
            var setter = (Action<T, TValue>)Cache.GetOrAdd(propertyName, name => BuildSetter<TValue>(name));
            setter(instance, value);
        }

        private static Action<T, TValue> BuildSetter<TValue>(string propertyName)
        {
            var prop = typeof(T).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMemberException(typeof(T).FullName, propertyName);
            var setMethod = prop.GetSetMethod(nonPublic: true)
                ?? throw new InvalidOperationException($"{typeof(T).Name}.{propertyName} has no setter");

            var inst = Expression.Parameter(typeof(T), "i");
            var val = Expression.Parameter(typeof(TValue), "v");
            var body = Expression.Call(inst, setMethod, val);
            return Expression.Lambda<Action<T, TValue>>(body, inst, val).Compile();
        }
    }
}
