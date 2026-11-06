using System;
using WinUiXaml.Workspace;

namespace WinUiXaml.LanguageServer;

internal static class FrameworkProfileArguments
{
    internal static XamlFrameworkProfile Parse(string[] arguments, Action<string>? writeDiagnostic = null)
    {
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            // The host launch contract supplies a workspace hint before the framework profile.
            // The resolver discovers the owning project itself, so consume this shared argument
            // without letting it mask a later --framework selection.
            if (string.Equals(argument, "--workspace", StringComparison.OrdinalIgnoreCase))
            {
                if (++index == arguments.Length || arguments[index].StartsWith("--", StringComparison.Ordinal))
                {
                    writeDiagnostic?.Invoke("--workspace requires a value; defaulting to winui.");
                    return XamlFrameworkProfile.WinUi;
                }

                continue;
            }

            if (string.Equals(argument, "--framework", StringComparison.OrdinalIgnoreCase))
            {
                if (++index == arguments.Length || arguments[index].StartsWith("--", StringComparison.Ordinal))
                {
                    writeDiagnostic?.Invoke("--framework requires a value; defaulting to winui.");
                    return XamlFrameworkProfile.WinUi;
                }

                try
                {
                    return XamlFrameworkProfile.Parse(arguments[index]);
                }
                catch (ArgumentException ex)
                {
                    writeDiagnostic?.Invoke($"{ex.Message}; defaulting to winui.");
                    return XamlFrameworkProfile.WinUi;
                }
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                writeDiagnostic?.Invoke($"Unknown argument '{argument}'; defaulting to winui.");
                return XamlFrameworkProfile.WinUi;
            }
        }

        return XamlFrameworkProfile.WinUi;
    }
}
