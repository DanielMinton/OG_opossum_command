namespace OG_opossum_command.Core.Engine;

/// <summary>
/// Engine-wide resource locks (spec §06, ADR-033). Keys are normalised by <see cref="Normalize"/>; a step holding
/// a key blocks every other step (in any run) that wants the same key. Keys are always taken in sorted order, so
/// two steps wanting the same pair can never deadlock.
/// </summary>
public sealed class ResourceLocks
{
    /// <summary>Lock kinds a workflow file may use.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["window", "file", "custom"];

    private readonly Dictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>
    /// Turns a file value such as <c>file:C:\Notes\today.txt</c> or <c>window:Outlook</c> into a canonical key, or
    /// returns an error message. File paths are made absolute and lower-cased; other kinds are kept verbatim.
    /// </summary>
    public static string? Normalize(string value, out string key)
    {
        key = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return "A lock must look like '<kind>:<name>', for example 'window:outlook' or 'file:C:\\Notes\\today.txt'.";
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == value.Length - 1)
        {
            return $"Lock '{value}' must look like '<kind>:<name>'. Kinds: {string.Join(", ", Kinds)}.";
        }

        var kind = value[..colon].Trim().ToLowerInvariant();
        var name = value[(colon + 1)..].Trim();
        if (!Kinds.Contains(kind))
        {
            return $"Lock '{value}' has unknown kind '{kind}'. Kinds: {string.Join(", ", Kinds)}.";
        }

        if (name.Length == 0)
        {
            return $"Lock '{value}' has an empty name.";
        }

        if (kind == "file")
        {
            var expanded = Environment.ExpandEnvironmentVariables(name);
            if (!Path.IsPathFullyQualified(expanded))
            {
                return $"Lock '{value}' must name a full path (%VAR% references are allowed).";
            }

            name = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded)).ToUpperInvariant();
        }

        key = $"{kind}:{name}";
        return null;
    }

    /// <summary>Takes every key (sorted) and returns a releaser; cancelling while waiting releases what was already taken.</summary>
    public async Task<IDisposable> AcquireAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var ordered = keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var taken = new List<SemaphoreSlim>(ordered.Count);
        try
        {
            foreach (var key in ordered)
            {
                var gate = GetGate(key);
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                taken.Add(gate);
            }
        }
        catch
        {
            Release(taken);
            throw;
        }

        return new Releaser(taken);
    }

    /// <summary>Whether <paramref name="key"/> is currently held by some step (diagnostics and tests).</summary>
    public bool IsHeld(string key)
    {
        lock (_gate)
        {
            return _locks.TryGetValue(key, out var gate) && gate.CurrentCount == 0;
        }
    }

    private SemaphoreSlim GetGate(string key)
    {
        lock (_gate)
        {
            if (!_locks.TryGetValue(key, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _locks[key] = gate;
            }

            return gate;
        }
    }

    private static void Release(List<SemaphoreSlim> taken)
    {
        for (var i = taken.Count - 1; i >= 0; i--)
        {
            taken[i].Release();
        }

        taken.Clear();
    }

    private sealed class Releaser : IDisposable
    {
        private List<SemaphoreSlim>? _taken;

        public Releaser(List<SemaphoreSlim> taken)
        {
            _taken = taken;
        }

        public void Dispose()
        {
            var taken = Interlocked.Exchange(ref _taken, null);
            if (taken is not null)
            {
                Release(taken);
            }
        }
    }
}
