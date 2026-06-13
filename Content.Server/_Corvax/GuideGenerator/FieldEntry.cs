using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server.Corvax.GuideGenerator;

public static class FieldEntry
{
    private static readonly Regex DoubleEntryRegex = new(@"^[+-]?\d+\.\d+$");

    public static object? DataNodeToObject(DataNode node)
    {
        if (node is MappingDataNode mapping)
        {
            var dict = new Dictionary<string, object?>();

            foreach (var kv in mapping)
            {
                dict[kv.Key] = DataNodeToObject(kv.Value);
            }

            if (node.Tag != null)
            {
                var wrapped = new Dictionary<string, object?>
                {
                    [node.Tag] = dict
                };
                return wrapped;
            }

            return dict;
        }

        if (node is SequenceDataNode sequence)
        {
            var items = new List<object?>();
            foreach (var item in sequence)
            {
                items.Add(DataNodeToObject(item));
            }

            var typedMap = new Dictionary<string, object?>();
            var canRewrite = true;
            foreach (var obj in items)
            {
                if (obj is not Dictionary<string, object?> dict ||
                    !dict.TryGetValue("type", out var typeVal) ||
                    typeVal is null)
                {
                    canRewrite = false;
                    break;
                }

                var key = $"type:{typeVal}";
                var cloned = new Dictionary<string, object?>(dict);
                cloned.Remove("type");
                typedMap[key] = cloned;
            }

            if (canRewrite && typedMap.Count > 0)
                return typedMap;

            return items;
        }

        if (node is ValueDataNode value)
        {
            if (value.IsNull)
                return null;

            var raw = value.Value;
            object? parsed;

            if (bool.TryParse(raw, out var boolRes))
                parsed = boolRes;
            else if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intRes))
                parsed = intRes;
            else if (DoubleEntryRegex.IsMatch(raw) &&
                double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleRes))
                parsed = doubleRes;
            else
                parsed = raw;

            if (node.Tag == null)
                return parsed;

            return new Dictionary<string, object?>
            {
                [node.Tag] = string.IsNullOrEmpty(raw)
                    ? new Dictionary<string, object?>()
                    : parsed
            };
        }

        return node.ToString();
    }

    public static void NormalizeFlagsToSequences(object instance, MappingDataNode node)
    {
        var type = instance.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;

        foreach (var key in node.Keys.ToList())
        {
            var prop = type.GetProperty(key, flags);
            MemberInfo? member = prop != null && prop.GetGetMethod(true) != null ? prop : type.GetField(key, flags);
            if (member == null)
                continue;

            var memberType = member is PropertyInfo p ? p.PropertyType : ((FieldInfo) member).FieldType;
            if (!memberType.IsEnum)
                continue;

            if (memberType.GetCustomAttribute<FlagsAttribute>(false) == null)
                continue;

            var value = member is PropertyInfo p2 ? p2.GetValue(instance) : ((FieldInfo) member).GetValue(instance);
            if (value == null)
                continue;

            var intVal = Convert.ToInt64(value);
            var names = new List<string>();
            foreach (var v in Enum.GetValues(memberType))
            {
                var i = Convert.ToInt64(v);
                if (i == 0)
                    continue;
                if ((i & (i - 1)) == 0 && (intVal & i) != 0)
                    names.Add(Enum.GetName(memberType, v)!);
            }

            node[key] = new SequenceDataNode(names.ToArray());
        }
    }

    public static void EnsureFieldsCollectionsInitialized(object instance)
    {
        if (CanSafelyInitializeDefault(instance, new HashSet<Type>()))
            EnsureFieldsCollectionsInitializedUnchecked(instance);
    }

    private static void EnsureFieldsCollectionsInitializedUnchecked(object instance)
    {
        foreach (var member in GetWritableMembers(instance.GetType()))
        {
            try
            {
                if (GetMemberValue(member, instance) != null)
                    continue;

                if (!TryCreateDefaultValue(GetMemberType(member), out var created, out var recurse) || created == null)
                    continue;

                SetMemberValue(member, instance, created);

                if (recurse)
                    EnsureFieldsCollectionsInitializedUnchecked(created);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static bool CanSafelyInitializeDefault(object instance, HashSet<Type> activeTypes)
    {
        var type = instance.GetType();
        if (!activeTypes.Add(type))
            return false;

        try
        {
            foreach (var member in GetWritableMembers(type))
            {
                try
                {
                    if (GetMemberValue(member, instance) != null)
                        continue;

                    if (!CanSafelyInitializeMember(GetMemberType(member), activeTypes))
                        return false;
                }
                catch
                {
                    // ignore
                }
            }

            return true;
        }
        finally
        {
            activeTypes.Remove(type);
        }
    }

    private static IEnumerable<MemberInfo> GetWritableMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (var field in type.GetFields(flags))
        {
            if (!field.IsInitOnly)
                yield return field;
        }

        foreach (var prop in type.GetProperties(flags))
        {
            if (prop.CanWrite && prop.GetIndexParameters().Length == 0)
                yield return prop;
        }
    }

    private static Type GetMemberType(MemberInfo member)
    {
        return member is PropertyInfo prop ? prop.PropertyType : ((FieldInfo) member).FieldType;
    }

    private static object? GetMemberValue(MemberInfo member, object instance)
    {
        return member is PropertyInfo prop ? prop.GetValue(instance) : ((FieldInfo) member).GetValue(instance);
    }

    private static void SetMemberValue(MemberInfo member, object instance, object value)
    {
        if (member is PropertyInfo prop)
            prop.SetValue(instance, value);
        else
            ((FieldInfo) member).SetValue(instance, value);
    }

    private static bool CanSafelyInitializeMember(Type type, HashSet<Type> activeTypes)
    {
        if (type == typeof(object))
            return false;

        if (type == typeof(string) || IsConcreteCollectionLike(type))
            return true;

        if (type.IsClass && !type.IsAbstract)
            return !activeTypes.Contains(type) && CanSafelyInitializeDefault(Activator.CreateInstance(type, true)!, activeTypes);

        if (!type.IsAbstract && !type.IsInterface)
            return true;

        var concrete = FindConcreteAssignableType(type);
        return concrete == null || !activeTypes.Contains(concrete) && CanSafelyInitializeDefault(Activator.CreateInstance(concrete)!, activeTypes);
    }

    private static bool TryCreateDefaultValue(Type type, out object? value, out bool recurse)
    {
        value = null;
        recurse = false;

        if (type == typeof(object))
            return false;

        if (type == typeof(string))
        {
            value = string.Empty;
            return true;
        }

        if (IsConcreteCollectionLike(type))
        {
            value = type.IsArray
                ? Array.CreateInstance(type.GetElementType()!, 0)
                : Activator.CreateInstance(type);
            return value != null;
        }

        if (type.IsClass && !type.IsAbstract)
        {
            value = Activator.CreateInstance(type, true);
            recurse = value != null;
            return value != null;
        }

        if (!type.IsAbstract && !type.IsInterface)
            return false;

        var concrete = FindConcreteAssignableType(type);
        if (concrete == null)
            return false;

        value = Activator.CreateInstance(concrete);
        recurse = value != null;
        return value != null;
    }

    private static bool IsConcreteCollectionLike(Type type)
    {
        return (typeof(IDictionary).IsAssignableFrom(type) ||
                typeof(IList).IsAssignableFrom(type) ||
                type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ||
                type.IsArray) &&
            type is { IsAbstract: false, IsInterface: false };
    }

    private static Type? FindConcreteAssignableType(Type target)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var types = asm.GetTypes();

            foreach (var t in types)
            {
                if (t.IsAbstract || t.IsInterface)
                    continue;
                if (!target.IsAssignableFrom(t))
                    continue;
                if (t.GetConstructor(Type.EmptyTypes) == null)
                    continue;
                return t;
            }
        }

        return null;
    }
}
