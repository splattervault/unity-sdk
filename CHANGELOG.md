# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [3.7.0] - 2026-11-15

### Security
- **Your API key belongs on servers only.** Every sample now reads the key from the `SV_API_KEY` environment variable and is compiled only into the Unity Editor and dedicated server builds (`#if UNITY_SERVER || UNITY_EDITOR`). Until now every sample kept the key in an Inspector field, which is saved into scenes and prefabs and ships inside the game build: anyone who pulls it out can start and stop your servers and spend your (or your organization's) credits. If you copied a sample into a game client, take the key out and replace it on your dashboard. Game clients will sign players in instead (SDK 4.0).
- README and INSTALLATION no longer suggest keeping the key in a `[SerializeField]` field or a ScriptableObject.

### Added
- `ClaimPoolServerAsync(poolId, request, onSuccess, onError)`: claim a warm server from one of your server pools and start a session on it (`POST /server-pools/{id}/claim`). Much faster than creating a session. Returns `PoolClaimResponse` with `sessionId` and `connection` (how players connect). Stopping the session (`StopCreditSessionAsync`) returns the server to its pool. Fails with 409 when no warm server is free. An organization key needs the `org:manage_pools` permission.
- `ClaimPoolServerRequest`: `friendlyName`, `mode`, `customVariables` (launch-argument overrides, sent as `argOverrides`), `idleTimeoutMinutes`, `autoDestroyOnProcessExit`, `scheduledEndTime`, with the same setters as `CreateSessionRequest`. All optional. On a claim, launch-argument values are always checked against the game's schema, also with the game owner's key.
- `SessionConnectionInfo`, `SessionConnectionPort`, `SessionConnectionInstructions`: host, main port, all player ports, join code, connection mode, launch link and the game's connection steps.
- `GameSession.ready`, `readyAt`, `readyDeadlineAt` with `IsReady()`, `GetReadyAt()` and `GetReadyDeadlineAt()`: whether, and when, the game server said it is ready for players. `ready` is null on an older API.
- `WaitForServerReady` has a new last parameter, `waitForGameReady` (default `false`, which keeps the 3.6 behaviour: return as soon as the session is Active). Pass `true` to also wait, once Active, for the game to say it is ready (`ready`). A game with no ready signal for the mode it runs never says so: the call then returns the Active session once its ready deadline has passed (about 5 minutes after the server is up) or when `maxWaitSeconds` runs out. Existing calls compile and behave unchanged.
- `OrgCreditStats.availableBalance`, `subscriptionCreditsFrozen`, `hasSubscription` and `autoBuyPack` (`AutoBuyPack { credits, price }`): fields the API already sent that the SDK did not read.
- `package.json` now depends on `com.unity.nuget.newtonsoft-json` 3.0.2, so Newtonsoft.Json is installed with the SDK. If your project already has a newer version, Unity keeps the newer one.

### Fixed
- `WaitForServerReady` (and `CreateAndWaitForMSTServer`) kept polling for the full 5 minutes when a server failed: it looked for "Failed" and "Error" statuses that the API never sends, and its own catch swallowed the error. It now stops as soon as the session is "Not Active" and throws `InvalidOperationException` with the stop reason. A failing status request is retried up to 3 times in a row and then thrown, instead of being hidden until the timeout.
- `GetOrgSubscriptionAsync` threw with an organization key whose org ID was not resolved (a client made with `new SplatterVaultClient(key)` instead of `CreateAsync`). That path reads `GET /org/subscription`, which answers a plain list, and the SDK expected `{ current, all }`. It now reads either answer and sets `current` the way the server does: the active subscription, else the newest.
- `OrgCreditStats.GetAvailableBalance()` counted subscription credits while they were frozen, so an organization could look able to pay when it was not. It now returns the API's `availableBalance`, which leaves frozen credits out (on an API that does not send it, the SDK leaves them out itself, as `CreditBalance.GetAvailableBalance()` already did).
- The Basic Examples sample did not compile: `TournamentManager` called `HasEnoughCredits` with one argument and awaited `async void` methods.
- `package.json` claimed Unity 2019.4, but the SDK uses `UnityWebRequest.Result`, which needs Unity 2020.2.

### Changed
- Minimum Unity version is now 2020.2.
- The MST sample is replaced by `SplatterVaultMSTSpawner`, a small server-side spawner for an MST master server: it starts a server (or claims one from a pool) with the master's address as launch arguments, and the game server registers its own room. The sample is now called "MST Spawner".
- The .NET test harness moved from `Tests/` to `Tests~/`. Unity skips folders whose name ends in `~`, so the harness is not imported into your project. A new `.npmignore` leaves the repository-only `CLAUDE.md` (which has no `.meta` file) out of the registry package. Installing from a registry (OpenUPM) therefore no longer warns about files without `.meta` files. A Git URL install still contains `CLAUDE.md`; Unity's warning about it is harmless.
- The repository URL is the same everywhere: `https://github.com/splattervault/unity-sdk`.

### Deprecated
- `OrgCreditStats.autoBuyCreditAmount`: the API never sent it (it sends `autoBuyPack`), so it was always null. The client now fills it from `autoBuyPack.credits`; use `autoBuyPack` instead.
- `Subscription.periodStart`: the API never sends it, so it is always null. Use `periodEnd`.

### Removed
- MST samples `MSTIntegrationExample`, `SplatterVaultMSTBridge` and `SplatterVaultSessionManager`: every MST call in them was commented out, they used the old `Barebones` namespaces, and they registered rooms from the spawning side, which is not how MST rooms work.
- The Photon Bolt sample. It was never listed in `package.json`, did not compile, and created servers from the game client, which needs the API key inside the client build.

## [3.6.0] - 2026-11-14

### Added
- `Region.MKC1` (Kansas City), a DigitalOcean region the session API already accepts.
- Vultr regions `Region.VDFW` (Dallas, TX), `Region.VORD` (Chicago, IL), `Region.VMIA` (Miami, FL) and `Region.VLAX` (Los Angeles, CA). These only work where the platform has switched that Vultr city on for your game; otherwise the create call is normally refused with a 400 error. You get one only by asking for it: a DigitalOcean region never falls back to Vultr, though a Vultr city you ask for can fall back to another Vultr city or a nearby DigitalOcean region.

### Notes
- The new members are appended to the end of the enum, so the integer values of all existing regions are unchanged (`NYC1` is still 0, `SYD1` is still 12). Unity saves `[SerializeField] Region` fields by integer value, so regions already chosen in scenes and prefabs keep pointing at the same datacenter. (3.4.0 inserted members in the middle of the enum and removed `SFO1`, which renumbered regions saved before 3.4.0; check scenes saved with 3.3.x or earlier if you have not already.)
- No wire change: `CreateSessionRequest.region` is still a plain string and the API already returns these codes, so code that passed them as strings works as before. Only `SetRegion(Region)` could not reach them.

## [3.5.0] - 2026-09-29

### Added
- `CreateSessionRequest.idleTimeoutMinutes` (nullable `int?`) and `SetIdleTimeoutMinutes(int)`. The platform stops the session once nobody has been connected for that many minutes (5–1440; the server rejects other values with 400). The clock starts when the server is ready, so a server nobody joins is stopped after that long. Pool-served sessions are returned to their pool. When omitted, nothing changes: the session is never idle-stopped. Serializes as `idleTimeoutMinutes` and is left out of the request when null.
- `GameSession` idle-monitoring fields: `idleTimeoutMinutes`, `idleMonitorState` (`starting` / `active` / `empty` / `unknown`), `idleSince` and `lastPeerSampleAt`, with `GetIdleSince()` and `GetLastPeerSampleAt()` (UTC `DateTime?`, culture-independent parsing).
- `GameSession.IsIdleTimedOut()`: true when `stopReason` is `"IDLE_TIMEOUT"`, the new stop reason for idle stops. Status strings are unchanged; an idle-stopped session is `"Not Active"`.

### Changed (server)
- `CreateSubscriptionSessionAsync` with an organization API key (`sv_org_...`) now fails with a 400 error ("Organization API keys create sessions with credits: use POST /rest/credits/sessions…") instead of an unexplained 403. It never succeeded with an org key: an organization's subscription grants monthly credits, so use `CreateCreditSessionAsync`. Personal keys are unaffected.

### Notes
- Requires server support. An older API ignores the field and the create response has `idleTimeoutMinutes == null`, so check it if you rely on the timeout.
- See the README "Idle Timeout" section for what counts as a connection and the known limitations (private-network clients, relay-only networking, server-browser queries).

## [3.4.0] - 2026-06-23

### Added
- `Region` enum now covers all 13 offered regions — added `ATL1` (Atlanta), `RIC1` (Richmond), `AMS3` (Amsterdam), `FRA1` (Frankfurt), `SGP1` (Singapore), `BLR1` (Bangalore), and `SYD1` (Sydney). Previously the enum exposed only 7 regions, so strongly-typed callers could not target most EU/APAC datacenters even though the session API accepts them.

### Breaking
- Removed the retired `Region.SFO1` value (the SFO1 datacenter is no longer offered). Code referencing `Region.SFO1` will no longer compile — use `Region.SFO2` or `Region.SFO3` instead.

## [3.3.0] - 2026-06-02

### Added
- `CreateSessionRequest.autoDestroyOnProcessExit` (nullable `bool?`) and `SetAutoDestroyOnProcessExit(bool)` setter. When `true`, the session is automatically stopped if the game server process exits (clean or crash). When omitted, the platform falls back to the per-game-type default configured by the game owner. Explicit `false` overrides a `true` game-type default.
- New session stop reason `AUTO_DESTROYED` (surfaced by the API) — distinguishes auto-destroyed sessions from `GAME_CRASH` / `PROCESS_EXIT`.

## [3.2.1] - 2026-06-01

### Fixed
- **Launch-argument overrides never reached the server.** `CreateSessionRequest.customVariables` serialized under the JSON key `customVariables`, but the API only reads `environmentVariables` (the field was renamed server-side). The C# field now carries `[JsonProperty("environmentVariables")]`, so overrides set via `AddCustomVariable` / `SetCustomVariables` are actually applied. No SDK API changes — `customVariables` and its methods keep the same names.

### Notes
- Override values are validated server-side against the game's launch-arg schema (number min/max, select options, text pattern, required). When a session is created with the **owning game's API key**, the owner may pass any value for a declared arg (server-side validation is bypassed). Flags not declared in the game's `launchArguments` are still ignored.

## [3.2.0] - 2026-04-07

### Breaking
- Removed session-level `isPublic` from `CreateSessionRequest` and `GameSession`. The backend no longer has a per-session public/private column — games that need the concept declare a boolean launchArgument with `semantic: "public"` that the session creator toggles via `customVariables`. To preserve old behavior, pass `customVariables = { "--public" = "true" }` on create (or use `request.AddCustomVariable("--public", "true")`).
- Removed `isPublic` from `MSTServerInfo` (populated from the removed session field).

## [3.1.0] - 2026-04-02

### Added
- `CreateAsync(apiKey, baseUrl)` — static factory that auto-resolves organization ID for org API keys via `GET /auth/me`
- `InitializeAsync()` — resolves auth context from the server, auto-sets `organizationId` for org keys
- `AuthContext` property — exposes the resolved auth context (type, organizationId, permissions, etc.)
- `CreateSessionAsync(request, serverType)` — unified session creation that routes to credit or subscription endpoint
- `AuthContext` model class for the `/auth/me` response
- `CreateSessionRequest.channel` field — select a build channel by name (e.g., "stable", "beta")
- `SetChannel(string)` helper on `CreateSessionRequest`
- Build Channels documentation section in README

### Changed
- `GetOrgCreditBalanceAsync` now falls back to `/org/credits` for org API keys when org ID is not explicitly set
- `GetOrgSubscriptionAsync` now falls back to `/org/subscription` for org API keys when org ID is not explicitly set
- Updated `ResolveOrgId` error message to suggest `CreateAsync()` for auto-resolution
- Updated `SimpleSessionManager` sample to use `CreateAsync` pattern

### Developer Experience
- **Org API key users no longer need to know or provide their organization ID** — `CreateAsync` handles it automatically
- Before: `new SplatterVaultClient("sv_org_key", orgId: 42)`
- After: `await SplatterVaultClient.CreateAsync("sv_org_key")`

## [3.0.0] - 2026-03-31

### Breaking Changes
- **Removed game-specific enums**: `GameType`, `PaintballMode`, `SnapshotMode`, `ValheimMode` and all `ToApiString()` extensions. The SDK no longer hardcodes knowledge of specific games.
- **`CreateSessionRequest.gameType` removed** — replaced by `gameKey`
- **`CreateSessionRequest.mode` removed** — mode is now a launch argument, pass via `AddCustomVariable()`
- **`CreateSessionRequest.gameTypeConfigKey` removed** — replaced by `gameKey`
- Helper methods removed: `SetGameType()`, `SetPaintballMode()`, `SetSnapshotMode()`, `SetValheimMode()`, `SetGameTypeConfigKey()`, `SetMode()`

### Added
- `CreateSessionRequest.gameKey` — game config key (e.g., `"sys_1774636058786_30e0fc4d"`), serializes as `gameTypeConfigKey` for the API
- `GetConfigurableArgsAsync(gameKey)` — fetch available launch arguments for a game (mode, max players, etc.)
- `StructuredLaunchArg` and `SelectOption` models for dynamic game configuration UI
- **Organization API key support** — `sv_org_` prefixed keys work across all endpoints
- Organization constructor, `GetOrgCreditBalanceAsync()`, `GetOrgSubscriptionAsync()`
- `OrgCreditStats`, `OrgSubscriptionInfo`, `SubscriptionDetails`, `CancelScheduleResult` models
- `GameSession.organizationId`, `stopReason`, `stopReasonDetails` fields
- .NET 8 integration test harness in `Tests/` directory

### Fixed
- `GameSession.stopReasonDetails` changed from `string` to `object` (API returns JSONB)

### Migration from v2.x
```csharp
// OLD (v2.x)
request.SetGameType(GameType.PaintballPlayground);
request.SetPaintballMode(PaintballMode.XBall);

// NEW (v3.0)
request.gameKey = "sys_1774636058786_30e0fc4d";  // from your dashboard
request.AddCustomVariable("-mstRoomMode", "XBall");  // mode is a launch arg

// Discover available options dynamically:
var args = await client.GetConfigurableArgsAsync(gameKey);
```

## [2.0.0] - 2026-03-20

### Breaking Changes
- `StopSessionAsync` now requires a `GameSession` object instead of a session ID, to route to the correct API endpoint based on `serverType`
- `UpdateSessionFriendlyNameAsync` now requires a `GameSession` object instead of a session ID
- `CreditBalance` fields changed from `int` to `float` to match API decimal values
- `CreditBalance.GetBalanceInHours()` replaced with `GetBalanceInHours(float creditsPerMinute)` — rates vary by server size (0.25–11.75)
- `CreditBalance.HasEnoughCredits(int)` replaced with `HasEnoughCredits(float minutes, float creditsPerMinute)`
- `GetConnectionString` no longer accepts a port parameter — uses the actual `slavePort` from the API
- Removed `JsonHelper` class (all deserialization now uses Newtonsoft.Json)

### Fixed
- **CRITICAL:** `StopSessionAsync` was calling `DELETE /game-sessions/:id` (admin-only delete endpoint) instead of `POST /credits/sessions/:id/stop` or `POST /subscriptions/sessions/:id/stop`
- **CRITICAL:** Credit balance/amount fields were `int` but API returns decimals (e.g., `19401.93`) — values were being truncated
- **HIGH:** `CreateSubscriptionSessionAsync` used `JsonUtility.ToJson` which silently dropped `Dictionary` fields (`customVariables`)
- **HIGH:** `UpdateSessionFriendlyNameAsync` called `PATCH /game-sessions/:id` (admin endpoint) instead of `PUT /{serverType}/sessions/:id/friendly-name`
- **HIGH:** All JSON deserialization used `JsonUtility` which cannot handle nullable fields, decimals, or nested objects — replaced with `Newtonsoft.Json` throughout
- `GetConnectionString` hardcoded port `7777` instead of using actual `slavePort` from API (production uses `8100`)
- `MSTServerInfo.port` hardcoded `8100` instead of reading from session

### Added
- `StopCreditSessionAsync` and `StopSubscriptionSessionAsync` for explicit endpoint control
- `StopSessionResult` class with `session`, `totalHours`, `totalCost` fields (returned by credit stop endpoint)
- `ServerSizeInfo` class (`id`, `friendlyName`, `creditsPerMinute`)
- `GameSession.hostname`, `slavePort`, `serverStoppedAt`, `serverSizeId`, `buildId`, `volumeId`, `creditsDeducted`, `serverSize` fields
- `GameSession.IsPending()`, `IsStopped()`, `GetServerPort()` helpers
- `CreditBalance.subscriptionBalance`, `adHocBalance`, `subscriptionCreditsFrozen`, `isInGracePeriod` fields
- `CreditBalance.GetAvailableBalance()` — respects frozen subscription credits
- `CreditStats.subscriptionBalance`, `adHocBalance`, `subscriptionCreditsFrozen` fields
- `Subscription.currentInstances` field
- `CreateSessionRequest.serverSizeId` field
- `Region.NYC1`, `Region.SFO2`, `Region.SFO3` enum values
- `PutAsync` HTTP method (used by friendly-name update endpoints)
- `.meta` files for all assets (deterministic GUIDs for stable references)

### Changed
- All JSON serialization/deserialization now uses `Newtonsoft.Json` consistently
- `UsageStats.creditBalance` and `creditHours` changed from `int` to `float`
- `CreditTransaction.amount` changed from `int` to `float`

## [1.1.0] - 2025-11-18

### Added
- Custom game type configuration support
- `gameTypeConfigKey` field in `CreateSessionRequest` for custom game type configs
- `customVariables` dictionary in `CreateSessionRequest` for custom variable values
- Helper methods for custom variables:
  - `SetGameTypeConfigKey(string configKey)` - Set custom config key
  - `AddCustomVariable(string name, object value)` - Add single custom variable
  - `SetCustomVariables(Dictionary<string, object> variables)` - Set multiple variables at once
  - `ClearCustomVariables()` - Clear all custom variables
- Documentation section on custom game type configurations
- Examples showing custom configuration usage with variables

### Changed
- Updated README with custom configuration examples and best practices
- Updated Models documentation to include new custom configuration fields

### Technical Details
- Fully backward compatible - all new fields are optional
- Custom variables support any object type (strings, numbers, booleans, etc.)
- Unique keys follow format: `usr_{userId}_{hash}` for user configs, `sys_{gameType}_{hash}` for system configs

## [1.0.0] - 2025-11-09

### Added
- Initial release of SplatterVault Unity SDK
- Complete API client for SplatterVault game session management
- Strongly-typed enums for game types, regions, and modes
- Full async/await support with UnityWebRequest
- Master Server Toolkit (MST) integration suite
- Comprehensive error handling and retry logic
- Session management features:
  - Create credit-based sessions
  - Create subscription-based sessions
  - Get session details
  - List user sessions
  - Stop sessions
  - Update friendly names
- Scheduling support:
  - Immediate start with/without auto-stop
  - Scheduled start with/without auto-stop
  - Flexible DateTime-based scheduling
- Credit management:
  - Check credit balance
  - Get credit statistics
  - Balance conversion (credits to hours)
- Subscription support:
  - Get subscription details
  - Usage statistics
- MST Integration:
  - Extension methods for MST compatibility
  - Automated bridge component
  - Server polling and ready detection
  - Unity Events integration
  - Auto-stop on empty server
- Example scripts:
  - SimpleSessionManager - Basic session management
  - TournamentManager - Advanced tournament scheduling
  - MSTIntegrationExample - MST integration patterns
  - SplatterVaultMSTBridge - Drag-and-drop bridge component
- Documentation:
  - Complete README with API reference
  - Installation guide
  - MST integration guide
  - Publishing guide for UPM

### Features
- Type-safe API with compile-time validation
- Game-specific mode enums (PaintballMode, SnapshotMode, ValheimMode)
- Region-specific enums (NYC3, TOR1, SFO1, LON1)
- Automatic JSON parsing and error handling
- Session state checking (IsActive, IsScheduled, IsReadyForMST)
- Connection string helpers
- MST property conversion
- Inspector-visible status fields
- Context menu helpers for debugging

### Technical Details
- Minimum Unity version: 2019.4
- .NET compatibility: .NET 4.x or .NET Standard 2.0
- No external dependencies
- Fully async/await compatible
- UnityWebRequest-based HTTP client
