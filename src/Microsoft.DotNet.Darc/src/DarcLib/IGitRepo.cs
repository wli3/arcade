// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Threading.Tasks;

namespace Microsoft.DotNet.DarcLib
{
    public interface IGitRepo
    {
        /// <summary>
        ///     Create a new branch in a repo.
        /// </summary>
        /// <param name="repoUri">Repository to create branch in.</param>
        /// <param name="newBranch">New branch name.</param>
        /// <param name="baseBranch">Branch to create @newBranch off of.</param>
        /// <returns></returns>
        Task CreateBranchAsync(string repoUri, string newBranch, string baseBranch);

        /// <summary>
        ///     Commit new changes to a repo
        /// </summary>
        /// <param name="filesToCommit">Files to update</param>
        /// <param name="repoUri">Repository uri to push changes to</param>
        /// <param name="branch">Repository branch to push changes to</param>
        /// <param name="commitMessage">Commit message for new commit.</param>
        /// <returns></returns>
        Task PushFilesAsync(List<GitFile> filesToCommit, string repoUri, string branch, string commitMessage);

        Task<IEnumerable<int>> SearchPullRequestsAsync(
            string repoUri,
            string pullRequestBranch,
            PrStatus status,
            string keyword = null,
            string author = null);

        Task<PrStatus> GetPullRequestStatusAsync(string pullRequestUrl);

        Task<string> GetPullRequestRepo(string pullRequestUrl);

        Task<string> CreatePullRequestAsync(
            string repoUri,
            string targetBranch,
            string sourceBranch,
            string title = null,
            string description = null);

        Task<string> UpdatePullRequestAsync(
            string pullRequestUri,
            string targetBranch,
            string sourceBranch,
            string title = null,
            string description = null);

        Task MergePullRequestAsync(string pullRequestUrl, MergePullRequestParameters parameters);

        Task<string> CreatePullRequestCommentAsync(string pullRequestUrl, string message);

        Task UpdatePullRequestCommentAsync(string pullRequestUrl, string commentId, string message);

        Task<List<GitFile>> GetFilesForCommitAsync(string repoUri, string commit, string path);

        /// <summary>
        ///     Retrieve the contents of a file in a repository.
        /// </summary>
        /// <param name="filePath">Path of file (relative to repo root)</param>
        /// <param name="repoUri">URI of repo containing the file.</param>
        /// <param name="branchOrCommit">Branch or commit to obtain file from.</param>
        /// <returns>Content of file.</returns>
        Task<string> GetFileContentsAsync(string filePath, string repoUri, string branchOrCommit);

        /// <summary>
        ///     Get the latest commit sha on a given branch for a repo
        /// </summary>
        /// <param name="repoUri">Repository to get latest commit in.</param>
        /// <param name="branch">Branch to get get latest commit in.</param>
        /// <returns>Latest commit sha.</returns>
        Task<string> GetLastCommitShaAsync(string repoUri, string branch);

        /// <summary>
        ///     Determine whether a file exists at a specified branch or commit.
        /// </summary>
        /// <param name="repoUri">Repository uri</param>
        /// <param name="filePath">Path of file</param>
        /// <param name="branch">Branch or commit in to check.</param>
        /// <returns>True if the file exists, false otherwise.</returns>
        Task<bool> FileExistsAsync(string repoUri, string filePath, string branch);

        Task<IList<Check>> GetPullRequestChecksAsync(string pullRequestUrl);

        /// <summary>
        ///     Get the source branch for the pull request.
        /// </summary>
        /// <param name="pullRequestUrl">url of pull request</param>
        /// <returns>Branch of pull request.</returns>
        Task<string> GetPullRequestSourceBranch(string pullRequestUrl);

        Task<IList<Commit>> GetPullRequestCommitsAsync(string pullRequestUrl);
    }
}
