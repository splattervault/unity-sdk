# Master Server Toolkit Integration Guide

This guide explains how to use SplatterVault with Master Server Toolkit (MST).

## Overview

SplatterVault and MST serve complementary roles:
- **SplatterVault**: runs your dedicated game servers (hosting, scaling, billing)
- **MST**: networking, matchmaking and lobbies

In MST, a game server registers its own room with the master server. With SplatterVault, nothing about that changes: your **master server** asks SplatterVault for a game server instead of starting a local process, and hands it the master's address as launch arguments. The game server boots on SplatterVault and registers its room with your master.

## Where This Code Runs

All of it runs on your **MST master server** (a dedicated server build), or in the Unity Editor while testing. The SplatterVault API key lives there only, read from the `SV_API_KEY` environment variable. Never put the key in a game client build: anyone who pulls it out can start and stop your servers and spend your credits. Players find rooms through MST as usual and never need the key. Game clients will sign players in instead (coming in SDK 4.0).

## What's Included

### Runtime: `MSTExtensions.cs` (namespace `SplatterVault.MST`)
- `WaitForServerReady()`: poll until the session is Active (with `waitForGameReady: true`, until the game says it is ready); throws as soon as the session stops
- `CreateAndWaitForMSTServer()`: create a credit session and wait for it in one call
- `GetMSTServerInfo()`, `MSTServerInfo.ToMSTProperties()`: session details in MST-friendly form
- `IsReadyForMST()`, `GetConnectionString()`: small helpers

### Sample: MST Spawner (`SplatterVaultMSTSpawner.cs`)
A small component for the master server. `SpawnAsync(roomName)` claims a warm server from a pool (when a pool ID is set) or starts a new one, passes the master's address as launch arguments, and returns the session once it is Active. `StopAsync(session)` stops it. It uses no MST types, so it compiles with or without MST. Import it from Package Manager → SplatterVault SDK → Samples. See its `MST_MASTER_INTEGRATION.md`.

## Quick Start

1. **Declare the launch arguments** your game server reads for the master address (MST's standard names are `-mstMasterIp` and `-mstMasterPort`) in your game's SplatterVault game config. Flags the game does not declare are ignored.
2. **Set the key** as `SV_API_KEY` on the master server's machine.
3. **Start servers from your master's spawn handler:**

```csharp
#if UNITY_SERVER || UNITY_EDITOR
using SplatterVault;
using SplatterVault.MST;

var client = await SplatterVaultClient.CreateAsync(
    System.Environment.GetEnvironmentVariable("SV_API_KEY"));

var request = new CreateSessionRequest { gameKey = "your_game_key", friendlyName = roomName };
request.SetRegion(Region.NYC3);
request.AddCustomVariable("-mstMasterIp", masterPublicAddress);
request.AddCustomVariable("-mstMasterPort", "5000");

GameSession session = await client.CreateCreditSessionAsync(request);
session = await client.WaitForServerReady(session.id, onStatusUpdate: s => Debug.Log(s));
// The game server registers its room with your master on its own.

// Later, when the room is finished:
await client.StopSessionAsync(session);
#endif
```

Or use the MST Spawner sample, which does the same and can claim warm pool servers first.

## Faster Starts with Server Pools

A server pool keeps servers of your game already running. `ClaimPoolServerAsync(poolId, request)` starts a session on one in seconds, with the same launch-argument overrides (`ClaimPoolServerRequest.AddCustomVariable`). It fails with 409 when no warm server is free; fall back to `CreateCreditSessionAsync`. With an organization key, claiming needs the `org:manage_pools` permission. Stopping the session returns the server to its pool. See the README's "Server Pools" section.

## Server Lifecycle

1. **Start**: the master creates a session (or claims a pool server)
2. **Boot**: the server starts; status goes Pending → Active
3. **Ready**: the game says it is ready (`session.ready`); `WaitForServerReady(…, waitForGameReady: true)` returns here (by default it returns at step 2)
4. **Register**: the game server registers its room with your master
5. **Play**: players find the room through MST and connect to the game server
6. **Stop**: the master stops the session (or the game exits, or the idle timeout fires)

## Troubleshooting

### The room never appears in MST
- Check that your game config declares the master-address launch arguments, and that the game server reads them
- Check that `masterPublicAddress` is reachable from the internet (not a LAN address)
- Look at the session's game log on your dashboard

### `WaitForServerReady` returns before the game is ready
- By default it returns as soon as the session is Active. Pass `waitForGameReady: true` to wait for the game's ready signal too.
- With `waitForGameReady: true`, a game with no ready signal for the mode it runs returns only once the ready deadline passes (about 5 minutes after the server is up) or at `maxWaitSeconds`. Configure a ready signal for every mode, or leave `waitForGameReady` off.

### `WaitForServerReady` throws "stopped before it was ready"
- The session ended while starting; the message carries the stop reason (for example `GAME_CRASH`). Check the game log on your dashboard and your credit balance.

## Support

- SplatterVault SDK: see `README.md`
- MST: see the Master Server Toolkit documentation
- Issues: report via Discord `/reportbug`

## License

This integration is provided as-is for use with SplatterVault and MST.
