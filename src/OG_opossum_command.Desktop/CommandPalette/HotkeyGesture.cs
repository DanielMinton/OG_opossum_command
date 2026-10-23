using System.Windows.Input;

namespace OG_opossum_command.Desktop.CommandPalette;

/// <summary>A parsed hotkey such as <c>Ctrl+Alt+Space</c>: at least one modifier plus one key.</summary>
public sealed record HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    /// <summary>The out-of-the-box palette hotkey.</summary>
    public const string DefaultText = "Ctrl+Alt+Space";

    /// <summary>The default gesture.</summary>
    public static HotkeyGesture Default => TryParse(DefaultText, out var gesture, out _) ? gesture : new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Alt, Key.Space);

    /// <summary>Parses <c>Ctrl+Alt+Space</c>, <c>Win+Shift+F9</c>, … Modifier names: Ctrl/Control, Alt, Shift, Win/Windows.</summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture, out string error)
    {
        gesture = null!;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Enter a hotkey such as Ctrl+Alt+Space.";
            return false;
        }

        var modifiers = ModifierKeys.None;
        Key? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    modifiers |= ModifierKeys.Control;
                    continue;
                case "ALT":
                    modifiers |= ModifierKeys.Alt;
                    continue;
                case "SHIFT":
                    modifiers |= ModifierKeys.Shift;
                    continue;
                case "WIN" or "WINDOWS":
                    modifiers |= ModifierKeys.Windows;
                    continue;
            }

            if (key is not null)
            {
                error = $"'{text}' names more than one key; use modifiers plus exactly one key.";
                return false;
            }

            var name = raw.Equals("SPACEBAR", StringComparison.OrdinalIgnoreCase) ? "Space" : raw;
            if (name.Length == 1 && char.IsDigit(name[0]))
            {
                name = "D" + name;
            }

            if (!Enum.TryParse<Key>(name, ignoreCase: true, out var parsed) || parsed == Key.None)
            {
                error = $"'{raw}' is not a key name. Examples: Space, F9, K, D1, Home.";
                return false;
            }

            key = parsed;
        }

        if (key is null)
        {
            error = $"'{text}' has no key; end with one, for example Ctrl+Alt+Space.";
            return false;
        }

        if (modifiers == ModifierKeys.None)
        {
            error = $"'{text}' needs at least one modifier (Ctrl, Alt, Shift, Win) so ordinary typing is not hijacked.";
            return false;
        }

        gesture = new HotkeyGesture(modifiers, key.Value);
        return true;
    }

    /// <summary>Canonical text form, e.g. <c>Ctrl+Alt+Space</c>.</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }
}
