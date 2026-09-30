using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Mroczware.PowerFullTool.Helpers.RoslynHelper
{
    public static class RoslynReferenceHelper
    {
        public static IEnumerable<MetadataReference> GetReferencesForProject(string csprojPath)
        {
            string targetFramework = GetTargetFramework(csprojPath);

            if (targetFramework.StartsWith("net4")) // Obsługuje .NET Framework
            {
                return GetNetFrameworkReferences(targetFramework);
            }
            else if (targetFramework.StartsWith("net")) // Obsługuje .NET Core / .NET 5+
            {
                return GetDotNetCoreReferences(targetFramework);
            }

            throw new NotSupportedException($"Nieobsługiwany TargetFramework: {targetFramework}");
        }

        private static string GetTargetFramework(string csprojPath)
        {
            var doc = XDocument.Load(csprojPath);
            var ns = doc.Root.GetDefaultNamespace();

            var tfm = doc.Descendants(ns + "TargetFramework").FirstOrDefault()?.Value
               ?? doc.Descendants(ns + "TargetFrameworks").FirstOrDefault()?.Value.Split(';').FirstOrDefault();

            if (string.IsNullOrEmpty(tfm))
                throw new Exception("Nie znaleziono TargetFramework w .csproj");

            return tfm;
        }

        private static IEnumerable<MetadataReference> GetDotNetCoreReferences(string targetFramework)
        {
            string dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? @"C:\Program Files\dotnet";
            string packsPath = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");

            string[] availableVersions = Directory.GetDirectories(packsPath)
                .Select(Path.GetFileName)
                .ToArray();

            string majorVersion = targetFramework.Replace("net", "").Split('.')[0]; // np. "6" dla net6.0

            string version = availableVersions
                .Where(v => v.StartsWith(majorVersion + "."))
                .OrderByDescending(v => Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0))
                .FirstOrDefault();

            if (version == null)
                throw new Exception($"Nie znaleziono Microsoft.NETCore.App.Ref dla {targetFramework}");

            string refPath = Path.Combine(packsPath, version, "ref", targetFramework);
            if (!Directory.Exists(refPath))
                throw new Exception($"Brak folderu ref/netX.X dla {targetFramework}");

            return Directory.EnumerateFiles(refPath, "*.dll")
                .Select(f => MetadataReference.CreateFromFile(f));
        }

        private static IEnumerable<MetadataReference> GetNetFrameworkReferences(string targetFramework)
        {
            string version = "v" + targetFramework.Substring(3, 1) + "." + targetFramework.Substring(4); // net48 → v4.8
            string path = Path.Combine(@"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework", version);

            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException($"Nie znaleziono folderu dla .NET Framework: {path}");

            return Directory.EnumerateFiles(path, "*.dll")
                .Select(f => MetadataReference.CreateFromFile(f));
        }
    }
}
