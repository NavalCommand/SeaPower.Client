using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SeaPower;

namespace NavalCommand.SeaPower.Client;

internal sealed class NativeUnitTypeDefinition
{
    public string Id { get; set; }
    public int SubCategory { get; set; }
    public IReadOnlyList<PlatformRole> PlatformRoles { get; set; }
    public IReadOnlyList<string> Loadouts { get; set; }
    public string[] NativeRoles { get; set; }
}

internal sealed class NativeUnitTypeCatalog
{
    const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
    const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
    static readonly Assembly GameAssembly = typeof(GameMain).Assembly;
    readonly Dictionary<string, NativeUnitTypeDefinition> unitTypes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, NativeUnitTypeDefinition> candidates = new();
    readonly FieldInfo loadoutVariantField;

    public NativeUnitTypeCatalog()
    {
        loadoutVariantField = typeof(ObjectBaseParameters).GetField("_loadoutVariant", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        ReadNativeCatalog();
    }

    public IReadOnlyCollection<NativeUnitTypeDefinition> UnitTypes => unitTypes.Values;

    public NativeUnitTypeDefinition GetOrCreate(ObjectBase unit)
    {
        string typeId = GetTypeId(unit._obp);
        if (unitTypes.TryGetValue(typeId, out NativeUnitTypeDefinition definition))
        {
            return definition;
        }
        definition = ReadDefinition(unit._obp, typeId, Array.Empty<string>());
        unitTypes[typeId] = definition;
        return definition;
    }

    public NativeUnitTypeDefinition GetCandidate(int candidateId)
    {
        return candidates.TryGetValue(candidateId, out NativeUnitTypeDefinition definition) ? definition : null;
    }

    public string GetSelectedLoadout(ObjectBase unit)
    {
        string typeId = GetTypeId(unit._obp);
        NativeUnitTypeDefinition definition = GetOrCreate(unit);
        object selected = loadoutVariantField?.GetValue(unit._obp);
        string selectedName = selected as string;
        if (!string.IsNullOrWhiteSpace(selectedName) && definition.Loadouts.Any(name => string.Equals(name, selectedName, StringComparison.OrdinalIgnoreCase)))
        {
            return LoadoutId(typeId, selectedName);
        }

        return LoadoutId(typeId, "empty");
    }

    public static string LoadoutId(string typeId, string loadoutName)
    {
        string normalizedName = string.IsNullOrWhiteSpace(loadoutName) ? "empty" : loadoutName.Trim();
        return typeId + "::loadout::" + Uri.EscapeDataString(normalizedName);
    }

    static string GetTypeId(ObjectBaseParameters parameters)
    {
        if (parameters == null)
        {
            throw new InvalidOperationException("native_unit_metadata_unavailable");
        }
        string folder = ReadString(parameters, "_objectIniFolder");
        string name = ReadString(parameters, "_objectIniName");
        string typeId = ClassificationMapping.CreateTypeId(folder, name);
        if (typeId.Length == 0)
        {
            throw new InvalidOperationException("native_unit_type_identity_unavailable");
        }
        return typeId;
    }

    void ReadNativeCatalog()
    {
        Type metadataProviderType = GameAssembly.GetType("SeaPower.UnitMetadataProvider", false)
            ?? GameAssembly.GetTypes().FirstOrDefault(type => type.Name == "UnitMetadataProvider");
        FieldInfo unitDataField = metadataProviderType?.GetField("UnitData", PublicStatic);
        FieldInfo unitIdMapField = metadataProviderType?.GetField("UnitIdMap", PublicStatic);
        if (unitDataField?.GetValue(null) is IDictionary unitData)
        {
            foreach (DictionaryEntry entry in unitData)
            {
                object key = entry.Key;
                object value = entry.Value;
                string folder = ReadTupleString(key, 1);
                string name = ReadTupleString(key, 2);
                object parameters = ReadTupleItem(value, 2);
                string id = ClassificationMapping.CreateTypeId(ReadString(parameters, "_objectIniFolder", folder), ReadString(parameters, "_objectIniName", name));
                if (id.Length == 0 || IsLoadoutUnit(parameters))
                {
                    continue;
                }

                NativeUnitTypeDefinition definition = ReadDefinition(parameters, id, ReadLoadoutNames(parameters));
                unitTypes[id] = definition;
            }
        }

        if (unitIdMapField?.GetValue(null) is IDictionary unitIdMap)
        {
            foreach (DictionaryEntry entry in unitIdMap)
            {
                if (!(entry.Key is int candidateId))
                {
                    continue;
                }
                object parameters = entry.Value;
                if (IsLoadoutUnit(parameters))
                {
                    continue;
                }
                string typeId = ClassificationMapping.CreateTypeId(ReadString(parameters, "_objectIniFolder"), ReadString(parameters, "_objectIniName"));
                if (typeId.Length == 0)
                {
                    continue;
                }
                if (!unitTypes.TryGetValue(typeId, out NativeUnitTypeDefinition definition))
                {
                    definition = ReadDefinition(parameters, typeId, ReadLoadoutNames(parameters));
                    unitTypes[typeId] = definition;
                }
                candidates[candidateId] = definition;
            }
        }
    }

    NativeUnitTypeDefinition ReadDefinition(object parameters, string typeId, IReadOnlyList<string> loadoutNames)
    {
        string[] nativeRoles = ReadSequence(parameters, "_unitRoles").ToArray();
        string typeAbbreviation = ReadString(parameters, "_typeAbbr");
        string typeFullName = ReadString(parameters, "_typeFullName");
        string nativeObjectType = ReadString(parameters, "_objectType");
        int subCategory = ClassificationMapping.MapAuthoritativeSubCategory(typeId, typeAbbreviation, typeFullName, nativeObjectType, nativeRoles);
        IReadOnlyList<PlatformRole> mappedRoles = ClassificationMapping.MapNativeRoles(nativeRoles);
        var loadouts = new List<string>(loadoutNames);
        if (!loadouts.Contains("empty", StringComparer.OrdinalIgnoreCase))
        {
            loadouts.Add("empty");
        }
        return new NativeUnitTypeDefinition
        {
            Id = typeId,
            SubCategory = subCategory,
            PlatformRoles = mappedRoles,
            Loadouts = loadouts.AsReadOnly(),
            NativeRoles = nativeRoles
        };
    }

    static IReadOnlyList<string> ReadLoadoutNames(object parameters)
    {
        var names = new List<string>();
        object loadouts = ReadMember(parameters, "_loadouts");
        if (loadouts is IDictionary loadoutMap)
        {
            foreach (DictionaryEntry entry in loadoutMap)
            {
                string name = ReadString(entry.Value, "Name", entry.Key?.ToString() ?? "");
                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name.Trim());
                }
            }
        }
        object fixedLoadout = ReadMember(parameters, "_fixedLoadout");
        string fixedName = ReadString(fixedLoadout, "Name");
        if (!string.IsNullOrWhiteSpace(fixedName) && !names.Contains(fixedName, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(fixedName.Trim());
        }
        return names;
    }

    static bool IsLoadoutUnit(object parameters)
    {
        return ReadBool(parameters, "IsLoadoutUnit");
    }

    static IEnumerable<string> ReadSequence(object value, string memberName)
    {
        object memberValue = ReadMember(value, memberName);
        if (memberValue is not IEnumerable sequence || memberValue is string)
        {
            yield break;
        }
        foreach (object item in sequence)
        {
            if (item != null)
            {
                yield return item.ToString();
            }
        }
    }

    static string ReadTupleString(object tuple, int itemNumber)
    {
        return ReadTupleItem(tuple, itemNumber)?.ToString() ?? "";
    }

    static object ReadTupleItem(object tuple, int itemNumber)
    {
        if (tuple == null)
        {
            return null;
        }
        FieldInfo field = tuple.GetType().GetField("Item" + itemNumber, BindingFlags.Public | BindingFlags.Instance);
        return field?.GetValue(tuple);
    }

    static string ReadString(object instance, string memberName, string fallback = "")
    {
        object value = ReadMember(instance, memberName);
        return value == null ? fallback : value.ToString();
    }

    static bool ReadBool(object instance, string memberName)
    {
        object value = ReadMember(instance, memberName);
        return value is bool boolean && boolean;
    }

    static object ReadMember(object instance, string memberName)
    {
        if (instance == null)
        {
            return null;
        }
        Type type = instance.GetType();
        FieldInfo field = type.GetField(memberName, PublicInstance | BindingFlags.NonPublic);
        if (field != null)
        {
            return field.GetValue(instance);
        }
        PropertyInfo property = type.GetProperty(memberName, PublicInstance | BindingFlags.NonPublic);
        return property?.GetValue(instance);
    }
}
