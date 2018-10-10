// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;

namespace Microsoft.DotNet.DarcLib
{
    public class AzureDevOpsClient : IGitRepo
    {
        private const string DefaultApiVersion = "5.0-preview.1";
        private readonly string _personalAccessToken;
        private readonly ILogger _logger;
        private readonly JsonSerializerSettings _serializerSettings;

        public AzureDevOpsClient(string accessToken, ILogger logger)
        {
            _personalAccessToken = accessToken;
            _logger = logger;
            _serializerSettings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore
            };
        }

        /// <summary>
        /// Executes a remote git command on Azure DevOps.
        /// </summary>
        /// <param name="accountName">Account name used for request</param>
        /// <param name="projectName">Project name used for the request</param>
        /// <param name="apiPath">Path to access (relative to base URI).</param>
        /// <returns></returns>
        private async Task<HttpResponseMessage> ExecuteRemoteGitCommand(HttpMethod method, string accountName,
                                                                        string projectName, string apiPath, ILogger logger,
                                                                        string body = null, string versionOverride = null)
        {
            // Construct the API URI
            string baseUri = "https://dev.azure.com/{accountName}/{projectName}/_apis/";
            using (HttpClient client = CreateHttpClient(baseUri, versionOverride))
            {
                HttpRequestManager requestManager = new HttpRequestManager(client, method, apiPath, logger, body, versionOverride);

                return await requestManager.ExecuteAsync();
            }
        }

        /// <summary>
        /// Create a new Http client for talking to Azure DevOps
        /// </summary>
        /// <param name="versionOverride">Override the API version.  Defaults to DefaultApiVersion</param>
        /// <param name="apiUri">Base URI of the API</param>
        /// <returns>New http client</returns>
        /// <remarks>Should we be doing this? Typically the number of HTTP clients is supposed to be kept to a minimum,
        /// and reused for best performance. An alternative would be to keep the HttpClient and base uri on the client,
        /// and check that the repo URIs passed to various methods either do not begin with the base URI or have the same 
        /// base URI.</remarks>
        private HttpClient CreateHttpClient(string apiUri, string versionOverride = null)
        {
            HttpClient client = new HttpClient
            {
                BaseAddress = new Uri(apiUri)
            };
            client.DefaultRequestHeaders.Add("Accept", $"application/json;api-version={versionOverride ?? DefaultApiVersion}");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes(string.Format("{0}:{1}", "", _personalAccessToken))));

            return client;
        }

        /// <summary>
        /// Create a visual studio services connection, typically used for manipulating pull requests
        /// </summary>
        /// <param name="uri">URI of pull request or other Azure DevOps item.</param>
        /// <returns>New VssConnection object</returns>
        private VssConnection CreateVssConnection(string uri)
        {
            var collectionUri = new UriBuilder(uri)
            {
                Path = "",
                Query = "",
                Fragment = ""
            };
            var creds = new VssCredentials(new VssBasicCredential("", _personalAccessToken));
            return new VssConnection(collectionUri.Uri, creds);
        }

        /// <summary>
        /// Retrieve the contents of a file in a repository.
        /// </summary>
        /// <param name="filePath">Path of file (relative to repo root)</param>
        /// <param name="repoUri">URI of repo containing the file.</param>
        /// <param name="branchOrCommit">Branch or commit to obtain file from.</param>
        /// <returns>Content of file.</returns>
        public async Task<string> GetFileContentsAsync(string filePath, string repoUri, string branchOrCommit)
        {
            _logger.LogInformation($"Getting the contents of file '{filePath}' from repo '{repoUri}' in branch '{branchOrCommit}'...");

            var (accountName, projectName, repositoryName) = ParseRepositoryUri(repoUri);

            HttpResponseMessage response = await this.ExecuteRemoteGitCommand(HttpMethod.Get, accountName, projectName,
                $"git/repositories/{repositoryName}/items?path={filePath}&version={branchOrCommit}&includeContent=true", _logger);

            _logger.LogInformation($"Getting the contents of file '{filePath}' from repo '{repoUri}' in branch '{branchOrCommit}' succeeded!");

            JObject responseContent = JObject.Parse(await response.Content.ReadAsStringAsync());

            return responseContent["content"].ToString();
        }

        /// <summary>
        /// Create a new branch in a repo.
        /// </summary>
        /// <param name="repoUri">Repository to create branch in.</param>
        /// <param name="newBranch">New branch name.</param>
        /// <param name="baseBranch">Branch to create @newBranch off of.</param>
        /// <returns></returns>
        public async Task CreateBranchAsync(string repoUri, string newBranch, string baseBranch)
        {
            var (accountName, projectName, repoName) = ParseRepositoryUri(repoUri);
            string body;

            List<AzureDevOpsRef> azureDevOpsRefs = new List<AzureDevOpsRef>();
            AzureDevOpsRef azureDevOpsRef;
            HttpResponseMessage response = null;

            string latestSha = await GetLastCommitShaAsync(repoName, baseBranch);

            response = await this.ExecuteRemoteGitCommand(HttpMethod.Get, accountName, projectName, $"git/repositories/{repoName}/refs/heads/{newBranch}", _logger);
            JObject responseContent = JObject.Parse(await response.Content.ReadAsStringAsync());

            // Azure DevOps doesn't fail with a 404 if a branch does not exist, it just returns an empty response object...
            if (responseContent["count"].ToObject<int>() == 0)
            {
                _logger.LogInformation($"'{newBranch}' branch doesn't exist. Creating it...");

                azureDevOpsRef = new AzureDevOpsRef($"refs/heads/{newBranch}", latestSha);
                azureDevOpsRefs.Add(azureDevOpsRef);
            }
            else
            {
                _logger.LogInformation($"Branch '{newBranch}' exists, making sure it is in sync with '{baseBranch}'...");

                string oldSha = await GetLastCommitShaAsync(repoName, $"{newBranch}");

                azureDevOpsRef = new AzureDevOpsRef($"refs/heads/{newBranch}", latestSha, oldSha);
                azureDevOpsRefs.Add(azureDevOpsRef);
            }

            body = JsonConvert.SerializeObject(azureDevOpsRefs, _serializerSettings);
            await this.ExecuteRemoteGitCommand(HttpMethod.Post, accountName, projectName, $"git/repositories/{repoName}/refs", _logger, body);
        }

        /// <summary>
        /// Commit new changes to a repo
        /// </summary>
        /// <param name="filesToCommit">Files to update</param>
        /// <param name="repoUri">Repository uri to push changes to</param>
        /// <param name="branch">Repository branch to push changes to</param>
        /// <param name="commitMessage">Commit message for new commit.</param>
        /// <returns></returns>
        public async Task PushFilesAsync(List<GitFile> filesToCommit, string repoUri, string branch, string commitMessage)
        {
            _logger.LogInformation($"Pushing files to '{branch}'...");

            List<AzureDevOpsChange> changes = new List<AzureDevOpsChange>();
            var (accountName, projectName, repoName) = ParseRepositoryUri(repoUri);

            foreach (GitFile gitfile in filesToCommit)
            {
                bool exists = await FileExistsAsync(repoUri, gitfile.FilePath, branch);

                AzureDevOpsChange change = new AzureDevOpsChange(gitfile.FilePath, gitfile.Content);

                if (exists)
                {
                    change.ChangeType = AzureDevOpsChangeType.Edit;
                }

                changes.Add(change);
            }

            AzureDevOpsCommit commit = new AzureDevOpsCommit(changes, "Dependency files update");

            string latestSha = await GetLastCommitShaAsync(repoName, branch);
            AzureDevOpsRefUpdate refUpdate = new AzureDevOpsRefUpdate($"refs/heads/{branch}", latestSha);

            AzureDevOpsPush azureDevOpsPush = new AzureDevOpsPush(refUpdate, commit);

            string body = JsonConvert.SerializeObject(azureDevOpsPush, _serializerSettings);

            // Azure DevOps' contents API is only supported in version 5.0-preview.2
            await this.ExecuteRemoteGitCommand(HttpMethod.Post, accountName, projectName, $"git/repositories/{repoName}/pushes", _logger, body, "5.0-preview.2");

            _logger.LogInformation($"Pushing files to '{branch}' succeeded!");
        }

        public async Task<IEnumerable<int>> SearchPullRequestsAsync(string repoUri, string pullRequestBranch, PrStatus status, string keyword = null, string author = null)
        {
            var (accountName, projectName, repoName) = ParseRepositoryUri(repoUri);
            StringBuilder query = new StringBuilder();
            AzureDevOpsPrStatus prStatus;

            switch (status)
            {
                case PrStatus.Open:
                    prStatus = AzureDevOpsPrStatus.Active;
                    break;
                case PrStatus.Closed:
                    prStatus = AzureDevOpsPrStatus.Abandoned;
                    break;
                case PrStatus.Merged:
                    prStatus = AzureDevOpsPrStatus.Completed;
                    break;
                default:
                    prStatus = AzureDevOpsPrStatus.None;
                    break;
            }

            query.Append($"searchCriteria.sourceRefName=refs/heads/{pullRequestBranch}&searchCriteria.status={prStatus.ToString().ToLower()}");

            if (!string.IsNullOrEmpty(keyword))
            {
                _logger.LogInformation("A keyword was provided but Azure DevOps doesn't support searching for PRs based on keywords and it won't be used...");
            }

            if (!string.IsNullOrEmpty(author))
            {
                query.Append($"&searchCriteria.creatorId={author}");
            }

            HttpResponseMessage response = await this.ExecuteRemoteGitCommand(HttpMethod.Get, accountName, projectName, 
                $"git/repositories/{repoName}/pullrequests?{query.ToString()}", _logger);

            JObject content = JObject.Parse(await response.Content.ReadAsStringAsync());
            JArray values = JArray.Parse(content["value"].ToString());

            IEnumerable<int> prs = values.Select(r => r["pullRequestId"].ToObject<int>());

            return prs;
        }

        public async Task<PrStatus> GetPullRequestStatusAsync(string pullRequestUrl)
        {
            var (accountName, projectName, id) = ParsePullRequestUri(pullRequestUrl);

            HttpResponseMessage response = await this.ExecuteRemoteGitCommand(HttpMethod.Get, null, null, pullRequestUrl, _logger);

            JObject responseContent = JObject.Parse(await response.Content.ReadAsStringAsync());

            if (Enum.TryParse(responseContent["status"].ToString(), true, out AzureDevOpsPrStatus status))
            {
                if (status == AzureDevOpsPrStatus.Active)
                {
                    return PrStatus.Open;
                }

                if (status == AzureDevOpsPrStatus.Completed)
                {
                    return PrStatus.Merged;
                }

                if (status == AzureDevOpsPrStatus.Abandoned)
                {
                    return PrStatus.Closed;
                }
            }

            return PrStatus.None;
        }

        public async Task<string> GetPullRequestRepo(string pullRequestUrl)
        {
            string url = GetPrPartialAbsolutePath(pullRequestUrl);

            HttpResponseMessage response = await this.ExecuteRemoteGitCommand(HttpMethod.Get, null,  null, url, _logger);

            JObject responseContent = JObject.Parse(await response.Content.ReadAsStringAsync());

            return responseContent["repository"]["remoteUrl"].ToString();
        }

        public async Task<string> CreatePullRequestAsync(string repoUri, string targetBranch, string sourceBranch, string title = null, string description = null)
        {
            string linkToPullRquest = await CreateOrUpdatePullRequestAsync(repoUri, targetBranch, sourceBranch, HttpMethod.Post,title, description);
            return linkToPullRquest;
        }

        public async Task<string> UpdatePullRequestAsync(string pullRequestUri, string targetBranch, string sourceBranch, string title = null, string description = null)
        {
            string linkToPullRquest = await CreateOrUpdatePullRequestAsync(pullRequestUri, targetBranch, sourceBranch, new HttpMethod("PATCH"), title, description);
            return linkToPullRquest;
        }

        public async Task MergePullRequestAsync(string pullRequestUrl, MergePullRequestParameters parameters)
        {
            var connection = CreateVssConnection(pullRequestUrl);
            var client = await connection.GetClientAsync<GitHttpClient>();

            var (team, repo, id) = ParsePullRequestUri(pullRequestUrl);

            await client.UpdatePullRequestAsync(
                new GitPullRequest
                {
                    Status = PullRequestStatus.Completed,
                    CompletionOptions = new GitPullRequestCompletionOptions
                    {
                        SquashMerge = parameters.SquashMerge,
                        DeleteSourceBranch = parameters.DeleteSourceBranch,
                    },
                    LastMergeSourceCommit = new GitCommitRef
                    {
                        CommitId = parameters.CommitToMerge
                    },
                },
                repo,
                id);
        }

        public async Task<string> CreatePullRequestCommentAsync(string pullRequestUrl, string message)
        {
            var connection = CreateVssConnection(pullRequestUrl);
            var client = await connection.GetClientAsync<GitHttpClient>();

            var (team, repo, id) = ParsePullRequestUri(pullRequestUrl);

            var thread = await client.CreateThreadAsync(new GitPullRequestCommentThread
            {
                Comments = new List<Comment>
                {
                    new Comment
                    {
                        CommentType = CommentType.Text,
                        Content = message,
                    },
                },
            }, team, repo, id);

            return thread.Id + "-" + thread.Comments.First().Id;
        }

        public async Task UpdatePullRequestCommentAsync(string pullRequestUrl, string commentId, string message)
        {
            var (threadId, commentIdValue) = ParseCommentId(commentId);

            var connection = CreateVssConnection(pullRequestUrl);
            var client = await connection.GetClientAsync<GitHttpClient>();

            var (team, repo, id) = ParsePullRequestUri(pullRequestUrl);

            await client.UpdateCommentAsync(new Comment
            {
                CommentType = CommentType.Text,
                Content = message,
            }, team, repo, id, threadId, commentIdValue);
        }

        private (int threadId, int commentId) ParseCommentId(string commentId)
        {
            var parts = commentId.Split('-');
            if (parts.Length != 2 ||
                int.TryParse(parts[0], out int threadId) ||
                int.TryParse(parts[1], out int commentIdValue))
            {
                throw new ArgumentException("The comment id '{commentId}' is in an invalid format", nameof(commentId));
            }

            return (threadId, commentIdValue);
        }

        public async Task CommentOnPullRequestAsync(string pullRequestUrl, string message)
        {
            // TODO: This is currently wrong.
            var (accountName, projectName, repoName) = ParseRepositoryUri(pullRequestUrl);
            List<AzureDevOpsCommentBody> comments = new List<AzureDevOpsCommentBody>
            {
                new AzureDevOpsCommentBody(message)
            };

            AzureDevOpsComment comment = new AzureDevOpsComment(comments);

            string body = JsonConvert.SerializeObject(comment, _serializerSettings);

            await this.ExecuteRemoteGitCommand(HttpMethod.Post, accountName, projectName, $"{pullRequestUrl}/threads", _logger, body);
        }

        public async Task<List<GitFile>> GetFilesForCommitAsync(string repoUri, string commit, string path)
        {
            List<GitFile> files = new List<GitFile>();

            await GetCommitMapForPathAsync(repoUri, commit, files, path);

            return files;
        }

        public async Task GetCommitMapForPathAsync(string repoUri, string commit, List<GitFile> files, string path)
        {
            _logger.LogInformation($"Getting the contents of file/files in '{path}' of repo '{repoUri}' at commit '{commit}'");

            var (accountName, projectName, repoName) = ParseRepositoryUri(repoUri);

            HttpResponseMessage response = await this.ExecuteRemoteGitCommand(HttpMethod.Get, accountName, projectName, 
                $"git/repositories/{repoName}/items?scopePath={path}&version={commit}&includeContent=true&versionType=commit&recursionLevel=full", _logger);

            JObject content = JObject.Parse(await response.Content.ReadAsStringAsync());
            List<AzureDevOpsItem> items = JsonConvert.DeserializeObject<List<AzureDevOpsItem>>(Convert.ToString(content["value"]));

            foreach (AzureDevOpsItem item in items)
            {
                if (!item.IsFolder)
                {
                    if (!GitFileManager.DependencyFiles.Contains(item.Path))
                    {
                        string fileContent = await GetFileContentsAsync(item.Path, repoUri, commit);
                        GitFile gitCommit = new GitFile(item.Path, fileContent);
                        files.Add(gitCommit);
                    }
                }
            }

            _logger.LogInformation($"Getting the contents of file/files in '{path}' of repo '{repoUri}' at commit '{commit}' succeeded!");
        }

        /// <summary>
        /// Get the latest commit sha on a given branch for a repo
        /// </summary>
        /// <param name="repoUri">Repository to get latest commit for.</param>
        /// <param name="branch">Branch to get get latest commit for.</param>
        /// <returns>Latest commit sha.</returns>
        /// <remarks>Throws if no commits were found.</remarks>
        public async Task<string> GetLastCommitShaAsync(string repoUri, string branch)
        {
            var (accountName, projectName, repoName) = ParseRepositoryUri(repoUri);

            HttpResponseMessage response = await this.ExecuteRemoteGitCommand(HttpMethod.Get, accountName, projectName, 
                $"git/repositories/{repoName}/commits?branch={branch}", _logger);

            JObject content = JObject.Parse(await response.Content.ReadAsStringAsync());

            JArray values = JArray.Parse(content["value"].ToString());

            if (!values.Any())
            {
                throw new Exception($"No commits found in branch '{branch}' of '{repoName}'");
            }

            return values[0]["commitId"].ToString();
        }

        public async Task<IList<Check>> GetPullRequestChecksAsync(string pullRequestUrl)
        {
            var (accountName, projectName, id) = ParsePullRequestUri(pullRequestUrl);
            string url = $"{pullRequestUrl}/statuses";

            HttpResponseMessage response = await this.ExecuteRemoteGitCommand(HttpMethod.Get, accountName, projectName, url, _logger);

            JObject content = JObject.Parse(await response.Content.ReadAsStringAsync());
            JArray values = JArray.Parse(content["value"].ToString());

            IList<Check> statuses = new List<Check>();
            foreach (JToken status in values)
            {
                if (Enum.TryParse(status["state"].ToString(), true, out AzureDevOpsCheckState state))
                {
                    CheckState checkState;

                    switch (state)
                    {
                        case AzureDevOpsCheckState.Error:
                            checkState = CheckState.Error;
                            break;
                        case AzureDevOpsCheckState.Failed:
                            checkState = CheckState.Failure;
                            break;
                        case AzureDevOpsCheckState.Pending:
                            checkState = CheckState.Pending;
                            break;
                        case AzureDevOpsCheckState.Succeeded:
                            checkState = CheckState.Success;
                            break;
                        default:
                            checkState = CheckState.None;
                            break;
                    }

                    statuses.Add(new Check(checkState, status["context"]["name"].ToString(), $"{pullRequestUrl}/{status["id"]}".ToString()));
                }
            }

            return statuses;
        }

        /// <summary>
        /// Get the source branch for the pull request.
        /// </summary>
        /// <param name="pullRequestUrl">url of pull request</param>
        /// <returns>Branch of pull request.</returns>
        public async Task<string> GetPullRequestSourceBranch(string pullRequestUrl)
        {
            var (accountName, projectName, id) = ParsePullRequestUri(pullRequestUrl);

            HttpResponseMessage response = await this.ExecuteRemoteGitCommand(HttpMethod.Get, accountName, projectName, pullRequestUrl, _logger);

            JObject content = JObject.Parse(await response.Content.ReadAsStringAsync());
            var sourceBranch = content["sourceRefName"].ToString();
            const string refsHeads = "refs/heads/";
            if (sourceBranch.StartsWith(refsHeads))
            {
                sourceBranch = sourceBranch.Substring(refsHeads.Length);
            }
            return sourceBranch;
        }

        public async Task<IList<Commit>> GetPullRequestCommitsAsync(string pullRequestUrl)
        {
            var connection = CreateVssConnection(pullRequestUrl);
            var client = await connection.GetClientAsync<GitHttpClient>();

            var (team, repo, id) = ParsePullRequestUri(pullRequestUrl);

            var commits = await client.GetPullRequestCommitsAsync(team, repo, id);

            return commits.Select(c => new Commit(c.Author.Name, c.CommitId)).ToList();
        }

        /// <summary>
        /// Determine whether a file exists at a specified branch or commit.
        /// </summary>
        /// <param name="repoUri">Repository uri</param>
        /// <param name="filePath">Path of file</param>
        /// <param name="branch">Branch or commit in to check.</param>
        /// <returns>True if the file exists, false otherwise.</returns>
        public async Task<bool> FileExistsAsync(string repoUri, string filePath, string branch)
        {
            var (accountName, projectName, repoName) = ParseRepositoryUri(repoUri);
            HttpResponseMessage response;

            try
            {
                response = await this.ExecuteRemoteGitCommand(HttpMethod.Get, accountName, projectName, 
                    $"git/repositories/{repoName}/items?path={filePath}&versionDescriptor[version]={branch}", _logger);
            }
            catch (HttpRequestException exc) when (exc.Message.Contains(((int)HttpStatusCode.NotFound).ToString()))
            {
                return false;
            }

            JObject content = JObject.Parse(await response.Content.ReadAsStringAsync());

            return string.IsNullOrEmpty(content["objectId"].ToString());
        }

        #region URI parsing

        private static readonly Regex prUriPattern = new Regex(@"^/(?<team>[^/])/_apis/git/repositories/(?<repo>[^/])/pullRequests/(?<id>\d+)$");

        private (string team, string repo, int id) ParsePullRequestUri(string uri)
        {
            var u = new UriBuilder(uri);
            var match = prUriPattern.Match(u.Path);
            if (!match.Success)
            {
                return default;
            }

            return (match.Groups["team"].Value, match.Groups["repo"].Value, int.Parse(match.Groups["id"].Value));
        }

        // Variety of forms are available for the account repo URI.
        // https://dev.azure.com/dnceng/internal/_git/dotnet-arcade
        // https://dnceng@dev.azure.com/dnceng/internal/_git/dotnet-arcade
        private const string accountNameCaptureName = "accountName";
        private const string projectNameCaptureName = "projectName";
        private const string repoNameCaptureName = "repoName";
        private static readonly string projectRepoCaptureSuffix = $"(?<{projectNameCaptureName}>.+)\\/_git\\/(?<{repoNameCaptureName}>.+)";
        private static readonly string form1 = $"(^https:\\/\\/)?dev\\.azure\\.com\\/(?<{accountNameCaptureName}>.+?)\\/{projectRepoCaptureSuffix}";
        private static readonly string form2 = $"(^https:\\/\\/)?(?<{accountNameCaptureName}>.+?)@dev\\.azure\\.com\\/{projectRepoCaptureSuffix}";

        /// <summary>
        /// Parse a repository uri to extract the elements.
        /// </summary>
        /// <param name="repoUri">Repository uri</param>
        /// <returns>Tuple of account name, project name and repository name.</returns>
        private (string accountName, string projectName, string repositoryName) ParseRepositoryUri(string repoUri)
        {
            Uri uri = new Uri(repoUri);
            string hostName = uri.Host;
            string accountName = null;
            string projectName = null;
            string repoName = null;

            List<Regex> uriRegex = new List<Regex>
            {
                new Regex(form1),
                new Regex(form2)
            };

            // Match available regexes.
            foreach (Regex regex in uriRegex)
            {
                Match m = regex.Match(repoUri);
                // If we are successful, then we should have all of the named capture groups.
                if (m.Success)
                {
                    if (!m.Groups[accountNameCaptureName].Success ||
                        !m.Groups[projectNameCaptureName].Success ||
                        !m.Groups[repoNameCaptureName].Success){
                        throw new ArgumentException($"Repository URI should be of the form dev.azure.com/<accountName>/<projectName>/_git/<repoName> " +
                                $"or <accountName>@dev.azure.com/<projectName>/_git/<repoName>");
                    }
                    accountName = m.Groups[accountNameCaptureName].Value;
                    projectName = m.Groups[projectNameCaptureName].Value;
                    repoName = m.Groups[repoNameCaptureName].Value;
                    break;
                }
            }
            
            if (accountName == null)
            {
                throw new ArgumentException($"Repository URI should be of the form dev.azure.com/<accountName>/<projectName>/_git/<repoName> " +
                                            $"or <accountName>@dev.azure.com/<projectName>/_git/<repoName>");
            }

            return (accountName, projectName, repoName);
        }

        private string GetPrPartialAbsolutePath(string prLink)
        {
            Uri uri = new Uri(prLink);
            string toRemove = $"{uri.Host}/_apis/git/";
            return prLink.Replace(toRemove, string.Empty);
        }

        #endregion

        private async Task<string> CreateOrUpdatePullRequestAsync(string uri, string mergeWithBranch, string sourceBranch, HttpMethod method, string title = null, string description = null)
        {
            string requestUri;
            var (accountName, projectName, repoName) = ParseRepositoryUri(uri);

            title = !string.IsNullOrEmpty(title) ? $"{PullRequestProperties.TitleTag} {title}" : PullRequestProperties.Title;
            description = description ?? PullRequestProperties.Description;

            AzureDevOpsPullRequest pullRequest = new AzureDevOpsPullRequest(title, description, sourceBranch, mergeWithBranch);

            string body = JsonConvert.SerializeObject(pullRequest, _serializerSettings);

            if (method == HttpMethod.Post)
            {
                requestUri = $"git/repositories/{repoName}/pullrequests";
            }
            else
            {
                requestUri = GetPrPartialAbsolutePath(uri);
            }

            HttpResponseMessage response = await this.ExecuteRemoteGitCommand(method, accountName, projectName, requestUri, _logger, body);

            JObject content = JObject.Parse(await response.Content.ReadAsStringAsync());

            return content["url"].ToString();
        }
    }
}
