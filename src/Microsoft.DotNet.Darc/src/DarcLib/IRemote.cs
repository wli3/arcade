// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.DotNet.Maestro.Client.Models;

namespace Microsoft.DotNet.DarcLib
{
    public interface IRemote
    {
        Task<Channel> CreateChannelAsync(string name, string classification);

        Task<IEnumerable<Subscription>> GetSubscriptionsAsync(string sourceRepo = null, string targetRepo = null, int? channelId = null);

        Task<Subscription> GetSubscriptionAsync(string subscriptionId);

        Task<Subscription> CreateSubscriptionAsync(string channelName, string sourceRepo, string targetRepo,
            string targetBranch, string updateFrequency, List<MergePolicy> mergePolicies);

        /// <summary>
        /// Delete a subscription by ID.
        /// </summary>
        /// <param name="subscriptionId">Id of subscription to delete.</param>
        /// <returns>Information on deleted subscription</returns>
        Task<Subscription> DeleteSubscriptionAsync(string subscriptionId);

        /// <summary>
        /// Create a pull request to update dependencies.
        /// </summary>
        /// <param name="repoUri">Repository that will receive the pull request.</param>
        /// <param name="targetBranch">Target branch for the pull request.</param>
        /// <param name="buildCommit">Commit that produced the the assets that are being updated</.param>
        /// <param name="buildRepository">Uri of the repo that produced the assets that are being updated.</param>
        /// <param name="buildAssets">Assets to update (if matching in target repo).</param>
        /// <param name="pullRequestSourceBranch">PR base branch (contains the changes desired for @branch).
        /// If ommitted, will be automatically generated based on targetBranch and a Guid.</param>
        /// <param name="pullRequestTitle">Title of the pull request.</param>
        /// <param name="pullRequestDescription">Description of the pull request.</param>
        /// <returns></returns>
        Task<string> CreatePullRequestAsync(string repoUri,
                                            string targetBranch,
                                            string buildCommit,
                                            string buildRepository,
                                            IEnumerable<Microsoft.DotNet.DarcLib.AssetData> buildAssets,
                                            string pullRequestSourceBranch = null,
                                            string pullRequestTitle = null,
                                            string pullRequestDescription = null);

        /// <summary>
        /// Updates an existing pull request with new assets.
        /// </summary>
        /// <param name="pullRequestUrl">Url of existing PR.</param>
        /// <param name="buildCommit">Commit of new build</param>
        /// <param name="buildRepositoryUri">Uri of new build.</param>
        /// <param name="targetBranch">Target branch for commit</param>
        /// <param name="assetsToUpdate">Assets needing updating</param>
        /// <param name="pullRequestTitle">Title to update pull request with.</param>
        /// <param name="pullRequestDescription">Description to update the pull request with</param>
        /// <returns></returns>
        Task<string> UpdatePullRequestAsync(string pullRequestUrl,
                                            string buildCommit,
                                            string buildRepositoryUri,
                                            string targetBranch,
                                            IEnumerable<Microsoft.DotNet.DarcLib.AssetData> assetsToUpdate,
                                            string pullRequestTitle = null,
                                            string pullRequestDescription = null);

        Task MergePullRequestAsync(string pullRequestUrl, MergePullRequestParameters parameters);

        Task<string> CreatePullRequestCommentAsync(string pullRequestUrl, string message);

        Task UpdatePullRequestCommentAsync(string pullRequestUrl, string commentId, string message);

        Task<PrStatus> GetPullRequestStatusAsync(string pullRequestUrl);

        Task<IList<Check>> GetPullRequestChecksAsync(string pullRequestUrl);

        Task<IEnumerable<int>> SearchPullRequestsAsync(string repoUri, string pullRequestBranch, PrStatus status, string keyword = null, string author = null);

        Task<IList<Commit>> GetPullRequestCommitsAsync(string pullRequestUrl);
    }
}
