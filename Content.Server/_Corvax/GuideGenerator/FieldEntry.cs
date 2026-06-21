using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server.Corvax.GuideGenerator;

public static class FieldEntry
{
    private static readonly Regex DoubleEntryRegex = new(@"^[+-]?\d+\.\d+$");

    private enum TypeCategory { Object, String, Collection, ConcreteClass, ValueType, AbstractOrInterface }

    private static TypeCategory ClassifyType(Type type) =>
        type == typeof(object) ? TypeCategory.Object :
        type == typeof(string) ? TypeCategory.String :
        IsConcreteCollectionLike(type) ? TypeCategory.Collection :
        type.IsClass && !type.IsAbstract ? TypeCategory.ConcreteClass :
        !type.IsAbstract && !type.IsInterface ? TypeCategory.ValueType :
        TypeCategory.AbstractOrInterface;

    private static string LowerFirst(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);

    public static object? ProcessNode(object instance, MappingDataNode node, MappingDataNode? composed = null)
    {
        NormalizeFlagsToSequences(instance, node);
        SupplementReadOnlyFields(instance.GetType(), node, composed);
        return DataNodeToObject(node);
    }

    public static object? ComputePrototypeDefault(Type kind, ISerializationManager ser)
    {
        try
        {
            var instance = Activator.CreateInstance(kind);
            if (instance == null)
                return null;
            try
            {
                EnsureFieldsCollectionsInitialized(instance);
                var node = ser.WriteValueAs<MappingDataNode>(kind, instance, true);
                node.Remove("id");
                NormalizeFlagsToSequences(instance, node);
                return DataNodeToObject(node);
            }
            finally
            {
                if (instance is IDisposable d)
                    d.Dispose();
            }
        }
        catch
        {
            return new Dictionary<string, object?>();
        }
    }

    public static object? ComputeComponentDefault(string compName, IComponentFactory compFactory, IEntityManager entMan, ISerializationManager ser
    )
    {
        if (!compFactory.TryGetRegistration(compName, out var registration))
            return null;

        var uid = entMan.CreateEntityUninitialized(null);
        try
        {
            var compInstance = compFactory.GetComponent(registration.Type);
            EnsureFieldsCollectionsInitialized(compInstance);
            entMan.AddComponent(uid, compInstance);
            var node = ser.WriteValueAs<MappingDataNode>(
                compInstance.GetType(),
                compInstance,
                true
            );
            NormalizeFlagsToSequences(compInstance, node);
            return DataNodeToObject(node);
        }
        catch
        {
            return new Dictionary<string, object?>();
        }
        finally
        {
            try
            {
                entMan.DeleteEntity(uid);
            }
            catch { }
        }
    }

    public static Dictionary<string, object?> DeduplicateAgainstDefault(object? defaultObj, Dictionary<string, object?> map)
    {
        var defaultDict = defaultObj as Dictionary<string, object?> ?? new Dictionary<string, object?>();

        foreach (var (_, fields) in map)
        {
            if (fields is Dictionary<string, object?> entDict)
                RemoveDefaultDuplicates(defaultDict, entDict);
        }

        return new Dictionary<string, object?> { ["default"] = defaultObj, ["id"] = map };
    }

    private static void RemoveDefaultDuplicates(Dictionary<string, object?> defaults, Dictionary<string, object?> target)
    {
        foreach (var key in target.Keys.ToList())
        {
            if (!defaults.TryGetValue(key, out var defaultVal))
                continue;

            var targetVal = target[key];

            if (AreEqual(defaultVal, targetVal))
                target.Remove(key);
            else if (defaultVal is Dictionary<string, object?> defaultDict && targetVal is Dictionary<string, object?> targetDict)
                RemoveDefaultDuplicates(defaultDict, targetDict);
        }
    }

    private static bool AreEqual(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;

        switch (a, b)
        {
            case (IDictionary<string, object?> dictA, IDictionary<string, object?> dictB):
                return dictA.Count == dictB.Count
                    && dictA.All(kv => dictB.TryGetValue(kv.Key, out var bVal) && AreEqual(kv.Value, bVal));
            case (IList listA, IList listB):
                return listA.Count == listB.Count
                    && Enumerable.Range(0, listA.Count).All(i => AreEqual(listA[i], listB[i]));
            default:
                return a.Equals(b);
        }
    }

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
            var items = sequence.Select(DataNodeToObject).ToList();

            var typedMap = new Dictionary<string, object?>();
            var allTyped = true;
            foreach (var obj in items)
            {
                if (obj is not Dictionary<string, object?> dict ||
                    !dict.TryGetValue("type", out var typeVal) ||
                    typeVal is null)
                { allTyped = false; break; }

                var clone = new Dictionary<string, object?>(dict);
                clone.Remove("type");
                typedMap[$"type:{typeVal}"] = clone;
            }

            return allTyped && typedMap.Count > 0 ? typedMap : items;
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

    public static void SupplementReadOnlyFields(Type type, MappingDataNode serialized, MappingDataNode? composed)
    {
        if (composed == null) return;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;

        foreach (var member in type.GetFields(flags).Cast<MemberInfo>().Concat(
            type.GetProperties(flags).Where(p => p.GetGetMethod(true) != null).Cast<MemberInfo>()))
        {
            var attr = member.GetCustomAttribute<DataFieldAttribute>();
            if (attr == null || !attr.ReadOnly) continue;

            var key = attr.Tag ?? LowerFirst(member.Name);
            if (!serialized.Has(key) && composed.Has(key))
                serialized[key] = composed[key].Copy();
        }
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

            var memberType = GetMemberType(member);
            if (!memberType.IsEnum)
                continue;

            if (memberType.GetCustomAttribute<FlagsAttribute>(false) == null)
                continue;

            var value = GetMemberValue(member, instance);
            if (value == null)
                continue;

            var intVal = Convert.ToInt64(value);
            var names = Enum.GetValues(memberType).Cast<object>()
                .Select(v => (Name: Enum.GetName(memberType, v)!, Val: Convert.ToInt64(v)))
                .Where(x => x.Val != 0 && (x.Val & (x.Val - 1)) == 0 && (intVal & x.Val) != 0)
                .Select(x => x.Name)
                .ToArray();

            node[key] = new SequenceDataNode(names);
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
            catch { }
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
                catch { }
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

    public static Type GetMemberType(MemberInfo member) =>
        member switch
        {
            PropertyInfo p => p.PropertyType,
            FieldInfo f => f.FieldType,
            _ => throw new ArgumentException($"Unsupported member type: {member.GetType()}", nameof(member)),
        };

    private static object? GetMemberValue(MemberInfo member, object instance) =>
        member switch
        {
            PropertyInfo p => p.GetValue(instance),
            FieldInfo f => f.GetValue(instance),
            _ => throw new ArgumentException($"Unsupported member type: {member.GetType()}", nameof(member)),
        };

    private static void SetMemberValue(MemberInfo member, object instance, object value)
    {
        switch (member)
        {
            case PropertyInfo p: p.SetValue(instance, value); break;
            case FieldInfo f: f.SetValue(instance, value); break;
        }
    }

    private static bool CanSafelyInitializeMember(Type type, HashSet<Type> activeTypes) =>
        ClassifyType(type) switch
        {
            TypeCategory.Object => false,
            TypeCategory.String or TypeCategory.Collection or TypeCategory.ValueType => true,
            TypeCategory.ConcreteClass => !activeTypes.Contains(type) && CanSafelyInitializeDefault(Activator.CreateInstance(type, true)!, activeTypes),
            TypeCategory.AbstractOrInterface => EvaluateAbstractSafety(type, activeTypes),
            _ => false,
        };

    private static bool EvaluateAbstractSafety(Type type, HashSet<Type> activeTypes)
    {
        var concrete = FindConcreteAssignableType(type);
        return concrete == null || !activeTypes.Contains(concrete) && CanSafelyInitializeDefault(Activator.CreateInstance(concrete)!, activeTypes);
    }

    private static bool TryCreateDefaultValue(Type type, out object? value, out bool recurse)
    {
        value = null;
        recurse = false;

        switch (ClassifyType(type))
        {
            case TypeCategory.Object:
                return false;
            case TypeCategory.String:
                value = string.Empty;
                return true;
            case TypeCategory.Collection:
                value = type.IsArray ? Array.CreateInstance(type.GetElementType()!, 0) : Activator.CreateInstance(type);
                return value != null;
            case TypeCategory.ConcreteClass:
                value = Activator.CreateInstance(type, true);
                recurse = value != null;
                return value != null;
            case TypeCategory.ValueType:
                return false;
            case TypeCategory.AbstractOrInterface:
                var concrete = FindConcreteAssignableType(type);
                if (concrete == null) return false;
                value = Activator.CreateInstance(concrete);
                recurse = value != null;
                return value != null;
        }

        return false;
    }

    private static bool IsConcreteCollectionLike(Type type) =>
        !type.IsAbstract && !type.IsInterface &&
        (typeof(IDictionary).IsAssignableFrom(type) ||
         typeof(IList).IsAssignableFrom(type) ||
         type.IsArray ||
         type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>));

    private static Type? FindConcreteAssignableType(Type target)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException e) {
                types = e.Types.Where(t => t != null).Cast<Type>().ToArray();
            }

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
