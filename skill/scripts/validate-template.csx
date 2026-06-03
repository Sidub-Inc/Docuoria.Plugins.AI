#load "_common.csx"

#nullable enable

// SCR-04 — Template.FromJson + Validate() static check (no engine required, but we host for consistency).
// Args: --template <file.json>
// stdout: { valid, errors }

using System.Text.Json;
using Docuoria.Models;

try
{
    Cli.Help(Args, "validate-template.csx", "Validate a template JSON file against the schema",
        ("template", true, "Path to the template JSON file", false));

    var templatePath = Cli.Require(Args, "template");
    if (!File.Exists(templatePath))
    {
        JsonOut.Error("template-not-found", $"Template not found at '{templatePath}'", null, 1);
    }

    var json = File.ReadAllText(templatePath);

    Template tpl;
    try
    {
        tpl = Template.FromJson(json);
    }
    catch (JsonException jex)
    {
        var message = jex.Message;
        var hint = (string?)null;

        // Detect fieldType string-vs-integer errors and add a helpful hint.
        if (message.Contains("FieldType", StringComparison.OrdinalIgnoreCase)
            || (message.Contains("fieldType", StringComparison.OrdinalIgnoreCase))
            || (message.Contains("could not be converted") && message.Contains("String", StringComparison.Ordinal)))
        {
            hint = "fieldType must be an integer (0=String, 1=Number, 2=Integer, 3=Boolean, 4=Date, 5=Timestamp). "
                 + "Do NOT use string values like \"String\" or \"Number\".";
        }

        JsonOut.Error("parse-error", hint is not null ? $"{message} — Hint: {hint}" : message, null, 1);
        return;
    }

    var errors = tpl.Validate();
    JsonOut.Write(new { valid = errors.Count == 0, errors });
}
catch (Exception ex)
{
    JsonOut.Error("unhandled", ex.Message, ex.ToString(), 1);
}
