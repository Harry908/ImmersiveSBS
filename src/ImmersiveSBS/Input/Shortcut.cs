using System.Windows.Input;

namespace ImmersiveSBS.Input;

/// <summary>A key with modifiers, written as "Ctrl+Alt+B".</summary>
public readonly record struct Shortcut(ModifierKeys Modifiers, Key Key)
{
    public static bool TryParse(string? text, out Shortcut shortcut)
    {
        shortcut = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = ModifierKeys.None;
        Key key = Key.None;
        foreach (string raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win" or "windows": modifiers |= ModifierKeys.Windows; break;
                default:
                    if (key != Key.None || !Enum.TryParse(raw, ignoreCase: true, out key)) return false;
                    break;
            }
        }

        // A global shortcut without a modifier would steal an ordinary key everywhere.
        if (key == Key.None || modifiers == ModifierKeys.None) return false;
        shortcut = new Shortcut(modifiers, key);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }
}
