# SplatterVault Unity SDK - Installation Guide

## Quick Installation

### Option 1: Unity Package Manager (Recommended)

1. Open the Unity Editor
2. Go to Window → Package Manager
3. Click the `+` button
4. Select "Add package from git URL"
5. Enter: `https://github.com/splattervault/unity-sdk.git#v4.0.0-preview.1`

The Package Manager also installs the SDK's one dependency, Newtonsoft Json (`com.unity.nuget.newtonsoft-json`).

### Option 2: Manual Installation

1. Copy the `Core` and `Runtime` folders into your project, e.g. `Assets/SplatterVault/`
2. Install **Newtonsoft Json** (`com.unity.nuget.newtonsoft-json`) from the Package Manager

```
YourUnityProject/
  Assets/
    SplatterVault/
      Core/                          (SplatterVault.Core.asmdef, player services)
      Runtime/                       (SplatterVault.Runtime.asmdef, server client, Unity transport and secure storage)
```

## Verification

After installation, check that the SDK compiles:

1. Create a new C# script in Unity
2. Add this code:

```csharp
using SplatterVault;
using UnityEngine;

public class TestSDK : MonoBehaviour
{
    void Start()
    {
        Debug.Log($"SplatterVault SDK loaded: {typeof(SplatterVaultClient).Assembly.GetName().Name}");
    }
}
```

3. Attach the script to a GameObject
4. Press Play - you should see the message in the Console

## Next Steps

1. **Get Your API Key**
   - Go to https://splattervault.com
   - Navigate to Settings → API Keys
   - Create a new API key
   - Wait for admin approval

2. **Keep the key on servers only** (see below)

3. **Try the Samples** (Package Manager → SplatterVault SDK → Samples)
   - **Basic Examples**: `SimpleSessionManager` (basic usage), `TournamentManager` (scheduling)
   - **MST Spawner**: `SplatterVaultMSTSpawner` for a Master Server Toolkit master server

4. **Read the Documentation**
   - See `README.md` for the full API reference

## Requirements

- Unity 2020.2 or later
- .NET 4.x or .NET Standard 2.0/2.1 API compatibility level
- Newtonsoft Json package (installed automatically with the Package Manager)
- Internet access enabled in Player Settings

## Securing Your API Key

**Your API key belongs on servers only:** dedicated server builds, your own backend, or the Unity Editor while you develop. Never put it in a game client build, in any form: a `[SerializeField]` field, a scene, a prefab, a ScriptableObject or a string in code. Players can pull anything out of a client build, and with your key anyone can start and stop your servers and spend your (or your organization's) credits. Game clients sign players in instead, with `SvPlayers` (SDK 4.0), which never takes a key.

Read the key from an environment variable on the machine that runs your server-side code, and keep that code out of client builds:

```csharp
#if UNITY_SERVER || UNITY_EDITOR
string apiKey = Environment.GetEnvironmentVariable("SV_API_KEY");
var client = await SplatterVaultClient.CreateAsync(apiKey);
#endif
```

- `UNITY_SERVER` is defined in dedicated server builds (Server Build / Dedicated Server build target); `UNITY_EDITOR` in the Editor.
- In the Editor, set the variable before starting the Editor (or Unity Hub), then restart it after changes.
- Never commit the key to version control.

## Troubleshooting

### "Cannot find type or namespace 'SplatterVault'"

**Solution:** Make sure the package (or the `Core` and `Runtime` folders) is in your project and Unity has finished compiling.

### "The type or namespace name 'Newtonsoft' could not be found"

**Solution:** Install the Newtonsoft Json package (`com.unity.nuget.newtonsoft-json`) from the Package Manager. The Package Manager install does this for you; a manual copy of `Core` and `Runtime` does not.

### "The type or namespace name 'Task' could not be found"

**Solution:**
1. Go to Edit → Project Settings → Player
2. Under "Other Settings" → "Api Compatibility Level"
3. Select ".NET 4.x" or ".NET Standard 2.0/2.1"

### "API key is required"

**Solution:** The key was empty. Set `SV_API_KEY` on the machine running the server or the Editor.

### Connection Errors

**Solution:**
1. Check internet connectivity
2. Verify the API key is correct and approved
3. Check firewall settings
4. Enable "Internet Access" in Player Settings

### Fields come back empty in an IL2CPP build (High stripping)

**Solution:** the package hands its `Runtime/link.xml` to the linker itself (`Editor/SvLinkXmlProvider.cs`), so this should not happen with a Package Manager install. Check that the `SplatterVault.Editor` assembly compiled (no errors in the Console) and that the package's `Editor` folder is present. With a manual install (`Core` and `Runtime` copied into `Assets`), Unity reads `Runtime/link.xml` there directly. See "IL2CPP Builds" in `README.md`.

## Support

- Documentation: See `README.md`
- API Docs: https://splattervault.com/developers?tab=reference
- Discord: Report issues with `/reportbug`
- Email: support@splattervault.com

## What's Included

```
unity-sdk/
├── README.md                          # Complete documentation
├── INSTALLATION.md                    # This file
├── CHANGELOG.md                       # Version history
├── MST_INTEGRATION.md                 # Master Server Toolkit guide
├── package.json                       # Unity Package Manager manifest
├── Core/                              # SplatterVault.Core: player sign-in, data, store (no UnityEngine)
├── Runtime/
│   ├── SplatterVaultClient.cs         # Server/tools API client (API key)
│   ├── Models.cs                      # Request and response models
│   ├── MSTExtensions.cs               # WaitForServerReady and MST helpers
│   ├── Players/                       # UnityWebRequest transport, platform secure storage
│   ├── Plugins/iOS/                   # Keychain native plugin
│   ├── AssemblyInfo.cs                # AlwaysLinkAssembly (keeps the start-up hook in IL2CPP builds)
│   ├── link.xml                       # IL2CPP stripping rules (handed to the linker by Editor/SvLinkXmlProvider.cs)
│   └── SplatterVault.Runtime.asmdef
├── Editor/SvLinkXmlProvider.cs        # Hands Runtime/link.xml to the IL2CPP linker (nothing to copy)
├── Samples~/
│   ├── PlayerAccounts/, PlayerData/, Store/, PlatformSignIn/   # game client samples
│   ├── BasicExamples/                 # SimpleSessionManager, TournamentManager
│   └── MSTIntegration/                # SplatterVaultMSTSpawner
├── Tests~/SplatterVault.Core.Tests/   # dotnet test of Core (no Unity, no server)
└── Tools~/sdk-mock/                   # local API mock for Editor/device checks (never imported; not in the registry package)
```

See `CHANGELOG.md` for the version history.

## License

This SDK is provided as-is for use with the SplatterVault platform.
