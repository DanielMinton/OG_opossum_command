using System.Text;
using System.Text.Json;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Exports;

/// <summary>Where a carryover came from (the flow's <c>source</c> field).</summary>
public enum CarryoverSource
{
    Todo,
    Email,
    Teams,
    Other,
}

/// <summary>One carryover item. Plain text only: nothing in it is opened, run, or followed.</summary>
/// <param name="Title">Single-line title, at most <see cref="CarryoverFileParser.MaxStringChars"/> characters.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="Due">Optional due date.</param>
/// <param name="Note">Optional single-line note.</param>
public sealed record CarryoverItem(string Title, CarryoverSource Source, DateOnly? Due, string? Note);

/// <summary>The parsed <c>tomorrow.json</c>.</summary>
/// <param name="ForDate">The local day the items are meant for; the app shows them only on that day.</param>
/// <param name="GeneratedAt">When the flow wrote the file.</param>
/// <param name="Items">At most <see cref="CarryoverFileParser.MaxItems"/> items, in file order.</param>
public sealed record CarryoverSnapshot(DateOnly ForDate, DateTimeOffset GeneratedAt, IReadOnlyList<CarryoverItem> Items);

/// <summary>Either a snapshot or readable errors.</summary>
public sealed record CarryoverParseResult(CarryoverSnapshot? Snapshot, IReadOnlyList<WorkflowValidationError> Errors)
{
    public bool IsValid => Snapshot is not null;

    /// <summary>The text was not JSON at all (typically a file OneDrive has only half written).</summary>
    public bool IsMalformedJson { get; init; }
}

/// <summary>
/// Strict parser for <c>tomorrow.json</c> (schema v1, ADR-051), which the user's Power Automate flow writes and the app
/// treats as untrusted input: duplicate keys and unknown properties (top level and per item) are errors, the file may
/// be at most <see cref="MaxFileBytes"/>, hold at most <see cref="MaxItems"/> items, and each string at most
/// <see cref="MaxStringChars"/> characters after control characters are turned into spaces; a NUL anywhere is refused.
/// </summary>
public static class CarryoverFileParser
{
    /// <summary>The only <c>schemaVersion</c> this build reads.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Largest file read (64 KB).</summary>
    public const int MaxFileBytes = 64 * 1024;

    /// <summary>Most items in one file.</summary>
    public const int MaxItems = 10;

    /// <summary>Longest string value (after control characters are stripped and the value is trimmed).</summary>
    public const int MaxStringChars = 200;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        AllowDuplicateProperties = false,
        MaxDepth = 8,
    };

    /// <summary>Parses the file text. <paramref name="source"/> labels errors (the file name).</summary>
    public static CarryoverParseResult Parse(string json, string source)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var errors = new ValidationErrors(source);
        if (json.Contains('\0', StringComparison.Ordinal))
        {
            errors.Add("$", "The file contains a NUL character and is refused.");
            return new CarryoverParseResult(null, errors.Items);
        }

        if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
        {
            errors.Add("$", $"The file is larger than {MaxFileBytes / 1024} KB and is refused.");
            return new CarryoverParseResult(null, errors.Items);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is null ? string.Empty : $" (line {ex.LineNumber + 1}, position {ex.BytePositionInLine + 1})";
            errors.Add("$", $"Not valid JSON{where}: {ex.Message}");
            return new CarryoverParseResult(null, errors.Items) { IsMalformedJson = true };
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add("$", "The file must contain one JSON object.");
                return new CarryoverParseResult(null, errors.Items);
            }

            var snapshot = Read(new JsonObjectReader(document.RootElement, "$", errors));
            return snapshot is null || errors.HasErrors
                ? new CarryoverParseResult(null, errors.Items)
                : new CarryoverParseResult(snapshot, []);
        }
    }

    private static CarryoverSnapshot? Read(JsonObjectReader root)
    {
        root.RejectUnknownProperties("schemaVersion", "forDate", "generatedAt", "items");

        var version = root.RequiredInt("schemaVersion", 1, int.MaxValue);
        if (version is not null && version != SchemaVersion)
        {
            root.AddError("schemaVersion", $"This version of the app reads carryovers schemaVersion {SchemaVersion} (file says {version}).");
        }

        var forDate = ReadDate(root, "forDate", required: true);
        var generatedAt = ReadTimestamp(root, "generatedAt");
        var readers = root.RequiredObjectArray("items");
        if (readers is null)
        {
            return null;
        }

        if (readers.Count > MaxItems)
        {
            root.AddError("items", $"'items' lists {readers.Count} entries; at most {MaxItems} are allowed (take the first {MaxItems} in the flow).");
            return null;
        }

        var items = new List<CarryoverItem>();
        var ok = true;
        foreach (var reader in readers)
        {
            var item = ReadItem(reader);
            if (item is null)
            {
                ok = false;
            }
            else
            {
                items.Add(item);
            }
        }

        return ok && version is not null && forDate is not null && generatedAt is not null
            ? new CarryoverSnapshot(forDate.Value, generatedAt.Value, items)
            : null;
    }

    private static CarryoverItem? ReadItem(JsonObjectReader reader)
    {
        reader.RejectUnknownProperties("title", "source", "due", "note");
        var title = CleanText(reader, "title", required: true);
        var sourceText = CleanText(reader, "source", required: true);
        CarryoverSource? source = null;
        if (sourceText is not null)
        {
            source = sourceText switch
            {
                "todo" => CarryoverSource.Todo,
                "email" => CarryoverSource.Email,
                "teams" => CarryoverSource.Teams,
                "other" => CarryoverSource.Other,
                _ => null,
            };
            if (source is null)
            {
                reader.AddError("source", $"'source' must be one of todo, email, teams, other (got '{sourceText}').");
            }
        }

        var due = ReadDate(reader, "due", required: false);
        var dueInvalid = reader.Has("due") && due is null && !IsEmpty(reader, "due");
        var note = CleanText(reader, "note", required: false);
        if (title is null || source is null || dueInvalid || (reader.Has("note") && note is null && !IsEmpty(reader, "note")))
        {
            return null;
        }

        return new CarryoverItem(title, source.Value, due, note);
    }

    /// <summary>
    /// A string with control characters turned into spaces, white space collapsed, and trimmed. <c>null</c>/<c>""</c> mean
    /// "absent" for optional fields. Longer than <see cref="MaxStringChars"/> is an error, as is a NUL.
    /// </summary>
    private static string? CleanText(JsonObjectReader reader, string name, bool required)
    {
        if (!reader.Element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                reader.AddError(name, $"Missing required property '{name}' (a non-empty string).");
            }

            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            reader.AddError(name, $"'{name}' must be a string.");
            return null;
        }

        var raw = value.GetString() ?? string.Empty;
        if (raw.Contains('\0', StringComparison.Ordinal))
        {
            reader.AddError(name, $"'{name}' contains a NUL character (\\u0000) and is refused.");
            return null;
        }

        var text = ExportText.SingleLine(raw);
        if (text.Length == 0)
        {
            if (required)
            {
                reader.AddError(name, $"'{name}' must not be blank.");
            }

            return null;
        }

        if (text.Length > MaxStringChars)
        {
            reader.AddError(name, $"'{name}' is {text.Length} characters long; at most {MaxStringChars} are allowed.");
            return null;
        }

        return text;
    }

    private static bool IsEmpty(JsonObjectReader reader, string name) =>
        reader.Element.TryGetProperty(name, out var value)
        && (value.ValueKind == JsonValueKind.Null || (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())));

    private static DateOnly? ReadDate(JsonObjectReader reader, string name, bool required)
    {
        if (!required && IsEmpty(reader, name))
        {
            return null;
        }

        var text = CleanText(reader, name, required);
        if (text is null)
        {
            return null;
        }

        if (StrictJsonValues.TryParseDate(text, out var date))
        {
            return date;
        }

        reader.AddError(name, $"'{name}' must be a date like \"2026-10-24\" (got '{text}').");
        return null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonObjectReader reader, string name)
    {
        var text = CleanText(reader, name, required: true);
        if (text is null)
        {
            return null;
        }

        if (StrictJsonValues.TryParseOffsetTimestamp(text, out var value))
        {
            return value;
        }

        reader.AddError(name, $"'{name}' must be an ISO 8601 timestamp with an offset, such as \"2026-10-23T16:30:00-07:00\" or \"2026-10-23T23:30:00Z\" (got '{text}').");
        return null;
    }
}
