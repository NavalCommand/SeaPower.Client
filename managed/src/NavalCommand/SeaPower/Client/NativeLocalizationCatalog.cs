using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SeaPower;

namespace NavalCommand.SeaPower.Client;

internal sealed class NativeLocalizationCatalog
{
    const BindingFlags StaticMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    const BindingFlags InstanceMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly Assembly GameAssembly = typeof(GameMain).Assembly;
    readonly Dictionary<string, object> localizationFiles = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<Type, MethodInfo> readMethods = new();
    readonly Dictionary<string, string> loadoutNames = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> ammunitionNames = new(StringComparer.OrdinalIgnoreCase);
    string currentLanguage;

    public string ReadLoadoutDisplayName(string loadoutName)
    {
        if (loadoutNames.TryGetValue(loadoutName ?? "", out string cachedName))
        {
            return cachedName;
        }

        string value = Read("loadout_names", "LoadoutNames", loadoutName, "").Trim();
        string displayName = string.IsNullOrWhiteSpace(value) || string.Equals(value, loadoutName, StringComparison.OrdinalIgnoreCase)
            ? null : value;
        loadoutNames.Add(loadoutName ?? "", displayName);
        return displayName;
    }

    public string ReadAmmunitionDisplayName(string ammunitionId)
    {
        if (ammunitionNames.TryGetValue(ammunitionId ?? "", out string cachedName))
        {
            return cachedName;
        }

        string value = Read("ammunition_names", "AmmunitionNames", ammunitionId, "");
        int separatorIndex = value.IndexOf(',');
        if (separatorIndex >= 0)
        {
            value = value.Substring(0, separatorIndex);
        }
        value = value.Trim();
        string displayName = string.IsNullOrWhiteSpace(value) || string.Equals(value, ammunitionId, StringComparison.OrdinalIgnoreCase)
            ? null : value;
        ammunitionNames.Add(ammunitionId ?? "", displayName);
        return displayName;
    }

    string Read(string fileName, string section, string key, string fallback)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return fallback;
        }

        object localizationFile = GetLocalizationFile(fileName);
        if (localizationFile == null)
        {
            return fallback;
        }

        Type handlerType = localizationFile.GetType();
        if (!readMethods.TryGetValue(handlerType, out MethodInfo readMethod))
        {
            readMethod = FindLocalizationMethod(handlerType.GetMethods(InstanceMembers), "readValue");
            if (readMethod == null)
            {
                return fallback;
            }
            readMethods.Add(handlerType, readMethod);
        }

        object result = InvokeLocalizationMethod(readMethod, localizationFile, section, key, fallback);
        return result as string ?? fallback;
    }

    object GetLocalizationFile(string fileName)
    {
        string language = GetCurrentLanguage();
        string cacheKey = language + "/" + fileName;
        if (localizationFiles.TryGetValue(cacheKey, out object cached))
        {
            return cached;
        }

        Type loaderType = FindGameType("LocalizationLoader");
        MethodInfo openMethod = loaderType == null
            ? null : FindLocalizationMethod(loaderType.GetMethods(StaticMembers), "OpenVirtualLocalizationFile");
        object localizationFile = openMethod == null
            ? null : InvokeLocalizationMethod(openMethod, null, "language_" + language, fileName, fileName);
        localizationFiles.Add(cacheKey, localizationFile);
        return localizationFile;
    }

    string GetCurrentLanguage()
    {
        if (!string.IsNullOrWhiteSpace(currentLanguage))
        {
            return currentLanguage;
        }

        currentLanguage = ReadCurrentLanguage();
        return currentLanguage;
    }

    static string ReadCurrentLanguage()
    {
        Type languageType = FindGameType("LanguageResourceHandler");
        Type singletonType = GameAssembly.GetTypes()
            .FirstOrDefault(type => type.IsGenericTypeDefinition && type.Name == "Singleton`1");
        if (languageType != null && singletonType != null)
        {
            Type closedSingletonType = singletonType.MakeGenericType(languageType);
            object languageHandler = closedSingletonType.GetProperty("Instance", StaticMembers)?.GetValue(null);
            object language = ReadMember(languageHandler, "Language");
            if (language is string languageCode && !string.IsNullOrWhiteSpace(languageCode))
            {
                return languageCode;
            }
        }

        return "en";
    }

    static Type FindGameType(string typeName)
    {
        return GameAssembly.GetTypes().FirstOrDefault(type => type.Name == typeName);
    }

    static MethodInfo FindLocalizationMethod(IEnumerable<MethodInfo> methods, string methodName)
    {
        return methods.FirstOrDefault(method =>
        {
            if (method.Name != methodName)
            {
                return false;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length < 3
                || parameters[0].ParameterType != typeof(string)
                || parameters[1].ParameterType != typeof(string)
                || parameters[2].ParameterType != typeof(string))
            {
                return false;
            }

            return parameters.Skip(3).All(parameter => parameter.IsOptional);
        });
    }

    static object InvokeLocalizationMethod(MethodInfo method, object instance, string first, string second, string third)
    {
        ParameterInfo[] parameters = method.GetParameters();
        var arguments = new object[parameters.Length];
        arguments[0] = first;
        arguments[1] = second;
        arguments[2] = third;
        for (int index = 3; index < parameters.Length; ++index)
        {
            arguments[index] = parameters[index].DefaultValue;
        }

        return method.Invoke(instance, arguments);
    }

    static object ReadMember(object instance, string memberName)
    {
        if (instance == null)
        {
            return null;
        }

        Type type = instance.GetType();
        FieldInfo field = type.GetField(memberName, InstanceMembers);
        if (field != null)
        {
            return field.GetValue(instance);
        }

        PropertyInfo property = type.GetProperty(memberName, InstanceMembers);
        return property?.GetValue(instance);
    }
}
