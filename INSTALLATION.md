# SplatterVault Unity SDK - Installation Guide

## Quick Installation

### Option 1: Unity Package Manager (Recommended)

1. Open the Unity Editor
2. Go to Window → Package Manager
3. Click the `+` button
4. Select "Add package from git URL"
5. Enter: `https://github.com/splattervault/unity-sdk.git#v3.7.0`

The Package Manager also installs the SDK's one dependency, Newtonsoft Json (`com.unity.nuget.newtonsoft-json`).

### Option 2: Manual Installation

1. Copy the `Runtime` folder into your project, e.g. `Assets/SplatterVault/`
2. Install **Newtonsoft Json** (`com.unity.nuget.newtonsoft-json`) from the Package Manager

```
YourUnityProject/
  Assets/
    SplatterVault/
      MSTExtensions.cs
      Models.cs
      SplatterVaultClient.cs
      SplatterVault.Runtime.asmdef
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

**Your API key belongs on servers only:** dedicated server builds, your own backend, or the Unity Editor while you develop. Never put it in a game client build, in any form: a `[SerializeField]` field, a scene, a prefab, a ScriptableObject or a string in code. Players can pull anything out of a client build, and with your key anyone can start and stop your servers and spend your (or your organization's) credits. Game clients will sign players in instead (coming in SDK 4.0).

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

**Solution:** Make sure the package (or the `Runtime` folder) is in your project and Unity has finished compiling.

### "The type or namespace name 'Newtonsoft' could not be found"

**Solution:** Install the Newtonsoft Json package (`com.unity.nuget.newtonsoft-json`) from the Package Manager. The Package Manager install does this for you; a manual copy of `Runtime` does not.

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

## Support

- Documentation: See `README.md`
- API Docs: https://splattervault.com/#/api-docs
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
├── Runtime/
│   ├── SplatterVaultClient.cs         # Main API client
│   ├── Models.cs                      # Request and response models
│   ├── MSTExtensions.cs               # WaitForServerReady and MST helpers
│   └── SplatterVault.Runtime.asmdef
└── Samples~/
    ├── BasicExamples/                 # SimpleSessionManager, TournamentManager
    └── MSTIntegration/                # SplatterVaultMSTSpawner
```

See `CHANGELOG.md` for the version history.

## License

This SDK is provided as-is for use with the SplatterVault platform.
