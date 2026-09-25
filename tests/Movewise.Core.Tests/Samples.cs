using System.Text.Json.Nodes;

namespace Movewise.Core.Tests;

/// <summary>Trimmed-down examples of what Microsoft Graph returns.</summary>
static class Samples
{
    public const string BreakGlassGroup = "8d10c1e2-0000-4000-8000-00000000e5b2";
    public const string AllStaffGroup = "3f2a9b10-0000-4000-8000-00000000c7d1";
    public const string HqLocation = "b61c7a33-0000-4000-8000-000000002f90";
    public const string CustomStrength = "5f7a1c20-0000-4000-8000-000000000abc";

    public static JsonObject ConditionalAccessPolicy() => JsonNode.Parse($$"""
        {
          "@odata.context": "https://graph.microsoft.com/v1.0/$metadata#identity/conditionalAccess/policies/$entity",
          "id": "0a1b2c3d-0000-4000-8000-000000000001",
          "templateId": null,
          "displayName": "CA001 - Require MFA for all users",
          "createdDateTime": "2025-01-01T10:00:00Z",
          "modifiedDateTime": "2026-09-20T08:30:00Z",
          "state": "enabled",
          "conditions": {
            "users": {
              "includeUsers": [ "All" ],
              "excludeUsers": [ "GuestsOrExternalUsers" ],
              "includeGroups": [ "{{AllStaffGroup}}" ],
              "excludeGroups": [ "{{BreakGlassGroup}}" ]
            },
            "applications": {
              "includeApplications": [ "All" ],
              "excludeApplications": [ "00000003-0000-0ff1-ce00-000000000000" ]
            },
            "locations": {
              "includeLocations": [ "All" ],
              "excludeLocations": [ "{{HqLocation}}", "AllTrusted" ]
            }
          },
          "grantControls": {
            "operator": "OR",
            "builtInControls": [],
            "authenticationStrength": { "id": "{{CustomStrength}}", "displayName": "Phishing-resistant plus" }
          }
        }
        """)!.AsObject();
}
