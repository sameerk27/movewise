using System.Text.Json;
using System.Text.Json.Nodes;

namespace Movewise.Core.Export;

/// <summary>
/// Turns sensitivity labels and label policies, as Get-Label and Get-LabelPolicy return them, into the documented
/// parameters of New-Label and New-LabelPolicy. Get-Label returns what a label does as LabelActions (JSON
/// strings) and Settings ("[key, value]" strings); New-Label only documents flat parameters such as
/// EncryptionEnabled or ApplyContentMarkingHeaderText, and reserves LabelActions and Settings for Microsoft's use.
/// </summary>
public static class SensitivityLabels
{
    /// <summary>Settings Movewise couldn't turn into a parameter; the admin sets these by hand after deploying.</summary>
    public const string NotCopied = "MovewiseNotCopied";

    // Get-Label properties New-Label reserves for Microsoft, or that only describe the label.
    static readonly string[] Internal = ["LabelActions", "Settings", "Setting", "Conditions", "MigrationId", "SchematizedDataCondition", "ColumnAssetCondition"];

    // Documented label advanced settings (New-Label -AdvancedSettings). DefaultSubLabelId is left out: it points at
    // a sublabel, which is created after its parent.
    static readonly HashSet<string> LabelAdvancedSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        "BlockContentAnalysisServices", "Color", "DefaultSharingScope", "DefaultShareLinkPermission",
        "DefaultShareLinkToExistingAccess", "MembersCanShare", "SMimeEncrypt", "SMimeSign",
    };

    /// <summary>Encryption rights values that mean the same in every tenant.</summary>
    public static readonly string[] EveryoneRights = ["AuthenticatedUsers", "Owner"];

    public static void FromGetLabel(JsonObject label)
    {
        var notCopied = new List<string>();

        foreach (var action in (label["LabelActions"] as JsonArray ?? []).Select(ParseAction).OfType<JsonObject>())
        {
            var type = Text(action, "Type")?.ToLowerInvariant();
            var subType = Text(action, "SubType")?.ToLowerInvariant();
            var settings = (action["Settings"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(s => Text(s, "Key") is not null)
                .GroupBy(s => Text(s, "Key")!.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => Text(g.First(), "Value") ?? "");
            var enabled = !Is(settings, "disabled", "true");

            switch (type, subType)
            {
                case ("encrypt", _):
                    label["EncryptionEnabled"] = enabled;
                    Copy(settings, "protectiontype", label, "EncryptionProtectionType", ProtectionType);
                    Copy(settings, "contentexpiredondateindaysornever", label, "EncryptionContentExpiredOnDateInDaysOrNever");
                    Copy(settings, "offlineaccessdays", label, "EncryptionOfflineAccessDays", Number);
                    Copy(settings, "donotforward", label, "EncryptionDoNotForward", Bool);
                    Copy(settings, "encryptonly", label, "EncryptionEncryptOnly", Bool);
                    Copy(settings, "promptuser", label, "EncryptionPromptUser", Bool);
                    Copy(settings, "doublekeyencryptionurl", label, "EncryptionDoubleKeyEncryptionUrl");
                    if (settings.TryGetValue("rightsdefinitions", out var rights))
                        label["EncryptionRightsDefinitions"] = ParseRights(rights);
                    Unhandled(settings, notCopied, "encryption",
                        "disabled", "protectiontype", "contentexpiredondateindaysornever", "offlineaccessdays", "donotforward",
                        "encryptonly", "promptuser", "doublekeyencryptionurl", "rightsdefinitions", "templatearchived", "templateid", "linkedtemplateid");
                    break;

                case ("applycontentmarking", "header" or "footer"):
                    var prefix = subType == "header" ? "ApplyContentMarkingHeader" : "ApplyContentMarkingFooter";
                    label[prefix + "Enabled"] = enabled;
                    CopyMarking(settings, label, prefix, "alignment", "margin");
                    Unhandled(settings, notCopied, $"{subType}", "disabled", "placement", "text", "fontsize", "fontcolor", "fontname", "alignment", "margin");
                    break;

                case ("applywatermarking", _):
                    label["ApplyWaterMarkingEnabled"] = enabled;
                    CopyMarking(settings, label, "ApplyWaterMarking", "layout");
                    Unhandled(settings, notCopied, "watermark", "disabled", "text", "fontsize", "fontcolor", "fontname", "layout");
                    break;

                case ("protectgroup", _):
                    label["SiteAndGroupProtectionEnabled"] = enabled;
                    Copy(settings, "privacy", label, "SiteAndGroupProtectionPrivacy", CapitalizeNode);
                    Copy(settings, "allowaccesstoguestusers", label, "SiteAndGroupProtectionAllowAccessToGuestUsers", Bool);
                    Copy(settings, "allowemailfromguestusers", label, "SiteAndGroupProtectionAllowEmailFromGuestUsers", Bool);
                    Unhandled(settings, notCopied, "group protection", "disabled", "privacy", "allowaccesstoguestusers", "allowemailfromguestusers");
                    break;

                case ("protectsite", _):
                    label["SiteAndGroupProtectionEnabled"] = enabled;
                    Copy(settings, "allowfullaccess", label, "SiteAndGroupProtectionAllowFullAccess", Bool);
                    Copy(settings, "allowlimitedaccess", label, "SiteAndGroupProtectionAllowLimitedAccess", Bool);
                    Copy(settings, "blockaccess", label, "SiteAndGroupProtectionBlockAccess", Bool);
                    Unhandled(settings, notCopied, "site protection", "disabled", "allowfullaccess", "allowlimitedaccess", "blockaccess");
                    break;

                default:
                    notCopied.Add($"{type}{(subType is null ? "" : "/" + subType)} settings");
                    break;
            }
        }

        var advanced = ParseSettings(label["Settings"]);
        var kept = new JsonObject();
        foreach (var (key, value) in advanced)
        {
            var documented = LabelAdvancedSettings.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (documented is not null)
                kept[documented] = value?.DeepClone();
            else if (key.Equals("DefaultSubLabelId", StringComparison.OrdinalIgnoreCase))
                notCopied.Add("the default sublabel (AdvancedSettings DefaultSubLabelId)");
        }
        if (kept.Count > 0)
            label["AdvancedSettings"] = kept;

        foreach (var name in Internal)
            label.Remove(name);
        if (notCopied.Count > 0)
            label[NotCopied] = new JsonArray(notCopied.Select(n => (JsonNode?)n).ToArray());
    }

    public static void FromGetLabelPolicy(JsonObject policy)
    {
        // Label policy advanced settings (mandatory labeling, default labels, …) are all meant to be set by admins.
        var advanced = ParseSettings(policy["Settings"]);
        if (advanced.Count > 0)
            policy["AdvancedSettings"] = advanced;
        foreach (var name in Internal)
            policy.Remove(name);
    }

    /// <summary>What New-Label takes: rights as "identity:RIGHT,RIGHT;identity:RIGHT".</summary>
    public static void PrepareLabelForCreate(JsonObject label)
    {
        if (label["EncryptionRightsDefinitions"] is JsonArray rights)
        {
            label["EncryptionRightsDefinitions"] = string.Join(";", rights.OfType<JsonObject>()
                .Where(r => Text(r, "Identity") is { Length: > 0 })
                .Select(r => $"{Text(r, "Identity")}:{Text(r, "Rights")}"));
        }
    }

    static JsonObject? ParseAction(JsonNode? node)
    {
        try
        {
            return node switch
            {
                JsonObject obj => obj,
                JsonValue value when value.TryGetValue<string>(out var text) => JsonNode.Parse(text) as JsonObject,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Rights come as JSON ([{"Identity":…,"Rights":…}]) or as "identity:RIGHTS;identity:RIGHTS".</summary>
    static JsonArray ParseRights(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is JsonArray parsed)
            {
                return new JsonArray(parsed.OfType<JsonObject>()
                    .Select(r => (JsonNode?)new JsonObject { ["Identity"] = Text(r, "Identity"), ["Rights"] = Text(r, "Rights") })
                    .ToArray());
            }
        }
        catch (JsonException)
        {
        }

        return new JsonArray(text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry =>
            {
                var colon = entry.IndexOf(':');
                return (JsonNode?)new JsonObject
                {
                    ["Identity"] = colon < 0 ? entry : entry[..colon],
                    ["Rights"] = colon < 0 ? "" : entry[(colon + 1)..],
                };
            })
            .ToArray());
    }

    /// <summary>"[key, value]" strings as an object.</summary>
    static JsonObject ParseSettings(JsonNode? node)
    {
        var result = new JsonObject();
        foreach (var text in (node as JsonArray ?? []).OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>())
        {
            var inner = text.Trim().TrimStart('[').TrimEnd(']');
            var comma = inner.IndexOf(',');
            if (comma <= 0)
                continue;
            result[inner[..comma].Trim()] = inner[(comma + 1)..].Trim();
        }
        return result;
    }

    static void CopyMarking(Dictionary<string, string> settings, JsonObject label, string prefix, params string[] extra)
    {
        Copy(settings, "text", label, prefix + "Text");
        Copy(settings, "fontsize", label, prefix + "FontSize", Number);
        Copy(settings, "fontcolor", label, prefix + "FontColor");
        Copy(settings, "fontname", label, prefix + "FontName");
        foreach (var key in extra)
            Copy(settings, key, label, prefix + Capitalize(key), key == "margin" ? Number : CapitalizeNode);
    }

    static void Copy(Dictionary<string, string> settings, string key, JsonObject label, string parameter, Func<string, JsonNode?>? convert = null)
    {
        if (settings.TryGetValue(key, out var value) && value.Length > 0)
            label[parameter] = convert is null ? value : convert(value);
    }

    static void Unhandled(Dictionary<string, string> settings, List<string> notCopied, string area, params string[] handled)
    {
        foreach (var key in settings.Keys.Except(handled, StringComparer.OrdinalIgnoreCase))
            notCopied.Add($"{area}: {key}");
    }

    static JsonNode? Bool(string value) => bool.TryParse(value, out var flag) ? flag : value;
    static JsonNode? Number(string value) => int.TryParse(value, out var number) ? number : value;
    static JsonNode? ProtectionType(string value) => value.ToLowerInvariant() switch
    {
        "template" => "Template",
        "userdefined" => "UserDefined",
        "removeprotection" => "RemoveProtection",
        _ => value,
    };
    static JsonNode? CapitalizeNode(string value) => Capitalize(value);
    static string Capitalize(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    static bool Is(Dictionary<string, string> settings, string key, string value) =>
        settings.TryGetValue(key, out var found) && found.Equals(value, StringComparison.OrdinalIgnoreCase);

    static string? Text(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
