# Plan: migrate enterprise applications

Status: **phases 1 and 2 done, phase 2 waiting for review.** Mockups: [enterprise-apps-mockups.html](enterprise-apps-mockups.html) (open it in a browser).

Work one phase at a time, each on `feature/enterprise-apps` (or a branch from it), and check in with the owner after each phase.

## Goal

Today apps are only a dependency of policies: `Matcher.MatchAppAsync` matches an app by app ID, and when the destination doesn't have it the admin is told to add it by hand. This feature adds an **Applications** section that migrates enterprise applications and app registrations with their settings, sign-on and user and group assignments. The policy migration stays as it is, and the two are linked so policies find the apps migrated for them.

## Decisions (made with the owner)

1. **Structure: separate but linked.** A new **Applications** item in the sidebar with its own steps: Choose apps → Map users & groups → Pre-flight → Deploy. It uses the tenants signed in on Connect. The policy flow (Connect, Discover, Map, Pre-flight, Deploy) does not change, apart from recognising migrated apps. No new service card on Connect; apps never appear in the Discover policy list.
2. **Scope:** enterprise apps (gallery and multi-tenant apps, same app ID in every tenant) and the source's own app registrations (recreated with a new app ID). Leave out Microsoft first-party apps (`appOwnerOrganizationId` = `f8cdef31-a31e-4b4a-93e4-5f571e91255a`) and managed identities.
3. **API permissions:** copied onto the app, but **admin consent is never granted by Movewise**. Listed for an admin to grant. Tests must check that no grant is ever created.
4. **SAML SSO:** migrated with a **new signing certificate** (private keys can't be exported). Results list each SAML app whose vendor side needs the new certificate or metadata.

## How the two flows link

| Situation | Behaviour |
|---|---|
| Apps migrated first (recommended) | Each app run records *source app ID → destination app ID* for the destination tenant. The policy Map screen reads it: a recreated app registration matches as **Migrated by Movewise**, and policies are rewritten with the new ID. |
| A policy needs an app missing from the destination | Its Map entry says "Not in the destination yet" with a **Migrate it in Applications** button that opens Applications with that app ticked. |
| Policies deployed before their apps | Policy pre-flight warns which policies point at apps still missing. |
| Apps only | The Applications section works on its own. |

## What moves

| Item | Enterprise app (same app ID) | App registration (new app ID) |
|---|---|---|
| The app | Service principal for the same app ID (gallery apps via `applicationTemplates/{id}/instantiate`) | Application + service principal, new app ID |
| Properties | Assignment required, visible to users (`HideApp` tag), notes, home page, logo | Same, plus redirect URIs, app roles (same role IDs), optional claims, exposed API scopes, `requiredResourceAccess` |
| Users and groups assigned | `appRoleAssignedTo`, recreated with matched users and groups, same app role | same |
| Owners | Matched users | same |
| SAML SSO | `preferredSingleSignOnMode`, identifier, reply URL, sign-on URL, relay state, claims mapping; new signing certificate (`addTokenSigningCertificate`) | same |
| API permissions | Listed | Requested permissions copied; consent listed |

**Not migrated (list under "Set up by hand"):** client secrets and certificate private keys, provisioning (SCIM) settings and credentials, password-based SSO credentials, Application Proxy connectors, consent grants.

## How it's built

- **Screens:** `Applications` sidebar item (`Components/Shell.razor`) and four pages with their own step bar, reusing the existing table, mapping and pre-flight markup and CSS so they look like the policy flow.
- **State:** its own state class, separate from `MigrationState`, so the flows never overwrite each other's selection or mapping. Both use `MigrationState.Source` / `Destination`.
- **Registry** (`src/Movewise.Core/Registry/ResourceRegistry.cs`): types `enterpriseApp` (`v1.0/servicePrincipals`) and `appRegistration` (`v1.0/applications`), used only by this section. References: owners, assigned users and groups.
- **Export** (`src/Movewise.Core/Export/Exporter.cs`): each app with `appRoleAssignedTo`, owners, SSO settings, claims mapping policy, and for registrations the manifest. Record what can't be copied.
- **App ID map:** new file per destination tenant beside the run files (`src/Movewise.Core/Deploy/RunStore.cs`). `Matcher.MatchAppAsync` (`src/Movewise.Core/Mapping/Matcher.cs`) checks it before reporting an app as missing. Rollback removes its entries.
- **Pre-flight:** consent needed, new SAML certificate, secrets to recreate, provisioning by hand, apps already there (skip or update in place), unmatched users and groups. Role check: Application Administrator or Cloud Application Administrator. In the policy flow: warn for policies pointing at missing apps.
- **Deploy** (`src/Movewise.Core/Deploy/Deployer.cs`, `DeployPlanner.cs`): its own run kind. Order: apps → SSO settings and certificate → owners and assignments. Progress saved after each step, resumable.
- **Rollback:** delete apps and assignments the run created (Entra keeps deleted apps 30 days), put back settings changed on existing apps.
- **Compare & drift:** apps can be included in comparisons and snapshots.
- **Demo** (`src/Movewise.App/Demo/`): Contoso gets a gallery SAML app, a multi-tenant app and two app registrations; one is referenced by a Conditional Access policy so the link can be tried.
- **Tests** (`tests/Movewise.Core.Tests`, fake Graph in `FakeGraph.cs`): export, deploy order, app ID map written and read by the Matcher, rollback, consent never granted.

## Phases

1. **Applications section + enterprise apps:** sidebar item, four steps, enterprise apps (same app ID) with settings, owners and assignments, rollback, demo, tests.
2. **App registrations + the link:** new app ID, app ID map, "Migrated by Movewise" and "Migrate it in Applications" on the policy Map, policy pre-flight warning, permissions listed, secrets under "Set up by hand".
3. **SAML SSO:** SSO settings and claims, new signing certificate, gallery-template instantiation.
4. **Docs and site:** README, a website page in `site/` ("Migrate enterprise applications between tenants", add it to `site/sitemap.xml` and `site/llms.txt`), release notes.

## Risks

- Some gallery apps can't be instantiated by template: fall back to "Set up by hand" with the template name.
- Anything hard-coding an app registration's old app ID keeps pointing at the source: the results list every new app ID.
- Policies migrated before apps can't point at missing apps: pre-flight warns and the policy Map links to Applications.

## Working notes for whoever implements this

- Build and test: `dotnet build Movewise.sln` and `dotnet test tests/Movewise.Core.Tests` (189 tests passing on master when this plan was written). The app must be closed before building, or the build can't replace `Movewise.exe`.
- Match the surrounding code and comment style; user-facing text is short, plain and precise.
- Keep token use low: no multi-agent workflows unless the owner asks, test mainly with unit tests and the demo, and take only a few screenshots at the end of a phase. When driving the app on screen, capture only the Movewise window and never the rest of the desktop.
- `src/Movewise.App/Services/UpdateService.cs`, `src/Movewise.Core/Baselines/Baseline.cs` and `src/Movewise.Core/Reports/` may have the owner's own work in progress: check with them before changing those.
- Commit and push only when the owner asks; open pull requests to `master`; the owner merges.
