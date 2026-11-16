# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [4.0.0-preview.2] - 2026-11-16

Where players join a session. `slavePort` is the room port the server gave the session (8100 and up), and players join on it only for games launched with `{{slavePort}}` or `{{port}}` in a launch argument. For a game that listens on a fixed port (for example Minecraft Java on 25565, Valheim on 2456), `GetServerPort()` gave the wrong port. The SDK now reads the session's connection details, which name the port players join on. This changes server and tools code only (the session models `SplatterVaultClient` answers, and `SplatterVault.MST`); the player services are unchanged.

### Added
- `GameSession.connection` (`SessionConnectionInfo`, the class pool claim answers already use): how players join, with `host`, `primaryPort` (the port players join on), `ports`, `password`, `code`, `serverName`, `connectionMode`, `launchUrl`, `showHost`, `showPassword`, `version`, `platform` and `instructions`. Null until the server has an address. Session reads (`GetSessionAsync`, and so `WaitForServerReady`) and session lists carry it.
- `GameSession.GetJoinHost()`: `connection.host`, else `slaveIp`.
- `GameSession.GetJoinPort()` (`int?`): `connection.primaryPort` when `connection` is present, else `slavePort`. Null while the port is not known: a game launched with the room port has none until the server has registered the room (a server still booting, or a pool server just claimed), and a game that declares no port has none. When it is null, read the session again a little later.

### Changed (breaking for preview users)
- `GameSession.GetServerPort()` is obsolete (a compiler warning, not an error) and returns `GetJoinPort() ?? 8100`. It used to return `slavePort ?? 8100`, so for a game that listens on a fixed port it now returns that port instead of the room port. Use `GetJoinPort()` and handle null.
- `MSTServerInfo.port` is now `int?` (was `int`) and comes from `GetJoinPort()`: null while the port is not known, where it used to be 8100. `ipAddress` is still `slaveIp`. `MSTServerInfo.GetConnectionAddress()` returns null until both are known.
- `GetConnectionString()` (`SplatterVault.MST`) uses `GetJoinPort()` and returns null while the port is not known (it used to fall back to 8100). The address is still `slaveIp`.

### Documentation
- README: new "Where players join" section (Step 5). The Quick Start, status, session list and pool claim examples use `GetJoinHost()` and `GetJoinPort()`. The pool claim example waits for the session and reads it, because for a game launched with the room port the claim answer's `primaryPort` is null until the room has registered. `slavePort` is described as the room port. The MST guides and the Basic Examples sample match.

### Tests
- `Tests~/SplatterVault.Core.Tests` now also compiles `Runtime/Models.cs` (it has no UnityEngine references) and checks the join helpers: connection details present (a fixed-port game and a room-port game), absent, null, and with a null port; a pool claim answer with a null port; and that the obsolete `GetServerPort()` still works.

### Notes
- `slaveIp` and `slavePort` are read as before. Where the API sends no `connection`, `GetJoinHost()` and `GetJoinPort()` give `slaveIp` and `slavePort`, the values `GetServerPort()` used.

## [4.0.0-preview.1] - 2026-11-15

Game clients can now use SplatterVault's studio services: player sign-in and accounts, player data, and the store. Nothing here takes or sends an API key; game builds never need one. **No breaking changes for v3 users** except two security fixes (see Security): everything from 3.7 (`SplatterVaultClient` for servers and tools, with an API key) is otherwise unchanged and source-compatible, and a project that never calls the player services keeps working as it is. This is a preview: the player services' API can still change before 4.0.0.

### Added
- **`SvPlayers`** (namespace `SplatterVault.Players`): the game client's entry point, made with your game id (or Game Key) and the API base URL. Requires an API with the studio services (`/rest/player-auth`, `/rest/player-data`, `/rest/store`).
- **`SvPlayers.Auth`**: sign in with Meta Quest (`userId`, `nonce`), Steam (hex ticket, or the ticket bytes), Xsolla, PlayFab, Supabase, or a player token from your own backend (`SignInWithStudioTokenAsync`); SplatterVault email accounts (request a code, verify it with an optional new password and display name, password sign-in, refresh, sign-out); guests (`SignInAsGuestAsync`, `CreateGuestAsync`, `SignInWithDeviceAsync`); `RestoreAsync` (the last way in on this device); `GetMeAsync`, `EraseMeAsync`; link and unlink platforms; add, change and remove the email (`currentPassword` or `oldCode`); change the password. Events `SignedIn`, `SignedOut`, `SignInRequired`, and an optional `Reauthenticate` callback.
- **Tokens**: the player token lives in memory only. The refresh token and the guest's device secret go to secure storage and are never handed out. The SDK renews email and guest sign-ins by itself (before expiry, and once after `401 token_expired`, then sends the request once more); one refresh at a time, and the new refresh token is saved before the new player token is used. Other sign-ins raise `SignInRequired` for a fresh proof.
- **`SvPlayers.Data`**: `GetMineAsync` (stores, keys, keysOnly), `WriteAsync` with the `SvDataOps` builder (`Set`, `Delete`, `Inc` with `ifVersion`, `field`, `min`, `max`), `GetGameDataAsync` (ETag cache, 304 returns the kept copy), `GetPublicAsync` (no credential). Values are `JToken` with typed `Get<T>`; versions are `long`.
- **`SvPlayers.Store`**: catalog, balances and items, history paging (`before`), `PurchaseAsync` (with `expectedPrice`), `ConsumeInstanceAsync` / `ConsumeSkuAsync`, `SyncMetaPurchasesAsync`. Every change carries an Idempotency-Key made when the player commits and reused on every retry; unanswered changes are kept in secure storage and finished with `ResumePendingAsync`. `Replayed` (HTTP 200) is a success.
- **`SvApiException`**: `Status`, `ErrorCode` (the API's `error`), `Message`, `Details`, `RetryAfter`, `IsTransient`, `IsOutcomeUnknown` (a write that may have been applied), `IsPending` and `IdempotencyKey` (store changes), `ErrorName`/`Errors` for framework-shaped answers. SDK codes with status 0: `network_error`, `timeout`, `not_signed_in`, `player_changed`, `change_in_progress`, `superseded`, `storage_unreadable`; `SvChangeCanceledException` when a cancellation token stops the wait for a store change; `outcome_unknown` (status 409) for a repeated `ifVersion` write answered `version_conflict`.
- **Retry rules** (`SvRetryPolicy`): only requests that cannot do anything twice are repeated (reads, store changes with their key, data writes whose every op has `ifVersion` only after 408, 429 or 503, a refresh within the 30-second resend window), after transient failures only, honouring `Retry-After`.
- **New assembly `SplatterVault.Core`** (`Core/`, no UnityEngine references): transport interface, errors, JSON settings (dates stay strings), retry policy, idempotency keys, secure-store interface, models and the player services. `SplatterVault.Runtime` now references it and adds the UnityWebRequest transport (completion event, timeout, abort on cancel, JSON bodies for every method including DELETE) and the platform secure stores: Android Keystore (AES-GCM, no extra dependency), iOS Keychain (native plugin in `Runtime/Plugins/iOS`), Windows DPAPI, and an encrypted-file fallback with a warning. Plug in your own `ISecureStore` or `ISvHttpTransport` through `SvPlayersOptions`.
- Code stripping (IL2CPP): `SplatterVault.Runtime` is marked `[assembly: AlwaysLinkAssembly]`, so the linker always processes it and keeps its start-up hook (`SvUnityPlatform`, which plugs the transport and secure storage into `SvPlayers`) even in a build whose code names no Runtime type; Core models carry `[Preserve]`. Unity reads `link.xml` files only from `Assets`, so the new editor assembly `SplatterVault.Editor` (`Editor/SvLinkXmlProvider.cs`, an `IUnityLinkerProcessor`) hands the package's `Runtime/link.xml` to the linker in every IL2CPP build: nothing to copy. `[Preserve]` alone is not enough (it keeps a type and its default constructor, not its property setters; a 2021.3 IL2CPP Android build stripped 103 of the models' 122 setters at Low and at High, and the first sign-in failed with `invalid_response`). The link.xml keeps `SplatterVault.Core` whole and, of `SplatterVault.Runtime`, the v3 JSON models and the start-up hook (not `SplatterVaultClient`, which stays only in builds that use it).
- Samples: **Player Accounts** (guest and email UI), **Player Data** (save and load with versions), **Store** (buy, use, history, resume) and **Platform Sign-in** (Meta Quest and Steam, compiled only with `SV_META_PLATFORM` or `STEAMWORKS_NET` / `SV_STEAMWORKS`). No API key anywhere.
- `Tests~/SplatterVault.Core.Tests`: a `dotnet test` project that compiles the real `Core/` sources against a fake transport.

- Storage safety: `ISecureStore.GetAsync` returns null only when nothing is stored and throws when storage cannot be read; the SDK then answers `storage_unreadable` and never makes a new guest over an unreadable one (`CreateGuestAsync(replaceExistingGuest: true)` is the explicit way out). `StorageFailed` event; `SvSignInResult.SavedToDevice`; `RetrySavingSecretsAsync()`. The guest secret is stored with the player it belongs to and only that player's erase, deletion or device unlink forgets it; an erased player's unanswered store changes are dropped.
- `RestoreAsync` restores only the last way the player signed in on this device (email or that guest), never a different player; `GetLastSignInMethodAsync()` tells a platform player's game which platform to use again.
- Refresh tokens are stored with their player and used only for that player (a leftover of another player is never used to renew or restore, and is deleted again); concurrent `SignInAsGuestAsync` calls share one sign-in; `DiscardAllPendingAsync()`, and an unreadable pending list can be deleted without reading it; token lifetimes are kept within 60 s to 24 h (odd or out-of-range `iat`/`exp` fall back to one hour); the refresh resend window runs on a monotonic clock.
- The v3 friendly-name requests build their JSON without an anonymous type (same JSON), so the narrower `link.xml` cannot strip what they send.
- Token expiry is measured on the device's clock (the token's lifetime from when the sign-in was sent), so a wrong device clock neither refreshes before every request nor waits past expiry. Refresh sends time out after 8 s (`SvPlayersOptions.RefreshTimeout`) so every resend lands inside the API's 30-second grace.
- `RemoveEmailAsync` sends `POST /player-auth/link/email/remove` (same body), because some HTTP stacks drop or refuse a DELETE body. On an API without that route (it answers 404), it sends the same body once as `DELETE /player-auth/link/email`, the older form.
- The UnityWebRequest transport never follows redirects (a redirect could carry a password or guest secret elsewhere). The encrypted-file fallback uses owner-only modes (0700/0600) and, on Android, the app's no-backup directory.

### Changed before release (device check and cross-SDK review, 2026-11-15)
- **IL2CPP builds work without copying `link.xml`.** A 2021.3.29f1 and a 6000.0.34f1 IL2CPP ARM64 Android build stripped 103 of the 122 property setters of the Core models at Low and at High (`[Preserve]` keeps a type and its default constructor only), and the first sign-in failed with `invalid_response`. The new editor assembly `SplatterVault.Editor` (`Editor/SvLinkXmlProvider.cs`, an `IUnityLinkerProcessor`) hands `Runtime/link.xml` to the linker in every build; checked on an Android emulator (API 35) with both editors.
- **Data writes with `ifVersion` are re-sent only after an answer that proves nothing was applied** (408, 429, 503, or the 401 `token_expired` renewal), no longer after no answer, 500, 502 or 504: those now throw with `IsOutcomeUnknown`, and a repeat answered `version_conflict` is `outcome_unknown`, never a plain conflict (a repeat after a lost answer could otherwise be read as "someone else changed it" and the game would apply its change twice). README and the Player Data sample show the recovery: read again.
- **Pending store changes are per player.** `ResumePendingAsync` checks the signed-in player right before each send and stops when another player signed in (the rest stays pending); every store change is sent only with its own player's token (`player_changed`, nothing sent, otherwise); kept changes are replaced and removed by key AND player; a change finished meanwhile is skipped. `DiscardPendingAsync(key)` discards only the signed-in player's change and needs a signed-in player.
- **Store errors say whether the change is still kept: `SvApiException.IsPending`** (true after no answer, 408, 429, 500, 502, 503, 504, a 401 or `player_changed`; false for a refusal or when it could not be kept). A 429 or 503 is not `IsOutcomeUnknown` but stays kept and is applied by a later resume, so the Store sample now turns "Buy" and "Use" off and offers "Finish unanswered purchases" whenever a change is pending (it used to offer "Buy" again after 408/429/503, a second purchase).
- **Store errors carry the change's `IdempotencyKey`**, so a "Retry" can send exactly that change again; README and the Store sample's "Finish unanswered purchases" button use `ResumePendingAsync` (never a new `PurchaseAsync` without the key). A store change answered `idempotency_conflict` on a repeat of its own send (the SDK's second or third send of it, or the re-send after a 401 renewal) stays pending as an unknown outcome; on its first send it is a refusal.
- **Android: secrets are never in a backup.** The Keystore store keeps its sealed values in owner-only files in the app's no-backup directory (`getNoBackupFilesDir()`), no longer in SharedPreferences, which Android Auto Backup copied to other devices where they could not be opened (`storage_unreadable`); the encrypted-file fallback moved there too. Values stored by earlier, unreleased builds of this preview are not read (sign in again).
- **Options are checked when the client is made** (`ArgumentOutOfRangeException`), never clamped quietly: `RequestTimeout` (0 < t ≤ 2^31 - 1 ms), `RefreshTimeout` (0 < t ≤ 10 s; was clamped to 10 s), `RenewBeforeExpiry` (0 to 30 min), and `SvRetryPolicy` setters (`MaxAttempts` 1 to 10, waits 0 to 2^31 - 1 ms, `Jitter` 0 to 1, NaN refused).
- A redirect answer reads as an `SvApiException` with the 3xx status and a message naming it (never followed, never repeated).
- **A late renewal never wins.** A sign-in the game asks for starts a new auth epoch as soon as its answer arrives, before anything is saved, and sets its session only while that epoch holds (`superseded` otherwise); sign-out starts one at its first step, erase when it clears the session. A refresh, guest renewal, `RestoreAsync` or `Reauthenticate` request started under an older epoch is not used (every refresh attempt and the device fallback re-check it after each await), and a late renewal's new refresh token is signed out on the server: a sign-out during a refresh used to sign the player back in, a late 401 after sign-out still asked the game to sign in again, a renewal landing while a new sign-in saved its token overwrote it, and a failed second refresh attempt deleted or spent the next player's token. Every player request is sent only as the player who was signed in when it was called. A password change keeps its new refresh token only while the same player is signed in; erase forgets only the erased player's records.
- **One send per store change at a time.** A change already being sent is joined by a new call with the same key and arguments (its answer is returned) and skipped by `ResumePendingAsync` (never joined: resuming from inside `Reauthenticate` used to deadlock). A different change under a key that is kept or being sent is refused with `change_in_progress` (nothing sent, the kept change untouched; it used to be replaced). The network send of a change runs to its answer: a caller's cancellation token stops only that caller's wait, with `SvChangeCanceledException` (an `OperationCanceledException` carrying `IdempotencyKey` and `IsPending`).
- **Two clients sharing one store** (two players on one machine, Multiplayer Play Mode) read the refresh token from storage again before every refresh, so one client's rotation no longer makes the other send a spent token (which ended the sign-in for both).
- **Storage:** every write uses a temp file of its own (two processes sharing a store can no longer tear a value); an interrupted write is recovered from its newest complete temp file on the next read (torn ones are deleted); deleting a value deletes its temp files first, so no secret outlives sign-out or erase. When the platform's store exists but cannot be opened (the Android Keystore store fails to open, an unreadable salt), the SDK uses `SvUnreadableSecureStore` (everything reads as `storage_unreadable`) instead of a different, empty store that would let a new guest be made over the stored one.
- **Secure storage is cheap on the main thread.** On a Quest 3 every sealed read or write cost ~13-16 ms (about a frame), because each one looked up the Keystore key, a Cipher and the JNI methods again. The Android Keystore store now makes the key handle, one Cipher and the method ids once per process (dropped and made again after any Java failure, such as `KeyPermanentlyInvalidatedException`; a value that still does not open is unreadable, as before), and the file stores (Android Keystore, Windows DPAPI, the encrypted-file fallback) keep each value they wrote or opened in memory with the sealed bytes it came from, and answer a read from memory only when the file still holds exactly those bytes (a value another process changed is opened from its file; an unreadable value is never kept; after any seal or open fails, nothing kept is used). File times are not used: on Android the first local-time conversion of a process loads the time zone data, about 2 s on the main thread. Each store checks once that what it seals opens again, and every write is compared with what landed in the file. A purchase now seals once and opens nothing (was: seal + open), a restore no longer rewrites an unchanged last sign-in (was: one extra seal per restore and per guest renewal).

### Security
- **`SplatterVaultClient` (v3, API key) never follows redirects** (`redirectLimit = 0`): UnityWebRequest followed up to 32 by default, sending the `X-API-Key` header to wherever the redirect pointed. A 3xx is now an error.
- **`SplatterVaultClient` refuses an `http://` base URL** unless its host is loopback (localhost, 127.0.0.1, ::1) or you use the new overloads with `allowInsecureHttp: true` (development only) on the constructors or `CreateAsync`. The 3.7 signatures are unchanged (code built against 3.7 keeps binding to them). `SvPlayers` already refused it (`AllowInsecureHttp`).

### Notes
- Manual installs (folders copied into `Assets`): copy the new `Core` folder next to `Runtime`; `SplatterVault.Runtime` now references `SplatterVault.Core`. Package Manager installs need nothing.
- Rotating refresh tokens: quitting after the API took a refresh token but before the new one was saved, then relaunching more than 30 seconds later, ends that email sign-in (sign in again).
- A guest exists on one device only: uninstalling, clearing data, a factory reset or moving to a new device (its secrets are never in a backup) loses it unless the player added an email.
- Checked on an Android emulator (API 35, ARM64 IL2CPP through translation, Unity 2021.3.29f1, Managed Stripping High, no `link.xml` copied; the stripping fix and the flow before the pending-per-player and redirect steps also with 6000.0.34f1): guest sign-in, data set/inc/read/`ifVersion` conflict, game data 304, catalog, purchase (replay, lost answer resent with its key, price change), consume, history, Retry-After, forced token expiry, the Keystore store in the no-backup directory, restore after a restart, sign-out, erase, pending changes per player and refused redirects.
- Not verified on devices yet: a Meta Quest itself (the Keystore store there, and its main-thread cost), iOS Keychain plugin, Windows DPAPI, Meta proof round trip, DELETE with a JSON body on Android and iOS (now only the fallback of `RemoveEmailAsync` on an older API).
- WebGL is not supported for player services: `new SvPlayers(...)` throws `PlatformNotSupportedException` (browsers block the API, CORS).
- Player data writes have no Idempotency-Key on the API: the SDK repeats one only when every op has `ifVersion` and the answer proved nothing was applied (408, 429, 503).
- A kept store change is applied whenever it is resumed, even one never sent before the game quit: resume at a moment the player expects it, or discard it.
- A player token allows 60 requests a minute across all routes: read on demand, never poll.

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
