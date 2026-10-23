using System.Globalization;
using System.Text.Json;

namespace OG_opossum_command.Core.Workflows;

/// <summary>
/// Reads one JSON object strictly and records readable errors instead of throwing.
/// Every accessor returns <c>null</c> when the property is missing or malformed, after recording why.
/// </summary>
public sealed class JsonObjectReader
{
    private readonly ValidationErrors _errors;

    public JsonObjectReader(JsonElement element, string jsonPath, ValidationErrors errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Element must be a JSON object.", nameof(element));
        }

        Element = element;
        JsonPath = jsonPath;
        _errors = errors;
    }

    /// <summary>The underlying object.</summary>
    public JsonElement Element { get; }

    /// <summary>Dotted path of this object inside the document (<c>$</c> for the root).</summary>
    public string JsonPath { get; }

    /// <summary>Records an error against a property of this object (or the object itself when <paramref name="name"/> is null).</summary>
    public void AddError(string? name, string message) => _errors.Add(PathOf(name), message);

    /// <summary>Whether the property exists (of any type).</summary>
    public bool Has(string name) => Element.TryGetProperty(name, out _);

    /// <summary>Records an error for every property that is not in <paramref name="allowed"/>. Typos are the usual cause.</summary>
    public void RejectUnknownProperties(params string[] allowed)
    {
        foreach (var property in Element.EnumerateObject())
        {
            if (Array.IndexOf(allowed, property.Name) < 0)
            {
                _errors.Add(PathOf(property.Name), $"Unknown property '{property.Name}'. Allowed: {string.Join(", ", allowed)}.");
            }
        }
    }

    /// <summary>A required non-blank string.</summary>
    public string? RequiredString(string name)
    {
        if (!Element.TryGetProperty(name, out var value))
        {
            _errors.Add(PathOf(name), $"Missing required property '{name}' (a non-empty string).");
            return null;
        }

        return ReadString(name, value);
    }

    /// <summary>An optional string; <c>null</c> when absent. Blank strings are an error.</summary>
    public string? OptionalString(string name) =>
        Element.TryGetProperty(name, out var value) ? ReadString(name, value) : null;

    /// <summary>
    /// An optional string for feeds that emit <c>null</c> or <c>""</c> for an empty field: absent, JSON null, and blank all
    /// yield <c>null</c> without an error; any other non-string is an error. The value is trimmed.
    /// </summary>
    public string? OptionalText(string name)
    {
        if (!Element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            _errors.Add(PathOf(name), $"'{name}' must be a string (or null).");
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>An optional integer within <paramref name="min"/>..<paramref name="max"/>; <c>null</c> when absent.</summary>
    public int? OptionalInt(string name, int min, int max)
    {
        if (!Element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            _errors.Add(PathOf(name), $"'{name}' must be a whole number between {min} and {max}.");
            return null;
        }

        if (number < min || number > max)
        {
            _errors.Add(PathOf(name), $"'{name}' must be between {min} and {max}, but was {number.ToString(CultureInfo.InvariantCulture)}.");
            return null;
        }

        return number;
    }

    /// <summary>A required integer within <paramref name="min"/>..<paramref name="max"/>.</summary>
    public int? RequiredInt(string name, int min, int max)
    {
        if (!Element.TryGetProperty(name, out _))
        {
            _errors.Add(PathOf(name), $"Missing required property '{name}' (a whole number between {min} and {max}).");
            return null;
        }

        return OptionalInt(name, min, max);
    }

    /// <summary>An optional boolean; <c>null</c> when absent.</summary>
    public bool? OptionalBool(string name)
    {
        if (!Element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        _errors.Add(PathOf(name), $"'{name}' must be true or false.");
        return null;
    }

    /// <summary>An optional array of non-blank strings; <c>null</c> when absent.</summary>
    public IReadOnlyList<string>? OptionalStringArray(string name)
    {
        if (!Element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            _errors.Add(PathOf(name), $"'{name}' must be an array of strings.");
            return null;
        }

        var result = new List<string>();
        var index = 0;
        var ok = true;
        foreach (var item in value.EnumerateArray())
        {
            var itemPath = $"{PathOf(name)}[{index.ToString(CultureInfo.InvariantCulture)}]";
            if (item.ValueKind != JsonValueKind.String)
            {
                _errors.Add(itemPath, "Must be a string.");
                ok = false;
            }
            else
            {
                result.Add(item.GetString()!);
            }

            index++;
        }

        return ok ? result : null;
    }

    /// <summary>An optional nested object; <c>null</c> when absent.</summary>
    public JsonObjectReader? OptionalObject(string name)
    {
        if (!Element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            _errors.Add(PathOf(name), $"'{name}' must be an object.");
            return null;
        }

        return new JsonObjectReader(value, PathOf(name), _errors);
    }

    /// <summary>A required nested object.</summary>
    public JsonObjectReader? RequiredObject(string name)
    {
        if (!Element.TryGetProperty(name, out _))
        {
            _errors.Add(PathOf(name), $"Missing required property '{name}' (an object).");
            return null;
        }

        return OptionalObject(name);
    }

    /// <summary>A required array of objects; items that are not objects are reported and skipped.</summary>
    public IReadOnlyList<JsonObjectReader>? RequiredObjectArray(string name)
    {
        if (!Element.TryGetProperty(name, out var value))
        {
            _errors.Add(PathOf(name), $"Missing required property '{name}' (an array of objects).");
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            _errors.Add(PathOf(name), $"'{name}' must be an array of objects.");
            return null;
        }

        var result = new List<JsonObjectReader>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var itemPath = $"{PathOf(name)}[{index.ToString(CultureInfo.InvariantCulture)}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                _errors.Add(itemPath, "Must be an object.");
            }
            else
            {
                result.Add(new JsonObjectReader(item, itemPath, _errors));
            }

            index++;
        }

        return result;
    }

    private string PathOf(string? name) => name is null ? JsonPath : (JsonPath == "$" ? name : $"{JsonPath}.{name}");

    private string? ReadString(string name, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            _errors.Add(PathOf(name), $"'{name}' must be a string.");
            return null;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            _errors.Add(PathOf(name), $"'{name}' must not be empty.");
            return null;
        }

        return text;
    }
}
