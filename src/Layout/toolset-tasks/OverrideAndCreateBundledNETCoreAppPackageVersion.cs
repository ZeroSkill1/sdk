﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.XPath;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.DotNet.Build.Tasks
{
    /// <summary>
    /// Use the runtime in dotnet/sdk instead of in the stage 0 to avoid circular dependency.
    /// If there is a change depended on the latest runtime. Without override the runtime version in BundledNETCoreAppPackageVersion
    /// we would need to somehow get this change in without the test, and then insertion dotnet/installer
    /// and then update the stage 0 back.
    ///
    /// Override NETCoreSdkVersion to stage 0 sdk version like 6.0.100-dev
    /// 
    /// Use a task to override since it was generated as a string literal replace anyway.
    /// And using C# can have better error when anything goes wrong.
    /// </summary>
    public sealed class OverrideAndCreateBundledNETCoreAppPackageVersion : Task
    {
        private static string _messageWhenMismatch =
            "{0} version {1} does not match BundledNETCoreAppPackageVersion {2}. " +
            "The schema of https://github.com/dotnet/installer/blob/main/src/redist/targets/GenerateBundledVersions.targets might change. " +
            "We need to ensure we can swap the runtime version from what's in stage0 to what dotnet/sdk used successfully";

        [Required] public string Stage0MicrosoftNETCoreAppRefPackageVersionPath { get; set; }

        [Required] public string MicrosoftNETCoreAppRefPackageVersion { get; set; }

        // TODO: remove this once linker packages are produced from dotnet/runtime
        // and replace it with MicrosoftNETCoreAppRefPackageVersion.
        [Required] public string MicrosoftNETILLinkTasksPackageVersion { get; set; }

        [Required] public string NewSDKVersion { get; set; }

        [Required] public string OutputPath { get; set; }

        public override bool Execute()
        {
            File.WriteAllText(OutputPath,
                ExecuteInternal(
                    File.ReadAllText(Stage0MicrosoftNETCoreAppRefPackageVersionPath),
                    MicrosoftNETCoreAppRefPackageVersion,
                    MicrosoftNETILLinkTasksPackageVersion,
                    NewSDKVersion));
            return true;
        }

        public static string ExecuteInternal(
            string stage0MicrosoftNETCoreAppRefPackageVersionContent,
            string microsoftNETCoreAppRefPackageVersion,
            string microsoftNETILLinkTasksPackageVersion,
            string newSDKVersion)
        {
            var projectXml = XDocument.Parse(stage0MicrosoftNETCoreAppRefPackageVersionContent);

            var ns = projectXml.Root.Name.Namespace;

            var propertyGroup = projectXml.Root.Elements(ns + "PropertyGroup").First();

            var isSDKServicing = IsSDKServicing(propertyGroup.Element(ns + "NETCoreSdkVersion").Value);

            propertyGroup.Element(ns + "NETCoreSdkVersion").Value = newSDKVersion;

            var originalBundledNETCoreAppPackageVersion =
                propertyGroup.Element(ns + "BundledNETCoreAppPackageVersion").Value;
            propertyGroup.Element(ns + "BundledNETCoreAppPackageVersion").Value = microsoftNETCoreAppRefPackageVersion;

            void CheckAndReplaceElement(XElement element)
            {
                if (element.Value != originalBundledNETCoreAppPackageVersion)
                {
                    throw new InvalidOperationException(string.Format(
                        _messageWhenMismatch,
                        element.ToString(), element.Value, originalBundledNETCoreAppPackageVersion));
                }

                element.Value = microsoftNETCoreAppRefPackageVersion;
            }

            void CheckAndReplaceAttribute(XAttribute attribute)
            {
                if (attribute.Value != originalBundledNETCoreAppPackageVersion)
                {
                    throw new InvalidOperationException(string.Format(
                        _messageWhenMismatch,
                        attribute.Parent.ToString() + " --- " + attribute.ToString(), attribute.Value,
                        originalBundledNETCoreAppPackageVersion));
                }

                attribute.Value = microsoftNETCoreAppRefPackageVersion;
            }

            if (!isSDKServicing)
            {
                CheckAndReplaceElement(propertyGroup.Element(ns + "BundledNETCorePlatformsPackageVersion"));
            }

            var itemGroup = projectXml.Root.Elements(ns + "ItemGroup").First();

            if (!isSDKServicing)
            {
                CheckAndReplaceAttribute(itemGroup
                    .Elements(ns + "KnownFrameworkReference").First().Attribute("DefaultRuntimeFrameworkVersion"));
                CheckAndReplaceAttribute(itemGroup
                    .Elements(ns + "KnownFrameworkReference").First().Attribute("TargetingPackVersion"));
            }

            CheckAndReplaceAttribute(itemGroup
                .Elements(ns + "KnownFrameworkReference").First().Attribute("LatestRuntimeFrameworkVersion"));

            CheckAndReplaceAttribute(itemGroup
                .Elements(ns + "KnownAppHostPack").First().Attribute("AppHostPackVersion"));
            CheckAndReplaceAttribute(itemGroup
                .Elements(ns + "KnownCrossgen2Pack").First().Attribute("Crossgen2PackVersion"));

            // TODO: remove this once we're using an SDK that contains https://github.com/dotnet/installer/pull/10250
            var crossgen2Rids = itemGroup.Elements(ns + "KnownCrossgen2Pack").First().Attribute("Crossgen2RuntimeIdentifiers");
            if (!crossgen2Rids.Value.Contains("osx-x64"))
            {
                crossgen2Rids.Value += ";osx-x64";
            }

            CheckAndReplaceAttribute(itemGroup
                .Elements(ns + "KnownRuntimePack").First().Attribute("LatestRuntimeFrameworkVersion"));

            // TODO: remove this once we're using an SDK that contains KnownILLinkPack: https://github.com/dotnet/installer/pull/15106
            {
                itemGroup.Add(new XElement(ns + "KnownILLinkPack",
                    new XAttribute("Include", "Microsoft.NET.ILLink.Tasks"),
                    new XAttribute("TargetFramework", "net8.0"),
                    new XAttribute("ILLinkPackVersion", microsoftNETILLinkTasksPackageVersion)));

                // Use 7.0 linker when targeting supported RIDS <= net7.0.
                var net70Version = "7.0.100-1.22579.2";

                foreach (var tfm in new[] { "net7.0", "net6.0", "net5.0", "netcoreapp3.1", "netcoreapp3.0" }) {
                    itemGroup.Add(new XElement(ns + "KnownILLinkPack",
                        new XAttribute("Include", "Microsoft.NET.ILLink.Tasks"),
                        new XAttribute("TargetFramework", tfm),
                        new XAttribute("ILLinkPackVersion", net70Version)));
                }
            }

            return projectXml.ToString();
        }

        /// <summary>
        /// For SDK servicing, few Attributes like "DefaultRuntimeFrameworkVersion" does not use the latest flowed version
        /// so there is no need to replace them.
        /// </summary>
        /// <returns></returns>
        private static bool IsSDKServicing(string sdkVersion)
        {
            var parsedSdkVersion = NuGet.Versioning.NuGetVersion.Parse(sdkVersion);

            return parsedSdkVersion.Patch % 100 != 0;
        }
    }
}
