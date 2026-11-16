# SplatterVault Unity SDK

Official Unity SDK for the SplatterVault platform, in two parts:

- **Game clients** (`SvPlayers`, SDK 4.0): sign players in (Meta Quest, Steam, Xsolla, PlayFab, Supabase, SplatterVault email accounts, guests), keep their saves and settings (player data), and run your store (currencies, catalog, inventory, purchases). No API key: see [Players: sign-in, data and store](#players-sign-in-data-and-store).
- **Servers and tools** (`SplatterVaultClient`, with an API key): create and manage dedicated game servers. Supports credit-based and subscription-based sessions, organization billing, dynamic game configuration, server pools and Master Server Toolkit integration.

## Installation

### Unity Package Manager (recommended)

In Unity: **Window > Package Manager > + > Add package from git URL** and enter:

```
https://github.com/splattervault/unity-sdk.git
```

Or add directly to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.splattervault.sdk": "https://github.com/splattervault/unity-sdk.git#v4.0.0-preview.1"
  }
}
```

### Manual

Copy the `Core/` and `Runtime/` folders into your Unity project's `Assets/SplatterVault/` directory, and install the **Newtonsoft Json** package (`com.unity.nuget.newtonsoft-json`) from the Package Manager.

### Requirements

- Unity 2020.2 or later
- .NET 4.x or .NET Standard 2.0/2.1 API compatibility level
- Newtonsoft.Json: installed automatically with the package (`com.unity.nuget.newtonsoft-json`)
- Internet access enabled in Player Settings

## Where Your API Key Lives

**The API key belongs on servers only:** your dedicated server builds, your own backend, or the Unity Editor while you develop. Never put it in a game client build. Anything in a client build (a serialized field, a scene, a prefab, a ScriptableObject, a string in code) can be pulled out by players, and with your key anyone can start and stop your servers and spend your (or your organization's) credits.

- Read the key from an environment variable on the machine that runs the code: `Environment.GetEnvironmentVariable("SV_API_KEY")`.
- Wrap code that uses it in `#if UNITY_SERVER || UNITY_EDITOR` so it is left out of client builds (all samples do this).
- Players connect to the servers you start with the address and code you give them; they never need the key.
- Game clients sign players in instead of using a key: `SvPlayers` (below) never takes one.

## Players: sign-in, data and store

Game client code (SDK 4.0). `SvPlayers` (namespace `SplatterVault.Players`) takes your **game id**, the number on your game's Sign-in tab, and **never an API key**: a game build must never hold one. Every service is off until you switch it on in the game console (Sign-in, Player data and Store tabs). Full request details are in the API reference ([splattervault.com/developers?tab=reference](https://splattervault.com/developers?tab=reference)).

Native builds only: in a WebGL build `new SvPlayers(...)` throws `PlatformNotSupportedException` (browsers block the API, CORS).

### Sign in

```csharp
using SplatterVault.Core;     // SvApiException
using SplatterVault.Players;  // SvPlayers

var players = new SvPlayers(123);                  // one per game, kept for the whole session

if (!await players.Auth.RestoreAsync())            // the LAST way in on this device: email (refresh token) or its guest
{
    var last = await players.Auth.GetLastSignInMethodAsync(); // "steam", "meta", ... or null (first launch, signed out)
    // Show your sign-in screen. A platform player is signed in with that platform again, never swapped for the guest.
    if (last == null) await players.Auth.SignInAsGuestAsync(); // e.g. first launch: a guest on this device
}

// SplatterVault email accounts
await players.Auth.RequestEmailCodeAsync("nova@example.com");             // always answers; 1 a minute
await players.Auth.VerifyEmailCodeAsync("nova@example.com", code);       // signs in, or signs up
await players.Auth.SignInWithPasswordAsync("nova@example.com", password);

// Platforms: a fresh proof for every sign-in
await players.Auth.SignInWithMetaAsync(user.ID, userProof.Value);        // Users.GetLoggedInUser / GetUserProof
await players.Auth.SignInWithSteamAsync(ticket.m_rgubTicket, ticket.m_cubTicket); // GetAuthTicketForWebApi("splattervault")
await players.Auth.SignInWithXsollaAsync(xsollaLoginToken);
await players.Auth.SignInWithPlayFabAsync(playFabSessionTicket);
await players.Auth.SignInWithSupabaseAsync(supabaseAccessToken);
await players.Auth.SignInWithStudioTokenAsync(tokenFromYourBackend);     // your server called POST /player-auth/studio
```

| `players.Auth` | Route |
|---|---|
| `GetMeAsync()`, `EraseMeAsync()` | `GET` / `DELETE /player-auth/me` (erasing deletes everything kept about the player) |
| `LinkMetaAsync`, `LinkSteamAsync`, `LinkXsollaAsync`, `LinkPlayFabAsync`, `LinkSupabaseAsync`, `UnlinkAsync(provider)` | `POST` / `DELETE /player-auth/link/:provider` |
| `RequestLinkEmailCodeAsync(email)`, `AddEmailAsync(email, code, password?)` | add an email (a guest keeps its player) |
| `ChangeEmailAsync(newEmail, code, currentPassword? \| oldCode?)`, `RemoveEmailAsync(currentPassword? \| oldCode?)`, `RequestEmailRemovalCodeAsync()` | change or remove the email (proof needed) |
| `ChangePasswordAsync(current, new)` | `PUT /player-auth/me/password` |
| `RefreshAsync()`, `SignOutAsync(forgetGuest: false)` | `POST /player-auth/refresh`, `/sign-out` |
| `CreateGuestAsync(displayName, replaceExistingGuest)`, `SignInWithDeviceAsync()`, `HasGuestOnDeviceAsync()`, `ForgetGuestOnDeviceAsync()` | guests |
| `RestoreAsync()`, `GetLastSignInMethodAsync()`, `RetrySavingSecretsAsync()` | start-up; events `SignedIn`, `SignedOut`, `SignInRequired`, `StorageFailed` |

**Restoring a sign-in.** `RestoreAsync` signs back in the way the player last signed in on this device, and nothing else: an email sign-in with that player's own refresh token, a guest with its device secret (only the guest that last signed in). Refresh tokens and guest secrets are stored with the player they belong to; one of another player (for example left behind because a delete failed) is never used, also not to renew a session, and is deleted again. Two `SignInAsGuestAsync` calls at once share one sign-in, so a first launch never makes two guests. After a Meta, Steam, Xsolla, PlayFab, Supabase or studio sign-in it answers false even when a guest secret is stored, and after `SignOutAsync` it answers false: the guest stays on the device for an explicit `SignInAsGuestAsync`, but the SDK never signs a different player in by itself. Offline it throws `network_error`, and with unreadable storage `storage_unreadable`.

**Tokens.** The player token (1 hour) lives in memory only: `players.Auth.PlayerToken`, for your own game servers to check offline (`GET /rest/player-auth/keys`). Never log or store it. The refresh token of an email sign-in (`svpr1_…`) and a guest's device secret (`svpd1_…`, its only copy) go straight to the device's secure storage and are never handed to your code.

The SDK renews a token by itself where it can: an email sign-in with its refresh token, a guest with its device secret. Expiry is counted on this device's clocks (the token's lifetime, kept within 60 s to 24 h, from when the sign-in was sent; due on either the wall clock or a monotonic one), so a phone whose clock is hours off, or is set during a refresh, neither refreshes before every request nor waits past expiry. It renews two minutes before the token runs out (at most every half lifetime), and after a `401 token_expired` it renews once and sends the request once more. Only one refresh runs at a time (requests that need one wait for it), and the new refresh token is saved before the new player token is used: a refresh token works once, and two refreshes of the same one would end the sign-in. Meta, Steam, Xsolla, PlayFab, Supabase and studio sign-ins have no refresh token: when their token runs out the SDK raises `SignInRequired`, and calls `Reauthenticate` if you set it:

```csharp
players.Auth.Reauthenticate = async (info, ct) =>
{
    await SignInWithSteamAgainAsync();   // get a new ticket and call SignInWithSteamAsync
    return players.Auth.IsSignedIn;      // true: the waiting requests are sent again, once
};
players.Auth.SignInRequired += info => ShowSignIn(info.Reason);   // TokenExpired, RefreshInvalid, PlayerDeleted, ...
```

**Rotating refresh tokens: the trade-off.** Each refresh token works once and the answer carries the next one; a lost answer may be resent with the same token only within 30 seconds. The SDK keeps every resend inside that window (8-second sends, no new send after 25 s, the next token saved before it is used). But if the game quits (or the phone dies) after the API took a refresh token and before the new one was saved, and the game starts again more than 30 seconds later, that email sign-in has ended: the player signs in again (`refresh_invalid`, `SignInRequired` with `RefreshInvalid`). Nothing else is lost.

**A sign-in or sign-out by the game always wins** over a renewal or a `RestoreAsync` still in flight: when its answer comes back after the game signed out or signed someone else in, the SDK does not use it (it never brings a signed-out player back or replaces the newer player) and ends that answer's refresh token on the server. Every player request is sent only as the player who was signed in when it was called.

**Guests live on one device.** A guest's secret exists only in this device's secure storage. Uninstalling, clearing the app's data, a factory reset, or moving to a new device (the iOS keychain items are this-device-only; on Android the SDK's values are never in a backup, see Backups below) loses the guest for good unless the player added an email. If storage ever reads as `storage_unreadable` (never as "no guest"), offer "start over" with `CreateGuestAsync(replaceExistingGuest: true)`.

### Player data

```csharp
var data = await players.Data.GetMineAsync(new[] { "settings", "matches" }); // player + readonly stores
var settings = data.Player.Get("settings", new GameSettings());
long version = data.Player.VersionOf("settings");                            // 0: not saved yet

try
{
    await players.Data.WriteAsync(new SvDataOps()
        .Set("settings", settings, ifVersion: version)   // never overwrites a newer save
        .Inc("matches", 1));                             // counters: add on the server
}
catch (SvApiException e) when (e.IsOutcomeUnknown)
{
    // No answer (or a repeat met a conflict): it MAY have been applied. Read again; never just write again.
    var now = await players.Data.GetMineAsync(new[] { "settings", "matches" });
}
catch (SvApiException e) when (e.ErrorCode == "version_conflict")
{
    var current = e.DetailsAs<List<SvVersionInfo>>();    // changed elsewhere: read again, then decide
}

var news = await players.Data.GetGameDataAsync();         // the game's public data; ETag-cached, 304 when unchanged
var before = await players.Data.GetPublicAsync();         // the same with no sign-in (launcher, menu)
```

- Players write only their `player` store (`SvDataOps` writes there), at most 25 ops a write, 30 writes a minute. Everything a player writes is untrusted: keep currency, XP and unlocks in `readonly` and change them from your backend.
- **Data writes have no Idempotency-Key.** The SDK sends a write again by itself only when **every** op has `ifVersion` **and** the answer proved nothing was applied (408, 429, 503, or a renewed token after `401 token_expired`). After no answer, a 500, 502 or 504 the write **may have been applied**: the exception has `IsOutcomeUnknown == true` and is never re-sent. A repeat that meets `version_conflict` is reported as `outcome_unknown` (status 409, `IsOutcomeUnknown`), never as a plain conflict, because the earlier send may be what changed the version.
- **Recovering from an unknown outcome:** read the keys again. When the key's version moved and the stored value is the one you wrote, it applied; otherwise write again with the new version. Never "apply it again" blindly (add one more win, add the reward again): that is how one win becomes two. Without `ifVersion` (an `Inc` counter) the same holds: read before counting again.
- Values are any JSON but null (`JToken`, or your own types through `Get<T>()` / `Set<T>()`); dates stay strings exactly as sent; versions are `long`. Send 64-bit ids (Steam, Meta) as strings.

### Store

```csharp
await players.Store.ResumePendingAsync();                     // after sign-in: finish changes a crash interrupted
var catalog = await players.Store.GetCatalogAsync();
var mine = await players.Store.GetMineAsync();                // balances and items

var sword = catalog.Find("sword_gold");
var result = await players.Store.PurchaseAsync(sword.Sku, "GOLD", sword.PriceIn("GOLD").Value); // expectedPrice: the price shown
await players.Store.ConsumeInstanceAsync(mine.Items[0].InstanceId);   // a one-of-a-kind copy
await players.Store.ConsumeSkuAsync("arrow", 5);                       // part of a stack, oldest first
var page = await players.Store.GetHistoryAsync(before: previousPage?.Next);
await players.Store.SyncMetaPurchasesAsync();                          // Quest add-ons: after a purchase and at start
```

- **`e.IsPending`: the change is still kept, to be finished.** Every store error says whether the change is kept for a later `ResumePendingAsync()`: true after no answer, after 408, 429, 500, 502, 503, 504, after a token problem (401) and when another player signed in first (`player_changed`); false for a refusal (`insufficient_funds`, `price_changed`, ...: nothing changed) and when it could not be kept (storage failed). A 429 or 503 proves nothing was applied *yet* (`IsOutcomeUnknown` is false), but the change stays kept and **a later resume applies it**. So while `IsPending` is true, **do not offer to buy again: offer "Finish"** (the Store sample's "Finish unanswered purchases").
- **A "Retry" or "Finish" button:** call `ResumePendingAsync()`, or send the same change once more with its key (`PurchaseAsync(sku, currency, price, idempotencyKey: e.IdempotencyKey)`). **Never call `PurchaseAsync` again without the key**: that is a new purchase and can charge twice. Every store error carries the change's `IdempotencyKey`; so does `SvChangeCanceledException` (an `OperationCanceledException`) when your cancellation token stops the wait. Cancelling never stops the send itself: it runs to its answer and the change stays kept until then.
- **One send at a time per change.** A change already being sent is never sent a second time at once: calling it again with the same key and arguments waits for that send and returns its answer; `ResumePendingAsync` skips it (that send decides it), so resuming from inside `Reauthenticate` cannot wait on itself. A different change under a key that is kept or being sent is refused at once with `change_in_progress` (nothing sent, `IsPending` false, the kept change untouched): use a new key for a new change.
- **Pending changes belong to their player.** `GetPendingAsync` and `ResumePendingAsync` see only the signed-in player's changes, and each one is sent only with that player's token: if another player signs in while a resume runs, it stops and leaves the rest pending (`player_changed`, nothing sent). A change finished meanwhile is skipped. The SDK never resumes by itself: call it after sign-in, at a moment the player expects it.
- **A kept change is applied when it is resumed**, even one the SDK knows was never applied yet (for example the game quit before it was sent) and the player has since forgotten. Resume where the player expects purchases to complete (after sign-in, in the store), or show them and `DiscardPendingAsync(key)` the ones they no longer want.
- **Unreadable list.** If the kept list cannot be read (a backup restored without its key), new changes are still sent with their keys but cannot be finished after a restart; `DiscardPendingAsync` and `DiscardAllPendingAsync()` delete such a list without reading it, and erasing the player does too.
- **Idempotency.** Every purchase and consume gets an Idempotency-Key when the player commits (`Guid.NewGuid().ToString("N")`, or pass your own `idempotencyKey`), the same key on every retry, so the API applies it once. The key and the exact request are kept in secure storage until the API answers; `ResumePendingAsync()` sends the unanswered ones again. `result.Replayed == true` (HTTP 200) means it had already happened: that is a success.
- A refusal (`insufficient_funds`, `price_changed`, `already_owned`, ...) changed nothing and is not kept. Trust the answer's `Balances` and `Items`, not your own arithmetic. Never let the client grant itself anything: rewards come from your backend.

### Errors, retries and limits

Every failure is an `SvApiException`: `Status`, `ErrorCode` (the API's `error`, such as `token_expired`, `insufficient_funds`, `version_conflict`), `Message` (a sentence to log or show), `Details`, `RetryAfter`, `IsTransient`, `IsOutcomeUnknown` (a write that may have been applied: see Player data and Store), `IsPending` and `IdempotencyKey` (store changes). **Act on `ErrorCode`, never on `Message`.** An answer without a code (an unknown route) has `ErrorCode == null` and its `ErrorName`. `Status == 0` means no HTTP answer came with it: the SDK codes `network_error`, `timeout` (sent, no answer), `not_signed_in`, `player_changed`, `change_in_progress`, `superseded` (a sign-in overtaken by a later sign-in or sign-out while it was saved) and `storage_unreadable` (nothing sent), and `token_expired` repeated by the SDK for a token the API already refused (nothing sent, so a dead sign-in never spends the 60-a-minute budget).

- The SDK sends a request again by itself only when that cannot do anything twice: reads, store changes (same key, byte-identical body) and a refresh within the API's 30-second resend window, after transient failures (no answer, 408, 429, 500, 502, 503, 504); data writes with `ifVersion` everywhere only after 408, 429 or 503. Up to 3 sends, backing off from 0.5 s. A `Retry-After` is always honoured; one over 10 s is not waited out but handed to you in `RetryAfter`. Sign-ins, email codes, new guests, account changes, the Meta purchase check and other data writes are never repeated by the SDK, and a refused proof (`proof_invalid`) is never sent again. Tune it with `SvPlayersOptions.RetryPolicy`.
- A player token has **60 requests a minute** across all routes. Never poll: read on demand (game data is ETag-cached).
- **No redirects, no plain HTTP.** The SDK never follows a redirect (a 3xx is an error, never repeated), so a player token, password or guest secret cannot travel to another address. `BaseUrl` must be `https://`, or `http://` on a loopback host (localhost, 127.0.0.1, ::1); `AllowInsecureHttp` (development only, off by default) allows other `http://` hosts.

### Secure storage

| Platform | Where |
|---|---|
| Android 6+ (Meta Quest included) | AES-GCM key in the Android Keystore; sealed values in owner-only files in the app's no-backup directory (never in Auto Backup or a device transfer) |
| iOS | Keychain, `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly` (native plugin `Runtime/Plugins/iOS`) |
| Windows (player and Editor) | DPAPI (current user), files under `Application.persistentDataPath/SplatterVault` |
| macOS, Linux, older Android | An encrypted file in owner-only files (0700 / 0600; Android: the app's no-backup directory, never external storage), with a warning: it is not plain text, but not protected from someone who can read the app's files. macOS players do not use the Keychain yet (it would need a native macOS plugin) |

**Two clients on one machine** (two standalone players, Multiplayer Play Mode's virtual players, the Editor next to a build) share one store by default, so they share one guest and one email sign-in. The SDK reads the refresh token again from storage right before every refresh, so one client renewing does not end the other's sign-in; to test with different players, give each client its own `SvPlayersOptions.StorageNamespace` (for example from a command-line argument).

**Cost.** Sealing or opening a value with the Android Keystore takes a Keystore round trip on Unity's main thread (about 15 ms on a Quest 3 without caching). The SDK makes the key handle and its lookups once per process and keeps every value it wrote or opened in memory with the sealed bytes it came from; a read still reads the file's few bytes and answers from memory only when they are those exact bytes: a purchase seals only the change it must keep, reading things back opens nothing, and an unchanged last sign-in is not written again. Only cold reads (the first of each value after launch, or one another process changed) and writes reach the Keystore, and after any seal or open fails nothing kept is used.

**Unreadable is not missing.** When storage cannot be read (a keychain locked before the first unlock, a value sealed on another device or with another salt, a damaged file), the SDK raises `StorageFailed`, never treats the guest as gone, and refuses to make a new guest over it: `SignInAsGuestAsync`, `CreateGuestAsync` and `HasGuestOnDeviceAsync` throw `storage_unreadable`, and it reads again next time. A new guest whose secret could not be saved signs in with `SavedToDevice == false` and `Renewable == false`: it lasts until the game quits unless the player adds an email or `RetrySavingSecretsAsync()` succeeds. A custom `ISecureStore` must keep this contract: `GetAsync` returns null only when there is certainly no value, and throws otherwise.

**Backups.** On Android the SDK keeps its sealed values in the app's no-backup directory, so Android Auto Backup and device-to-device transfers never copy them: a value sealed with this device's Keystore key could not be opened on another device anyway (it would read as `storage_unreadable`). Nothing to add to your backup rules. A guest therefore never moves to a new device with a backup: offer "add an email" to players who want to keep their guest.

Plug in your own with `new SvPlayers(new SvPlayersOptions { GameId = 123, SecureStore = myStore })` (any `ISecureStore`). The transport (`ISvHttpTransport`), the base URL, timeouts and the retry policy are options too. Out-of-range numbers are refused when the client is made (`ArgumentOutOfRangeException`), never clamped quietly: `RequestTimeout` more than 0 and at most 2^31 - 1 ms, `RefreshTimeout` more than 0 and at most 10 s, `RenewBeforeExpiry` 0 to 30 minutes, `RetryPolicy.MaxAttempts` 1 to 10, its waits 0 to 2^31 - 1 ms, `Jitter` 0 to 1.

### Samples and tests

Package Manager > SplatterVault SDK > Samples: **Player Accounts** (guest and email sign-in UI), **Player Data** (save and load with versions), **Store** (buy, use, history, resume) and **Platform Sign-in** (Meta Quest and Steam; compiled only with the `SV_META_PLATFORM` or `STEAMWORKS_NET` / `SV_STEAMWORKS` scripting define, so they build without those SDKs).

The engine-free core (`Core/`, assembly `SplatterVault.Core`) has unit tests that run without Unity or a server: `cd "Tests~/SplatterVault.Core.Tests" && dotnet test`.

For checks in the Editor or on a device without a real game, the repository has a local API mock in `Tools~/sdk-mock` (Node 18+, no dependencies; fake test credentials only, it never calls SplatterVault; Unity does not import the folder). Start it with `node "Tools~/sdk-mock/server.mjs" --port 8787` and use `new SvPlayers(new SvPlayersOptions { GameId = 4242, BaseUrl = "http://127.0.0.1:8787/rest" })`; plain `http://` is accepted only for loopback hosts, so on an Android or Quest device forward the port first (`adb reverse tcp:8787 tcp:8787`). Its README lists the test game, the fake credentials, and the controls for faults and token expiry; `node "Tools~/sdk-mock/selftest.mjs"` checks the mock itself.

**Not verified on devices yet** (this is a preview): a Meta Quest itself (the Android Keystore store there, and what its JNI calls cost on the main thread at start-up), the iOS Keychain plugin, Windows DPAPI, the Meta proof round trip, and DELETE requests with a JSON body on Android and iOS (now sent only when `RemoveEmailAsync` falls back on an older API). An IL2CPP Android build at Managed Stripping High (no `link.xml` copied, see [IL2CPP Builds](#il2cpp-builds-iosandroid-and-code-stripping)) was checked end to end on an Android emulator with Unity 2021.3 (and, before the latest fixes, 6000.0).

## Quick Start (servers and tools)

```csharp
// Server-side code (dedicated server build or Editor), see "Where Your API Key Lives"
using System;
using SplatterVault;
using SplatterVault.MST; // WaitForServerReady

// 1. Create a client with your API key, read from the environment
var client = new SplatterVaultClient(Environment.GetEnvironmentVariable("SV_API_KEY"));

// 2. Discover available game options
var args = await client.GetConfigurableArgsAsync("your_game_key");
// Returns configurable launch arguments: mode (select), maxPlayers (number), etc.

// 3. Create a session
var request = new CreateSessionRequest
{
    gameKey = "your_game_key",   // from your SplatterVault dashboard
    friendlyName = "My Server"
};
request.AddCustomVariable("-mstRoomMode", "XBall");  // set game mode
request.AddCustomVariable("-maxPlayers", "10");       // set max players

GameSession session = await client.CreateCreditSessionAsync(request);
Debug.Log($"Server starting! Code: {session.code}");

// 4. Wait until the session is Active: the server is up (throws if the session stops first)
session = await client.WaitForServerReady(session.id);

// 5. Tell players where to connect
Debug.Log($"Connect to {session.slaveIp}:{session.GetServerPort()}");

// 6. Stop when done
StopSessionResult result = await client.StopCreditSessionAsync(session.id);
Debug.Log($"Cost: {result.totalCost} credits ({result.totalHours} hours)");
```

## Step-by-Step Guide

### Step 1: Get Your API Key and Game Key

1. Sign up at [splattervault.com](https://splattervault.com)
2. Go to your dashboard and generate an API key
   - **Personal key** (`sv_...`): bills to your personal credits
   - **Organization key** (`sv_org_...`): bills to your org's credits. Create sessions with `CreateCreditSessionAsync`; subscription sessions need a personal key
3. Set it as the `SV_API_KEY` environment variable on the machines that run your server-side code (see [Where Your API Key Lives](#where-your-api-key-lives))
4. Find your **game key** in the game configuration section (e.g., `sys_1774636058786_30e0fc4d`)

### Step 2: Initialize the Client

```csharp
using SplatterVault;

string apiKey = Environment.GetEnvironmentVariable("SV_API_KEY");

// Recommended: CreateAsync auto-resolves the org ID for org keys (sv_org_...)
var client = await SplatterVaultClient.CreateAsync(apiKey);

// Custom API URL (for local development)
var client = await SplatterVaultClient.CreateAsync(apiKey,
    baseUrl: "http://localhost:3000/rest");

// Manual initialization (if you prefer constructor + explicit org ID)
var client = new SplatterVaultClient(apiKey, organizationId: 1);
```

### Step 3: Discover Game Options

Each game defines configurable launch arguments. Query them to build dynamic UI:

```csharp
var args = await client.GetConfigurableArgsAsync("your_game_key");

foreach (var arg in args)
{
    Debug.Log($"{arg.label} ({arg.type}): {arg.description}");

    if (arg.type == "select" && arg.options != null)
    {
        foreach (var opt in arg.options)
            Debug.Log($"  - {opt.label} = {opt.value}");
    }

    if (arg.type == "number")
        Debug.Log($"  Range: {arg.min} - {arg.max}");
}
```

**Argument types:**
| Type | Description | UI Widget |
|------|-------------|-----------|
| `select` | Predefined choices (e.g., game mode) | Dropdown |
| `number` | Numeric with min/max (e.g., max players) | Slider / Input |
| `text` | Free text with optional regex validation | Text field |
| `boolean` | Toggle (e.g., friendly fire) | Checkbox |
| `hidden` | Platform-managed, not shown to users | None |

Arguments with `semantic: "mode"` represent the game mode selection.

### Step 4: Create a Session

```csharp
var request = new CreateSessionRequest
{
    gameKey = "your_game_key",
    friendlyName = "Practice Match"
};
request.SetRegion(Region.NYC3);

// Pass any configurable arguments as custom variables
request.AddCustomVariable("-mstRoomMode", "XBall");
request.AddCustomVariable("-maxPlayers", "10");

// Set auto-stop time (optional)
request.SetScheduledEndTime(DateTime.UtcNow.AddHours(2));

// Stop automatically after 15 minutes with nobody connected (optional, see "Idle Timeout")
request.SetIdleTimeoutMinutes(15);

// Create with credits or subscription
GameSession session = await client.CreateCreditSessionAsync(request);
// or: await client.CreateSubscriptionSessionAsync(request);

Debug.Log($"Session {session.id} created, status: {session.status}");
```

### Build Channels

Games can have multiple build channels (e.g., "stable", "beta", "dev"). When creating a session, the server picks which build to use in this order:

1. **Explicit `buildId`** — If you pass a `buildId`, that exact build is used
2. **Named `channel`** — If you pass a `channel` name, the build deployed to that channel is used
3. **Default channel** — If neither is provided, the game's default channel build is used
4. **Legacy fallback** — If no channels are configured, the single active build is used

```csharp
// Use the default channel (most common — no extra config needed)
var request = new CreateSessionRequest { gameKey = "your_game_key" };

// Use a specific channel (e.g., beta testing)
var request = new CreateSessionRequest
{
    gameKey = "your_game_key",
    channel = "beta"
};

// Use an exact build ID (rare — for pinning to a known-good build)
var request = new CreateSessionRequest
{
    gameKey = "your_game_key",
    buildId = 42
};
```

Most developers should just omit both `channel` and `buildId` — the default channel is managed from the SplatterVault dashboard.

### Idle Timeout

Set `idleTimeoutMinutes` to have the platform stop the session once nobody has been connected to it for that many minutes. Leave it unset (null) and the session behaves exactly as before: it is never stopped for being idle.

```csharp
var request = new CreateSessionRequest { gameKey = "your_game_key" };
request.SetIdleTimeoutMinutes(15);   // 5 to 1440 minutes

GameSession session = await client.CreateCreditSessionAsync(request);
if (session.idleTimeoutMinutes == null)
    Debug.LogWarning("This server does not support idle timeout; the session will not idle-stop.");
```

**How it works**
- **Range:** 5 to 1440 minutes (24 hours), whole minutes. The server checks the range; any other value makes the create call throw with a 400 error. Omit the field to turn the feature off.
- **What counts as a connection:** any public IP address that holds an established TCP connection to a port the game server process is listening on, or sends it at least 2 UDP packets within a 10-second window during a check. Players, spectators and admin tools all count. The server checks about every 30 seconds. For a game whose connected clients go quiet for long stretches (for example a lobby with slow keepalives), ask the game owner to set `processConfig.idleDetection.minPacketsPerSource` to 1.
- **When the clock starts:** when the game server is ready (the game's ready signal, or 2 minutes after launch for games without one). The clock runs even if nobody ever joins, so a server nobody joins is stopped about `idleTimeoutMinutes` after it becomes ready. Any connection, or any check that fails, restarts the clock.
- **The stop:** the session ends as if you had stopped it: status `"Not Active"`, `stopReason` `"IDLE_TIMEOUT"` (check with `session.IsIdleTimedOut()`). Credit sessions are billed for the time the server ran, idle minutes included.
- **Pool-served sessions** are returned to their server pool rather than destroyed; the pool's own settings decide what happens to the server.
- **Other auto-stops still apply.** `scheduledEndTime`, `autoDestroyOnProcessExit` and credit limits work as before; whichever fires first ends the session.
- **Scheduled sessions:** the timeout applies once the scheduled server has started and is ready.

**Stopping an idle-stopped session.** `StopSessionAsync` (and the credit/subscription variants) on a session that already idle-stopped throws with a 400 error, "Can only stop active sessions". Refresh the session and check `IsStopped()` first if your code may race the idle stop.

**Monitoring fields** on `GameSession` (all null when the session has no idle timeout):

| Field | Meaning |
|-------|---------|
| `idleTimeoutMinutes` | The timeout set at creation. Null if you did not set one, or the server does not support idle timeout |
| `idleMonitorState` | `"starting"` (not ready yet), `"active"` (someone connected), `"empty"` (nobody connected), `"unknown"` (could not check; treated like a connection, so the clock restarts) |
| `idleSince` | When the current empty period began (UTC, ISO 8601). `GetIdleSince()` returns a UTC `DateTime` |
| `lastPeerSampleAt` | When the server last checked connections. `GetLastPeerSampleAt()` returns a UTC `DateTime`. If it stays null well after the session is Active, the server is not monitoring this session and it will not idle-stop |

**Limitations**
- Clients on private addresses (same LAN or VPC as the server) are not counted, so a server used only from a private network looks empty. This mostly affects local test setups.
- Relay traffic counts only when it arrives on a port the game server listens on. If your server reaches a relay service through an outbound connection (TCP/WebSocket, or a connected UDP socket), players on the relay are not seen: a server that listens on no ports reports `"unknown"` and is never idle-stopped, but a server that also listens on any other port (a query or admin port, for example) looks empty and **will be idle-stopped with players connected**. Do not set `idleTimeoutMinutes` for such games.
- Server-browser queries and relay keep-alives on the game port count as connections, so a publicly listed server may never look idle.
- A player who joins in the last half-minute before the stop may be disconnected.

### Step 5: Monitor Session Status

Sessions go through these states: **Scheduled** -> **Pending** -> **Active** -> **Not Active**

"Active" means the server is up. The game may still be loading: `ready` turns true once the game says it is ready for players (its ready signal, or the start of a match).

```csharp
var session = await client.GetSessionAsync(sessionId);

if (session.IsPending())
    Debug.Log("Server is provisioning...");
else if (session.IsActive() && session.IsReady())
    Debug.Log($"Ready! Connect to {session.slaveIp}:{session.GetServerPort()}");
else if (session.IsActive())
    Debug.Log("Server is up, game still loading...");
else if (session.IsStopped())
    Debug.Log($"Session has ended ({session.stopReason})");
```

Or let the SDK poll for you (`using SplatterVault.MST;`):

```csharp
// Returns when the session is Active. Throws InvalidOperationException as soon as the session
// stops (with its stop reason), or TimeoutException if it never becomes Active.
GameSession ready = await client.WaitForServerReady(sessionId, maxWaitSeconds: 300);
```

By default `WaitForServerReady` returns as soon as the session is Active. Pass `waitForGameReady: true` to also wait for the game to say it is ready for players (`ready`). Only do that for a game whose ready signal is configured for every mode it runs: a game with no ready signal never reports `ready`, and the call then returns the Active session once its ready deadline (`readyDeadlineAt`, about 5 minutes after the server is up) has passed or when `maxWaitSeconds` runs out. If the API does not report readiness at all (`ready` is null), it returns on Active.

### Step 6: Stop a Session

```csharp
// By session ID (credit sessions return billing info)
StopSessionResult result = await client.StopCreditSessionAsync(session.id);
Debug.Log($"Billed: {result.totalCost} credits for {result.totalHours} hours");

// Auto-route by server type (credit vs subscription)
await client.StopSessionAsync(session);
```

### Step 7: Check Your Balance

```csharp
// Personal credits
CreditBalance balance = await client.GetCreditBalanceAsync();
Debug.Log($"Available: {balance.GetAvailableBalance()} credits");
Debug.Log($"~{balance.GetBalanceInHours(0.25f):F1} hours at 0.25 credits/min");

// Organization credits
OrgCreditStats orgBalance = await client.GetOrgCreditBalanceAsync();
Debug.Log($"Org balance: {orgBalance.GetAvailableBalance()}");
```

### Server Pools: Claim a Warm Server

A server pool keeps servers of one game already running. Claiming one starts a session on it in seconds instead of booting a new server. Set up pools on your dashboard; the pool fixes the game, region, server size and build.

```csharp
var claim = new ClaimPoolServerRequest { friendlyName = "Ranked 12", mode = "XBall" };
claim.AddCustomVariable("-maxPlayers", "10");   // launch-argument overrides, as on CreateSessionRequest
claim.SetIdleTimeoutMinutes(15);                // optional, 5-1440

try
{
    PoolClaimResponse claimed = await client.ClaimPoolServerAsync(poolId, claim);
    var c = claimed.connection;                 // null only if the server has no address yet
    Debug.Log($"Session {claimed.sessionId}: connect to {c?.host}:{c?.primaryPort} (code {c?.code})");

    // Optional: wait for the game's ready signal (using SplatterVault.MST;)
    await client.WaitForServerReady(claimed.sessionId, waitForGameReady: true);
}
catch (Exception ex)
{
    // 409 when no warm server is free right now: create a session instead, or retry
    Debug.LogWarning($"Claim failed: {ex.Message}");
}
```

- **Who may claim:** the owner of a personal pool; for an organization's pool, an org API key or member with the `org:manage_pools` permission.
- **Billing:** the pool's servers are billed through the pool, not per session, so stopping a claimed session reports no cost.
- **Stopping:** `StopCreditSessionAsync(sessionId)` (or `StopSessionAsync(session)`) ends the session and returns the server to its pool. `idleTimeoutMinutes`, `autoDestroyOnProcessExit` and `scheduledEndTime` stop it the same way.
- **Launch arguments:** values in `customVariables` are always checked against the game's launch-argument schema on a claim, also with the game owner's key. An invalid value fails the claim with 400 and the server stays in the pool.
- **Connection info:** `connection` holds `host`, `primaryPort`, every player port in `ports`, `code`/`password`, `connectionMode` (`direct_ip`, `launch_url`, `server_browser`, `code_only`), `launchUrl`, `showHost`/`showPassword` and the game's connection `instructions` with the live values filled in.

## Examples

### List Active Sessions

```csharp
var sessions = await client.GetMySessionsAsync();
foreach (var s in sessions)
{
    Debug.Log($"[{s.id}] {s.friendlyName ?? s.code} - {s.status} ({s.gameType})");
    if (s.IsActive())
        Debug.Log($"  Connect: {s.slaveIp}:{s.GetServerPort()}");
}
```

### Schedule a Tournament

```csharp
DateTime startTime = DateTime.UtcNow.AddHours(1);

for (int i = 0; i < 4; i++)
{
    var matchStart = startTime.AddMinutes(i * 40);
    var matchEnd = matchStart.AddMinutes(30);

    var request = new CreateSessionRequest
    {
        gameKey = "your_game_key",
        friendlyName = $"Match {i + 1}"
    };
    request.SetScheduledStartTime(matchStart);
    request.SetScheduledEndTime(matchEnd);

    var session = await client.CreateCreditSessionAsync(request);
    Debug.Log($"Scheduled match {i + 1}: code {session.code}, starts {matchStart:HH:mm}");
}
```

### Organization Server Management

```csharp
// CreateAsync auto-resolves org ID — no need to provide it (SV_API_KEY holds an sv_org_... key)
var client = await SplatterVaultClient.CreateAsync(Environment.GetEnvironmentVariable("SV_API_KEY"));
Debug.Log($"Organization: {client.AuthContext.organizationName}");

// Session creation bills to org credits automatically
var session = await client.CreateCreditSessionAsync(new CreateSessionRequest
{
    gameKey = "your_game_key",
    friendlyName = "Team Practice"
});

// Check org balance (org ID auto-resolved)
var credits = await client.GetOrgCreditBalanceAsync();
Debug.Log($"Org has {credits.GetAvailableBalance()} credits remaining");

// Check org subscription (org ID auto-resolved)
var sub = await client.GetOrgSubscriptionAsync();
Debug.Log($"Subscription: {sub.current?.tier ?? "none"}");
```

### Dynamic Game Configuration UI

```csharp
// Build a settings panel from available arguments
var args = await client.GetConfigurableArgsAsync("your_game_key");
var overrides = new Dictionary<string, object>();

foreach (var arg in args)
{
    switch (arg.type)
    {
        case "select":
            // Show dropdown with arg.options
            string selected = ShowDropdown(arg.label, arg.options);
            overrides[arg.flag] = selected;
            break;

        case "number":
            // Show slider with arg.min / arg.max
            float value = ShowSlider(arg.label, arg.min ?? 0, arg.max ?? 100);
            overrides[arg.flag] = value.ToString();
            break;

        case "boolean":
            // Show toggle
            bool enabled = ShowToggle(arg.label);
            overrides[arg.flag] = enabled ? (arg.trueValue ?? "True") : (arg.falseValue ?? "False");
            break;

        case "text":
            // Show text input
            string text = ShowTextField(arg.label);
            overrides[arg.flag] = text;
            break;
    }
}

// Create session with user's selections
var request = new CreateSessionRequest { gameKey = "your_game_key" };
request.SetCustomVariables(overrides);
var session = await client.CreateCreditSessionAsync(request);
```

### MST Integration

With Master Server Toolkit, your **master server** starts game servers on SplatterVault, and each game server registers its own room with the master, as it would on any machine. Pass the master's public address to the game server as launch arguments your game declares:

```csharp
// On the MST master server (server-side code)
using SplatterVault.MST;

var request = new CreateSessionRequest { gameKey = "your_game_key", friendlyName = roomName };
request.AddCustomVariable("-mstMasterIp", masterPublicAddress);
request.AddCustomVariable("-mstMasterPort", "5000");

// Create the server and wait until it is Active (polls every 5 s, up to 5 min)
MSTServerInfo serverInfo = await client.CreateAndWaitForMSTServer(request,
    onProgress: (status) => Debug.Log(status));
```

The **MST Spawner** sample (`SplatterVaultMSTSpawner`) wraps this, and can claim warm pool servers first. See [MST_INTEGRATION.md](MST_INTEGRATION.md).

## API Reference

### SplatterVaultClient

#### Constructor & Factory
```csharp
// Recommended: auto-resolves org ID for org keys
await SplatterVaultClient.CreateAsync(string apiKey)
await SplatterVaultClient.CreateAsync(string apiKey, string baseUrl)

// Manual construction (org ID must be set separately for org-scoped endpoints)
new SplatterVaultClient(string apiKey)
new SplatterVaultClient(string apiKey, int organizationId)
new SplatterVaultClient(string apiKey, string baseUrl)
```

#### Properties
| Property | Type | Description |
|----------|------|-------------|
| `IsOrganizationKey` | `bool` | True for `sv_org_` keys |
| `OrganizationId` | `int?` | Org ID for org-scoped endpoints |
| `AuthContext` | `AuthContext` | Resolved auth context (after `InitializeAsync`/`CreateAsync`) |

#### Methods

| Method | Returns | Description |
|--------|---------|-------------|
| `InitializeAsync()` | `AuthContext` | Resolve auth context; auto-sets org ID for org keys |
| `CreateSessionAsync(request, serverType)` | `GameSession` | Create session (routes by serverType, default "Credit") |
| `GetConfigurableArgsAsync(gameKey)` | `List<StructuredLaunchArg>` | Get available launch arguments for a game |
| `CreateCreditSessionAsync(request)` | `GameSession` | Create credit-billed session |
| `CreateSubscriptionSessionAsync(request)` | `GameSession` | Create subscription-billed session (personal keys only; org keys get 400) |
| `GetSessionAsync(sessionId)` | `GameSession` | Get session details |
| `GetMySessionsAsync()` | `List<GameSession>` | List your sessions |
| `StopCreditSessionAsync(sessionId)` | `StopSessionResult` | Stop credit session (returns billing) |
| `StopSubscriptionSessionAsync(sessionId)` | `GameSession` | Stop subscription session |
| `StopSessionAsync(session)` | `StopSessionResult` | Auto-routes by serverType |
| `UpdateSessionFriendlyNameAsync(session, name)` | `GameSession` | Rename a session |
| `UpdateSessionScheduleAsync(session, start, end)` | `GameSession` | Update schedule times |
| `CancelSessionScheduleAsync(session)` | `GameSession` | Cancel scheduled session |
| `ClaimPoolServerAsync(poolId, request?)` | `PoolClaimResponse` | Claim a warm server from a server pool (see [Server Pools](#server-pools-claim-a-warm-server)) |
| `GetCreditBalanceAsync()` | `CreditBalance` | Get personal credit balance |
| `GetCreditStatsAsync()` | `CreditStats` | Get detailed credit stats |
| `GetSubscriptionAsync()` | `SubscriptionDetails` | Get subscription info |
| `GetUsageStatsAsync()` | `UsageStats` | Get usage statistics |
| `GetOrgCreditBalanceAsync(orgId?)` | `OrgCreditStats` | Get org credit balance |
| `GetOrgSubscriptionAsync(orgId?)` | `OrgSubscriptionInfo` | Get org subscription |

All methods also accept optional `Action<T> onSuccess` and `Action<string> onError` callbacks.

Extension methods in `SplatterVault.MST`:

| Method | Returns | Description |
|--------|---------|-------------|
| `WaitForServerReady(sessionId, maxWaitSeconds = 300, onStatusUpdate = null, waitForGameReady = false)` | `GameSession` | Poll until the session is Active (with `waitForGameReady`, until the game is ready); throws as soon as the session stops (see [Step 5](#step-5-monitor-session-status)) |
| `CreateAndWaitForMSTServer(request, onProgress)` | `MSTServerInfo` | Create a credit session and wait until it is Active |

### Models

#### CreateSessionRequest
| Field | Type | Description |
|-------|------|-------------|
| `gameKey` | `string` | Game config key (required) |
| `region` | `string` | Server region (default: "NYC3") |
| `friendlyName` | `string` | Display name |
| `scheduledStartTime` | `string` | ISO 8601 start time |
| `scheduledEndTime` | `string` | ISO 8601 auto-stop time |
| `serverSizeId` | `int?` | Server size (defaults per game) |
| `organizationId` | `int?` | Org billing (auto-injected for org keys) |
| `buildId` | `int?` | Specific game build |
| `channel` | `string` | Build channel name (e.g., "stable", "beta") |
| `customVariables` | `Dictionary<string, object>` | Launch argument overrides (sent to the API as `environmentVariables`) |
| `autoDestroyOnProcessExit` | `bool?` | Stop the session when the game process exits. Null = the game's default |
| `idleTimeoutMinutes` | `int?` | Stop the session after this many minutes with no connections (5–1440). Null = never (see [Idle Timeout](#idle-timeout)) |

**Setters:** `SetRegion()`, `SetScheduledStartTime()`, `SetScheduledEndTime()`, `SetOrganizationId()`, `SetBuildId()`, `SetChannel()`, `SetAutoDestroyOnProcessExit()`, `SetIdleTimeoutMinutes()`, `AddCustomVariable()`, `SetCustomVariables()`, `ClearCustomVariables()`

#### GameSession (response)
| Field | Type | Description |
|-------|------|-------------|
| `id` | `int` | Session ID |
| `code` | `string` | Join code |
| `status` | `string` | Pending, Active, Scheduled, Not Active |
| `gameType` | `string` | Resolved game type name |
| `mode` | `string` | Resolved game mode |
| `region` | `string` | Server region |
| `slaveIp` | `string` | Server IP address |
| `slavePort` | `int?` | Server port |
| `serverType` | `string` | Credit or Subscription |
| `serverSize` | `ServerSizeInfo` | Size details with creditsPerMinute |
| `friendlyName` | `string` | Display name |
| `organizationId` | `int?` | Owning organization |
| `stopReason` | `string` | Why the session ended, e.g. `"IDLE_TIMEOUT"`, `"AUTO_DESTROYED"`. Null while running |
| `idleTimeoutMinutes` | `int?` | Idle timeout set at creation (null = none) |
| `idleMonitorState` | `string` | `starting`, `active`, `empty` or `unknown` (null = not monitored) |
| `idleSince` | `string` | Start of the current no-connection period (ISO 8601, UTC) |
| `lastPeerSampleAt` | `string` | Last connection check (ISO 8601, UTC) |
| `ready` | `bool?` | True once the game said it is ready for players. Null when the API does not report readiness |
| `readyAt` | `string` | When the game first said it was ready (ISO 8601, UTC) |
| `readyDeadlineAt` | `string` | When the game was expected to be ready by: server up + 5 minutes (ISO 8601, UTC). Recorded only |

**Helpers:** `IsActive()`, `IsPending()`, `IsScheduled()`, `IsStopped()`, `IsReady()`, `IsIdleTimedOut()`, `GetServerPort()`, `GetScheduledStartTime()`, `GetScheduledEndTime()`, `GetServerStartTime()`, `GetIdleSince()`, `GetLastPeerSampleAt()`, `GetReadyAt()`, `GetReadyDeadlineAt()`

#### ClaimPoolServerRequest
| Field | Type | Description |
|-------|------|-------------|
| `friendlyName` | `string` | Display name |
| `mode` | `string` | Game mode (the API uses "default" when omitted) |
| `customVariables` | `Dictionary<string, object>` | Launch argument overrides (sent as `argOverrides`) |
| `idleTimeoutMinutes` | `int?` | Stop after this many minutes with no connections (5–1440) |
| `autoDestroyOnProcessExit` | `bool?` | Stop when the game process exits. Null = the game's default |
| `scheduledEndTime` | `string` | ISO 8601 auto-stop time |

**Setters:** `SetScheduledEndTime()`, `SetIdleTimeoutMinutes()`, `SetAutoDestroyOnProcessExit()`, `AddCustomVariable()`, `SetCustomVariables()`, `ClearCustomVariables()`

#### PoolClaimResponse
| Field | Type | Description |
|-------|------|-------------|
| `sessionId` | `int` | The new session on the claimed server |
| `connection` | `SessionConnectionInfo` | How players connect (null if the server has no address yet) |

#### StructuredLaunchArg
| Field | Type | Description |
|-------|------|-------------|
| `flag` | `string` | Argument flag (e.g., "-maxPlayers") |
| `type` | `string` | text, number, boolean, select, hidden |
| `label` | `string` | Display label |
| `description` | `string` | Help text |
| `required` | `bool` | Must be provided |
| `options` | `List<SelectOption>` | Choices for select type |
| `min` / `max` | `float?` | Range for number type |
| `semantic` | `string` | Hint: "mode", "password", "serverName" |

### Regions

```csharp
// US East
Region.NYC1   // New York 1
Region.NYC3   // New York 3 (default)
Region.ATL1   // Atlanta
Region.RIC1   // Richmond
Region.TOR1   // Toronto

// US West
Region.SFO2   // San Francisco 2
Region.SFO3   // San Francisco 3

// Europe
Region.LON1   // London
Region.AMS3   // Amsterdam
Region.FRA1   // Frankfurt

// Asia-Pacific
Region.SGP1   // Singapore
Region.BLR1   // Bangalore
Region.SYD1   // Sydney

// US Central
Region.MKC1   // Kansas City

// Vultr (only where the platform has switched the city on for your game)
Region.VDFW   // Dallas, TX
Region.VORD   // Chicago, IL
Region.VMIA   // Miami, FL
Region.VLAX   // Los Angeles, CA
```

The Vultr regions (`VDFW`, `VORD`, `VMIA`, `VLAX`) only work where the platform has switched that Vultr city on for your game; otherwise the API normally refuses the create with a 400 error. You get one only by asking for it: a DigitalOcean region never falls back to Vultr, though a Vultr city you ask for can fall back to another Vultr city or a nearby DigitalOcean region.

New regions are always added at the end of the enum, so a `Region` saved in a scene or prefab (Unity stores it as a number) keeps pointing at the same datacenter after an upgrade.

## Error Handling (SplatterVaultClient)

Player services throw `SvApiException` (see [Errors, retries and limits](#errors-retries-and-limits)). `SplatterVaultClient` methods throw exceptions on failure. Use try/catch or the callback pattern:

```csharp
// Try/catch pattern
try
{
    var session = await client.CreateCreditSessionAsync(request);
}
catch (Exception ex)
{
    // ex.Message contains: "API Error (403): Insufficient credits to start a session"
    Debug.LogError(ex.Message);
}

// Callback pattern
await client.CreateCreditSessionAsync(request,
    onSuccess: (session) => Debug.Log($"Created: {session.code}"),
    onError: (error) => Debug.LogError(error)
);
```

**Common errors:**
| Code | Meaning |
|------|---------|
| 400 | Invalid request, e.g. `idleTimeoutMinutes` outside 5–1440, stopping a session that has already stopped, or creating a subscription session with an org key |
| 401 | Invalid or expired API key |
| 403 | Insufficient credits or permissions |
| 404 | Session, pool or game config not found |
| 409 | `ClaimPoolServerAsync`: no warm server free in the pool right now (or the claim is busy, retry) |
| 500 | Server error (report to support) |

## Troubleshooting

### "API key is required"
The key was empty: set the `SV_API_KEY` environment variable on the machine running the server or the Editor (restart the Editor after setting it). Don't commit keys to version control, and never put them in a game client build.

### "Game type configuration not found"
Your `gameKey` doesn't match an active config. Check it on your dashboard.

### "Insufficient credits"
Check balance with `GetCreditBalanceAsync()`. Purchase credits on the dashboard.

### IL2CPP Builds (iOS/Android) and code stripping
IL2CPP builds strip managed code that nothing seems to use. Newtonsoft.Json fills the SDK's models by reflection, so a stripped setter or constructor would leave fields empty in a player build.

- The package keeps what it needs by itself, at every stripping level: its editor script `Editor/SvLinkXmlProvider.cs` (an `IUnityLinkerProcessor`) hands the package's `Runtime/link.xml` to the linker in every IL2CPP build, so **there is nothing to copy**. That file keeps `SplatterVault.Core` whole and, of `SplatterVault.Runtime`, the v3 JSON models and the start-up hook (`SplatterVault.Runtime` is also marked `[assembly: AlwaysLinkAssembly]`). `SplatterVaultClient` itself is kept only where your code uses it, so a game client build does not carry the API-key client.
- Why it is needed: `[Preserve]` on a model type keeps the type and its default constructor, not its property setters. Newtonsoft.Json is the only caller of those setters, so without the link.xml the linker removes them even at the Low level, and every answer reads as empty fields (the first sign-in fails with `invalid_response`).
- With a manual install (`Core` and `Runtime` copied into `Assets`) Unity reads `Runtime/link.xml` directly.
- If you also strip Newtonsoft.Json, add it to a link.xml of your own in `Assets`:

```xml
<linker>
  <assembly fullname="Newtonsoft.Json" preserve="all"/>
</linker>
```

## Support

- Dashboard: [splattervault.com](https://splattervault.com)
- Report bugs: `/reportbug` in Discord
- API docs: [splattervault.com/developers?tab=reference](https://splattervault.com/developers?tab=reference)

## License

MIT License. See [LICENSE.md](LICENSE.md) for details.
