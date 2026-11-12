using OpenRevo.PluginSdk;

namespace OpenRevo.PluginHost;

public static class PluginViewPolicy
{
    public static void Validate(PluginView view)
    {
        if (view.Title is not { Length: > 0 and <= 120 } || view.Controls is not { Length: <= 32 })
            throw new InvalidDataException("Invalid plugin view.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var control in view.Controls)
        {
            if (control == null || !PluginPackage.Identifier(control.Id) || !ids.Add(control.Id) ||
                control.Kind is not ("text" or "button" or "toggle" or "choice") ||
                control.Label is not { Length: <= 160 } || control.Value?.Length > 1024)
                throw new InvalidDataException("Invalid plugin control.");
            if (control.Kind == "toggle" && control.Value is not ("true" or "false"))
                throw new InvalidDataException("Invalid toggle value.");
            if (control.Kind == "choice")
            {
                if (control.Choices is not { Length: >= 1 and <= 12 } ||
                    control.Choices.Any(c => c == null || !PluginPackage.Identifier(c.Value) || c.Label is not { Length: > 0 and <= 80 }) ||
                    control.Choices.Select(c => c.Value).Distinct(StringComparer.Ordinal).Count() != control.Choices.Length ||
                    !control.Choices.Any(c => c.Value == control.Value))
                    throw new InvalidDataException("Invalid choice options.");
            }
            else if (control.Choices != null) throw new InvalidDataException("Unexpected choice options.");
        }
    }
}
