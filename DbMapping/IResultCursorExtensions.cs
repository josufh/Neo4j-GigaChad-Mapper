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

        Dictionary<string, object> dict = raw switch
        {
            Dictionary<string, object> dso => dso,
            _ => throw new InvalidOperationException($"Field '{field}' is not a map (got {raw?.GetType().FullName ?? "null"}).")
        };

        return MapProperties<T>(dict);
    }

    private static T MapProperties<T>(Dictionary<string, object> source) where T : new()
    {
        T instance = new();
        MapIntoInstance(instance, source);
        return instance;
    }

    private static void MapIntoInstance(object destination, Dictionary<string, object> rawSource)
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

        targetType = GetUnderlyingTypeIfNullable(targetType);

        Func<object, Type, object?> Map = GetMapper(raw, targetType);

        return Map(raw, targetType);
    }

    private static Type GetUnderlyingTypeIfNullable(Type targetType)
    {
        Type? underlying = Nullable.GetUnderlyingType(targetType);
        if (underlying is not null)
            return underlying;
        return targetType;
    }

    private static Func<object, Type, object?> GetMapper(object raw, Type targetType)
    {
        if (targetType.IsAssignableFrom(raw.GetType()))
            return MapAssignable;

        if (targetType == typeof(string))
            return MapString;

        if (targetType.IsEnum)
            return MapEnum;

        if (raw is IConvertible &&
           (targetType.IsPrimitive ||
            targetType == typeof(decimal) ||
            targetType == typeof(DateTime) ||
            targetType == typeof(Guid)))
        {
            return MapScalar;
        }

        if (targetType == typeof(DateTime))
        {
            if (raw is string)
                return MapDateTimeString;
            return MapDateTimeNeo4jType;
        }

        if (targetType == typeof(Guid) && raw is string)
            return MapGuidString;

        if (typeof(IEnumerable).IsAssignableFrom(targetType) && targetType != typeof(string))
            return MapEnumerable;

        if (raw is Dictionary<string, object>)
            return MapComplex;

        return MapFallback;
    }

    private static object? MapAssignable(object raw, Type _) 
        => raw;

    private static object? MapString(object raw, Type _)
        => Convert.ToString(raw, CultureInfo.InvariantCulture);
        
    private static object? MapScalar(object raw, Type targetType)
    {
        try
        {
            return Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static object? MapEnum(object raw, Type targetType)
    {
        if (raw is string s && Enum.TryParse(targetType, s, true, out object? enumVal))
            return enumVal;

        if (raw is IConvertible)
            return Enum.ToObject(targetType, Convert.ToInt32(raw, CultureInfo.InvariantCulture));

        return null;
    }

    private static object? MapDateTimeString(object raw, Type _)
        => DateTime.Parse((string)raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static object? MapGuidString(object raw, Type _)
        => Guid.Parse((string)raw);

    private static object? MapDateTimeNeo4jType(object raw, Type _)
    {
        if (raw is ZonedDateTime zdt)
            return zdt.ToDateTimeOffset().UtcDateTime;

        if (raw is LocalDateTime ldt)
            return new DateTime(ldt.Year, ldt.Month, ldt.Day, ldt.Hour, ldt.Minute, ldt.Second, ldt.Nanosecond / 1_000_000, DateTimeKind.Unspecified);

        return null;
    }

    private static object? MapEnumerable(object raw, Type targetType)
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

        return null;
    }

    private static object? MapComplex(object raw, Type targetType)
    {
        object obj = Activator.CreateInstance(targetType)
                    ?? throw new InvalidOperationException($"Cannot create {targetType.FullName}");
        MapIntoInstance(obj, (Dictionary<string, object>)raw);
        return obj;
    }
    
    private static object? MapFallback(object raw, Type targetType)
    {
        try { return Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture); }
        catch { return null; }
    }
}