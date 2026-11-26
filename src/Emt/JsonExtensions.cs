using Newtonsoft.Json.Linq;
using System.Diagnostics.CodeAnalysis;

namespace FSDEV.MOD.Emt.Emt;

public static class JsonExtensions
{
    public static bool TryGetValue<T>(this JObject stepJson,
       string key,
       JTokenType expectedType,
       [NotNullWhen(true)][NotNullIfNotNull("defaultValue")] out T? value,
       [NotNullWhen(false)] out string? errorMessage,
       bool optional = false,
       T? defaultValue = default)
    {
        errorMessage = null;
        value = defaultValue;
        if (!stepJson.TryGetValue(key, out var token))
        {
            if (!optional)
            {
                errorMessage = $"Field '{key}' missing";
            }

            return optional;
        }

        if (token.Type != expectedType)
        {
            errorMessage = $"Field '{key}' has wrong type";
            return false;
        }

        if (token.ToObject<T>() is not T tokenValue)
        {
            errorMessage = $"Field '{key}' has wrong value";
            return false;
        }

        value = tokenValue;

        return true;
    }

    public static bool TryGetGuid(this JObject stepJson,
       string key,
       out Guid value,
       [NotNullWhen(false)] out string? errorMessage,
       bool optional = false)
    {
        value = Guid.Empty;
        errorMessage = null;

        if (!stepJson.TryGetValue(key, out var token))
        {
            if (!optional)
            {
                errorMessage = $"Field '{key}' missing";
            }

            return optional;
        }

        if (token.Type != JTokenType.String)
        {
            errorMessage = $"Field '{key}' has wrong type";
            return false;
        }

        if (!Guid.TryParse(token.ToObject<string>(), out var id))
        {
            errorMessage = $"Field '{key}' has wrong value";
            return false;
        }

        value = id;

        return true;
    }
}