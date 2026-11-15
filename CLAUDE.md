# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

SplatterVault Unity SDK — Official C# SDK for Unity game developers to create and manage dedicated game servers through the SplatterVault (ppg-vpl) API. Supports credit-based and subscription-based sessions, organization billing, game configuration discovery, and Master Server Toolkit (MST) integration.

## Ecosystem Context

This SDK consumes the ppg-vpl backend API (`../ppg-vpl/api/`). It is one of several consumers:

- **ppds-ux** (`../ppg-vpl/ppds-ux/`) — Primary web frontend (full API surface)
- **vpl-ux** (`../vpl-ux/`) — Third-party league frontend (league/team/matchup subset)
- **dedicatedServer** (`../ppg-vpl/dedicatedServer/`) — Game server wrapper (Socket.IO)

**When API endpoints change** in `../ppg-vpl/api/`, this SDK's `SplatterVaultClient.cs` and `Models.cs` may need updates.

## Repository Structure

```
Runtime/
├── SplatterVaultClient.cs         — Main API client (all HTTP methods)
├── Models.cs                      — All request/response models
├── MSTExtensions.cs               — Master Server Toolkit integration
├── SplatterVault.Runtime.asmdef   — Unity assembly definition
Samples~/                          — server/tools code only: #if UNITY_SERVER || UNITY_EDITOR, key from SV_API_KEY
├── BasicExamples/                 — SimpleSessionManager, TournamentManager
├── MSTIntegration/                — SplatterVaultMSTSpawner (MST master-side spawner, can claim pool servers)
Tests~/                            — Unity skips folders ending in ~ (no .meta files needed, nothing imported)
├── SplatterVault.Tests/           — .NET 8 integration harness (own copy of the client; defaults to production, do not run casually; `--models-only` runs the offline model checks, including the Region drift check)
.npmignore                         — keeps repo-only files without a .meta (CLAUDE.md, .gitignore) out of the registry package (npm pack, as OpenUPM builds it); add any new top-level file without a .meta here, and repeat .gitignore rules here (npm skips .gitignore once .npmignore exists)
```

## API Key Rule

The API key belongs on servers only (dedicated server builds, a backend, the Editor). Never in a game client build, a serialized field, scene, prefab or ScriptableObject: anyone who extracts it can start/stop the org's servers and spend its credits. Samples read it from the `SV_API_KEY` environment variable inside `#if UNITY_SERVER || UNITY_EDITOR`. Game clients will use player sign-in (SDK 4.0).

## Package Info

- Package name: `com.splattervault.sdk`
- Version: 3.7.0
- Unity requirement: 2020.2+ (`UnityWebRequest.Result`)
- Runtime dependency: `com.unity.nuget.newtonsoft-json` 3.0.2 (declared in package.json; its DLL is auto-referenced, so the asmdef lists no reference)
- Install via Unity Package Manager: `https://github.com/splattervault/unity-sdk.git` (a move to a `splattervault` GitHub org is planned: keep this URL identical in package.json, README, INSTALLATION and here so it is one find/replace)

## API Endpoints Consumed

All requests use API key authentication via `X-API-Key` header (not JWT Bearer tokens). Base URL defaults to `https://splattervault.com/rest`.

### Session Management

- `POST /credits/sessions` — Create credit session
- `POST /subscriptions/sessions` — Create subscription session
- `GET /game-sessions/{id}` — Get session by ID
- `GET /game-sessions/my-sessions` — List user's sessions (personal key)
- `GET /credits/sessions` — List credit sessions (org key)
- `GET /subscriptions/sessions` — List subscription sessions (org key)
- `POST /credits/sessions/{id}/stop` — Stop credit session (returns billing)
- `POST /subscriptions/sessions/{id}/stop` — Stop subscription session
- `PUT /credits/sessions/{id}/friendly-name` — Update credit session name
- `PUT /subscriptions/sessions/{id}/friendly-name` — Update subscription session name
- `PUT /credits/sessions/{id}/schedule` — Update credit session schedule
- `PUT /subscriptions/sessions/{id}/schedule` — Update subscription session schedule
- `POST /credits/sessions/{id}/cancel-schedule` — Cancel scheduled credit session
- `POST /subscriptions/sessions/{id}/cancel-schedule` — Cancel scheduled subscription session

### Credits & Billing

- `GET /credits` — Personal credit balance
- `GET /credits/stats` — Credit statistics with recent transactions

### Subscriptions

- `GET /subscriptions` — Subscription details (current + all)
- `GET /subscriptions/usage` — Usage stats (instances, balance, limits)

### Organization

- `GET /organizations/{orgId}/credits` — Org credit balance
- `GET /organizations/{orgId}/subscription` — Org subscription info

### Auth Context
- `GET /auth/me` — Resolve authenticated caller's context (org ID, permissions for org keys; user info for user keys)

### Organization (org-key-friendly aliases)
- `GET /org/credits` — Org credit balance (no org ID needed in URL for org API keys)
- `GET /org/subscription` — Org subscription info (no org ID needed in URL for org API keys). Answers a bare array (newest first), unlike `/organizations/{orgId}/subscription` (`{ current, all }`); `GetOrgSubscriptionAsync` reads both and picks `current` as the server does (first `"active"`, else newest)

### Server Pools

- `POST /server-pools/{id}/claim` — Claim a warm pool server (`ClaimPoolServerAsync`). Body: friendlyName, mode, argOverrides, idleTimeoutMinutes, autoDestroyOnProcessExit, scheduledEndTime. Answer: `{ sessionId, serverInstanceId, connection }`; the SDK models `sessionId` and `connection` (serverInstanceId is internal). Org keys need `org:manage_pools`. 409 = no warm server free.

### Game Configuration

- `GET /game-types/{gameKey}/configurable-args` — Discoverable launch arguments (type, options, min/max, validation)

## Key Models (Runtime/Models.cs)

- `CreateSessionRequest` — Session creation payload (`gameKey` serialized as `gameTypeConfigKey`, `customVariables` serialized as `environmentVariables` — both via `[JsonProperty]`; plus region, channel)
- `GameSession` — Session response (status strings: `"Active"`, `"Pending"`, `"Scheduled"`, `"Not Active"`; serverType: `"Credit"`, `"Subscription"`; readiness `ready` (bool?, null on older APIs), `readyAt`, `readyDeadlineAt`)
- `ClaimPoolServerRequest` / `PoolClaimResponse` / `SessionConnectionInfo` — Pool claim request, answer and player connection details
- `CreditBalance` — Balance with `GetAvailableBalance()` helper (handles frozen subscription credits)
- `StopSessionResult` — Billing info (totalHours, totalCost)
- `StructuredLaunchArg` — Game config arg definition (types: select, number, text, boolean, hidden)
- `OrgCreditStats` / `OrgSubscriptionInfo` — Organization billing models. `OrgCreditStats.GetAvailableBalance()` returns the API's `availableBalance` (frozen subscription credits left out); `autoBuyPack { credits, price }` is what the API sends (`autoBuyCreditAmount` is obsolete, filled from it); `Subscription.periodStart` is obsolete (never sent)
- `ApiResponse<T>` / `ApiError` — Standard API envelope
- `Region` enum — 18 regions: 14 DigitalOcean (NYC1, NYC3, ATL1, RIC1, TOR1, SFO2, SFO3, LON1, AMS3, FRA1, SGP1, BLR1, SYD1, MKC1) plus 4 Vultr (VDFW, VORD, VMIA, VLAX; only where the platform has switched that Vultr city on for the game: the API normally answers 400 otherwise; a DigitalOcean region never falls back to Vultr). Append new members at the end only: Unity saves `[SerializeField] Region` fields by integer value, and the test copy in `Tests~/SplatterVault.Tests/Models.cs` must match (a model test checks)

## Authentication

Uses API key auth (`X-API-Key` header), NOT JWT Bearer tokens:

- **Personal keys**: `sv_...` prefix — bills to personal credits
- **Organization keys**: `sv_org_...` prefix — auto-detected via `StartsWith("sv_org_")`, auto-injects `organizationId` on session creation. Use `CreateAsync()` to auto-resolve org ID from the key via `GET /auth/me`.

The API key middleware in ppg-vpl API resolves the key to a user context server-side. This is different from how ppds-ux and vpl-ux authenticate (which use JWT from login flow).

## Important Conventions

1. **JSON serialization** — Uses Newtonsoft.Json with `NullValueHandling.Ignore`. The `gameKey` field serializes as `gameTypeConfigKey` via `[JsonProperty]`.
2. **Status string constants** — The SDK hard-codes status checks: `IsActive()` checks `== "Active"`, `IsPending()` checks `== "Pending"`, etc. If these status values change in the API, the SDK breaks.
3. **Dual callback + async pattern** — All methods return `Task<T>` AND accept optional `onSuccess`/`onError` callbacks for Unity coroutine compatibility.
4. **Org key auto-routing** — `GetMySessionsAsync()` fetches from different endpoints depending on whether it's a personal or org key.
5. **UnityWebRequest** — All HTTP done via `UnityWebRequest` (not HttpClient), required for Unity's threading model.

## API Change Impact

If the ppg-vpl API changes any of the consumed endpoints:

- **Response field renames/removals** — Update `Models.cs` (fields are deserialized by name)
- **Status enum changes** — Update `IsActive()`, `IsPending()`, `IsScheduled()`, `IsStopped()` in `GameSession` class
- **Endpoint path changes** — Update URL strings in `SplatterVaultClient.cs`
- **Auth mechanism changes** — Update `SetRequestHeader("X-API-Key", apiKey)` calls
- **New required request fields** — Update `CreateSessionRequest` and any other request models

## No CLI Build/Test Commands

This is a Unity package, not a standalone project. It is built and tested within Unity projects that import it. Tests run via the Unity Test Runner.
