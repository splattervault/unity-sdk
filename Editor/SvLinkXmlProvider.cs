using System.IO;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;

namespace SplatterVault.Editor
{
    /// <summary>
    /// Hands the package's <c>Runtime/link.xml</c> to Unity's managed code linker in every IL2CPP build. Unity reads
    /// link.xml files only from the Assets folder; a package supplies its own through
    /// <see cref="IUnityLinkerProcessor"/>. Without it, every stripping level (Low included) removes the JSON models' property
    /// setters (only Newtonsoft.Json calls them, by reflection) and every answer reads as empty fields.
    /// </summary>
    internal sealed class SvLinkXmlProvider : IUnityLinkerProcessor
    {
        public int callbackOrder => 0;

        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(SvLinkXmlProvider).Assembly);
            if (package == null) return null; // copied into Assets by hand: Unity reads Runtime/link.xml there itself
            var path = Path.GetFullPath(Path.Combine(package.resolvedPath, "Runtime", "link.xml"));
            return File.Exists(path) ? path : null;
        }

#if !UNITY_2021_2_OR_NEWER
        public void OnBeforeRun(BuildReport report, UnityLinkerBuildPipelineData data)
        {
        }

        public void OnAfterRun(BuildReport report, UnityLinkerBuildPipelineData data)
        {
        }
#endif
    }
}
