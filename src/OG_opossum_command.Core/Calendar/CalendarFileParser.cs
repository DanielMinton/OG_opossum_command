using System.Text.Json;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Calendar;

/// <summary>Either a snapshot or the readable errors that stopped the file from parsing.</summary>
public sealed record CalendarParseResult(CalendarSnapshot? Snapshot, IReadOnlyList<WorkflowValidationError> Errors)
{
    public bool IsValid => Snapshot is not null;

    /// <summary>The text was not JSON at all (typically a file OneDrive has only half written), as opposed to JSON in the wrong shape.</summary>
    public bool IsMalformedJson { get; init; }
}

/// <summary>
/// Strict parser for the calendar file the Power Automate flow writes (docs/POWER_AUTOMATE_FLOW.md, ADR-039).
/// Unknown properties, wrong types, a wrong <c>schemaVersion</c>, and timestamps without an offset are errors;
/// optional text fields tolerate <c>null</c> and <c>""</c> because the flow emits those for empty values.
/// </summary>
public static class CalendarFileParser
{
    /// <summary>The only <c>schemaVersion</c> this build reads.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Upper bound on events in one file; the flow asks for about a day and a half.</summary>
    public const int MaxEvents = 500;

    /// <summary>Subjects longer than this are cut (a subject is not a schema matter).</summary>
    public const int MaxSubjectLength = 200;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        AllowDuplicateProperties = false,
    };

    /// <summary>Parses the file text. <paramref name="source"/> labels errors (the file path).</summary>
    public static CalendarParseResult Parse(string json, string source)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var errors = new ValidationErrors(source);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is null ? string.Empty : $" (line {ex.LineNumber + 1}, position {ex.BytePositionInLine + 1})";
            errors.Add("$", $"Not valid JSON{where}: {ex.Message}");
            return new CalendarParseResult(null, errors.Items) { IsMalformedJson = true };
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add("$", "The calendar file must contain one JSON object.");
                return new CalendarParseResult(null, errors.Items);
            }

            var snapshot = Read(new JsonObjectReader(document.RootElement, "$", errors));
            return snapshot is null || errors.HasErrors
                ? new CalendarParseResult(null, errors.Items)
                : new CalendarParseResult(snapshot, []);
        }
    }

    private static CalendarSnapshot? Read(JsonObjectReader root)
    {
        root.RejectUnknownProperties("schemaVersion", "generatedAt", "source", "timeZone", "events");

        var version = root.RequiredInt("schemaVersion", 1, int.MaxValue);
        if (version is not null && version != SchemaVersion)
        {
            root.AddError("schemaVersion", $"This version of the app reads calendar schemaVersion {SchemaVersion} (file says {version}).");
        }

        var generatedAt = ReadTimestamp(root, "generatedAt", required: true, allowDateOnly: false);
        var source = root.OptionalText("source");
        var timeZone = root.OptionalText("timeZone");

        var eventReaders = root.RequiredObjectArray("events");
        if (eventReaders is null)
        {
            return null;
        }

        if (eventReaders.Count > MaxEvents)
        {
            root.AddError("events", $"'events' lists {eventReaders.Count} items; at most {MaxEvents} are read.");
            return null;
        }

        var events = new List<CalendarEvent>();
        var ok = true;
        foreach (var reader in eventReaders)
        {
            var item = ReadEvent(reader);
            if (item is null)
            {
                ok = false;
            }
            else
            {
                events.Add(item);
            }
        }

        if (!ok || generatedAt is null || version is null)
        {
            return null;
        }

        return new CalendarSnapshot(generatedAt.Value, source, timeZone, events.OrderBy(e => e.Start).ThenBy(e => e.Subject, StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    private static CalendarEvent? ReadEvent(JsonObjectReader reader)
    {
        reader.RejectUnknownProperties("subject", "start", "end", "isAllDay", "location", "link", "showAs");

        var isAllDay = reader.OptionalBool("isAllDay") ?? false;
        var subject = reader.OptionalText("subject") ?? "(no subject)";
        if (subject.Length > MaxSubjectLength)
        {
            subject = subject[..MaxSubjectLength];
        }

        var start = ReadTimestamp(reader, "start", required: true, allowDateOnly: isAllDay);
        var end = ReadTimestamp(reader, "end", required: true, allowDateOnly: isAllDay);
        if (start is not null && end is not null && end < start)
        {
            reader.AddError("end", "'end' is before 'start'.");
            return null;
        }

        var location = reader.OptionalText("location");
        var showAs = reader.OptionalText("showAs")?.ToLowerInvariant();

        Uri? link = null;
        var linkText = reader.OptionalText("link");
        if (linkText is not null)
        {
            if (Uri.TryCreate(linkText, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(parsed.Host))
            {
                link = parsed;
            }
            else
            {
                reader.AddError("link", "'link' must be an absolute https:// address or empty. The value is not shown because meeting links can carry tokens.");
                return null;
            }
        }

        return start is null || end is null ? null : new CalendarEvent(subject, start.Value, end.Value, isAllDay, location, link, showAs);
    }

    /// <summary>
    /// An ISO 8601 timestamp that carries an offset (<c>2026-10-22T16:00:00Z</c>, <c>2026-10-22T09:00:00.0000000-07:00</c>).
    /// Offset-less values are rejected because the Outlook connector exposes both kinds and the wrong one shifts every
    /// event by hours. A bare date is accepted only where <paramref name="allowDateOnly"/> (all-day events).
    /// </summary>
    private static DateTimeOffset? ReadTimestamp(JsonObjectReader reader, string name, bool required, bool allowDateOnly)
    {
        var text = required ? reader.RequiredString(name) : reader.OptionalString(name);
        if (text is null)
        {
            return null;
        }

        text = text.Trim();
        if (StrictJsonValues.TryParseOffsetTimestamp(text, out var value))
        {
            return value;
        }

        if (allowDateOnly && StrictJsonValues.TryParseDate(text, out var date))
        {
            return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }

        reader.AddError(name, $"'{name}' must be an ISO 8601 timestamp with an offset, such as \"2026-10-22T16:00:00Z\" or \"2026-10-22T09:00:00-07:00\" (got '{text}'). In the flow, pick the start/end field whose sample ends in Z or +hh:mm; see docs/POWER_AUTOMATE_FLOW.md.");
        return null;
    }
}
