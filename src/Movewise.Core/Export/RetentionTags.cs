using System.Text.Json.Nodes;

namespace Movewise.Core.Export;

/// <summary>Mailbox retention tags (messaging records management), as read with Get-RetentionPolicyTag.</summary>
public static class RetentionTags
{
    /// <summary>
    /// The retention period comes back as a time span, which JSON turns into an object of its parts. New-RetentionPolicyTag
    /// takes it as text ("730.00:00:00"), so it's put back that way.
    /// </summary>
    public static void FromGet(JsonObject tag)
    {
        if (tag["AgeLimitForRetention"] is JsonObject span)
            tag["AgeLimitForRetention"] = AsText(span);
    }

    static JsonNode? AsText(JsonObject span)
    {
        if (span["Ticks"] is JsonValue ticks && ticks.TryGetValue<long>(out var value))
            return TimeSpan.FromTicks(value).ToString("c", System.Globalization.CultureInfo.InvariantCulture);
        if (span["Days"] is JsonValue days && days.TryGetValue<int>(out var count))
            return $"{count}.00:00:00";
        return null;
    }
}
