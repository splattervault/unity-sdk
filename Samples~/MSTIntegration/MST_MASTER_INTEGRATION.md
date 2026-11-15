# SplatterVault Spawner for an MST Master Server

`SplatterVaultMSTSpawner.cs` lets your Master Server Toolkit (MST) master start game servers on SplatterVault instead of on its own machines.

## Where this code runs

On your **MST master server only** (a dedicated server build, or the Unity Editor while testing). The file is wrapped in `#if UNITY_SERVER || UNITY_EDITOR`, so it is left out of game client builds.

The SplatterVault API key is read from the `SV_API_KEY` environment variable of the machine that runs the master. Never put the key in a game client build, a serialized field, a scene or a prefab: anyone who pulls it out can start and stop your servers and spend your credits. Game clients will use player sign-in instead (coming in SDK 4.0).

## How it works

```
1. Your MST master gets a spawn request
2. It calls spawner.SpawnAsync(roomName)
3. The SDK claims a warm server from your pool (if poolId is set), or starts a new server
   with the master's address as launch arguments (-mstMasterIp / -mstMasterPort by default)
4. The game server boots and registers its own room with your master, as it would anywhere
5. SpawnAsync returns the session once it is Active (the server is up; the game may still be loading)
6. Later, spawner.StopAsync(session) stops it (a pool server goes back to its pool)
```

The spawner uses no MST types, so it compiles whether or not MST is installed. Your master's spawn handler calls it.

## Setup

1. Import this sample and add `SplatterVaultMSTSpawner` to a GameObject in your master server scene.
2. Set **Game Key** (from your SplatterVault dashboard), **Region**, and **Master Public Address** / **Master Port**.
3. Make sure your game's SplatterVault game config declares the launch arguments named in **Master Ip Flag** and **Master Port Flag**. Flags the game does not declare are ignored.
4. Optional: set **Pool Id** to claim warm servers from a server pool. If the pool has no free server, the spawner starts a new one.
5. Set `SV_API_KEY` on the master's machine. With an organization key (`sv_org_...`), pool claims need the `org:manage_pools` permission.

## Regions

**Region** takes any `Region` value (18): the DigitalOcean regions `NYC1`, `NYC3`, `ATL1`, `RIC1`, `TOR1`, `SFO2`, `SFO3`, `LON1`, `AMS3`, `FRA1`, `SGP1`, `BLR1`, `SYD1` and `MKC1` (Kansas City), and the Vultr cities `VDFW` (Dallas, TX), `VORD` (Chicago, IL), `VMIA` (Miami, FL) and `VLAX` (Los Angeles, CA). It is used when the spawner starts a new server; a claimed pool server runs where its pool is.

The Vultr regions (`VDFW`, `VORD`, `VMIA`, `VLAX`) only work where the platform has switched that Vultr city on for your game; otherwise the API normally refuses the create with a 400 error. You get one only by asking for it: a DigitalOcean region never falls back to Vultr, though a Vultr city you ask for can fall back to another Vultr city or a nearby DigitalOcean region.

## Example

```csharp
// In your master server's spawn handler
GameSession session = await spawner.SpawnAsync("Ranked room 12");
Debug.Log($"Room server {session.code} at {session.slaveIp}:{session.GetServerPort()}");

// When the room is done
await spawner.StopAsync(session);
```

`SpawnAsync` throws when the server stops before it is Active, or is not Active within 5 minutes.
