using System;

namespace SplatterVault.Core
{
    /// <summary>
    /// Keeps a type from managed code stripping (IL2CPP). Unity's linker honours any attribute named
    /// <c>PreserveAttribute</c>, so this assembly needs no UnityEngine reference. On a type it keeps the type and its
    /// default constructor only, not its property setters: the package's Runtime/link.xml keeps this whole assembly,
    /// and Editor/SvLinkXmlProvider.cs hands that file to the linker in every IL2CPP build.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Constructor |
                    AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Enum,
        Inherited = false)]
    internal sealed class PreserveAttribute : Attribute
    {
    }
}
