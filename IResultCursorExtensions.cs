using System.Collections;
using System.Globalization;
using System.Reflection;
using Neo4j.Driver;

namespace DbMapping;

public static class IResultCursorExtensions
{
    public static async Task<T> SingleAsync<T>(this IResultCursor cursor) where T : new()
    {
        IRecord record = await cursor.SingleAsync();

        if (record.Keys.Count != 1)
            throw new InvalidOperationException("More than one key found");

        string field = record.Keys.Single();
        object raw = record[field];

        IDictionary<string, object> dict = raw switch
        {
            IDictionary<string, object> dso => dso,
            _ => throw new InvalidOperationException($"Field '{field}' is not a map (got {raw?.GetType().FullName ?? "null"}).")
        };

        return MapProperties<T>(dict);
    }

    private static T MapProperties<T>(IDictionary<string, object> source) where T : new()
    {
        T instance = new();
        MapIntoInstance(instance, source);
        return instance;
    }

    private static void MapIntoInstance(object destination, IDictionary<string, object> rawSource)
    {
        Dictionary<string, object> source = new(rawSource, StringComparer.OrdinalIgnoreCase);

        foreach (PropertyInfo prop in destination.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.CanWrite))
        {
            if (!source.TryGetValue(prop.Name, out object? raw) || raw is null)
            continue;

            try
            {
                object? converted = ConvertValueRecursive(raw, prop.PropertyType);
                if (converted is not null || Nullable.GetUnderlyingType(prop.PropertyType) != null)
                    prop.SetValue(destination, converted);
            }
            catch { }
        }
    }

    private static object? ConvertValueRecursive(object raw, Type targetType)
    {
        if (raw is null)
            return null;

        Type? underlying = Nullable.GetUnderlyingType(targetType);
        if (underlying is not null)
            targetType = underlying;

        Type rawType = raw.GetType();

        if (targetType.IsAssignableFrom(rawType))
            return raw;

        if (targetType == typeof(string))
            return Convert.ToString(raw, CultureInfo.InvariantCulture);

        if (targetType.IsEnum)
        {
            if (raw is string s && Enum.TryParse(targetType, s, true, out object? enumVal))
                return enumVal;
            if (raw is IConvertible)
                return Enum.ToObject(targetType, Convert.ToInt32(raw, CultureInfo.InvariantCulture));
        }

        if (raw is IConvertible &&
            (targetType.IsPrimitive ||
             targetType == typeof(decimal) ||
             targetType == typeof(DateTime) ||
             targetType == typeof(Guid)))
        {
            try
            {
                return Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture);
            }
            catch { }
        }

        if (targetType == typeof(DateTime) && raw is string dtStr &&
            DateTime.TryParse(dtStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime dt))
        {
            return dt;
        }

        if (targetType == typeof(Guid) && raw is string guidStr &&
            Guid.TryParse(guidStr, out Guid guid))
        {
            return guid;
        }

        if (targetType == typeof(DateTime))
        {
            if (raw is ZonedDateTime zdt)
                return zdt.ToDateTimeOffset().UtcDateTime;
            if (raw is LocalDateTime ldt)
                return new DateTime(ldt.Year, ldt.Month, ldt.Day, ldt.Hour, ldt.Minute, ldt.Second, ldt.Nanosecond / 1_000_000, DateTimeKind.Unspecified);
        }

        if (typeof(IEnumerable).IsAssignableFrom(targetType) && targetType != typeof(string))
        {
            Type elemType = targetType.IsArray
                ? targetType.GetElementType()!
                : (targetType.IsGenericType ? targetType.GetGenericArguments().First() : typeof(object));

            IEnumerable? rawEnum = raw as IEnumerable;

            if (rawEnum is not null && raw is not string)
            {
                Type listType = typeof(List<>).MakeGenericType(elemType);
                IList tmp = (IList)Activator.CreateInstance(listType)!;

                foreach (object item in rawEnum)
                    tmp.Add(ConvertValueRecursive(item!, elemType));

                if (targetType.IsArray)
                {
                    Array arr = Array.CreateInstance(elemType, tmp.Count);
                    tmp.CopyTo(arr, 0);
                    return arr;
                }

                if (targetType.IsAssignableFrom(listType))
                    return tmp;

                if (Activator.CreateInstance(targetType) is IList targetList)
                {
                    foreach (object i in tmp) targetList.Add(i);
                    return targetList;
                }

                return tmp;
            }
        }

        if (raw is IDictionary<string, object> dso)
        {
            object obj = Activator.CreateInstance(targetType)
                      ?? throw new InvalidOperationException($"Cannot create {targetType.FullName}");
            MapIntoInstance(obj, dso);
            return obj;
        }
        
        if (raw is IConvertible)
        {
            try { return Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture); }
            catch { }
        }

        return null;
    }
}