// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Microsoft.DotNet.Build.Tasks.VisualStudio
{
    internal struct IbcEntry
    {
        private const string TechnologyName = "IBC";
        private const string VSInstallationRootVar = "%VisualStudio.InstallationUnderTest.Path%";

        public readonly string RelativeInstallationPath;
        public readonly string InstrumentationArguments;

        public IbcEntry(string relativeInstallationPath, string instrumentationArguments)
        {
            RelativeInstallationPath = relativeInstallationPath;
            InstrumentationArguments = instrumentationArguments;
        }

        public JObject ToJson()
            => new JObject(
                new JProperty("Technology", TechnologyName),
                new JProperty("RelativeInstallationPath", RelativeInstallationPath),
                new JProperty("InstrumentationArguments", InstrumentationArguments));

        public static IEnumerable<IbcEntry> GetEntriesFromAssembly(AssemblyOptProfTraining assembly)
        {
            foreach (var args in assembly.InstrumentationArguments)
            {
                yield return new IbcEntry(
                    relativeInstallationPath: args.RelativeInstallationFolder.Replace("/", "\\") + $"\\{assembly.Assembly}",
                    instrumentationArguments: $"/ExeConfig:\"{VSInstallationRootVar}\\{args.InstrumentationExecutable.Replace("/", "\\")}");
            }
        }

        public static IEnumerable<IbcEntry> GetEntriesFromVsixJsonManifest(JObject json)
        {
            const string DefaultInstrumentationArgs = "/ExeConfig:\"" + VSInstallationRootVar + "\\Common7\\IDE\\vsn.exe\"";

            bool isNgened(JToken file)
                => file["ngen"] != null || file["ngenPriority"] != null || file["ngenArchitecture"] != null || file["ngenApplication"] != null;

            bool isPEFile(string filePath)
            {
                string ext = Path.GetExtension(filePath);
                return StringComparer.OrdinalIgnoreCase.Equals(ext, ".dll") ||
                       StringComparer.OrdinalIgnoreCase.Equals(ext, ".exe");
            }

            if (json["extensionDir"] != null)
            {
                var extensionDir = ((string)json["extensionDir"]).Replace("[installdir]\\", string.Empty);
                return from file in (JArray)json["files"]
                       let fileName = (string)file["fileName"]
                       where isNgened(file) && isPEFile(fileName)
                       let filePath = $"{extensionDir}\\{fileName.Replace("/", string.Empty)}"
                       select new IbcEntry(filePath, DefaultInstrumentationArgs);
            }
            else
            {
                return from file in (JArray)json["files"]
                       let fileName = (string)file["fileName"]
                       let ngenApplication = (string)file["ngenApplication"]
                       where isNgened(file) && isPEFile(fileName)
                       let filePath = fileName.Replace("/Contents/", string.Empty).Replace("/", "\\")
                       let args = (ngenApplication != null) ? $"/ExeConfig:\"{VSInstallationRootVar}{ngenApplication.Replace("[installDir]", string.Empty)}\"" : DefaultInstrumentationArgs
                       select new IbcEntry(filePath, args);
            }
        }
    }
}
