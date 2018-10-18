// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.DotNet.Maestro.Client;
using Microsoft.DotNet.Maestro.Client.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.DotNet.DarcLib
{
    public class Remote : IRemote
    {
        private readonly IMaestroApi _barClient;
        private readonly GitFileManager _fileManager;
        private readonly IGitRepo _gitClient;
        private readonly ILogger _logger;

        public Remote(DarcSettings settings, ILogger logger)
        {
            ValidateSettings(settings);

            _logger = logger;

            if (settings.GitType == GitRepoType.GitHub)
            {
                _gitClient = new GitHubClient(settings.PersonalAccessToken, _logger);
            }
            else if (settings.GitType == GitRepoType.AzureDevOps)
            {
                _gitClient = new AzureDevOpsClient(settings.PersonalAccessToken, _logger);
            }

            // Only initialize the file manager if we have a git client, which excludes "None"
            if (_gitClient != null)
            {
                _fileManager = new GitFileManager(_gitClient, _logger);
            }

            // Initialize the bar client if there is a password
            if (!string.IsNullOrEmpty(settings.BuildAssetRegistryPassword))
            {
                if (!string.IsNullOrEmpty(settings.BuildAssetRegistryBaseUri))
                {
                    _barClient = ApiFactory.GetAuthenticated(
                        settings.BuildAssetRegistryBaseUri,
                        settings.BuildAssetRegistryPassword);
                }
                else
                {
                    _barClient = ApiFactory.GetAuthenticated(settings.BuildAssetRegistryPassword);
                }
            }
        }

        /// <summary>
        ///     Creates a new channel in the Build Asset Registry
        /// </summary>
        /// <param name="name">Name of channel</param>
        /// <param name="classification">Classification of the channel</param>
        /// <returns>Newly created channel</returns>
        public async Task<Channel> CreateChannelAsync(string name, string classification)
        {
            CheckForValidBarClient();
            return await _barClient.Channels.CreateChannelAsync(name, classification);
        }

        public async Task<IEnumerable<Subscription>> GetSubscriptionsAsync(
            string sourceRepo = null,
            string targetRepo = null,
            int? channelId = null)
        {
            CheckForValidBarClient();
            return await _barClient.Subscriptions.GetAllSubscriptionsAsync(sourceRepo, targetRepo, channelId);
        }

        public async Task<Subscription> GetSubscriptionAsync(string subscriptionId)
        {
            CheckForValidBarClient();
            if (!Guid.TryParse(subscriptionId, out Guid subscriptionGuid))
            {
                throw new ArgumentException($"Subscription id '{subscriptionId}' is not a valid guid.");
            }

            return await _barClient.Subscriptions.GetSubscriptionAsync(subscriptionGuid);
        }

        /// <summary>
        ///     Create a new subscription
        /// </summary>
        /// <param name="channelName">Name of source channel</param>
        /// <param name="sourceRepo">URL of source repository</param>
        /// <param name="targetRepo">URL of target repository where updates should be made</param>
        /// <param name="targetBranch">Name of target branch where updates should be made</param>
        /// <param name="updateFrequency">Frequency of updates, can be 'none', 'everyBuild' or 'everyDay'</param>
        /// <param name="mergePolicies">
        ///     Dictionary of merge policies. Each merge policy is a name of a policy with an associated blob
        ///     of metadata
        /// </param>
        /// <returns>Newly created subscription, if successful</returns>
        public async Task<Subscription> CreateSubscriptionAsync(
            string channelName,
            string sourceRepo,
            string targetRepo,
            string targetBranch,
            string updateFrequency,
            List<MergePolicy> mergePolicies)
        {
            CheckForValidBarClient();
            var subscriptionData = new SubscriptionData
            {
                ChannelName = channelName,
                SourceRepository = sourceRepo,
                TargetRepository = targetRepo,
                TargetBranch = targetBranch,
                Policy = new SubscriptionPolicy {UpdateFrequency = updateFrequency, MergePolicies = mergePolicies}
            };
            return await _barClient.Subscriptions.CreateAsync(subscriptionData);
        }

        /// <summary>
        ///     Delete a subscription by id
        /// </summary>
        /// <param name="subscriptionId">Id of subscription to delete</param>
        /// <returns>Information on deleted subscriptio</returns>
        public async Task<Subscription> DeleteSubscriptionAsync(string subscriptionId)
        {
            CheckForValidBarClient();
            if (!Guid.TryParse(subscriptionId, out Guid subscriptionGuid))
            {
                throw new ArgumentException($"Subscription id '{subscriptionId}' is not a valid guid.");
            }

            return await _barClient.Subscriptions.DeleteSubscriptionAsync(subscriptionGuid);
        }

        /// <summary>
        ///     Create a pull request to update dependencies.
        /// </summary>
        /// <param name="targetRepositoryUri">Repository that will receive the pull request.</param>
        /// <param name="targetBranch">Target branch for the pull request.</param>
        /// <param name="buildCommit">Commit that produced the the assets that are being updated</.param>
        /// <param name="buildRepositoryUri">Uri of the repo that produced the assets that are being updated.</param>
        /// <param name="buildAssets">Assets to update (if matching in target repo).</param>
        /// <param name="sourceBranch">PR source branch (contains the changes desired for @targetBranch).</param>
        /// <param name="pullRequestTitle">Title of the pull request.</param>
        /// <param name="pullRequestDescription">Description of the pull request.</param>
        /// <returns></returns>
        public async Task<string> CreatePullRequestAsync(
            string targetRepositoryUri,
            string targetBranch,
            string buildCommit,
            string buildRepositoryUri,
            IEnumerable<AssetData> buildAssets,
            string sourceBranch = null,
            string pullRequestTitle = null,
            string pullRequestDescription = null)
        {
            CheckForValidGitClient();
            _logger.LogInformation(
                $"Create pull request to update dependencies in repo '{targetRepositoryUri}' and branch '{targetBranch}'...");

            // Get the items to update based on the target branch.
            IEnumerable<DependencyDetail> itemsToUpdate = await GetRequiredUpdatesAsync(
                targetRepositoryUri,
                targetBranch,
                buildCommit,
                buildRepositoryUri,
                buildAssets);

            string linkToPr = null;

            if (itemsToUpdate.Any())
            {
                // Determine the name of the base branch if it is not provided.
                // Base branch must be unique because darc could have multiple PRs open in the same repo at the same time
                sourceBranch = sourceBranch ?? $"darc-{targetBranch}-{Guid.NewGuid()}";

                await _gitClient.CreateBranchAsync(targetRepositoryUri, sourceBranch, targetBranch);

                // Once the branch is created or updated based on targetBranch, updating the branch should be
                // done based on the information in that branch, not the target branch.
                await CommitFilesForPullRequestAsync(targetRepositoryUri, targetBranch, buildCommit, itemsToUpdate);

                linkToPr = await _gitClient.CreatePullRequestAsync(
                    targetRepositoryUri,
                    targetBranch,
                    sourceBranch,
                    pullRequestTitle,
                    pullRequestDescription);

                _logger.LogInformation(
                    $"Updating dependencies in repo '{targetRepositoryUri}' and branch '{targetBranch}' succeeded! PR link is: {linkToPr}");

                return linkToPr;
            }

            return linkToPr;
        }

        public async Task<IList<Check>> GetPullRequestChecksAsync(string pullRequestUrl)
        {
            CheckForValidGitClient();
            _logger.LogInformation($"Getting status checks for pull request '{pullRequestUrl}'...");

            IList<Check> checks = await _gitClient.GetPullRequestChecksAsync(pullRequestUrl);

            return checks;
        }

        public async Task<IEnumerable<int>> SearchPullRequestsAsync(
            string repoUri,
            string pullRequestBranch,
            PrStatus status,
            string keyword = null,
            string author = null)
        {
            CheckForValidGitClient();
            return await _gitClient.SearchPullRequestsAsync(repoUri, pullRequestBranch, status, keyword, author);
        }

        public Task<IList<Commit>> GetPullRequestCommitsAsync(string pullRequestUrl)
        {
            CheckForValidGitClient();
            return _gitClient.GetPullRequestCommitsAsync(pullRequestUrl);
        }

        public Task<string> CreatePullRequestCommentAsync(string pullRequestUrl, string message)
        {
            CheckForValidGitClient();
            return _gitClient.CreatePullRequestCommentAsync(pullRequestUrl, message);
        }

        public Task UpdatePullRequestCommentAsync(string pullRequestUrl, string commentId, string message)
        {
            CheckForValidGitClient();
            return _gitClient.UpdatePullRequestCommentAsync(pullRequestUrl, commentId, message);
        }

        public async Task<PrStatus> GetPullRequestStatusAsync(string pullRequestUrl)
        {
            CheckForValidGitClient();
            _logger.LogInformation($"Getting the status of pull request '{pullRequestUrl}'...");

            PrStatus status = await _gitClient.GetPullRequestStatusAsync(pullRequestUrl);

            _logger.LogInformation($"Status of pull request '{pullRequestUrl}' is '{status}'");

            return status;
        }

        /// <summary>
        ///     Updates an existing pull request with new assets.
        /// </summary>
        /// <param name="pullRequestUrl">Url of existing PR.</param>
        /// <param name="buildCommit">Commit of new build</param>
        /// <param name="buildRepositoryUri">Uri of new build.</param>
        /// <param name="targetBranch">Target branch for commit</param>
        /// <param name="assetsToUpdate">Assets needing updating</param>
        /// <param name="pullRequestTitle">Title to update pull request with.</param>
        /// <param name="pullRequestDescription">Description to update the pull request with</param>
        /// <returns></returns>
        public async Task<string> UpdatePullRequestAsync(
            string pullRequestUrl,
            string buildCommit,
            string buildRepositoryUri,
            string targetBranch,
            IEnumerable<AssetData> assetsToUpdate,
            string pullRequestTitle = null,
            string pullRequestDescription = null)
        {
            CheckForValidGitClient();
            _logger.LogInformation($"Updating pull request '{pullRequestUrl}'...");

            string linkToPr = null;

            string repoUri = await _gitClient.GetPullRequestRepo(pullRequestUrl);
            string pullRequestSourceBranch = await _gitClient.GetPullRequestSourceBranch(pullRequestUrl);

            // TODO: Update the source branch here?

            // When we update, do so based on the pull request source branch, as it may have drifted from target branch.
            IEnumerable<DependencyDetail> itemsToUpdate = await GetRequiredUpdatesAsync(
                repoUri,
                pullRequestSourceBranch,
                buildCommit,
                buildRepositoryUri,
                assetsToUpdate);

            await CommitFilesForPullRequestAsync(repoUri, targetBranch, buildCommit, itemsToUpdate);

            linkToPr = await _gitClient.UpdatePullRequestAsync(
                pullRequestUrl,
                targetBranch,
                pullRequestSourceBranch,
                pullRequestTitle,
                pullRequestDescription);

            _logger.LogInformation(
                $"Updating dependencies in repo '{repoUri}' and branch '{targetBranch}' succeeded! PR link is: {linkToPr}");

            return linkToPr;
        }

        public async Task MergePullRequestAsync(string pullRequestUrl, MergePullRequestParameters parameters)
        {
            CheckForValidGitClient();
            _logger.LogInformation($"Merging pull request '{pullRequestUrl}'...");

            await _gitClient.MergePullRequestAsync(pullRequestUrl, parameters ?? new MergePullRequestParameters());

            _logger.LogInformation($"Merging pull request '{pullRequestUrl}' succeeded!");
        }

        /// <summary>
        ///     Retrieve the list of channels from the build asset registry.
        /// </summary>
        /// <param name="classification">Optional classification to get</param>
        /// <returns></returns>
        public async Task<IEnumerable<Channel>> GetChannelsAsync(string classification = null)
        {
            CheckForValidBarClient();
            return await _barClient.Channels.GetAsync(classification);
        }

        /// <summary>
        ///     Called prior to operations requiring the BAR.  Throws if a bar client isn't available.
        /// </summary>
        private void CheckForValidBarClient()
        {
            if (_barClient == null)
            {
                throw new ArgumentException("Must supply a build asset registry password");
            }
        }

        /// <summary>
        ///     Called prior to operations requiring the BAR.  Throws if a git client isn't available;
        /// </summary>
        private void CheckForValidGitClient()
        {
            if (_gitClient == null)
            {
                throw new ArgumentException("Must supply a valid GitHub/Azure DevOps PAT");
            }
        }

        private void ValidateSettings(DarcSettings settings)
        {
            // Should have a git repo type of AzureDevOps, GitHub, or None.
            if (settings.GitType == GitRepoType.GitHub || settings.GitType == GitRepoType.AzureDevOps)
            {
                // PAT is required for these types.
                if (string.IsNullOrEmpty(settings.PersonalAccessToken))
                {
                    throw new ArgumentException("The personal access token is missing...");
                }
            }
            else if (settings.GitType != GitRepoType.None)
            {
                throw new ArgumentException($"Unexpected git repo type: {settings.GitType}");
            }
        }

        /// <summary>
        ///     Determine the dependencies in the target repository that need updates based on the
        ///     assets produced in a build.
        /// </summary>
        /// <param name="repoUri">Repository containing the targeted for update</param>
        /// <param name="branch">Branch targetd for update</param>
        /// <param name="buildCommit">Commit that produced the the assets may need update.</.param>
        /// <param name="buildRepositoryUri">Uri of the repo that produced the assets that may need update.</param>
        /// <param name="buildAssets">Assets that may need update.</param>
        /// <returns>List of updated dependency elements.</returns>
        private async Task<IEnumerable<DependencyDetail>> GetRequiredUpdatesAsync(
            string repoUri,
            string branch,
            string buildCommit,
            string buildRepositoryUri,
            IEnumerable<AssetData> buildAssets)
        {
            CheckForValidGitClient();
            _logger.LogInformation($"Check if repo '{repoUri}' and branch '{branch}' needs updates...");

            var toUpdate = new List<DependencyDetail>();
            IEnumerable<DependencyDetail> dependencyDetails =
                await _fileManager.ParseVersionDetailsXmlAsync(repoUri, branch);

            foreach (DependencyDetail dependency in dependencyDetails)
            {
                // Compare asset name case insensitively, but dependency update below will reset the name to the 
                // new asset name, to account for cases where the asset name casing might be corrected.
                AssetData asset = buildAssets
                    .Where(a => a.Name.Equals(dependency.Name, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();

                if (asset == null)
                {
                    _logger.LogInformation($"Dependency '{dependency.Name}' not found in the updated assets...");
                    continue;
                }

                // Update the dependency with the asset info from the build.
                dependency.Version = asset.Version;
                dependency.Name = asset.Name;
                dependency.Commit = buildCommit;
                dependency.RepoUri = buildRepositoryUri;
                toUpdate.Add(dependency);
            }

            _logger.LogInformation(
                $"Getting dependencies which need to be updated in repo '{repoUri}' and branch '{branch}' succeeded!");

            return toUpdate;
        }

        private async Task<List<GitFile>> GetScriptFilesAsync(string repoUri, string commit)
        {
            CheckForValidGitClient();
            _logger.LogInformation("Generating commits for script files");

            List<GitFile> files = await _gitClient.GetFilesForCommitAsync(repoUri, commit, "eng/common");

            _logger.LogInformation("Generating commits for script files succeeded!");

            return files;
        }

        /// <summary>
        ///     Commit updates of files to a branch.
        /// </summary>
        /// <param name="repoUri">Repository containing branch to update</param>
        /// <param name="branch">Branch to update</param>
        /// <param name="buildCommit">Commit of the build containing the new assets.</param>
        /// <param name="itemsToUpdate">Items that need updating.</param>
        /// <returns></returns>
        private async Task CommitFilesForPullRequestAsync(
            string repoUri,
            string branch,
            string buildCommit,
            IEnumerable<DependencyDetail> itemsToUpdate)
        {
            CheckForValidGitClient();
            GitFileContentContainer fileContainer =
                await _fileManager.UpdateDependencyFiles(itemsToUpdate, repoUri, branch);
            List<GitFile> filesToCommit = fileContainer.GetFilesToCommitMap();

            // If there is an arcade asset that we need to update we try to update the script files as well
            DependencyDetail arcadeItem =
                itemsToUpdate.Where(i => i.Name.ToLower().Contains("arcade")).FirstOrDefault();

            if (arcadeItem != null && repoUri != arcadeItem.RepoUri)
            {
                List<GitFile> engCommonsFiles = await GetScriptFilesAsync(arcadeItem.RepoUri, buildCommit);
                filesToCommit.AddRange(engCommonsFiles);
            }

            await _gitClient.PushFilesAsync(filesToCommit, repoUri, branch, "Updating version files");
        }
    }
}
