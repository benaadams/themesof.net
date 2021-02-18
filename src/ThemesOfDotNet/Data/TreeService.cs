﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Octokit;

namespace ThemesOfDotNet.Data
{
    public sealed class TreeService
    {
        private readonly IWebHostEnvironment _environment;
        private readonly GitHubTreeProvider _githubTreeProvider;
        private readonly AzureDevOpsTreeProvider _azureTreeProvider;
        private readonly ILogger _logger;
        private LoadTreeJob _loadTreeJob;

        public TreeService(IWebHostEnvironment environment, GitHubTreeProvider gitHubTreeProvider, AzureDevOpsTreeProvider azureTreeProvider, ILogger<TreeService> logger)
        {
            _environment = environment;
            _githubTreeProvider = gitHubTreeProvider;
            _azureTreeProvider = azureTreeProvider;
            _logger = logger;
        }

        public Tree Tree => _loadTreeJob?.Tree;

        public DateTimeOffset? LoadDateTime => _loadTreeJob?.LoadDateTime;

        public TimeSpan? LoadDuration => _loadTreeJob?.LoadDuration;

        private sealed class LoadTreeJob
        {
            private readonly Tree _oldTree;
            private readonly CancellationTokenSource _cancellationTokenSource;
            private readonly Stopwatch _stopwatch;
            private readonly Task<Tree> _treeTask;
            private Tree _tree;

            public LoadTreeJob(Tree oldTree, DateTimeOffset? lastLoadDateTime, Func<CancellationToken, Task<Tree>> treeLoader)
            {
                _oldTree = oldTree;
                LoadDateTime = lastLoadDateTime;
                _cancellationTokenSource = new CancellationTokenSource();
                _stopwatch = Stopwatch.StartNew();
                _treeTask = treeLoader(_cancellationTokenSource.Token);
            }

            public void Cancel()
            {
                _cancellationTokenSource.Cancel();
            }

            public async Task<bool> WaitForLoad()
            {
                try
                {
                    _tree = await _treeTask;
                    LoadDateTime = DateTimeOffset.Now;
                    return true;
                }
                catch (TaskCanceledException)
                {
                    return false;
                }
                catch (RateLimitExceededException)
                {
                    // TODO: We need to figure out a better strategy here. Ideally, we'd use ex.Reset
                    //       and schedule a retry later when our quota resets.
                    _tree = _oldTree ?? Tree.Empty;
                    return false;
                }
                finally
                {
                    _stopwatch.Stop();
                }
            }

            public DateTimeOffset? LoadDateTime { get; private set; }

            public TimeSpan? LoadDuration => _treeTask.IsCompleted ? _stopwatch.Elapsed : (TimeSpan?) null;

            public Tree Tree => _tree ?? _oldTree;
        }

        public async Task InvalidateAsync(bool force = false)
        {
            var oldJob = _loadTreeJob;
            var newJob = new LoadTreeJob(Tree, oldJob?.LoadDateTime, ct => LoadTree(force, ct));

            oldJob?.Cancel();

            Interlocked.CompareExchange(ref _loadTreeJob, newJob, oldJob);

            try
            {
                if (await newJob.WaitForLoad())
                    Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
            }
        }

        private async Task<Tree> LoadTree(bool force, CancellationToken cancellationToken)
        {
            if (!_environment.IsDevelopment())
            {
                return await LoadTreeFromProvidersAsync(cancellationToken);
            }
            else
            {
                var tree = force ? null : await LoadTreeFromCacheAsync();
                if (tree == null)
                    tree = await LoadTreeFromProvidersAsync(cancellationToken);
                await SaveTreeToCacheAsync(tree);
                return tree;
            }
        }

        private async Task<Tree> LoadTreeFromProvidersAsync(CancellationToken cancellationToken)
        {
            var gitHubTreeTask = _githubTreeProvider.GetTreeAsync(cancellationToken);
            var azureTreeTask = _azureTreeProvider.GetTreeAsync(cancellationToken);
            await Task.WhenAll(gitHubTreeTask, azureTreeTask);
            return SortTree(MergeTrees(gitHubTreeTask.Result, azureTreeTask.Result));
        }

        private async Task<Tree> LoadTreeFromCacheAsync()
        {
            var fileName = GetCacheFileName();
            if (!File.Exists(fileName))
                return null;

            using var stream = File.OpenRead(fileName);
            var result = await JsonSerializer.DeserializeAsync<Tree>(stream);
            result.Initialize();
            return result;
        }

        private async Task SaveTreeToCacheAsync(Tree tree)
        {
            var fileName = GetCacheFileName();
            using var stream = File.Create(fileName);
            await JsonSerializer.SerializeAsync(stream, tree);
        }

        private string GetCacheFileName()
        {
            return Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location), "tree.json");
        }

        private Tree MergeTrees(Tree result1, Tree result2)
        {
            return new Tree(result1.Roots.Concat(result2.Roots));
        }

        private Tree SortTree(Tree tree)
        {
            var roots = tree.Roots.ToList();
            SortNodes(roots);
            return new Tree(roots);
        }

        private void SortNodes(List<TreeNode> nodes)
        {
            nodes.Sort();

            foreach (var node in nodes)
                SortNodes(node.Children);
        }

        public event EventHandler Changed;
    }
}
