// Runtime/link.xml reaches the linker through Editor/SvLinkXmlProvider.cs (Unity reads link.xml only from Assets).
// AlwaysLinkAssembly makes the managed code linker process this assembly in every IL2CPP build, even
// one whose code names none of its types, so its [RuntimeInitializeOnLoadMethod] start-up hook (SvUnityPlatform,
// which plugs the UnityWebRequest transport and the platform's secure storage into SvPlayers) is kept. It does not
// keep anything else: unused types are still stripped as usual.
[assembly: UnityEngine.Scripting.AlwaysLinkAssembly]
