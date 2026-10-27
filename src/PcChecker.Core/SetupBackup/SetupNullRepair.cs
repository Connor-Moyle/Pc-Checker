using System.Collections;
using System.Reflection;

namespace PcChecker.Core.SetupBackup;

/// <summary>
/// Makes settings read from a hand-edited or damaged setup file safe to use. A file can hold <c>null</c> where the
/// app expects a value (<c>"Presets": [null]</c>, <c>"Hotkey": null</c>, <c>{"Name": null}</c>); the pages and
/// background services that read those lists would then fail on every start. Following the nullable annotations of
/// the settings classes:
/// <list type="bullet">
/// <item>a null item in a list, or a null value in a dictionary, is removed;</item>
/// <item>a list item whose <c>Name</c> is null (an unnamed profile or preset) is removed;</item>
/// <item>any other null text or object gets its default value (the value a new instance has);</item>
/// <item>an item that cannot be made whole is removed.</item>
/// </list>
/// Properties declared as nullable (<c>string?</c>) are left alone.
/// </summary>
public static class SetupNullRepair
{
    private const int MaxDepth = 16;

    /// <summary>
    /// Repairs <paramref name="target"/> in place, or only its properties named in <paramref name="onlyProperties"/>.
    /// Returns how many list or dictionary entries were removed.
    /// </summary>
    public static int Repair(object target, IEnumerable<string>? onlyProperties = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        var walker = new Walker();
        walker.RepairObject(target, 0, onlyProperties?.ToHashSet(StringComparer.Ordinal));
        return walker.Removed;
    }

    private sealed class Walker
    {
        private readonly NullabilityInfoContext _nullability = new();

        public int Removed { get; private set; }

        /// <summary>False when <paramref name="obj"/> has a required value that is missing and has no default.</summary>
        public bool RepairObject(object obj, int depth, HashSet<string>? only = null)
        {
            if (depth > MaxDepth)
            {
                return true;
            }

            var type = obj.GetType();
            object? defaults = null;
            bool defaultsMade = false;
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0 || property.PropertyType.IsValueType
                    || (only is not null && !only.Contains(property.Name)))
                {
                    continue;
                }

                var info = _nullability.Create(property);
                var value = property.GetValue(obj);
                bool required = info.ReadState == NullabilityState.NotNull;
                if (value is not null && !RepairValue(value, info, depth + 1))
                {
                    value = null;
                    required = true;
                }

                if (value is not null || !required)
                {
                    continue;
                }

                if (!property.CanWrite || property.SetMethod is not { IsPublic: true })
                {
                    return false;
                }

                if (!defaultsMade)
                {
                    defaults = TryCreate(type);
                    defaultsMade = true;
                }

                var replacement = (defaults is null ? null : property.GetValue(defaults)) ?? Fallback(property.PropertyType);
                if (replacement is null)
                {
                    return false;
                }

                property.SetValue(obj, replacement);
            }

            return true;
        }

        private bool RepairValue(object value, NullabilityInfo info, int depth)
        {
            switch (value)
            {
                case string:
                    return true;
                case IDictionary dictionary when !dictionary.IsReadOnly:
                    RepairDictionary(dictionary, info.GenericTypeArguments.Length == 2 ? info.GenericTypeArguments[1] : null, depth);
                    return true;
                case IList list when !list.IsReadOnly && !list.IsFixedSize:
                    RepairList(list, info.GenericTypeArguments.Length == 1 ? info.GenericTypeArguments[0] : null, depth);
                    return true;
                default:
                    return !IsSettingsType(value.GetType()) || RepairObject(value, depth);
            }
        }

        private void RepairList(IList list, NullabilityInfo? item, int depth)
        {
            bool itemsRequired = item is null || item.ReadState != NullabilityState.Nullable;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var entry = list[i];
                bool remove = entry is null
                    ? itemsRequired
                    : HasNullName(entry) || (item is not null && !RepairValue(entry, item, depth));
                if (remove)
                {
                    list.RemoveAt(i);
                    Removed++;
                }
            }
        }

        private void RepairDictionary(IDictionary dictionary, NullabilityInfo? item, int depth)
        {
            bool valuesRequired = item is null || item.ReadState != NullabilityState.Nullable;
            var remove = new List<object>();
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Value is null ? valuesRequired : item is not null && !RepairValue(entry.Value, item, depth))
                {
                    remove.Add(entry.Key);
                }
            }

            foreach (var key in remove)
            {
                dictionary.Remove(key);
                Removed++;
            }
        }

        /// <summary>A profile, preset or other named item without a name.</summary>
        private bool HasNullName(object entry)
        {
            var name = entry.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            return name is not null && name.PropertyType == typeof(string) && name.CanRead && name.GetIndexParameters().Length == 0
                && name.GetValue(entry) is null && _nullability.Create(name).ReadState == NullabilityState.NotNull;
        }
    }

    /// <summary>Classes of this assembly (the settings and profile types); framework types are not walked into.</summary>
    private static bool IsSettingsType(Type type) =>
        type.IsClass && type.Assembly == typeof(SetupNullRepair).Assembly;

    private static object? Fallback(Type type) => type == typeof(string) ? string.Empty : TryCreate(type);

    private static object? TryCreate(Type type)
    {
        if (type.IsAbstract || type.IsInterface || type.GetConstructor(Type.EmptyTypes) is null)
        {
            return null;
        }

        try
        {
            return Activator.CreateInstance(type);
        }
        catch (Exception ex) when (ex is TargetInvocationException or MemberAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}
