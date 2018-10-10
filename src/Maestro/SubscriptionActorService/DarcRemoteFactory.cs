// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Maestro.GitHub;
using Microsoft.DotNet.DarcLib;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SubscriptionActorService
{
    public class DarcRemoteFactory : IDarcRemoteFactory
    {
        public DarcRemoteFactory(
            ILoggerFactory loggerFactory,
            IConfigurationRoot configuration,
            IGitHubTokenProvider gitHubTokenProvider)
        {
            LoggerFactory = loggerFactory;
            Configuration = configuration;
            GitHubTokenProvider = gitHubTokenProvider;
        }

        public ILoggerFactory LoggerFactory { get; }
        public IConfigurationRoot Configuration { get; }
        public IGitHubTokenProvider GitHubTokenProvider { get; }

        // Variety of forms are available for the account repo URI.
        // https://dev.azure.com/dnceng/internal/_git/dotnet-arcade
        // https://dnceng@dev.azure.com/dnceng/internal/_git/dotnet-arcade
        private const string accountNameCaptureName = "accountName";
        private static readonly string form1 = $"(^https:\\/\\/)?dev\\.azure\\.com\\/(?<{accountNameCaptureName}>.+?)\\/";
        private static readonly string form2 = $"(^https:\\/\\/)?(?<{accountNameCaptureName}>.+)@dev\\.azure\\.com";

        public async Task<IRemote> CreateAsync(string repoUrl, long installationId)
        {
            var settings = new DarcSettings();
            if (repoUrl.Contains("github.com"))
            {
                if (installationId == default)
                {
                    throw new SubscriptionException($"No installation is avaliable for repository '{repoUrl}'");
                }

                settings.GitType = GitRepoType.GitHub;
                settings.PersonalAccessToken = await GitHubTokenProvider.GetTokenForInstallation(installationId);
            }
            // The PAT will generally be different based on the installation, and is obtained from KeyVault.
            else if (repoUrl.Contains("dev.azure.com"))
            {
                string accountName = null;

                List<Regex> uriRegex = new List<Regex>()
                {
                    new Regex(form1),
                    new Regex(form2)
                };

                // Match available regexes.
                foreach (Regex regex in uriRegex)
                {
                    Match m = regex.Match(repoUrl);
                    // If we are successful, then we should have all of the named capture groups.
                    if (m.Success)
                    {
                        if (!m.Groups[accountNameCaptureName].Success)
                        {
                            throw new SubscriptionException($"Repository URI should be of the form dev.azure.com/<accountName>/<projectName>/_git/<repoName> " +
                                $"or <accountName>@dev.azure.com/<projectName>/_git/<repoName>");
                        }
                        accountName = m.Groups[accountNameCaptureName].Value;
                        break;
                    }
                }

                if (accountName == null)
                {
                    throw new SubscriptionException($"Repository URI should be of the form dev.azure.com/<accountName>/<projectName>/_git/<repoName> " +
                                $"or <accountName>@dev.azure.com/<projectName>/_git/<repoName>");
                }

                settings.GitType = GitRepoType.AzureDevOps;
                // Grab the PAT from the AzureDevOpsPATs section of the configuration
                settings.PersonalAccessToken = Configuration.GetSection("AzureDevOpsPATs")[accountName];
                // If PAT is empty, throw
                if (string.IsNullOrEmpty(settings.PersonalAccessToken))
                {
                    throw new SubscriptionException($"PAT is not available for Azure DevOps Account '{accountName}'." +
                        $"Ensure that that a key with name '{accountName}' exists in configuration section 'AzureDevOpsPATs'");
                }
            }
            else
            {
                throw new SubscriptionException($"Could not identify the git repository type for repository URL '{repoUrl}'");
            }

            return new Remote(settings, LoggerFactory.CreateLogger<Remote>());
        }
    }
}
