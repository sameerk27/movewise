using System.IO;
using System.Text.Json.Nodes;
using Movewise.App.State;
using Movewise.Core.Access;
using Movewise.Core.Export;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.App.Demo;

/// <summary>
/// Two made-up tenants, Contoso (source) and Fabrikam (destination), for trying Movewise without real tenants.
/// They're built so every screen has something to show: a name conflict, a missing license, a group with no match,
/// a domain to map, an address that isn't in the destination, sublabels, and Teams group assignments.
/// Everything stays in memory; deploying changes only the demo destination.
/// </summary>
static class DemoTenants
{
    /// <summary>Demo runs are kept apart from real ones.</summary>
    public static string RunsRoot { get; } = Path.Combine(Path.GetTempPath(), "Movewise demo runs");

    const string SourceTenantId = "11111111-1111-4111-8111-111111111111";
    const string DestinationTenantId = "22222222-2222-4222-8222-222222222222";

    // Contoso's objects
    const string AllStaff = "a1000000-0000-4000-8000-000000000001";
    const string BreakGlass = "a1000000-0000-4000-8000-000000000002";
    const string IntunePilot = "a1000000-0000-4000-8000-000000000003";
    const string Executives = "a1000000-0000-4000-8000-000000000004";
    const string BreakGlassUser = "a2000000-0000-4000-8000-000000000001";
    const string Hq = "a3000000-0000-4000-8000-000000000001";
    const string Strength = "a4000000-0000-4000-8000-000000000001";
    const string CorporateFilter = "a5000000-0000-4000-8000-000000000001";
    const string ConfidentialLabel = "a6000000-0000-4000-8000-000000000001";
    const string FinanceLabel = "a6000000-0000-4000-8000-000000000002";

    public static (TenantConnection Source, TenantConnection Destination) Create() =>
        (Connection(TenantRole.Source, Contoso(), SourceGraph(), SourceExchange(), SourceCompliance(), SourceTeams()),
         Connection(TenantRole.Destination, Fabrikam(), DestinationGraph(), DestinationExchange(), new DemoPowerShell(false), DestinationTeams()));

    static TenantConnection Connection(TenantRole role, TenantInfo info, DemoGraph graph, DemoPowerShell exchange, DemoPowerShell compliance, DemoPowerShell teams)
    {
        // Every type the demo has none of reads as empty rather than missing.
        graph.EnsureCollections(ResourceRegistry.All.Where(t => !t.UsesPowerShell && !t.IsSettings).Select(t => t.ListPath.Split('?')[0]));
        return Build(role, info, graph, exchange, compliance, teams);
    }

    static TenantConnection Build(TenantRole role, TenantInfo info, DemoGraph graph, DemoPowerShell exchange, DemoPowerShell compliance, DemoPowerShell teams) => new()
    {
        Role = role,
        Info = info,
        Roles = RoleRequirements.Check(role, info.RoleTemplateIds),
        // Like a real source, the demo source can only be read.
        // The demo Graph also answers as the Defender for Endpoint API: their paths don't overlap.
        Clients = new TenantClients(role == TenantRole.Source ? new ReadOnlyGraph(graph) : graph, exchange, compliance, teams,
            role == TenantRole.Source ? new ReadOnlyGraph(graph) : graph),
        IsDemo = true,
    };

    static TenantInfo Contoso() => new(SourceTenantId, "Contoso (demo)", "contoso.onmicrosoft.com", "contoso.com", "admin@contoso.com", "Demo admin",
        false, [DirectoryRoles.GlobalAdministrator], ["Global Administrator"], ["SPE_E5"])
    {
        ServicePlans = new HashSet<string>(["AAD_PREMIUM", "AAD_PREMIUM_P2", "INTUNE_A", "ATP_ENTERPRISE", "THREAT_INTELLIGENCE"], StringComparer.OrdinalIgnoreCase),
    };

    // No Entra ID P2 in the destination, so risk-based Conditional Access shows a license warning.
    static TenantInfo Fabrikam() => new(DestinationTenantId, "Fabrikam (demo)", "fabrikam.onmicrosoft.com", "fabrikam.com", "admin@fabrikam.com", "Demo admin",
        false, [DirectoryRoles.GlobalAdministrator], ["Global Administrator"], ["SPE_E3", "EMS"])
    {
        ServicePlans = new HashSet<string>(["AAD_PREMIUM", "INTUNE_A", "ATP_ENTERPRISE"], StringComparer.OrdinalIgnoreCase),
    };

    // ---------- Contoso ----------

    static DemoGraph SourceGraph() => new DemoGraph()
        .Add("v1.0/organization", J($$"""{ "id": "{{SourceTenantId}}", "displayName": "Contoso" }"""))
        .Add("v1.0/domains",
            J("""{ "id": "contoso.com", "isVerified": true, "isInitial": false, "isDefault": true }"""),
            J("""{ "id": "contoso.onmicrosoft.com", "isVerified": true, "isInitial": true, "isDefault": false }"""))
        .Object("v1.0/sites/root", J("""{ "siteCollection": { "hostname": "contoso.sharepoint.com" } }"""))
        .Settings(contoso: true)
        .Add("beta/deviceManagement/deviceManagementScripts", J("""
            { "id": "ps1", "displayName": "Map finance drive", "runAsAccount": "user", "scriptContent": "TmV3LVBTRHJpdmUgLU5hbWUgRiAtUm9vdCBcXGZzMDFcZmluYW5jZQ==", "fileName": "map-drive.ps1" }
            """))
        .Add("beta/deviceManagement/deviceHealthScripts",
            J("""{ "id": "hs0", "displayName": "Restart stopped Office C2R svc", "isGlobalScript": true }"""),
            J("""{ "id": "hs1", "displayName": "Clear browser cache weekly", "isGlobalScript": false, "detectionScriptContent": "ZXhpdCAx", "remediationScriptContent": "ZXhpdCAw", "runAsAccount": "user" }"""))
        .Add("beta/deviceManagement/windowsFeatureUpdateProfiles", J("""{ "id": "fu1", "displayName": "Windows 11 24H2", "featureUpdateVersion": "Windows 11, version 24H2" }"""))
        .Add("beta/deviceManagement/groupPolicyConfigurations", J("""
            { "id": "gp1", "displayName": "Edge - home page",
              "__definitionValues": [ { "id": "dv1", "enabled": true, "definition": { "id": "d1a3-edge-homepage" },
                "presentationValues": [ { "@odata.type": "#microsoft.graph.groupPolicyPresentationValueText", "id": "pv1", "value": "https://intranet.contoso.com", "presentation": { "id": "p1-homepage-url" } } ] } ] }
            """))
        .Add("v1.0/policies/crossTenantAccessPolicy/partners", J("""{ "tenantId": "8d1a2b3c-0000-4000-8000-000000000111", "inboundTrust": { "isMfaAccepted": true, "isCompliantDeviceAccepted": true } }"""))
        .Add("v1.0/roleManagement/directory/roleDefinitions", J("""
            { "id": "cr1", "displayName": "Helpdesk - reset passwords", "isBuiltIn": false, "isEnabled": true,
              "rolePermissions": [ { "allowedResourceActions": ["microsoft.directory/users/password/update"] } ] }
            """))
        .Add("api/indicators", J("""{ "id": "ind1", "indicatorValue": "bad-updates.example", "indicatorType": "DomainName", "action": "Block", "title": "Fake update site", "severity": "High", "description": "Seen in a phishing campaign" }"""))
        .Add("beta/security/rules/detectionRules", J("""
            { "id": "dr1", "displayName": "Many failed sign-ins then success", "isEnabled": true,
              "queryCondition": { "queryText": "IdentityLogonEvents | where ActionType == 'LogonFailed'" }, "schedule": { "period": "1H" } }
            """))
        .Add("v1.0/sites", J("""{ "id": "s1", "displayName": "Finance", "webUrl": "https://contoso.sharepoint.com/sites/finance" }"""))
        .Add("v1.0/groups",
            J($$"""{ "id": "{{AllStaff}}", "displayName": "All staff", "mailNickname": "allstaff", "securityEnabled": true, "mailEnabled": false, "groupTypes": [] }"""),
            J($$"""{ "id": "{{BreakGlass}}", "displayName": "Break glass accounts", "mailNickname": "breakglass", "description": "Emergency access accounts, excluded from Conditional Access.", "securityEnabled": true, "mailEnabled": false, "groupTypes": [] }"""),
            J($$"""{ "id": "{{IntunePilot}}", "displayName": "Intune pilot devices", "mailNickname": "intunepilot", "securityEnabled": true, "mailEnabled": false, "groupTypes": [] }"""),
            J($$"""{ "id": "{{Executives}}", "displayName": "Executives", "mailNickname": "execs", "mail": "execs@contoso.com", "securityEnabled": true, "mailEnabled": true, "groupTypes": ["Unified"] }"""))
        .Add("v1.0/users",
            J($$"""{ "id": "{{BreakGlassUser}}", "displayName": "Break glass 1", "userPrincipalName": "breakglass1@contoso.onmicrosoft.com", "mail": null }"""),
            J("""{ "id": "a2000000-0000-4000-8000-000000000002", "displayName": "Anna Lindqvist", "userPrincipalName": "anna@contoso.com", "mail": "anna@contoso.com" }"""))
        .Add("v1.0/servicePrincipals")

        // Entra ID
        .Add("v1.0/identity/conditionalAccess/namedLocations", J($$"""
            { "@odata.type": "#microsoft.graph.ipNamedLocation", "id": "{{Hq}}", "displayName": "HQ - Seattle", "isTrusted": true,
              "ipRanges": [ { "@odata.type": "#microsoft.graph.iPv4CidrRange", "cidrAddress": "203.0.113.0/24" } ] }
            """))
        .Add("v1.0/policies/authenticationStrengthPolicies", J($$"""
            { "id": "{{Strength}}", "displayName": "Phishing-resistant plus", "description": "FIDO2 or Windows Hello", "policyType": "custom",
              "allowedCombinations": [ "fido2", "windowsHelloForBusiness" ] }
            """))
        .Add("v1.0/identity/conditionalAccess/policies",
            J($$"""
            { "id": "c1", "displayName": "CA001 - Require MFA for all users", "state": "enabled",
              "conditions": { "clientAppTypes": ["all"],
                "users": { "includeUsers": ["All"], "excludeUsers": [], "includeGroups": [], "excludeGroups": ["{{BreakGlass}}"] },
                "applications": { "includeApplications": ["All"], "excludeApplications": [] },
                "locations": { "includeLocations": ["All"], "excludeLocations": ["{{Hq}}"] } },
              "grantControls": { "operator": "OR", "builtInControls": ["mfa"] } }
            """),
            J("""
            { "id": "c2", "displayName": "CA002 - Block legacy authentication", "state": "enabled",
              "conditions": { "clientAppTypes": ["exchangeActiveSync", "other"],
                "users": { "includeUsers": ["All"], "excludeUsers": [], "includeGroups": [], "excludeGroups": [] },
                "applications": { "includeApplications": ["All"] } },
              "grantControls": { "operator": "OR", "builtInControls": ["block"] } }
            """),
            J($$"""
            { "id": "c3", "displayName": "CA003 - Block high-risk sign-ins", "state": "enabled",
              "conditions": { "signInRiskLevels": ["high"], "clientAppTypes": ["all"],
                "users": { "includeUsers": [], "includeGroups": ["{{AllStaff}}"], "excludeGroups": ["{{BreakGlass}}"] },
                "applications": { "includeApplications": ["All"] } },
              "grantControls": { "operator": "OR", "builtInControls": ["block"] } }
            """),
            J($$"""
            { "id": "c4", "displayName": "CA004 - Phishing-resistant MFA for admins", "state": "enabledForReportingButNotEnforced",
              "conditions": { "clientAppTypes": ["all"],
                "users": { "includeRoles": ["62e90394-69f5-4237-9190-012177145e10"], "excludeUsers": ["{{BreakGlassUser}}"] },
                "applications": { "includeApplications": ["All"] } },
              "grantControls": { "operator": "AND", "authenticationStrength": { "id": "{{Strength}}", "displayName": "Phishing-resistant plus" } } }
            """))

        // Intune
        .Add("beta/deviceManagement/roleScopeTags",
            J("""{ "id": "0", "displayName": "Default", "isBuiltIn": true }"""),
            J("""{ "id": "1", "displayName": "Europe", "description": "Devices in the EU", "isBuiltIn": false }"""))
        .Add("beta/deviceManagement/assignmentFilters", J($$"""
            { "id": "{{CorporateFilter}}", "displayName": "Corporate Windows devices", "platform": "windows10AndLater",
              "rule": "(device.deviceOwnership -eq \"Corporate\")", "roleScopeTags": ["0"] }
            """))
        .Add("beta/deviceManagement/configurationPolicies", J($$"""
            { "id": "sc1", "name": "Windows - BitLocker", "description": "Encrypt OS drives", "platforms": "windows10", "technologies": "mdm",
              "roleScopeTagIds": ["0", "1"],
              "settings": [ { "id": "0", "settingInstance": { "@odata.type": "#microsoft.graph.deviceManagementConfigurationChoiceSettingInstance",
                "settingDefinitionId": "device_vendor_msft_bitlocker_requiredeviceencryption",
                "choiceSettingValue": { "value": "device_vendor_msft_bitlocker_requiredeviceencryption_1", "children": [] } } } ],
              "__assignments": [ { "target": { "@odata.type": "#microsoft.graph.groupAssignmentTarget", "groupId": "{{AllStaff}}",
                "deviceAndAppManagementAssignmentFilterId": "{{CorporateFilter}}", "deviceAndAppManagementAssignmentFilterType": "include" } } ] }
            """))
        .Add("beta/deviceManagement/deviceConfigurations", J($$"""
            { "@odata.type": "#microsoft.graph.windows10GeneralConfiguration", "id": "dc1", "displayName": "Windows - Device restrictions",
              "passwordRequired": true, "passwordMinimumLength": 12, "roleScopeTagIds": ["0"],
              "__assignments": [ { "target": { "@odata.type": "#microsoft.graph.groupAssignmentTarget", "groupId": "{{IntunePilot}}",
                "deviceAndAppManagementAssignmentFilterId": null, "deviceAndAppManagementAssignmentFilterType": "none" } } ] }
            """))
        .Add("beta/deviceManagement/deviceCompliancePolicies", J("""
            { "@odata.type": "#microsoft.graph.windows10CompliancePolicy", "id": "cp1", "displayName": "Windows - Compliance",
              "bitLockerEnabled": true, "secureBootEnabled": true, "roleScopeTagIds": ["0"],
              "scheduledActionsForRule": [ { "id": "r1", "ruleName": "PasswordRequired",
                "scheduledActionConfigurations": [ { "id": "a1", "actionType": "block", "gracePeriodHours": 24 } ] } ],
              "__assignments": [ { "target": { "@odata.type": "#microsoft.graph.allDevicesAssignmentTarget" } } ] }
            """))
        .Add("beta/deviceAppManagement/iosManagedAppProtections", J($$"""
            { "id": "ios1", "displayName": "iOS - Protect work data", "pinRequired": true, "dataBackupBlocked": true, "roleScopeTagIds": ["0"],
              "apps": [ { "id": "com.microsoft.office.outlook.ios", "mobileAppIdentifier": { "@odata.type": "#microsoft.graph.iosMobileAppIdentifier", "bundleId": "com.microsoft.office.outlook" } } ],
              "__assignments": [ { "target": { "@odata.type": "#microsoft.graph.groupAssignmentTarget", "groupId": "{{AllStaff}}" } } ] }
            """))
        .Add("beta/deviceAppManagement/androidManagedAppProtections")
        .Add("beta/deviceManagement/windowsAutopilotDeploymentProfiles", J($$"""
            { "@odata.type": "#microsoft.graph.azureADWindowsAutopilotDeploymentProfile", "id": "ap1", "displayName": "Autopilot - Standard user",
              "deviceNameTemplate": "CON-%SERIAL%", "roleScopeTagIds": ["0"],
              "__assignments": [ { "target": { "@odata.type": "#microsoft.graph.groupAssignmentTarget", "groupId": "{{IntunePilot}}" } } ] }
            """));

    static DemoPowerShell SourceExchange() => new DemoPowerShell(readOnly: true)
        .Add("OrganizationConfig", J("""{ "DisplayName": "Contoso", "Name": "contoso.onmicrosoft.com" }"""))
        .Add("AntiPhishPolicy",
            J("""{ "Name": "Office365 AntiPhish Default", "Guid": "e1", "IsDefault": true }"""),
            J("""
            { "Name": "Executive protection", "Guid": "e2", "Enabled": true, "EnableTargetedUserProtection": true, "EnableMailboxIntelligence": true,
              "PhishThresholdLevel": 2, "TargetedDomainsToProtect": ["contoso.com"], "EnableOrganizationDomainsProtection": true,
              "WhenChanged": "2026-05-02T10:00:00Z", "ExchangeVersion": "0.20 (15.0.0.0)" }
            """))
        .Add("AntiPhishRule", J("""
            { "Name": "Executive protection", "Guid": "e3", "AntiPhishPolicy": "Executive protection", "State": "Enabled", "Priority": 0,
              "SentToMemberOf": ["execs@contoso.com"] }
            """))
        .Add("QuarantinePolicy",
            J("""{ "Name": "AdminOnlyAccessPolicy", "Guid": "q1", "EndUserQuarantinePermissionsValue": 0 }"""),
            J("""{ "Name": "DefaultGlobalTag", "Identity": "DefaultGlobalTag", "Guid": "q2", "QuarantinePolicyType": "GlobalQuarantinePolicy", "EndUserSpamNotificationFrequency": "1.00:00:00" }"""),
            J("""{ "Name": "Finance - request release", "Guid": "q3", "EndUserQuarantinePermissionsValue": 23, "ESNEnabled": true, "QuarantinePolicyType": "QuarantinePolicy" }"""))
        .Add("HostedContentFilterPolicy",
            J("""{ "Name": "Default", "Identity": "Default", "Guid": "e4", "IsDefault": true, "BulkThreshold": 5, "SpamAction": "Quarantine", "MarkAsSpamBulkMail": "On" }"""),
            J("""
            { "Name": "Finance spam filtering", "Guid": "e4b", "BulkThreshold": 5, "SpamAction": "Quarantine",
              "SpamQuarantineTag": "Finance - request release", "HighConfidencePhishQuarantineTag": "AdminOnlyAccessPolicy" }
            """))
        .Add("HostedContentFilterRule", J("""
            { "Name": "Finance spam filtering", "Guid": "e4c", "HostedContentFilterPolicy": "Finance spam filtering", "State": "Enabled", "Priority": 0,
              "SentToMemberOf": ["execs@contoso.com"] }
            """))
        .Add("TenantAllowBlockListItems",
            J("""{ "ListType": "Sender", "Identity": "tabl-1", "Value": "invoices@bad-payments.example", "Action": "Block", "Notes": "Invoice fraud, May 2026" }"""),
            J("""{ "ListType": "Url", "Identity": "tabl-2", "Value": "*.phish-login.example/*", "Action": "Block" }"""),
            J("""{ "ListType": "Sender", "Identity": "tabl-3", "Value": "old-campaign@spam.example", "Action": "Block", "ExpirationDate": "2020-01-01T00:00:00Z" }"""))
        .Add("HostedOutboundSpamFilterPolicy", J("""{ "Name": "Default", "Guid": "e5", "IsDefault": true }"""))
        .Add("HostedOutboundSpamFilterRule")
        .Add("MalwareFilterPolicy", J("""{ "Name": "Default", "Identity": "Default", "Guid": "e6", "IsDefault": true, "EnableFileFilter": true, "ZapEnabled": true }"""))
        .Add("AtpPolicyForO365", J("""{ "Name": "Default", "Identity": "Default", "EnableATPForSPOTeamsODB": true, "EnableSafeDocs": true, "AllowSafeDocsOpen": false }"""))
        .Add("EOPProtectionPolicyRule",
            J("""{ "Name": "Standard Preset Security Policy", "Identity": "Standard Preset Security Policy", "State": "Enabled", "HostedContentFilterPolicy": "Standard Preset Security Policy1700000000001", "SentToMemberOf": ["execs@contoso.com"] }"""),
            J("""{ "Name": "Strict Preset Security Policy", "Identity": "Strict Preset Security Policy", "State": "Disabled" }"""))
        .Add("MalwareFilterRule")
        .Add("SafeLinksPolicy", J("""{ "Name": "Safe Links - everyone", "Guid": "e7", "EnableSafeLinksForEmail": true, "EnableSafeLinksForTeams": true, "TrackClicks": true }"""))
        .Add("SafeLinksRule", J("""{ "Name": "Safe Links - everyone", "Guid": "e8", "SafeLinksPolicy": "Safe Links - everyone", "State": "Enabled", "Priority": 0, "RecipientDomainIs": ["contoso.com"] }"""))
        .Add("SafeAttachmentPolicy")
        .Add("SafeAttachmentRule")
        .Add("TransportRule", J("""
            { "Name": "Block executable attachments", "Guid": "e9", "Mode": "Enforce", "State": "Enabled", "Priority": 0,
              "AttachmentExtensionMatchesWords": ["exe", "js", "vbs"], "RejectMessageReasonText": "Executable attachments aren't allowed.",
              "ExceptIfFrom": ["it-admins@contoso.com"] }
            """))
        .Add("MobileDeviceMailboxPolicy",
            J("""{ "Name": "Default", "Guid": "e10", "IsDefault": true }"""),
            J("""{ "Name": "Corporate phones", "Guid": "e11", "PasswordEnabled": true, "MinPasswordLength": 6, "AllowSimplePassword": false }"""))
        .Add("RemoteDomain",
            J("""{ "Name": "Default", "Guid": "e12", "DomainName": "*", "AutoForwardEnabled": false }"""),
            J("""{ "Name": "Partner - Northwind", "Guid": "e13", "DomainName": "northwind.example", "AutoReplyEnabled": true, "AutoForwardEnabled": false, "TNEFEnabled": false }"""))
        .Add("OwaMailboxPolicy",
            J("""{ "Name": "OwaMailboxPolicy-Default", "Guid": "e14", "IsDefault": true }"""),
            J("""{ "Name": "Frontline workers", "Guid": "e15", "WacOMEXEnabled": false, "PersonalAccountsEnabled": false, "OfflineEnabledWeb": false }"""))
        .Add("RetentionPolicyTag",
            J("""{ "Name": "1 Week Delete", "Guid": "e16", "Type": "Personal", "RetentionAction": "DeleteAndAllowRecovery", "AgeLimitForRetention": { "Days": 7, "Ticks": 6048000000000 } }"""),
            J("""{ "Name": "Finance 7 year archive", "Guid": "e17", "Type": "All", "RetentionAction": "MoveToArchive", "RetentionEnabled": true, "AgeLimitForRetention": { "Days": 2555, "Ticks": 2207520000000000 } }"""))
        .Add("RetentionPolicy",
            J("""{ "Name": "Default MRM Policy", "Guid": "e18", "IsDefault": true, "RetentionPolicyTagLinks": ["1 Week Delete"] }"""),
            J("""{ "Name": "Finance mailboxes", "Guid": "e19", "RetentionPolicyTagLinks": ["Finance 7 year archive", "1 Week Delete"] }"""))
        .Add("JournalRule", J("""
            { "Name": "Journal executives", "Guid": "e20", "Recipient": "execs@contoso.com", "JournalEmailAddress": "journal@archive-vault.example",
              "Scope": "Global", "Enabled": true }
            """));

    static DemoPowerShell SourceCompliance()
    {
        static string Action(string type, string? subType, params (string Key, string Value)[] settings) => new JsonObject
        {
            ["Type"] = type,
            ["SubType"] = subType,
            ["Settings"] = new JsonArray(settings.Select(s => (JsonNode?)new JsonObject { ["Key"] = s.Key, ["Value"] = s.Value }).ToArray()),
        }.ToJsonString();

        var confidential = J($$"""{ "Guid": "{{ConfidentialLabel}}", "Name": "Confidential", "DisplayName": "Confidential", "Tooltip": "Business data that must stay inside Contoso.", "ContentType": "File, Email", "ParentId": null, "Priority": 1 }""");
        confidential["LabelActions"] = new JsonArray(
            Action("encrypt", null, ("disabled", "false"), ("protectiontype", "template"), ("offlineaccessdays", "7"),
                ("rightsdefinitions", """[{"Identity":"AuthenticatedUsers","Rights":"VIEW,VIEWRIGHTSDATA"},{"Identity":"contoso.com","Rights":"VIEW,EDIT,PRINT"}]""")),
            Action("applycontentmarking", "footer", ("text", "Confidential - Contoso"), ("fontsize", "10"), ("fontcolor", "#D13438"), ("alignment", "center")));
        confidential["Settings"] = new JsonArray("[color, #D13438]", "[isparent, True]");

        var finance = J($$"""{ "Guid": "{{FinanceLabel}}", "Name": "Confidential - Finance", "DisplayName": "Finance", "Tooltip": "Financial data.", "ContentType": "File, Email", "ParentId": "{{ConfidentialLabel}}", "Priority": 2 }""");
        finance["LabelActions"] = new JsonArray(
            Action("applycontentmarking", "header", ("text", "FINANCE - CONFIDENTIAL"), ("fontsize", "12"), ("alignment", "left")),
            Action("applywatermarking", null, ("text", "Finance"), ("layout", "diagonal"), ("fontsize", "40")));

        return new DemoPowerShell(readOnly: true)
            .Add("Label", confidential, finance)
            .Add("LabelPolicy", J("""
                { "Name": "All employees", "Guid": "p1", "Labels": ["Confidential", "Confidential - Finance"], "ExchangeLocation": [ { "Name": "All" } ],
                  "Settings": [ "[requiredowngradejustification, true]" ] }
                """))
            .Add("DlpCompliancePolicy", J("""
                { "Name": "Credit card numbers", "Guid": "d1", "Mode": "Enable",
                  "ExchangeLocation": [ { "Name": "All" } ],
                  "SharePointLocation": [ { "Name": "https://contoso.sharepoint.com/sites/finance", "DisplayName": "Finance" } ] }
                """))
            .Add("DlpComplianceRule", J("""
                { "Name": "Block credit card sharing", "Guid": "d2", "ParentPolicyName": "Credit card numbers", "BlockAccess": true, "Disabled": false,
                  "ContentContainsSensitiveInformation": [ { "name": "Credit Card Number", "mincount": "1" } ], "NotifyUser": ["SiteAdmin"] }
                """))
            .Add("RetentionCompliancePolicy", J("""{ "Name": "Email - keep 7 years", "Guid": "r1", "Enabled": true, "ExchangeLocation": [ { "Name": "All" } ] }"""))
            .Add("RetentionComplianceRule", J("""{ "Name": "Keep 7 years", "Guid": "r2", "Policy": "Email - keep 7 years", "RetentionDuration": 2555, "RetentionComplianceAction": "Keep" }"""));
    }

    static DemoPowerShell SourceTeams() => new DemoPowerShell(readOnly: true)
        .Add("CsTenant", J("""{ "DisplayName": "Contoso" }"""))
        .Add("CsTeamsMeetingPolicy",
            J("""{ "Identity": "Global", "AllowCloudRecording": true }"""),
            J("""{ "Identity": "Tag:AllOn", "AllowCloudRecording": true }"""),
            J("""{ "Identity": "Tag:Executives", "Description": "Meetings for executives", "AllowCloudRecording": true, "AllowTranscription": true, "AutoAdmittedUsers": "EveryoneInCompany" }"""))
        .Add("CsTeamsMessagingPolicy",
            J("""{ "Identity": "Global", "AllowGiphy": true }"""),
            J("""{ "Identity": "Tag:Restricted chat", "AllowGiphy": false, "AllowMemes": false, "AllowUserDeleteMessage": false }"""))
        .Add("CsTenantFederationConfiguration", J("""{ "Identity": "Global", "AllowFederatedUsers": true, "AllowTeamsConsumer": false, "AllowTeamsConsumerInbound": false, "AllowPublicUsers": false }"""))
        .Add("CsTeamsClientConfiguration", J("""{ "Identity": "Global", "AllowGuestUser": true, "AllowDropBox": false, "AllowBox": false, "AllowGoogleDrive": false }"""))
        .Add("CsGroupPolicyAssignment", J($$"""{ "GroupId": "{{Executives}}", "PolicyType": "TeamsMeetingPolicy", "PolicyName": "Executives", "Rank": 1 }"""));

    // ---------- Fabrikam ----------

    static DemoGraph DestinationGraph() => new DemoGraph()
        .Add("v1.0/organization", J($$"""{ "id": "{{DestinationTenantId}}", "displayName": "Fabrikam" }"""))
        .Add("v1.0/domains",
            J("""{ "id": "fabrikam.com", "isVerified": true, "isInitial": false, "isDefault": true }"""),
            J("""{ "id": "fabrikam.onmicrosoft.com", "isVerified": true, "isInitial": true, "isDefault": false }"""))
        .Object("v1.0/sites/root", J("""{ "siteCollection": { "hostname": "fabrikam.sharepoint.com" } }"""))
        .Settings(contoso: false)
        .Add("v1.0/sites", J("""{ "id": "s2", "displayName": "Finance", "webUrl": "https://fabrikam.sharepoint.com/sites/finance" }"""))
        .Add("v1.0/groups",
            J("""{ "id": "b1000000-0000-4000-8000-000000000001", "displayName": "All staff", "mailNickname": "all-staff", "securityEnabled": true, "groupTypes": [] }"""),
            J("""{ "id": "b1000000-0000-4000-8000-000000000003", "displayName": "Intune Pilot (devices)", "mailNickname": "intunepilot", "securityEnabled": true, "groupTypes": [] }"""),
            J("""{ "id": "b1000000-0000-4000-8000-000000000004", "displayName": "Leadership team", "mailNickname": "leadership", "mail": "execs@fabrikam.com", "securityEnabled": true, "groupTypes": ["Unified"] }"""))
        .Add("v1.0/users",
            J("""{ "id": "b2000000-0000-4000-8000-000000000001", "displayName": "Break glass 1", "userPrincipalName": "breakglass1@fabrikam.onmicrosoft.com", "mail": null }"""),
            J("""{ "id": "b2000000-0000-4000-8000-000000000002", "displayName": "Anna Lindqvist", "userPrincipalName": "anna@fabrikam.com", "mail": "anna@fabrikam.com" }"""))
        .Add("v1.0/servicePrincipals")
        .Add("v1.0/identity/conditionalAccess/namedLocations")
        .Add("v1.0/policies/authenticationStrengthPolicies")
        .Add("v1.0/identity/conditionalAccess/policies", J("""
            { "id": "f1", "displayName": "CA002 - Block legacy authentication", "state": "enabled",
              "conditions": { "clientAppTypes": ["exchangeActiveSync", "other"], "users": { "includeUsers": ["All"] }, "applications": { "includeApplications": ["All"] } },
              "grantControls": { "operator": "OR", "builtInControls": ["block"] } }
            """))
        .Add("beta/deviceManagement/roleScopeTags", J("""{ "id": "0", "displayName": "Default", "isBuiltIn": true }"""))
        .Add("beta/deviceManagement/assignmentFilters")
        .Add("beta/deviceManagement/configurationPolicies")
        .Add("beta/deviceManagement/deviceConfigurations")
        .Add("beta/deviceManagement/deviceCompliancePolicies")
        .Add("beta/deviceAppManagement/iosManagedAppProtections")
        .Add("beta/deviceAppManagement/androidManagedAppProtections")
        .Add("beta/deviceManagement/windowsAutopilotDeploymentProfiles");

    static DemoPowerShell DestinationExchange() => new DemoPowerShell(readOnly: false)
        .Add("OrganizationConfig", J("""{ "DisplayName": "Fabrikam", "Name": "fabrikam.onmicrosoft.com" }"""))
        .Add("AntiPhishPolicy", J("""{ "Name": "Office365 AntiPhish Default", "Guid": "f-e1", "IsDefault": true }"""))
        .Add("MobileDeviceMailboxPolicy", J("""{ "Name": "Default", "Guid": "f-e2", "IsDefault": true }"""))
        .Add("QuarantinePolicy", J("""{ "Name": "AdminOnlyAccessPolicy", "Guid": "f-q1", "EndUserQuarantinePermissionsValue": 0 }"""))
        .Add("QuarantinePolicy", J("""{ "Name": "DefaultGlobalTag", "Identity": "DefaultGlobalTag", "Guid": "f-q2", "QuarantinePolicyType": "GlobalQuarantinePolicy", "EndUserSpamNotificationFrequency": "3.00:00:00" }"""))
        .Add("HostedContentFilterPolicy", J("""{ "Name": "Default", "Identity": "Default", "Guid": "f-s1", "IsDefault": true, "BulkThreshold": 7, "SpamAction": "MoveToJmf", "MarkAsSpamBulkMail": "On" }"""))
        .Add("MalwareFilterPolicy", J("""{ "Name": "Default", "Identity": "Default", "Guid": "f-m1", "IsDefault": true, "EnableFileFilter": true, "ZapEnabled": true }"""))
        .Add("AtpPolicyForO365", J("""{ "Name": "Default", "Identity": "Default", "EnableATPForSPOTeamsODB": false, "EnableSafeDocs": false, "AllowSafeDocsOpen": false }"""))
        .Add("EOPProtectionPolicyRule",
            J("""{ "Name": "Standard Preset Security Policy", "Identity": "Standard Preset Security Policy", "State": "Disabled", "HostedContentFilterPolicy": "Standard Preset Security Policy1800000000009" }"""),
            J("""{ "Name": "Strict Preset Security Policy", "Identity": "Strict Preset Security Policy", "State": "Disabled" }"""))
        .Add("RemoteDomain", J("""{ "Name": "Default", "Identity": "Default", "Guid": "f-e3", "DomainName": "*", "AutoForwardEnabled": true }"""))
        .Add("HostedOutboundSpamFilterPolicy", J("""{ "Name": "Default", "Identity": "Default", "Guid": "f-o1", "IsDefault": true }"""))
        .Add("OwaMailboxPolicy", J("""{ "Name": "OwaMailboxPolicy-Default", "Guid": "f-e4", "IsDefault": true }"""))
        .Add("RetentionPolicyTag", J("""{ "Name": "1 Week Delete", "Guid": "f-e5", "Type": "Personal" }"""))
        .Add("RetentionPolicy", J("""{ "Name": "Default MRM Policy", "Guid": "f-e6", "IsDefault": true }"""));

    static DemoPowerShell DestinationTeams() => new DemoPowerShell(readOnly: false)
        .Add("CsTenant", J("""{ "DisplayName": "Fabrikam" }"""))
        .Add("CsTeamsMeetingPolicy", J("""{ "Identity": "Global", "AllowCloudRecording": false }"""))
        .Add("CsTeamsMessagingPolicy", J("""{ "Identity": "Global", "AllowGiphy": true }"""))
        .Add("CsTenantFederationConfiguration", J("""{ "Identity": "Global", "AllowFederatedUsers": true, "AllowTeamsConsumer": true, "AllowTeamsConsumerInbound": true, "AllowPublicUsers": false }"""))
        .Add("CsTeamsClientConfiguration", J("""{ "Identity": "Global", "AllowGuestUser": true, "AllowDropBox": false, "AllowBox": false, "AllowGoogleDrive": false }"""));

    static JsonObject J(string json) => JsonNode.Parse(json)!.AsObject();
}
