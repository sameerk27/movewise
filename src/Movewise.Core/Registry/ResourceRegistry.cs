using System.Text.Json.Nodes;
using Movewise.Core.Access;
using Movewise.Core.Export;

namespace Movewise.Core.Registry;

/// <summary>The policy types Movewise knows how to migrate, in the order they must be created.</summary>
public static class ResourceRegistry
{
    // Entra ID
    public const string NamedLocation = "namedLocation";
    public const string AuthenticationStrength = "authenticationStrength";
    public const string ConditionalAccessPolicy = "conditionalAccessPolicy";
    public const string EntraUserSettings = "entraUserSettings";
    public const string AuthenticationMethodsPolicy = "authenticationMethodsPolicy";
    public const string AuthMethodAuthenticator = "authMethodMicrosoftAuthenticator";
    public const string AuthMethodFido2 = "authMethodFido2";
    public const string AuthMethodSms = "authMethodSms";
    public const string AuthMethodVoice = "authMethodVoice";
    public const string AuthMethodEmail = "authMethodEmail";
    public const string AuthMethodSoftwareOath = "authMethodSoftwareOath";
    public const string AuthMethodTemporaryAccessPass = "authMethodTemporaryAccessPass";
    public const string CrossTenantDefault = "crossTenantAccessDefault";
    public const string CrossTenantPartner = "crossTenantAccessPartner";
    public const string DirectoryRole = "customDirectoryRole";

    // Intune
    public const string ScopeTag = "scopeTag";
    public const string AssignmentFilter = "assignmentFilter";
    public const string SettingsCatalogPolicy = "settingsCatalogPolicy";
    public const string DeviceConfiguration = "deviceConfiguration";
    public const string CompliancePolicy = "compliancePolicy";
    public const string IosAppProtectionPolicy = "iosAppProtectionPolicy";
    public const string AndroidAppProtectionPolicy = "androidAppProtectionPolicy";
    public const string AutopilotProfile = "autopilotProfile";
    public const string NotificationTemplate = "notificationTemplate";
    public const string DeviceCategory = "deviceCategory";
    public const string IntuneRole = "intuneRole";
    public const string TermsAndConditions = "termsAndConditions";
    public const string PowerShellScript = "powerShellScript";
    public const string MacShellScript = "macShellScript";
    public const string Remediation = "remediation";
    public const string FeatureUpdateProfile = "featureUpdateProfile";
    public const string QualityUpdateProfile = "qualityUpdateProfile";
    public const string DriverUpdateProfile = "driverUpdateProfile";
    public const string AdministrativeTemplate = "administrativeTemplate";
    public const string ManagedAppConfiguration = "managedAppConfiguration";
    public const string ManagedDeviceAppConfiguration = "managedDeviceAppConfiguration";
    public const string EnrollmentConfiguration = "enrollmentConfiguration";
    public const string IntuneApp = "intuneApp";

    // Directory objects that policies point at but Movewise does not migrate; they are mapped instead.
    public const string Group = "group";
    public const string User = "user";
    public const string Application = "application";

    // Defender for Office 365
    public const string AntiPhishPolicy = "antiPhishPolicy";
    public const string AntiSpamPolicy = "antiSpamPolicy";
    public const string OutboundSpamPolicy = "outboundSpamPolicy";
    public const string AntiMalwarePolicy = "antiMalwarePolicy";
    public const string SafeLinksPolicy = "safeLinksPolicy";
    public const string SafeAttachmentPolicy = "safeAttachmentPolicy";
    public const string QuarantinePolicy = "quarantinePolicy";
    public const string BlockedSender = "tenantAllowBlockSender";
    public const string BlockedUrl = "tenantAllowBlockUrl";
    public const string BlockedFile = "tenantAllowBlockFile";
    public const string DefaultAntiPhish = "defaultAntiPhishPolicy";
    public const string DefaultAntiSpam = "defaultAntiSpamPolicy";
    public const string DefaultOutboundSpam = "defaultOutboundSpamPolicy";
    public const string DefaultAntiMalware = "defaultAntiMalwarePolicy";
    public const string AtpSettings = "atpSettings";
    public const string QuarantineSettings = "quarantineSettings";
    public const string StandardPresetEop = "standardPresetEop";
    public const string StrictPresetEop = "strictPresetEop";
    public const string StandardPresetMdo = "standardPresetMdo";
    public const string StrictPresetMdo = "strictPresetMdo";

    // Exchange Online
    public const string TransportRule = "transportRule";
    public const string MobileDevicePolicy = "mobileDevicePolicy";
    public const string RemoteDomain = "remoteDomain";
    public const string OwaMailboxPolicy = "owaMailboxPolicy";
    public const string MailboxRetentionTag = "mailboxRetentionTag";
    public const string MailboxRetentionPolicy = "mailboxRetentionPolicy";
    public const string JournalRule = "journalRule";
    public const string DefaultRemoteDomain = "defaultRemoteDomain";
    public const string DefaultOwaPolicy = "defaultOwaMailboxPolicy";
    public const string DefaultMobileDevicePolicy = "defaultMobileDevicePolicy";

    // Purview
    public const string DlpPolicy = "dlpPolicy";
    public const string RetentionPolicy = "retentionPolicy";
    public const string SensitivityLabel = "sensitivityLabel";
    public const string LabelPolicy = "labelPolicy";
    public const string EndpointDlpSettings = "endpointDlpSettings";
    public const string RetentionLabel = "retentionLabel";
    public const string SensitiveInfoTypePackage = "sensitiveInfoTypePackage";
    public const string AutoLabelPolicy = "autoLabelPolicy";
    public const string AlertPolicy = "alertPolicy";
    public const string AuditRetentionPolicy = "auditRetentionPolicy";

    // SharePoint and OneDrive
    public const string SharePointSettings = "sharePointSettings";

    // Defender for Endpoint
    public const string EndpointIndicator = "endpointIndicator";
    public const string DetectionRule = "customDetectionRule";

    // Teams
    public const string TeamsMeetingPolicy = "teamsMeetingPolicy";
    public const string TeamsMessagingPolicy = "teamsMessagingPolicy";
    public const string TeamsCallingPolicy = "teamsCallingPolicy";
    public const string TeamsAppSetupPolicy = "teamsAppSetupPolicy";
    public const string TeamsAppPermissionPolicy = "teamsAppPermissionPolicy";
    public const string TeamsChannelsPolicy = "teamsChannelsPolicy";
    public const string TeamsUpdateManagementPolicy = "teamsUpdateManagementPolicy";
    public const string TeamsGlobalMeetingPolicy = "teamsGlobalMeetingPolicy";
    public const string TeamsGlobalMessagingPolicy = "teamsGlobalMessagingPolicy";
    public const string TeamsGlobalCallingPolicy = "teamsGlobalCallingPolicy";
    public const string TeamsGlobalAppSetupPolicy = "teamsGlobalAppSetupPolicy";
    public const string TeamsGlobalChannelsPolicy = "teamsGlobalChannelsPolicy";
    public const string TeamsExternalAccess = "teamsExternalAccess";
    public const string TeamsClientSettings = "teamsClientSettings";
    public const string TeamsMeetingSettings = "teamsMeetingSettings";
    public const string TeamsGuestMeetingSettings = "teamsGuestMeetingSettings";
    public const string TeamsGuestMessagingSettings = "teamsGuestMessagingSettings";
    public const string TeamsGuestCallingSettings = "teamsGuestCallingSettings";

    // Values in Exchange and Purview policies that point into the tenant. Mapped, not migrated.
    public const string Domain = "domain";
    public const string Recipient = "recipient";
    public const string Site = "site";

    /// <summary>What PowerShell returns about an object that the service sets itself.</summary>
    static readonly string[] PowerShellReadOnly =
    [
        "id", "Id", "Identity", "Guid", "ImmutableId", "DistinguishedName", "ObjectCategory", "ObjectClass", "ObjectVersion",
        "WhenCreated", "WhenChanged", "WhenCreatedUTC", "WhenChangedUTC", "CreationTimeUtc", "ModificationTimeUtc",
        "ExchangeVersion", "ExchangeObjectId", "OrganizationId", "OriginatingServer", "IsValid", "ObjectState",
        "PSComputerName", "PSShowComputerName", "RunspaceId", "IsDefault", "IsBuiltInProtection", "RecommendedPolicyType",
        "CreatedBy", "LastModifiedBy", "DistributionStatus", "DistributionResults", "DistributionSyncStatus",
        "LastStatusUpdateTime", "ReadOnly", "Workload", "RuleVersion",
    ];

    // Preset and built-in Defender policies exist in every tenant and are managed by Microsoft.
    static readonly string[] PresetPolicyPrefixes =
    [
        "Standard Preset Security Policy", "Strict Preset Security Policy", "Built-In Protection Policy", "Office365 AntiPhish Default",
    ];

    // Quarantine policies Microsoft creates in every tenant, under the same names.
    static readonly string[] BuiltInQuarantinePolicies =
    [
        "AdminOnlyAccessPolicy", "DefaultFullAccessPolicy", "DefaultFullAccessWithNotificationPolicy", "NotificationEnabledPolicy", "DefaultGlobalTag",
    ];

    // Mailbox retention tags Microsoft creates in every tenant, under the same names.
    static readonly string[] BuiltInRetentionTags =
    [
        "Default 2 year move to archive", "Personal 1 year move to archive", "Personal 5 year move to archive", "Personal never move to archive",
        "1 Week Delete", "1 Month Delete", "6 Month Delete", "1 Year Delete", "5 Year Delete", "Never Delete",
        "Recoverable Items 14 days move to archive", "Junk Email", "Deleted Items",
    ];

    // Teams policies Microsoft creates in every tenant. "Global" is the org-wide default.
    static readonly HashSet<string> BuiltInTeamsPolicies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Global", "Default", "AllOn", "AllOff", "RestrictedAnonymousAccess", "RestrictedAnonymousNoRecording", "Kiosk",
        "FirstLineWorker", "EduFaculty", "EduStudent", "AllowCalling", "DisallowCalling", "AllowCallingPreventTollBypass",
        "AllowCallingPreventForwardingtoPhone",
    };

    // Special values that mean the same in every tenant.
    static readonly string[] EveryLocation = ["All"];
    static readonly string[] NotifySpecial = ["SiteAdmin", "LastModifier", "Owner"];

    // How Entra's authentication methods policy says "everyone", and an empty target.
    static readonly string[] AllUsersTarget = ["all_users", "00000000-0000-0000-0000-000000000000"];

    // Built-in authentication strengths have the same ID in every tenant.
    static readonly string[] BuiltInAuthenticationStrengths =
    [
        "00000000-0000-0000-0000-000000000002",
        "00000000-0000-0000-0000-000000000003",
        "00000000-0000-0000-0000-000000000004",
    ];

    const string DeviceManagement = "beta/deviceManagement";
    const string DeviceAppManagement = "beta/deviceAppManagement";

    public static IReadOnlyList<ResourceType> All { get; } =
    [
        // ---------- Entra ID ----------
        new ResourceType(
            NamedLocation,
            M365Service.Entra,
            "Named location",
            "v1.0/identity/conditionalAccess/namedLocations",
            "displayName",
            ["id", "createdDateTime", "modifiedDateTime"],
            [],
            []),

        new ResourceType(
            AuthenticationStrength,
            M365Service.Entra,
            "Authentication strength",
            "v1.0/policies/authenticationStrengthPolicies?$filter=policyType eq 'custom'",
            "displayName",
            ["id", "createdDateTime", "modifiedDateTime", "policyType", "requirementsSatisfied"],
            [],
            [])
        {
            PrepareForCreate = body => body.Remove("combinationConfigurations"),
        },

        new ResourceType(
            ConditionalAccessPolicy,
            M365Service.Entra,
            "Conditional Access policy",
            "v1.0/identity/conditionalAccess/policies",
            "displayName",
            ["id", "createdDateTime", "modifiedDateTime", "templateId"],
            [
                new("conditions.users.includeUsers[]", User) { Ignore = Set("All", "None", "GuestsOrExternalUsers") },
                new("conditions.users.excludeUsers[]", User) { Ignore = Set("All", "None", "GuestsOrExternalUsers") },
                new("conditions.users.includeGroups[]", Group),
                new("conditions.users.excludeGroups[]", Group),
                new("conditions.applications.includeApplications[]", Application) { Ignore = Set("All", "None", "Office365", "MicrosoftAdminPortals") },
                new("conditions.applications.excludeApplications[]", Application) { Ignore = Set("All", "None", "Office365", "MicrosoftAdminPortals") },
                new("conditions.locations.includeLocations[]", NamedLocation) { Ignore = Set("All", "AllTrusted") },
                new("conditions.locations.excludeLocations[]", NamedLocation) { Ignore = Set("All", "AllTrusted") },
                new("grantControls.authenticationStrength.id", AuthenticationStrength)
                {
                    Ignore = Set(BuiltInAuthenticationStrengths),
                    OnRemove = RemoveBehavior.ClearParent,
                },
            ],
            [NamedLocation, AuthenticationStrength])
        {
            // Only the strength's ID can be sent; its name and combinations belong to the strength itself.
            PrepareForCreate = body =>
            {
                if (body["grantControls"] is JsonObject grant && grant["authenticationStrength"] is JsonObject strength)
                    grant["authenticationStrength"] = new JsonObject { ["id"] = strength["id"]?.DeepClone() };
            },
        },

        // What users and guests may do: register apps, create groups and tenants, read other users, invite guests.
        GraphSettings(EntraUserSettings, M365Service.Entra, "Entra ID user settings", "v1.0/policies/authorizationPolicy", [], [],
            DirectoryRoles.PrivilegedRoleAdministrator),

        // Which sign-in methods users may use, and for which groups. The policy itself holds the registration campaign;
        // its methods are changed one by one below. The migration state (legacy MFA and SSPR) isn't touched.
        GraphSettings(AuthenticationMethodsPolicy, M365Service.Entra, "Authentication methods policy", "v1.0/policies/authenticationMethodsPolicy",
            [
                new("registrationEnforcement.authenticationMethodsRegistrationCampaign.includeTargets[].id", Group)
                    { Ignore = Set(AllUsersTarget), OnRemove = RemoveBehavior.RemoveContainingItem },
                new("registrationEnforcement.authenticationMethodsRegistrationCampaign.excludeTargets[].id", Group)
                    { Ignore = Set(AllUsersTarget), OnRemove = RemoveBehavior.RemoveContainingItem },
                new("reportSuspiciousActivitySettings.includeTarget.id", Group) { Ignore = Set(AllUsersTarget) },
            ],
            ["policyVersion", "policyMigrationState", "authenticationMethodConfigurations"],
            DirectoryRoles.AuthenticationPolicyAdministrator),
        AuthenticationMethod(AuthMethodAuthenticator, "Sign-in method: Microsoft Authenticator", "MicrosoftAuthenticator",
            "#microsoft.graph.microsoftAuthenticatorAuthenticationMethodConfiguration", AuthenticatorFeatureTargets()),
        AuthenticationMethod(AuthMethodFido2, "Sign-in method: passkeys (FIDO2)", "Fido2", "#microsoft.graph.fido2AuthenticationMethodConfiguration"),
        AuthenticationMethod(AuthMethodSms, "Sign-in method: SMS", "Sms", "#microsoft.graph.smsAuthenticationMethodConfiguration"),
        AuthenticationMethod(AuthMethodVoice, "Sign-in method: voice call", "Voice", "#microsoft.graph.voiceAuthenticationMethodConfiguration"),
        AuthenticationMethod(AuthMethodEmail, "Sign-in method: email one-time passcode", "Email", "#microsoft.graph.emailAuthenticationMethodConfiguration"),
        AuthenticationMethod(AuthMethodSoftwareOath, "Sign-in method: third-party authenticator apps", "SoftwareOath",
            "#microsoft.graph.softwareOathAuthenticationMethodConfiguration"),
        AuthenticationMethod(AuthMethodTemporaryAccessPass, "Sign-in method: Temporary Access Pass", "TemporaryAccessPass",
            "#microsoft.graph.temporaryAccessPassAuthenticationMethodConfiguration"),

        // Cross-tenant access: the defaults for every other tenant, and settings for particular partner tenants
        // (B2B collaboration, B2B direct connect, trust of partners' MFA and device claims).
        GraphSettings(CrossTenantDefault, M365Service.Entra, "Cross-tenant access default settings", "v1.0/policies/crossTenantAccessPolicy/default",
            CrossTenantTargets(), ["isServiceDefault"], DirectoryRoles.SecurityAdministrator),
        new ResourceType(
            CrossTenantPartner,
            M365Service.Entra,
            "Cross-tenant access partner",
            "v1.0/policies/crossTenantAccessPolicy/partners",
            "tenantId",
            ["isServiceProvider", "isInMultiTenantOrganization", "identitySynchronization"],
            CrossTenantTargets(),
            [])
        {
            IdProperty = "tenantId",
            AdminRoles = [DirectoryRoles.SecurityAdministrator],
            // In a merger, the source often has settings for the destination itself, which can't be its own partner.
            BlockWhen = (partner, destination) => string.Equals(partner["tenantId"]?.GetValue<string>(), destination.TenantId, StringComparison.OrdinalIgnoreCase)
                ? $"This partner is {destination.DisplayName} itself, which can't be its own partner. Leave it out; set up the reverse in the source if it's still needed."
                : null,
        },

        // Custom Entra admin roles. Who holds them isn't copied: the admins are different people.
        new ResourceType(
            DirectoryRole,
            M365Service.Entra,
            "Custom Entra admin role",
            "v1.0/roleManagement/directory/roleDefinitions?$filter=isBuiltIn eq false",
            "displayName",
            ["id", "isBuiltIn", "templateId", "version", "inheritsPermissionsFrom"],
            [],
            [])
        {
            AdminRoles = [DirectoryRoles.PrivilegedRoleAdministrator],
            Include = item => item["isBuiltIn"]?.GetValue<bool>() != true,
        },

        // ---------- Intune ----------
        new ResourceType(
            ScopeTag,
            M365Service.Intune,
            "Scope tag",
            $"{DeviceManagement}/roleScopeTags",
            "displayName",
            ["id", "isBuiltIn"],
            [],
            [])
        {
            Include = item => item["isBuiltIn"]?.GetValue<bool>() != true,
        },

        new ResourceType(
            AssignmentFilter,
            M365Service.Intune,
            "Assignment filter",
            $"{DeviceManagement}/assignmentFilters",
            "displayName",
            ["id", "createdDateTime", "lastModifiedDateTime"],
            [new("roleScopeTags[]", ScopeTag) { Ignore = Set("0") }],
            [ScopeTag]),

        new ResourceType(
            SettingsCatalogPolicy,
            M365Service.Intune,
            "Settings catalog policy",
            $"{DeviceManagement}/configurationPolicies",
            "name",
            ["id", "createdDateTime", "lastModifiedDateTime", "settingCount", "creationSource", "isAssigned"],
            IntuneReferences(),
            [ScopeTag, AssignmentFilter])
        {
            ItemPath = $"{DeviceManagement}/configurationPolicies('{{id}}')?$expand=settings",
            AssignmentsPath = $"{DeviceManagement}/configurationPolicies('{{id}}')/assignments",
            AssignPath = $"{DeviceManagement}/configurationPolicies('{{id}}')/assign",
            PrepareForCreate = body => StripIds(body, "settings"),
        },

        new ResourceType(
            DeviceConfiguration,
            M365Service.Intune,
            "Device configuration profile",
            $"{DeviceManagement}/deviceConfigurations",
            "displayName",
            ["id", "createdDateTime", "lastModifiedDateTime", "version", "supportsScopeTags"],
            IntuneReferences(),
            [ScopeTag, AssignmentFilter])
        {
            AssignmentsPath = $"{DeviceManagement}/deviceConfigurations/{{id}}/assignments",
            AssignPath = $"{DeviceManagement}/deviceConfigurations/{{id}}/assign",
        },

        // The emails a compliance policy sends users; each has a message per language, added once the template exists.
        new ResourceType(
            NotificationTemplate,
            M365Service.Intune,
            "Notification message template",
            $"{DeviceManagement}/notificationMessageTemplates",
            "displayName",
            ["id", "lastModifiedDateTime", "defaultLocale"],
            [new("roleScopeTagIds[]", ScopeTag) { Ignore = Set("0") }],
            [ScopeTag])
        {
            Children = new ChildItems(
                $"{DeviceManagement}/notificationMessageTemplates/{{id}}/localizedNotificationMessages",
                "localizedNotificationMessages",
                messages => messages.OfType<JsonObject>()
                    .Select(m => ($"{DeviceManagement}/notificationMessageTemplates/{{id}}/localizedNotificationMessages", (JsonNode)Without(m, "id", "lastModifiedDateTime")))
                    .ToList()),
        },

        new ResourceType(
            DeviceCategory,
            M365Service.Intune,
            "Device category",
            $"{DeviceManagement}/deviceCategories",
            "displayName",
            ["id"],
            [new("roleScopeTagIds[]", ScopeTag) { Ignore = Set("0") }],
            [ScopeTag]),

        // Custom Intune admin roles. Who holds them (role assignments) isn't copied: the admins are different people.
        new ResourceType(
            IntuneRole,
            M365Service.Intune,
            "Intune admin role",
            $"{DeviceManagement}/roleDefinitions",
            "displayName",
            ["id", "isBuiltIn", "isBuiltInRoleDefinition", "roleAssignments"],
            [new("roleScopeTagIds[]", ScopeTag) { Ignore = Set("0") }],
            [ScopeTag])
        {
            Include = item => item["isBuiltIn"]?.GetValue<bool>() != true && item["isBuiltInRoleDefinition"]?.GetValue<bool>() != true,
        },

        new ResourceType(
            CompliancePolicy,
            M365Service.Intune,
            "Compliance policy",
            $"{DeviceManagement}/deviceCompliancePolicies",
            "displayName",
            ["id", "createdDateTime", "lastModifiedDateTime", "version"],
            [
                .. IntuneReferences(),
                new("scheduledActionsForRule[].scheduledActionConfigurations[].notificationTemplateId", NotificationTemplate)
                    { Ignore = Set("00000000-0000-0000-0000-000000000000") },
            ],
            [ScopeTag, AssignmentFilter, NotificationTemplate])
        {
            // Actions for noncompliance aren't in the list, and a compliance policy can't be created without them.
            ItemPath = $"{DeviceManagement}/deviceCompliancePolicies/{{id}}?$expand=scheduledActionsForRule($expand=scheduledActionConfigurations)",
            AssignmentsPath = $"{DeviceManagement}/deviceCompliancePolicies/{{id}}/assignments",
            AssignPath = $"{DeviceManagement}/deviceCompliancePolicies/{{id}}/assign",
            PrepareForCreate = body =>
            {
                StripIds(body, "scheduledActionsForRule");
                foreach (var action in (body["scheduledActionsForRule"] as JsonArray ?? []).OfType<JsonObject>())
                    StripIds(action, "scheduledActionConfigurations");
            },
        },

        new ResourceType(
            IosAppProtectionPolicy,
            M365Service.Intune,
            "iOS app protection policy",
            $"{DeviceAppManagement}/iosManagedAppProtections",
            "displayName",
            ["id", "createdDateTime", "lastModifiedDateTime", "version", "deployedAppCount", "isAssigned"],
            IntuneReferences(),
            [ScopeTag])
        {
            ItemPath = $"{DeviceAppManagement}/iosManagedAppProtections/{{id}}?$expand=apps",
            AssignmentsPath = $"{DeviceAppManagement}/iosManagedAppProtections/{{id}}/assignments",
            AssignPath = $"{DeviceAppManagement}/iosManagedAppProtections/{{id}}/assign",
            TargetAppsPath = $"{DeviceAppManagement}/iosManagedAppProtections/{{id}}/targetApps",
        },

        new ResourceType(
            AndroidAppProtectionPolicy,
            M365Service.Intune,
            "Android app protection policy",
            $"{DeviceAppManagement}/androidManagedAppProtections",
            "displayName",
            ["id", "createdDateTime", "lastModifiedDateTime", "version", "deployedAppCount", "isAssigned"],
            IntuneReferences(),
            [ScopeTag])
        {
            ItemPath = $"{DeviceAppManagement}/androidManagedAppProtections/{{id}}?$expand=apps",
            AssignmentsPath = $"{DeviceAppManagement}/androidManagedAppProtections/{{id}}/assignments",
            AssignPath = $"{DeviceAppManagement}/androidManagedAppProtections/{{id}}/assign",
            TargetAppsPath = $"{DeviceAppManagement}/androidManagedAppProtections/{{id}}/targetApps",
        },

        new ResourceType(
            AutopilotProfile,
            M365Service.Intune,
            "Autopilot profile",
            $"{DeviceManagement}/windowsAutopilotDeploymentProfiles",
            "displayName",
            ["id", "createdDateTime", "lastModifiedDateTime"],
            IntuneReferences(),
            [ScopeTag])
        {
            AssignmentsPath = $"{DeviceManagement}/windowsAutopilotDeploymentProfiles/{{id}}/assignments",
            // Autopilot profiles have no assign action; each assignment is posted to the collection.
            AssignPath = $"{DeviceManagement}/windowsAutopilotDeploymentProfiles/{{id}}/assignments",
            AssignOneByOne = true,
        },

        // Scripts: the list leaves the script itself out, so each is read on its own.
        IntuneWithAssignments(PowerShellScript, "Windows PowerShell script", "deviceManagementScripts", "deviceManagementScriptAssignments", readItem: true),
        IntuneWithAssignments(MacShellScript, "macOS shell script", "deviceShellScripts", "deviceManagementScriptAssignments", readItem: true),
        IntuneWithAssignments(Remediation, "Remediation", "deviceHealthScripts", "deviceHealthScriptAssignments", readItem: true,
            readOnly: ["isGlobalScript", "highestAvailableVersion", "version", "deviceHealthScriptType"],
            // Scripts Microsoft publishes are in every tenant already.
            include: item => item["isGlobalScript"]?.GetValue<bool>() != true),

        // Windows Update for Business profiles. Update rings are device configuration profiles, which are copied already.
        IntuneWithAssignments(FeatureUpdateProfile, "Windows feature update profile", "windowsFeatureUpdateProfiles", "assignments",
            readOnly: ["deployableContentDisplayName", "endOfSupportDate"]),
        IntuneWithAssignments(QualityUpdateProfile, "Windows quality update profile", "windowsQualityUpdateProfiles", "assignments",
            readOnly: ["deployableContentDisplayName", "releaseDateDisplayName"]),
        IntuneWithAssignments(DriverUpdateProfile, "Windows driver update profile", "windowsDriverUpdateProfiles", "assignments",
            readOnly: ["deviceReporting", "newUpdates", "inventorySyncStatus"]),

        // Administrative templates (ADMX): the template, then its settings, which name Microsoft's built-in definitions.
        // Settings from ADMX files imported into the source have IDs of their own; they fail with Microsoft's message.
        IntuneWithAssignments(AdministrativeTemplate, "Administrative template", "groupPolicyConfigurations", "assignments") with
        {
            Children = new ChildItems(
                $"{DeviceManagement}/groupPolicyConfigurations/{{id}}/definitionValues?$expand=definition($select=id),presentationValues($expand=presentation($select=id))",
                "definitionValues",
                AdministrativeTemplateSettings),
        },

        // App configuration: for apps on managed devices (which name the Intune apps they configure), and for apps
        // protected by app protection policies (which name apps by bundle or package ID, the same everywhere).
        IntuneWithAssignments(ManagedDeviceAppConfiguration, "App configuration policy (managed devices)", "mobileAppConfigurations", "assignments",
            appManagement: true, extraReferences: [new("targetedMobileApps[]", IntuneApp)]),
        IntuneWithAssignments(ManagedAppConfiguration, "App configuration policy (managed apps)", "targetedManagedAppConfigurations", "assignments",
            appManagement: true, readOnly: ["deployedAppCount", "isAssigned"]) with
        {
            ItemPath = $"{DeviceAppManagement}/targetedManagedAppConfigurations/{{id}}?$expand=apps",
            TargetAppsPath = $"{DeviceAppManagement}/targetedManagedAppConfigurations/{{id}}/targetApps",
        },

        // Enrollment restrictions, Windows Hello for Business and enrollment status pages. The default ones (priority 0)
        // are in every tenant; the priority of new ones is set by Intune.
        IntuneWithAssignments(EnrollmentConfiguration, "Enrollment restriction or status page", "deviceEnrollmentConfigurations", "enrollmentConfigurationAssignments",
            readOnly: ["priority"],
            include: item => item["priority"]?.GetValue<int>() is > 0,
            extraReferences: [new("selectedMobileAppIds[]", IntuneApp)]),

        new ResourceType(
            TermsAndConditions,
            M365Service.Intune,
            "Terms and conditions",
            $"{DeviceManagement}/termsAndConditions",
            "displayName",
            ["id", "createdDateTime", "lastModifiedDateTime", "modifiedDateTime", "version"],
            IntuneReferences(),
            [ScopeTag])
        {
            AssignmentsPath = $"{DeviceManagement}/termsAndConditions/{{id}}/assignments",
            AssignPath = $"{DeviceManagement}/termsAndConditions/{{id}}/assignments",
            AssignOneByOne = true,
        },

        // Intune apps: never copied (their content can't be), only matched by name for the policies that name them.
        new ResourceType(
            IntuneApp,
            M365Service.Intune,
            "Intune app",
            $"{DeviceAppManagement}/mobileApps?$select=id,displayName",
            "displayName",
            ["id"],
            [],
            [])
        {
            ReferenceOnly = true,
        },

        // ---------- Defender for Office 365 (Exchange Online PowerShell) ----------
        // What users can do with the mail a policy quarantines. Policies name theirs, so these come first.
        new ResourceType(
            QuarantinePolicy,
            M365Service.Defender,
            "Quarantine policy",
            "",
            "Name",
            [.. PowerShellReadOnly, "EndUserQuarantinePermissions", "QuarantinePolicyType"],
            [],
            [])
        {
            Backend = Backend.ExchangePowerShell,
            Cmdlets = new("Get-QuarantinePolicy", "New-QuarantinePolicy", "Remove-QuarantinePolicy"),
            IdIsName = true,
            Include = item =>
                !string.Equals(item["QuarantinePolicyType"]?.GetValue<string>(), "GlobalQuarantinePolicy", StringComparison.OrdinalIgnoreCase)
                && !BuiltInQuarantinePolicies.Contains(item["Name"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase),
        },

        DefenderPolicy(AntiPhishPolicy, "Anti-phishing policy", "AntiPhish",
        [
            new("TargetedDomainsToProtect[]", Domain),
            new("ExcludedDomains[]", Domain),
            .. Quarantine("SpoofQuarantineTag", "TargetedUserQuarantineTag", "TargetedDomainQuarantineTag", "MailboxIntelligenceQuarantineTag"),
        ]),

        DefenderPolicy(AntiSpamPolicy, "Anti-spam policy", "HostedContentFilter",
        [
            new("RedirectToRecipients[]", Recipient),
            .. Quarantine("SpamQuarantineTag", "HighConfidenceSpamQuarantineTag", "PhishQuarantineTag", "HighConfidencePhishQuarantineTag", "BulkQuarantineTag"),
        ]),

        DefenderPolicy(OutboundSpamPolicy, "Outbound spam policy", "HostedOutboundSpamFilter",
        [
            new("NotifyOutboundSpamRecipients[]", Recipient),
            new("BccSuspiciousOutboundAdditionalRecipients[]", Recipient),
        ]),

        DefenderPolicy(AntiMalwarePolicy, "Anti-malware policy", "MalwareFilter",
        [
            new("InternalSenderAdminAddress", Recipient),
            new("ExternalSenderAdminAddress", Recipient),
            .. Quarantine("QuarantineTag"),
        ]),

        DefenderPolicy(SafeLinksPolicy, "Safe Links policy", "SafeLinks", []),

        DefenderPolicy(SafeAttachmentPolicy, "Safe Attachments policy", "SafeAttachment",
        [
            new("RedirectAddress", Recipient),
            .. Quarantine("QuarantineTag"),
        ]),

        // The Tenant Allow/Block List: senders, URLs and files blocked (or allowed) for the whole organization.
        AllowBlockList(BlockedSender, "Tenant allow/block sender", "Sender", [new("Value", Recipient)]),
        AllowBlockList(BlockedUrl, "Tenant allow/block URL", "Url", []),
        AllowBlockList(BlockedFile, "Tenant allow/block file", "FileHash", []),

        // Settings every tenant already has: changed to match the source, never created or deleted.
        Settings(DefaultAntiPhish, M365Service.Defender, "Default anti-phishing policy", "Get-AntiPhishPolicy", "Set-AntiPhishPolicy", IsDefault,
            [
                new("TargetedDomainsToProtect[]", Domain),
                new("ExcludedDomains[]", Domain),
                .. Quarantine("SpoofQuarantineTag", "TargetedUserQuarantineTag", "TargetedDomainQuarantineTag", "MailboxIntelligenceQuarantineTag"),
            ]),
        Settings(DefaultAntiSpam, M365Service.Defender, "Default anti-spam policy", "Get-HostedContentFilterPolicy", "Set-HostedContentFilterPolicy", IsDefault,
            [
                new("RedirectToRecipients[]", Recipient),
                .. Quarantine("SpamQuarantineTag", "HighConfidenceSpamQuarantineTag", "PhishQuarantineTag", "HighConfidencePhishQuarantineTag", "BulkQuarantineTag"),
            ]),
        Settings(DefaultOutboundSpam, M365Service.Defender, "Default outbound spam policy", "Get-HostedOutboundSpamFilterPolicy", "Set-HostedOutboundSpamFilterPolicy", IsDefault,
            [
                new("NotifyOutboundSpamRecipients[]", Recipient),
                new("BccSuspiciousOutboundAdditionalRecipients[]", Recipient),
            ]),
        Settings(DefaultAntiMalware, M365Service.Defender, "Default anti-malware policy", "Get-MalwareFilterPolicy", "Set-MalwareFilterPolicy", IsDefault,
            [
                new("InternalSenderAdminAddress", Recipient),
                new("ExternalSenderAdminAddress", Recipient),
                .. Quarantine("QuarantineTag"),
            ]),
        Settings(AtpSettings, M365Service.Defender, "Safe Links and Safe Attachments settings", "Get-AtpPolicyForO365", "Set-AtpPolicyForO365", _ => true, [],
            missing: "They come with Defender for Office 365. Add a license that includes it, then run pre-flight again."),
        Settings(QuarantineSettings, M365Service.Defender, "Quarantine notification settings", "Get-QuarantinePolicy", "Set-QuarantinePolicy",
            item => string.Equals(item["QuarantinePolicyType"]?.GetValue<string>(), "GlobalQuarantinePolicy", StringComparison.OrdinalIgnoreCase),
            [new("EndUserSpamNotificationCustomFromAddress", Recipient)],
            extraReadOnly: ["QuarantinePolicyType", "EndUserQuarantinePermissions"],
            getParameters: new Dictionary<string, string> { ["QuarantinePolicyType"] = "GlobalQuarantinePolicy" }),
        PresetPolicy(StandardPresetEop, "Standard preset protection (Exchange Online Protection)", "EOP", "Standard"),
        PresetPolicy(StrictPresetEop, "Strict preset protection (Exchange Online Protection)", "EOP", "Strict"),
        PresetPolicy(StandardPresetMdo, "Standard preset protection (Defender for Office 365)", "ATP", "Standard"),
        PresetPolicy(StrictPresetMdo, "Strict preset protection (Defender for Office 365)", "ATP", "Strict"),

        // ---------- Exchange Online ----------
        new ResourceType(
            TransportRule,
            M365Service.Exchange,
            "Mail flow rule",
            "",
            "Name",
            PowerShellReadOnly,
            [
                .. RecipientConditions(""),
                .. new[] { "BlindCopyTo", "AddToRecipients", "CopyTo", "RedirectMessageTo", "ModerateMessageByUser" }
                    .Select(p => new ReferenceRule($"{p}[]", Recipient)),
                new("GenerateIncidentReport", Recipient),
            ],
            [])
        {
            Backend = Backend.ExchangePowerShell,
            Cmdlets = new("Get-TransportRule", "New-TransportRule", "Remove-TransportRule"),
            MakeSafe = rule => SetIf(rule, "Mode", "Enforce", "Audit",
                "Created in test mode: it's evaluated and logged, but takes no action on mail. Set it to Enforce after checking message trace."),
        },

        new ResourceType(
            MobileDevicePolicy,
            M365Service.Exchange,
            "Mobile device mailbox policy",
            "",
            "Name",
            PowerShellReadOnly,
            [],
            [])
        {
            Backend = Backend.ExchangePowerShell,
            Cmdlets = new("Get-MobileDeviceMailboxPolicy", "New-MobileDeviceMailboxPolicy", "Remove-MobileDeviceMailboxPolicy"),
            // The default policy exists in every tenant.
            Include = item => item["IsDefault"]?.GetValue<bool>() != true,
        },

        // How mail to one outside domain is handled (automatic replies, forwarding, formats). The Default one ("*")
        // exists in every tenant.
        new ResourceType(
            RemoteDomain,
            M365Service.Exchange,
            "Remote domain",
            "",
            "Name",
            PowerShellReadOnly,
            [new("DomainName", Domain)],
            [])
        {
            Backend = Backend.ExchangePowerShell,
            Cmdlets = new("Get-RemoteDomain", "New-RemoteDomain", "Remove-RemoteDomain") { Set = "Set-RemoteDomain" },
            Include = item => item["DomainName"]?.GetValue<string>() != "*",
        },

        new ResourceType(
            OwaMailboxPolicy,
            M365Service.Exchange,
            "Outlook on the web policy",
            "",
            "Name",
            PowerShellReadOnly,
            [],
            [])
        {
            Backend = Backend.ExchangePowerShell,
            Cmdlets = new("Get-OwaMailboxPolicy", "New-OwaMailboxPolicy", "Remove-OwaMailboxPolicy") { Set = "Set-OwaMailboxPolicy" },
            Include = item => item["IsDefault"]?.GetValue<bool>() != true,
        },

        // Messaging records management: retention tags, and the mailbox retention policies that group them.
        new ResourceType(
            MailboxRetentionTag,
            M365Service.Exchange,
            "Mailbox retention tag",
            "",
            "Name",
            PowerShellReadOnly,
            [],
            [])
        {
            Backend = Backend.ExchangePowerShell,
            Cmdlets = new("Get-RetentionPolicyTag", "New-RetentionPolicyTag", "Remove-RetentionPolicyTag"),
            IdIsName = true,
            Include = item =>
                item["SystemTag"]?.GetValue<bool>() != true
                && !BuiltInRetentionTags.Contains(item["Name"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase),
            AfterRead = RetentionTags.FromGet,
        },

        new ResourceType(
            MailboxRetentionPolicy,
            M365Service.Exchange,
            "Mailbox retention policy",
            "",
            "Name",
            PowerShellReadOnly,
            [new("RetentionPolicyTagLinks[]", MailboxRetentionTag) { Ignore = Set(BuiltInRetentionTags) }],
            [MailboxRetentionTag])
        {
            Backend = Backend.ExchangePowerShell,
            Cmdlets = new("Get-RetentionPolicy", "New-RetentionPolicy", "Remove-RetentionPolicy"),
            Include = item => item["IsDefault"]?.GetValue<bool>() != true && item["IsDefaultArbitrationMailbox"]?.GetValue<bool>() != true,
        },

        new ResourceType(
            JournalRule,
            M365Service.Exchange,
            "Journal rule",
            "",
            "Name",
            PowerShellReadOnly,
            [new("JournalEmailAddress", Recipient), new("Recipient", Recipient)],
            [])
        {
            Backend = Backend.ExchangePowerShell,
            Cmdlets = new("Get-JournalRule", "New-JournalRule", "Remove-JournalRule"),
            MakeSafe = rule => SetIf(rule, "Enabled", true, false,
                "Created switched off, so it doesn't send copies of mail anywhere yet. Turn it on after checking where it sends them."),
        },

        Settings(DefaultRemoteDomain, M365Service.Exchange, "Default remote domain", "Get-RemoteDomain", "Set-RemoteDomain",
            item => item["DomainName"]?.GetValue<string>() == "*", [], extraReadOnly: ["DomainName"]),
        Settings(DefaultOwaPolicy, M365Service.Exchange, "Default Outlook on the web policy", "Get-OwaMailboxPolicy", "Set-OwaMailboxPolicy", IsDefault, []),
        Settings(DefaultMobileDevicePolicy, M365Service.Exchange, "Default mobile device mailbox policy", "Get-MobileDeviceMailboxPolicy", "Set-MobileDeviceMailboxPolicy", IsDefault, []),

        // ---------- Purview (Security & Compliance PowerShell) ----------
        // Custom sensitive information types, as the rule package (XML) that defines them. DLP and auto-labeling rules
        // name them by the IDs in that package, which stay the same, so they're created first.
        new ResourceType(
            SensitiveInfoTypePackage,
            M365Service.Purview,
            "Custom sensitive information types",
            "",
            "RuleCollectionName",
            [.. PowerShellReadOnly, "Version", "Publisher", "LocalizedName", "Description"],
            [],
            [])
        {
            Backend = Backend.CompliancePowerShell,
            // The package carries its name and IDs inside; New takes only the file.
            Cmdlets = new("Get-DlpSensitiveInformationTypeRulePackage", "New-DlpSensitiveInformationTypeRulePackage", "Remove-DlpSensitiveInformationTypeRulePackage",
                NameParameter: null),
            // Microsoft's own packages are in every tenant.
            Include = item => !(item["Publisher"]?.ToString() ?? "").StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                && !(item["RuleCollectionName"]?.ToString() ?? "").StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase),
            PrepareForCreate = body =>
            {
                body["FileData"] = body["SerializedClassificationRuleCollection"]?.DeepClone();
                body.Remove("SerializedClassificationRuleCollection");
            },
        },

        new ResourceType(
            DlpPolicy,
            M365Service.Purview,
            "DLP policy",
            "",
            "Name",
            PowerShellReadOnly,
            [
                .. Locations(),
                new("rules[].NotifyUser[]", Recipient) { Ignore = Set(NotifySpecial) },
                new("rules[].GenerateIncidentReport[]", Recipient) { Ignore = Set(NotifySpecial) },
                new("rules[].GenerateAlert[]", Recipient) { Ignore = Set(NotifySpecial) },
                .. RecipientConditions("rules[]."),
            ],
            [])
        {
            Backend = Backend.CompliancePowerShell,
            Cmdlets = new("Get-DlpCompliancePolicy", "New-DlpCompliancePolicy", "Remove-DlpCompliancePolicy", ["DistributionDetail"]),
            Rules = new("Get-DlpComplianceRule", "New-DlpComplianceRule", "Remove-DlpComplianceRule", "ParentPolicyName", "Policy", ListPerPolicy: true),
            MakeSafe = policy => SetIf(policy, "Mode", "Enable", "TestWithoutNotifications",
                "Created in simulation mode: matches are reported, but nothing is blocked and users see no tips. Turn it on after checking the reports."),
        },

        // Retention labels, before the retention policies that publish or apply them (their rules name labels by name).
        new ResourceType(
            RetentionLabel,
            M365Service.Purview,
            "Retention label",
            "",
            "Name",
            [.. PowerShellReadOnly, "Policy", "HasRetentionAction", "ComplianceTagType", "FilePlanMetadata"],
            [new("ReviewerEmail[]", Recipient)],
            [])
        {
            Backend = Backend.CompliancePowerShell,
            Cmdlets = new("Get-ComplianceTag", "New-ComplianceTag", "Remove-ComplianceTag"),
            IdIsName = true,
        },

        new ResourceType(
            RetentionPolicy,
            M365Service.Purview,
            "Retention policy",
            "",
            "Name",
            PowerShellReadOnly,
            [.. Locations(), new("ModernGroupLocation[]", Recipient) { Ignore = Set(EveryLocation) }, new("ModernGroupLocationException[]", Recipient)],
            [])
        {
            Backend = Backend.CompliancePowerShell,
            Cmdlets = new("Get-RetentionCompliancePolicy", "New-RetentionCompliancePolicy", "Remove-RetentionCompliancePolicy", ["DistributionDetail"]),
            Rules = new("Get-RetentionComplianceRule", "New-RetentionComplianceRule", "Remove-RetentionComplianceRule", "Policy", "Policy", ListPerPolicy: true),
            // Adaptive scopes aren't migrated, so policies that use them can't be recreated.
            Include = item => item["AdaptiveScopeLocation"] is not JsonArray { Count: > 0 },
            MakeSafe = policy =>
            {
                var changes = new List<string>();
                if (policy["RestrictiveRetention"]?.GetValue<bool>() == true)
                {
                    policy.Remove("RestrictiveRetention");
                    changes.Add("Preservation Lock isn't copied: a locked policy can never be turned off or deleted. Lock it yourself once you've checked it.");
                }
                changes.AddRange(SetIf(policy, "Enabled", true, false,
                    "Created switched off, so it can't delete anything yet. Turn it on after checking its locations and settings."));
                return changes;
            },
        },

        new ResourceType(
            SensitivityLabel,
            M365Service.Purview,
            "Sensitivity label",
            "",
            "Name",
            [.. PowerShellReadOnly, "ParentLabelDisplayName", "IsParent", "IsLabelGroup", "Disabled", "Mode", "LabelActions"],
            [
                // A sublabel points at its parent, which is created first.
                new("ParentId", SensitivityLabel),
                new("EncryptionRightsDefinitions[].Identity", Recipient) { Ignore = Set(SensitivityLabels.EveryoneRights) },
            ],
            [])
        {
            Backend = Backend.CompliancePowerShell,
            Cmdlets = new("Get-Label", "New-Label", "Remove-Label"),
            AfterRead = SensitivityLabels.FromGetLabel,
            PrepareForCreate = SensitivityLabels.PrepareLabelForCreate,
        },

        new ResourceType(
            LabelPolicy,
            M365Service.Purview,
            "Label policy",
            "",
            "Name",
            PowerShellReadOnly,
            [
                new("Labels[]", SensitivityLabel),
                .. new[] { "defaultlabelid", "outlookdefaultlabel", "powerbidefaultlabelid", "teamworkdefaultlabelid", "siteandgroupdefaultlabelid" }
                    .Select(key => new ReferenceRule($"AdvancedSettings.{key}", SensitivityLabel) { Ignore = Set("None") }),
                .. Locations(),
                new("ModernGroupLocation[]", Recipient) { Ignore = Set(EveryLocation) },
                new("ModernGroupLocationException[]", Recipient),
            ],
            [SensitivityLabel])
        {
            Backend = Backend.CompliancePowerShell,
            Cmdlets = new("Get-LabelPolicy", "New-LabelPolicy", "Remove-LabelPolicy"),
            AfterRead = SensitivityLabels.FromGetLabelPolicy,
        },

        // Auto-labeling: applies a sensitivity label to matching mail and files. Created in simulation, so nothing is
        // labeled until the admin has checked the results and turned it on.
        new ResourceType(
            AutoLabelPolicy,
            M365Service.Purview,
            "Auto-labeling policy",
            "",
            "Name",
            PowerShellReadOnly,
            [
                .. Locations(),
                new("ApplySensitivityLabel", SensitivityLabel),
                .. RecipientConditions("rules[]."),
            ],
            [SensitivityLabel])
        {
            Backend = Backend.CompliancePowerShell,
            Cmdlets = new("Get-AutoSensitivityLabelPolicy", "New-AutoSensitivityLabelPolicy", "Remove-AutoSensitivityLabelPolicy", ["DistributionDetail"]),
            Rules = new("Get-AutoSensitivityLabelRule", "New-AutoSensitivityLabelRule", "Remove-AutoSensitivityLabelRule", "ParentPolicyName", "Policy", ListPerPolicy: true),
            MakeSafe = policy => SetIf(policy, "Mode", "Enable", "TestWithoutNotifications",
                "Created in simulation: it shows what would be labeled, but labels nothing. Turn it on after checking the simulation."),
        },

        // Alert policies. Microsoft's own (system) alerts are in every tenant.
        new ResourceType(
            AlertPolicy,
            M365Service.Purview,
            "Alert policy",
            "",
            "Name",
            [.. PowerShellReadOnly, "IsSystemRule", "LogicalOperationName", "PolicyId", "CorrelationPolicyId"],
            [new("NotifyUser[]", Recipient)],
            [])
        {
            Backend = Backend.CompliancePowerShell,
            Cmdlets = new("Get-ProtectionAlert", "New-ProtectionAlert", "Remove-ProtectionAlert"),
            Include = item => item["IsSystemRule"]?.GetValue<bool>() != true,
        },

        // How long audit log records are kept (Audit Premium). Each needs a priority, unlike other policies.
        new ResourceType(
            AuditRetentionPolicy,
            M365Service.Purview,
            "Audit log retention policy",
            "",
            "Name",
            PowerShellReadOnly,
            [new("UserIds[]", Recipient)],
            [])
        {
            Backend = Backend.CompliancePowerShell,
            Cmdlets = new("Get-UnifiedAuditLogRetentionPolicy", "New-UnifiedAuditLogRetentionPolicy", "Remove-UnifiedAuditLogRetentionPolicy"),
            KeepsPriority = true,
        },

        // Endpoint DLP's tenant-wide settings: file path exclusions, restricted apps and browsers, service domains.
        // Get-PolicyConfig holds other Purview switches too; only these are read, so nothing else can change.
        Settings(EndpointDlpSettings, M365Service.Purview, "Endpoint DLP settings", "Get-PolicyConfig", "Set-PolicyConfig", _ => true, [],
            missing: "Endpoint DLP needs onboarded devices and a license that includes it. Set it up in the Purview portal, then run pre-flight again.",
            backend: Backend.CompliancePowerShell,
            setTakesIdentity: false,
            afterRead: item =>
            {
                foreach (var name in item.Select(p => p.Key).ToList())
                {
                    if (name is not ("EndpointDlpGlobalSettings" or "EndpointDlpBrowserRestrictions" or "Name" or "Identity"))
                        item.Remove(name);
                }
            }),

        // ---------- Defender for Endpoint ----------
        // Indicators: files, IP addresses, URLs, domains and certificates to block, warn about or allow on devices.
        // They name device groups by name; groups missing in the destination make the indicator fail with Defender's message.
        new ResourceType(
            EndpointIndicator,
            M365Service.DefenderEndpoint,
            "Defender for Endpoint indicator",
            "api/indicators",
            "indicatorValue",
            ["id", "creationTimeDateTimeUtc", "createdBy", "createdByDisplayName", "createdBySource", "lastUpdateTime", "lastUpdatedBy",
             "sourceType", "externalId", "rbacGroupIds", "mitreTechniques", "historicalDetection", "lookBackPeriod"],
            [],
            [])
        {
            Backend = Backend.DefenderApi,
            // Expired ones would be refused, and do nothing anyway.
            Include = item => item["expirationTime"] is not JsonValue expires
                || !expires.TryGetValue<string>(out var text)
                || !DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
                || date > DateTimeOffset.UtcNow,
        },

        // Custom detection rules (advanced hunting queries run on a schedule). Created switched off.
        new ResourceType(
            DetectionRule,
            M365Service.DefenderEndpoint,
            "Custom detection rule",
            "beta/security/rules/detectionRules",
            "displayName",
            ["id", "createdBy", "createdDateTime", "lastModifiedBy", "lastModifiedDateTime", "lastRunDetails", "detectorId"],
            [],
            [])
        {
            MakeSafe = rule => SetIf(rule, "isEnabled", true, false,
                "Created switched off, so it raises no alerts and takes no actions yet. Turn it on after checking its query and actions."),
        },

        // ---------- SharePoint and OneDrive ----------
        // Sharing, sync and site creation for the whole tenant.
        GraphSettings(SharePointSettings, M365Service.SharePoint, "SharePoint and OneDrive settings", "v1.0/admin/sharepoint/settings",
            [new("sharingAllowedDomainList[]", Domain), new("sharingBlockedDomainList[]", Domain)],
            // The sync app's allowed domains are the tenant's own domain GUIDs; the managed paths are the service's.
            ["allowedDomainGuidsForSyncApp", "availableManagedPathsForSiteCreation"],
            DirectoryRoles.SharePointAdministrator),

        // ---------- Teams (Microsoft Teams PowerShell) ----------
        TeamsPolicy(TeamsMeetingPolicy, "Teams meeting policy", "TeamsMeetingPolicy"),
        TeamsPolicy(TeamsMessagingPolicy, "Teams messaging policy", "TeamsMessagingPolicy"),
        TeamsPolicy(TeamsCallingPolicy, "Teams calling policy", "TeamsCallingPolicy"),
        TeamsPolicy(TeamsAppSetupPolicy, "Teams app setup policy", "TeamsAppSetupPolicy"),
        TeamsPolicy(TeamsAppPermissionPolicy, "Teams app permission policy", "TeamsAppPermissionPolicy"),
        TeamsPolicy(TeamsChannelsPolicy, "Teams channels policy", "TeamsChannelsPolicy"),
        TeamsPolicy(TeamsUpdateManagementPolicy, "Teams update policy", "TeamsUpdateManagementPolicy"),

        // The org-wide (Global) policies: what everyone gets unless a policy is assigned to them.
        TeamsSettings(TeamsGlobalMeetingPolicy, "Org-wide Teams meeting policy (Global)", "TeamsMeetingPolicy"),
        TeamsSettings(TeamsGlobalMessagingPolicy, "Org-wide Teams messaging policy (Global)", "TeamsMessagingPolicy"),
        TeamsSettings(TeamsGlobalCallingPolicy, "Org-wide Teams calling policy (Global)", "TeamsCallingPolicy"),
        TeamsSettings(TeamsGlobalAppSetupPolicy, "Org-wide Teams app setup policy (Global)", "TeamsAppSetupPolicy"),
        TeamsSettings(TeamsGlobalChannelsPolicy, "Org-wide Teams channels policy (Global)", "TeamsChannelsPolicy"),

        // Teams settings for the whole organization. The lists of allowed and blocked domains for external access are
        // objects Set can't take, so they're left for the admin.
        TeamsSettings(TeamsExternalAccess, "Teams external access settings", "TenantFederationConfiguration",
            ["AllowedDomains", "BlockedDomains", "AllowedTrialTenantDomains", "AllowedDomainsAsAList"]),
        TeamsSettings(TeamsClientSettings, "Teams guest access and cloud storage settings", "TeamsClientConfiguration"),
        TeamsSettings(TeamsMeetingSettings, "Teams meeting settings", "TeamsMeetingConfiguration"),
        TeamsSettings(TeamsGuestMeetingSettings, "Teams guest meeting settings", "TeamsGuestMeetingConfiguration"),
        TeamsSettings(TeamsGuestMessagingSettings, "Teams guest messaging settings", "TeamsGuestMessagingConfiguration"),
        TeamsSettings(TeamsGuestCallingSettings, "Teams guest calling settings", "TeamsGuestCallingConfiguration"),
    ];

    /// <summary>
    /// A Teams policy. It does nothing until it's assigned, so its group assignments come with it. Assignments
    /// to single users aren't migrated: the users are different in the destination.
    /// </summary>
    static ResourceType TeamsPolicy(string id, string displayName, string noun) =>
        new(id, M365Service.Teams, displayName, "", "Name", [.. PowerShellReadOnly, "Key", "ScopeClass", "XsAnyElements", "Element", "DataSource"],
            [new("groupAssignments[].GroupId", Group) { OnRemove = RemoveBehavior.RemoveContainingItem }],
            [])
        {
            Backend = Backend.TeamsPowerShell,
            Cmdlets = new($"Get-Cs{noun}", $"New-Cs{noun}", $"Remove-Cs{noun}", NameParameter: "Identity"),
            TeamsPolicyType = noun,
            Include = item => !BuiltInTeamsPolicies.Contains(item["Name"]?.GetValue<string>() ?? item["Identity"]?.GetValue<string>() ?? "Global"),
        };

    static bool IsDefault(JsonObject item) => item["IsDefault"]?.GetValue<bool>() == true;

    /// <summary>
    /// Settings every tenant has (see <see cref="SettingsCmdlets"/>). Identified by their type: the source's are compared
    /// with the destination's, and only the ones that differ are changed.
    /// </summary>
    static ResourceType Settings(
        string id, M365Service service, string displayName, string get, string set, Func<JsonObject, bool> pick, ReferenceRule[] references,
        string? missing = null, string[]? extraReadOnly = null, IReadOnlyDictionary<string, string>? getParameters = null, (string, string)? onOff = null,
        Backend backend = Backend.ExchangePowerShell, bool setTakesIdentity = true, Action<JsonObject>? afterRead = null) =>
        new(id, service, displayName, "", "Name", [.. PowerShellReadOnly, .. extraReadOnly ?? []], references,
            references.Any(r => r.TargetType == QuarantinePolicy) ? [QuarantinePolicy] : [])
        {
            Backend = backend,
            Settings = new SettingsCmdlets(get, set, pick,
                missing ?? $"Every tenant normally has it. Open it once in the admin center so it's set up, then run pre-flight again.")
            {
                GetParameters = getParameters,
                Switch = onOff,
                SetTakesIdentity = setTakesIdentity,
            },
            AfterRead = afterRead,
        };

    /// <summary>A Teams org-wide setting or the org-wide (Global) policy of a type, which Set changes by the Identity "Global".</summary>
    static ResourceType TeamsSettings(string id, string displayName, string noun, string[]? extraReadOnly = null) =>
        Settings(id, M365Service.Teams, displayName, $"Get-Cs{noun}", $"Set-Cs{noun}",
            item => string.Equals(item["Identity"]?.GetValue<string>(), "Global", StringComparison.OrdinalIgnoreCase),
            [],
            extraReadOnly: ["Key", "ScopeClass", "XsAnyElements", "Element", "DataSource", .. extraReadOnly ?? []],
            backend: Backend.TeamsPowerShell);

    /// <summary>Settings held in one Microsoft Graph object, read with GET and changed with PATCH.</summary>
    static ResourceType GraphSettings(
        string id, M365Service service, string displayName, string path, ReferenceRule[] references, string[] readOnly, string adminRole,
        string missing = "Every tenant has these. If Microsoft Graph can't read them, check the account's admin roles, then run pre-flight again.",
        Action<JsonObject>? prepare = null) =>
        new(id, service, displayName, "", "displayName", ["id", "displayName", "description", "lastModifiedDateTime", .. readOnly], references, [])
        {
            Backend = Backend.Graph,
            Settings = SettingsCmdlets.Graph(path, missing),
            AdminRoles = [adminRole],
            PrepareForCreate = prepare,
        };

    /// <summary>
    /// One sign-in method in the authentication methods policy: whether it's on, and for which groups. Graph needs the
    /// configuration's type named when it's changed.
    /// </summary>
    static ResourceType AuthenticationMethod(string id, string displayName, string method, string odataType, ReferenceRule[]? extra = null) =>
        GraphSettings(id, M365Service.Entra, displayName,
            $"v1.0/policies/authenticationMethodsPolicy/authenticationMethodConfigurations/{method}",
            [
                new("includeTargets[].id", Group) { Ignore = Set(AllUsersTarget), OnRemove = RemoveBehavior.RemoveContainingItem },
                new("excludeTargets[].id", Group) { Ignore = Set(AllUsersTarget), OnRemove = RemoveBehavior.RemoveContainingItem },
                .. extra ?? [],
            ],
            [],
            DirectoryRoles.AuthenticationPolicyAdministrator,
            prepare: body => body["@odata.type"] = odataType);

    /// <summary>
    /// The groups cross-tenant access settings apply to, for each direction. "AllUsers" means everyone. A target can
    /// be a single user; it's matched as a group, so it shows as unmatched for the admin to decide.
    /// </summary>
    static ReferenceRule[] CrossTenantTargets() =>
        new[] { "b2bCollaborationInbound", "b2bCollaborationOutbound", "b2bDirectConnectInbound", "b2bDirectConnectOutbound", "tenantRestrictions" }
            .Select(direction => new ReferenceRule($"{direction}.usersAndGroups.targets[].target", Group)
            {
                Ignore = Set("AllUsers"),
                OnRemove = RemoveBehavior.RemoveContainingItem,
            })
            .ToArray();

    /// <summary>The Microsoft Authenticator feature settings (show app name, show location…), each for some groups.</summary>
    static ReferenceRule[] AuthenticatorFeatureTargets() =>
        new[] { "displayAppInformationRequiredState", "displayLocationInformationRequiredState", "companionAppAllowedState", "numberMatchingRequiredState" }
            .SelectMany(feature => new[] { "includeTarget", "excludeTarget" }.Select(target =>
                new ReferenceRule($"featureSettings.{feature}.{target}.id", Group) { Ignore = Set(AllUsersTarget) }))
            .ToArray();


    /// <summary>
    /// A preset security policy (Standard or Strict): Microsoft sets the protection itself; what the admin chooses is
    /// whether it's on and who it applies to, which is its rule. EOP covers anti-spam and anti-malware; ATP (Defender for
    /// Office 365) adds Safe Links, Safe Attachments and impersonation protection.
    /// </summary>
    static ResourceType PresetPolicy(string id, string displayName, string noun, string level) =>
        Settings(id, M365Service.Defender, displayName, $"Get-{noun}ProtectionPolicyRule", $"Set-{noun}ProtectionPolicyRule",
            item => string.Equals(item["Name"]?.GetValue<string>(), $"{level} Preset Security Policy", StringComparison.OrdinalIgnoreCase),
            [.. RecipientConditions("")],
            missing: $"Turn on the {level} preset security policy once in the Microsoft Defender portal (Email & collaboration > Policies & rules > " +
                "Threat policies > Preset security policies), even for nobody, so the destination has it. Then run pre-flight again.",
            // The protection policies it links to have names that differ in every tenant; they aren't the admin's choice.
            extraReadOnly: ["HostedContentFilterPolicy", "AntiPhishPolicy", "MalwareFilterPolicy", "SafeLinksPolicy", "SafeAttachmentPolicy", "Priority"],
            onOff: ($"Enable-{noun}ProtectionPolicyRule", $"Disable-{noun}ProtectionPolicyRule"));

    /// <summary>Quarantine policies a Defender policy names. The built-in ones exist everywhere under the same name.</summary>
    static IEnumerable<ReferenceRule> Quarantine(params string[] properties) =>
        properties.Select(p => new ReferenceRule(p, QuarantinePolicy) { Ignore = Set(BuiltInQuarantinePolicies) });

    /// <summary>
    /// One list of the Tenant Allow/Block List. Each entry is named by its value. Expired entries are left out;
    /// entries without an end date stay without one.
    /// </summary>
    static ResourceType AllowBlockList(string id, string displayName, string listType, ReferenceRule[] references) =>
        new(id, M365Service.Defender, displayName, "", "Value", [.. PowerShellReadOnly, "SubmissionID", "LastUsedDate", "LastModifiedDateTime", "EntryValueHash", "SysManaged"], references, [])
        {
            Backend = Backend.ExchangePowerShell,
            Cmdlets = new("Get-TenantAllowBlockListItems", "New-TenantAllowBlockListItems", "Remove-TenantAllowBlockListItems", NameParameter: "Entries")
            {
                Scope = new Dictionary<string, string> { ["ListType"] = listType },
                RemoveIdentityParameter = "Ids",
            },
            Include = item => item["ExpirationDate"] is not JsonValue expires
                || !expires.TryGetValue<string>(out var text)
                || !DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
                || date > DateTimeOffset.UtcNow,
            PrepareForCreate = AllowBlockEntry,
        };

    /// <summary>An entry as read, in the shape New-TenantAllowBlockListItems takes: Block or Allow, and an end date or none.</summary>
    static void AllowBlockEntry(JsonObject entry)
    {
        var allow = string.Equals(entry["Action"]?.GetValue<string>(), "Allow", StringComparison.OrdinalIgnoreCase);
        entry.Remove("Action");
        entry[allow ? "Allow" : "Block"] = true;
        if (entry["ExpirationDate"] is null)
        {
            entry.Remove("ExpirationDate");
            entry["NoExpiration"] = true;
        }
    }

    /// <summary>A Defender policy and its rule, which sets who the policy applies to.</summary>
    static ResourceType DefenderPolicy(string id, string displayName, string noun, ReferenceRule[] policyReferences) =>
        new(id, M365Service.Defender, displayName, "", "Name", PowerShellReadOnly, [.. policyReferences, .. RecipientConditions("rules[].")], [QuarantinePolicy])
        {
            Backend = Backend.ExchangePowerShell,
            Cmdlets = new($"Get-{noun}Policy", $"New-{noun}Policy", $"Remove-{noun}Policy"),
            Rules = new($"Get-{noun}Rule", $"New-{noun}Rule", $"Remove-{noun}Rule", $"{noun}Policy", $"{noun}Policy"),
            Include = item =>
                item["IsDefault"]?.GetValue<bool>() != true
                && item["IsBuiltInProtection"]?.GetValue<bool>() != true
                && !PresetPolicyPrefixes.Any(p => (item["Name"]?.GetValue<string>() ?? "").StartsWith(p, StringComparison.OrdinalIgnoreCase)),
        };

    /// <summary>
    /// Who a rule applies to, and who it leaves out: recipients, their groups, and their domains. The conditions (not the
    /// exceptions) limit the rule, so they mustn't be left empty.
    /// </summary>
    static IEnumerable<ReferenceRule> RecipientConditions(string prefix) =>
        new[] { "", "ExceptIf" }.SelectMany(except => new ReferenceRule[]
        {
            new($"{prefix}{except}SentTo[]", Recipient) { LimitsScope = except.Length == 0 },
            new($"{prefix}{except}SentToMemberOf[]", Recipient) { LimitsScope = except.Length == 0 },
            new($"{prefix}{except}From[]", Recipient) { LimitsScope = except.Length == 0 },
            new($"{prefix}{except}FromMemberOf[]", Recipient) { LimitsScope = except.Length == 0 },
            new($"{prefix}{except}RecipientDomainIs[]", Domain) { LimitsScope = except.Length == 0 },
            new($"{prefix}{except}SenderDomainIs[]", Domain) { LimitsScope = except.Length == 0 },
        });

    /// <summary>Where a Purview policy applies: mailboxes and groups, SharePoint sites, OneDrive accounts.</summary>
    static IEnumerable<ReferenceRule> Locations() =>
        new[] { "", "Exception" }.SelectMany(except => new ReferenceRule[]
        {
            new($"ExchangeLocation{except}[]", Recipient) { Ignore = Set(EveryLocation) },
            new($"TeamsLocation{except}[]", Recipient) { Ignore = Set(EveryLocation) },
            new($"SharePointLocation{except}[]", Site) { Ignore = Set(EveryLocation) },
            new($"OneDriveLocation{except}[]", Site) { Ignore = Set(EveryLocation) },
        });

    /// <summary>Changes a property from one value to another, and describes it, only when it has that value.</summary>
    static IEnumerable<string> SetIf<T>(JsonObject policy, string property, T from, T to, string description)
    {
        if (policy[property] is JsonValue value && value.TryGetValue<T>(out var current) && EqualityComparer<T>.Default.Equals(current, from))
        {
            policy[property] = JsonValue.Create(to);
            return [description];
        }
        return [];
    }

    public static ResourceType Get(string id) =>
        All.FirstOrDefault(t => t.Id == id) ?? throw new KeyNotFoundException($"Unknown resource type '{id}'.");

    /// <summary>The types to read from a service. Reference-only types (Intune apps) are only matched, never read for migrating.</summary>
    public static IEnumerable<ResourceType> For(M365Service service) => All.Where(t => t.Service == service && !t.ReferenceOnly);

    /// <summary>Every Intune policy can be assigned to groups, narrowed by a filter, and tagged with scope tags.</summary>
    static ReferenceRule[] IntuneReferences() =>
    [
        new("assignments[].target.groupId", Group) { OnRemove = RemoveBehavior.RemoveContainingItem },
        new("assignments[].target.deviceAndAppManagementAssignmentFilterId", AssignmentFilter)
        {
            Ignore = Set("00000000-0000-0000-0000-000000000000"),
            OnRemove = RemoveBehavior.ClearValue,
            ClearedSibling = ("deviceAndAppManagementAssignmentFilterType", "none"),
        },
        new("roleScopeTagIds[]", ScopeTag) { Ignore = Set("0") },
    ];

    /// <summary>
    /// An Intune object with the usual groups, filters and scope tags, whose assignments are read from its assignments
    /// and sent with its assign action.
    /// </summary>
    /// <param name="collection">The collection under deviceManagement (or deviceAppManagement, with <paramref name="appManagement"/>).</param>
    /// <param name="assignBody">The property the assign action takes the assignments in.</param>
    /// <param name="readItem">True when the list leaves out what's needed (a script's content), so each is read on its own.</param>
    static ResourceType IntuneWithAssignments(
        string id, string displayName, string collection, string assignBody, bool readItem = false, bool appManagement = false,
        string[]? readOnly = null, Func<JsonObject, bool>? include = null, ReferenceRule[]? extraReferences = null)
    {
        var path = $"{(appManagement ? DeviceAppManagement : DeviceManagement)}/{collection}";
        return new ResourceType(
            id,
            M365Service.Intune,
            displayName,
            path,
            "displayName",
            ["id", "createdDateTime", "lastModifiedDateTime", "version", .. readOnly ?? []],
            [.. IntuneReferences(), .. extraReferences ?? []],
            [ScopeTag, AssignmentFilter])
        {
            ItemPath = readItem ? $"{path}/{{id}}" : null,
            AssignmentsPath = $"{path}/{{id}}/assignments",
            AssignPath = $"{path}/{{id}}/assign",
            AssignBodyProperty = assignBody,
            Include = include,
        };
    }

    /// <summary>
    /// An administrative template's settings as Intune takes them: one request that adds every setting, each bound to
    /// its definition and each value to its presentation. Built-in definitions have the same IDs in every tenant.
    /// </summary>
    static IReadOnlyList<(string Path, JsonNode Body)> AdministrativeTemplateSettings(JsonArray values)
    {
        const string definitions = "https://graph.microsoft.com/beta/deviceManagement/groupPolicyDefinitions";
        var added = new JsonArray();
        foreach (var value in values.OfType<JsonObject>())
        {
            var definitionId = value["definition"]?["id"]?.GetValue<string>();
            if (definitionId is null)
                continue;
            var presentations = new JsonArray();
            foreach (var presentation in (value["presentationValues"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var item = Without(presentation, "id", "createdDateTime", "lastModifiedDateTime", "presentation");
                if (presentation["presentation"]?["id"]?.GetValue<string>() is { } presentationId)
                    item["presentation@odata.bind"] = $"{definitions}('{definitionId}')/presentations('{presentationId}')";
                presentations.Add(item);
            }
            added.Add(new JsonObject
            {
                ["enabled"] = value["enabled"]?.DeepClone(),
                ["definition@odata.bind"] = $"{definitions}('{definitionId}')",
                ["presentationValues"] = presentations,
            });
        }
        if (added.Count == 0)
            return [];
        return [($"{DeviceManagement}/groupPolicyConfigurations/{{id}}/updateDefinitionValues",
            new JsonObject { ["added"] = added, ["updated"] = new JsonArray(), ["deletedIds"] = new JsonArray() })];
    }

    /// <summary>A copy of an object without the given properties.</summary>
    static JsonObject Without(JsonObject item, params string[] properties)
    {
        var copy = (JsonObject)item.DeepClone();
        foreach (var property in properties)
            copy.Remove(property);
        return copy;
    }

    /// <summary>Removes the source's IDs from the items of an array; the destination gives them new ones.</summary>
    static void StripIds(JsonObject body, string arrayProperty)
    {
        foreach (var item in (body[arrayProperty] as JsonArray ?? []).OfType<JsonObject>())
            item.Remove("id");
    }

    static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
}
