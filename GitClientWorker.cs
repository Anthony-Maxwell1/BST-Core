using System.Net.WebSockets;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using System.Text;
using System.Text.Json;

public class GitClientWorker : BackgroundService
{
    private readonly ILogger<GitClientWorker> _logger;
    private ClientWebSocket _ws;

    // -------------------------------------------------------------------------
    // Repository helpers
    // -------------------------------------------------------------------------

    private static Repository OpenOrClone(string path, string remoteUrl = null)
    {
        if (Repository.IsValid(path))
            return new Repository(path);

        if (!string.IsNullOrEmpty(remoteUrl))
        {
            Repository.Clone(remoteUrl, path);
            return new Repository(path);
        }

        throw new Exception("No valid repository found and no remote URL provided.");
    }

    private static Repository CreateRepository(string path)
    {
        Repository.Init(path);
        return new Repository(path);
    }

    private static void AddRemote(Repository repo, string name, string url)
    {
        var existing = repo.Network.Remotes[name];
        if (existing == null)
            repo.Network.Remotes.Add(name, url);
        else
            repo.Network.Remotes.Update(name, r => r.Url = url);
    }

    private static void Commit(Repository repo, string message, string name, string email)
    {
        var author = new Signature(name, email, DateTimeOffset.Now);

        if (!repo.RetrieveStatus().IsDirty)
            return;

        repo.Commit(message, author, author);
    }

    private static CredentialsHandler GetCredentials(string username, string password) =>
        (_url, _user, _cred) => new UsernamePasswordCredentials
        {
            Username = username,
            Password = password
        };

    private static void Push(Repository repo, string remoteName, string branchName, CredentialsHandler creds)
    {
        var remote = repo.Network.Remotes[remoteName];
        repo.Network.Push(remote, $"refs/heads/{branchName}", new PushOptions
        {
            CredentialsProvider = creds
        });
    }

    private static MergeResult Pull(Repository repo, string name, string email, CredentialsHandler creds)
    {
        var signature = new Signature(name, email, DateTimeOffset.Now);
        return Commands.Pull(repo, signature, new PullOptions
        {
            FetchOptions = new FetchOptions { CredentialsProvider = creds }
        });
    }

    private static void StageAll(Repository repo) => Commands.Stage(repo, "*");

    // -------------------------------------------------------------------------
    // git.json helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the path to projects/git.json, creating the file if it doesn't exist.
    /// </summary>
    private string GitJsonPath =>
        Path.Combine(AppContext.BaseDirectory, "projects", "git.json");

    private Dictionary<string, string> LoadGitJson()
    {
        var path = GitJsonPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (!File.Exists(path))
            return new Dictionary<string, string>();

        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(text)
               ?? new Dictionary<string, string>();
    }

    private void SaveGitJson(Dictionary<string, string> map)
    {
        var path = GitJsonPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
    }

    // -------------------------------------------------------------------------
    // File-system helpers
    // -------------------------------------------------------------------------

    private string ReposRoot => Path.Combine(AppContext.BaseDirectory, "repos");
    private string ProjectsRoot => Path.Combine(AppContext.BaseDirectory, "projects");
    private string UnpackedRoot => Path.Combine(AppContext.BaseDirectory, "unpacked");

    /// <summary>
    /// Copies every file/directory from <paramref name="src"/> into <paramref name="dst"/>,
    /// creating <paramref name="dst"/> if necessary.
    /// </summary>
    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(src))
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }

    /// <summary>
    /// Returns the first file found directly inside <paramref name="folder"/> (BFS order),
    /// or null if the folder is empty.
    /// </summary>
    private static string FirstFileInFolder(string folder)
    {
        if (!Directory.Exists(folder)) return null;

        // BFS over the directory tree
        var queue = new Queue<string>();
        queue.Enqueue(folder);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var files = Directory.GetFiles(current);
            if (files.Length > 0)
                return files.OrderBy(f => f).First();
            foreach (var sub in Directory.GetDirectories(current).OrderBy(d => d))
                queue.Enqueue(sub);
        }
        return null;
    }

    // -------------------------------------------------------------------------
    // Action: create a brand-new repo (local init)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a fresh local repository at repos/{uuid} with the standard structure and README.
    /// Returns the uuid that was generated.
    /// </summary>
    private string HandleCreateRepo(string remoteUrl, string remoteName,
                                    string gitUsername, string gitPassword)
    {
        var uuid = Guid.NewGuid().ToString();
        var repoPath = Path.Combine(ReposRoot, uuid);
        Directory.CreateDirectory(repoPath);

        var repo = CreateRepository(repoPath);

        // Required subdirectories
        Directory.CreateDirectory(Path.Combine(repoPath, "BST"));
        Directory.CreateDirectory(Path.Combine(repoPath, "project"));

        // README
        var readmePath = Path.Combine(repoPath, "README.md");
        File.WriteAllText(readmePath,
            "# Read-only mirror\n\n" +
            "This repository is intended for online consumption and **must not be edited manually** — " +
            "any changes will be lost on the next push.\n\n" +
            "To update the project, re-upload the place file into the `BST/` folder via the client. " +
            "`BST/` must contain **only one file**.\n");

        StageAll(repo);

        var author = new Signature("GitClient", "gitclient@localhost", DateTimeOffset.Now);
        repo.Commit("Initial commit", author, author);

        if (!string.IsNullOrEmpty(remoteUrl))
        {
            AddRemote(repo, remoteName ?? "origin", remoteUrl);
            var creds = GetCredentials(gitUsername, gitPassword);
            Push(repo, remoteName ?? "origin",
                 repo.Head.FriendlyName,
                 creds);
        }

        repo.Dispose();
        return uuid;
    }

    // -------------------------------------------------------------------------
    // Action: clone
    // -------------------------------------------------------------------------

    private async Task HandleClone(JsonElement args)
    {
        if (!args.TryGetProperty("remoteUrl", out var remoteUrlProp))
        {
            _logger.LogWarning("clone: missing remoteUrl");
            return;
        }

        var remoteUrl  = remoteUrlProp.GetString();
        var username   = args.TryGetProperty("username",  out var u) ? u.GetString() : string.Empty;
        var password   = args.TryGetProperty("password",  out var p) ? p.GetString() : string.Empty;
        var projectName = args.TryGetProperty("projectName", out var pn) ? pn.GetString() : null;

        Directory.CreateDirectory(ReposRoot);

        var uuid     = Guid.NewGuid().ToString();
        var repoPath = Path.Combine(ReposRoot, uuid);

        try
        {
            // 1. Clone into repos/{uuid}
            var cloneOptions = new CloneOptions();
            if (!string.IsNullOrEmpty(username))
                cloneOptions.FetchOptions.CredentialsProvider = GetCredentials(username, password);

            Repository.Clone(remoteUrl, repoPath, cloneOptions);

            using var repo = new Repository(repoPath);

            repo.Config.Set("core.longpaths", true);

            // 2. Copy the first file in BST/ from repo root to projects/
            var bstPath = Path.Combine(repoPath, "BST");
            var firstFile = FirstFileInFolder(bstPath);

            Directory.CreateDirectory(ProjectsRoot);

            if (firstFile != null)
            {
                var destFileName = Path.GetFileName(firstFile);
                File.Copy(firstFile, Path.Combine(ProjectsRoot, destFileName), overwrite: true);

                // Use the file name (without extension) as project name if not provided
                if (string.IsNullOrEmpty(projectName))
                    projectName = Path.GetFileNameWithoutExtension(destFileName);
            }
            else
            {
                _logger.LogWarning("clone: BST/ folder is empty or does not exist in the cloned repo.");
                if (string.IsNullOrEmpty(projectName))
                    projectName = uuid; // fallback
            }

            // 3. Update git.json
            var map = LoadGitJson();
            map[projectName] = uuid;
            SaveGitJson(map);

            _logger.LogInformation("clone: repo cloned as uuid={uuid}, project={project}", uuid, projectName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "clone: error cloning repository");
        }
    }

    // -------------------------------------------------------------------------
    // Action: commit (stage → commit → push)
    // -------------------------------------------------------------------------

    private async Task HandleCommit(JsonElement args)
    {
        if (!args.TryGetProperty("projectName", out var pnProp))
        {
            _logger.LogWarning("commit: missing projectName");
            return;
        }

        if (!args.TryGetProperty("message", out var msgProp))
        {
            _logger.LogWarning("commit: missing message");
            return;
        }

        var projectName    = pnProp.GetString();
        var commitMessage  = msgProp.GetString();
        var gitName        = args.TryGetProperty("authorName",  out var an) ? an.GetString() : "GitClient";
        var gitEmail       = args.TryGetProperty("authorEmail", out var ae) ? ae.GetString() : "gitclient@localhost";
        var username       = args.TryGetProperty("username",    out var u)  ? u.GetString()  : string.Empty;
        var password       = args.TryGetProperty("password",    out var pw) ? pw.GetString() : string.Empty;
        var remoteName     = args.TryGetProperty("remote",      out var r)  ? r.GetString()  : "origin";
        var branchName     = args.TryGetProperty("branch",      out var b)  ? b.GetString()  : null;

        // Look up uuid
        var map = LoadGitJson();
        if (!map.TryGetValue(projectName, out var uuid))
        {
            _logger.LogWarning("commit: no uuid found for project '{project}'", projectName);
            return;
        }

        var repoPath = Path.Combine(ReposRoot, uuid);
        if (!Repository.IsValid(repoPath))
        {
            _logger.LogWarning("commit: no valid repo at repos/{uuid}", uuid);
            return;
        }

        try
        {
            // a. Copy the project file from projects/ to repos/{uuid}/BST/
            var projectFile = Directory.GetFiles(ProjectsRoot)
                                       .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                                                                .Equals(projectName, StringComparison.OrdinalIgnoreCase));
            if (projectFile != null)
            {
                var bstDir = Path.Combine(repoPath, "BST");
                Directory.CreateDirectory(bstDir);
                // Clear existing files so BST contains only one file
                foreach (var old in Directory.GetFiles(bstDir))
                    File.Delete(old);
                File.Copy(projectFile, Path.Combine(bstDir, Path.GetFileName(projectFile)), overwrite: true);
            }
            else
            {
                _logger.LogWarning("commit: no project file found for '{project}' in projects/", projectName);
            }

            // b. Copy entire unpacked/ structure to repos/{uuid}/project/
            if (Directory.Exists(UnpackedRoot))
            {
                var projectDir = Path.Combine(repoPath, "project");
                Directory.CreateDirectory(projectDir);
                CopyDirectory(UnpackedRoot, projectDir);
            }

            using var repo = new Repository(repoPath);

            // c. Stage all changes
            StageAll(repo);

            // d. Commit
            var author = new Signature(gitName, gitEmail, DateTimeOffset.Now);
            if (!repo.RetrieveStatus().IsDirty)
            {
                _logger.LogInformation("commit: nothing to commit for project '{project}'", projectName);
                return;
            }
            repo.Commit(commitMessage, author, author);

            // e. Push
            var creds = GetCredentials(username, password);
            Push(repo, remoteName, branchName ?? repo.Head.FriendlyName, creds);

            _logger.LogInformation("commit: pushed project '{project}' (uuid={uuid})", projectName, uuid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "commit: error during commit/push for project '{project}'", projectName);
        }
    }

    // -------------------------------------------------------------------------
    // Action: pull
    // -------------------------------------------------------------------------

    private async Task HandlePull(JsonElement args)
    {
        if (!args.TryGetProperty("projectName", out var pnProp))
        {
            _logger.LogWarning("pull: missing projectName");
            return;
        }

        var projectName = pnProp.GetString();
        var gitName     = args.TryGetProperty("authorName",  out var an) ? an.GetString() : "GitClient";
        var gitEmail    = args.TryGetProperty("authorEmail", out var ae) ? ae.GetString() : "gitclient@localhost";
        var username    = args.TryGetProperty("username",    out var u)  ? u.GetString()  : string.Empty;
        var password    = args.TryGetProperty("password",    out var pw) ? pw.GetString() : string.Empty;

        var map = LoadGitJson();
        if (!map.TryGetValue(projectName, out var uuid))
        {
            _logger.LogWarning("pull: no uuid found for project '{project}'", projectName);
            return;
        }

        var repoPath = Path.Combine(ReposRoot, uuid);
        if (!Repository.IsValid(repoPath))
        {
            _logger.LogWarning("pull: no valid repo at repos/{uuid}", uuid);
            return;
        }

        try
        {
            using var repo = new Repository(repoPath);
            var creds = GetCredentials(username, password);

            // 1. Fetch and check for upstream changes
            var remote = repo.Network.Remotes["origin"];
            Commands.Fetch(repo, remote.Name, Array.Empty<string>(), new FetchOptions
            {
                CredentialsProvider = creds
            }, null);

            var trackingBranch = repo.Head.TrackedBranch;
            if (trackingBranch == null)
            {
                _logger.LogWarning("pull: no tracking branch configured for '{project}'", projectName);
                return;
            }

            var behind = repo.Head.TrackingDetails.BehindBy ?? 0;
            if (behind == 0)
            {
                _logger.LogInformation("pull: '{project}' is already up to date.", projectName);
                return;
            }

            // 2. Pull — if a merge is required, abort
            var mergeResult = Pull(repo, gitName, gitEmail, creds);

            if (mergeResult.Status == MergeStatus.Conflicts)
            {
                _logger.LogError("pull: merge conflict detected for '{project}' — aborting. Manual resolution required.", projectName);
                // Reset to pre-merge state
                repo.Reset(ResetMode.Hard, repo.Head.Tip);
                return;
            }

            // 3. Copy the updated project file from BST/ to projects/
            var bstPath   = Path.Combine(repoPath, "BST");
            var firstFile = FirstFileInFolder(bstPath);

            if (firstFile != null)
            {
                Directory.CreateDirectory(ProjectsRoot);
                var destPath = Path.Combine(ProjectsRoot, Path.GetFileName(firstFile));
                File.Copy(firstFile, destPath, overwrite: true);
                _logger.LogInformation("pull: updated projects/{file} from remote.", Path.GetFileName(firstFile));
            }
            else
            {
                _logger.LogWarning("pull: BST/ is empty after pull for project '{project}'.", projectName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "pull: error during pull for project '{project}'", projectName);
        }
    }

    // -------------------------------------------------------------------------
    // Message dispatch
    // -------------------------------------------------------------------------

    private async Task ProcessGitAction(JsonElement args, string id)
    {
        if (!args.TryGetProperty("action", out var actionProp))
            return;

        switch (actionProp.GetString())
        {
            case "clone":
                await HandleClone(args);
                break;

            case "commit":
                await HandleCommit(args);
                break;

            case "pull":
                await HandlePull(args);
                break;

            default:
                _logger.LogWarning("ProcessGitAction: unknown action '{action}'", actionProp.GetString());
                break;
        }
    }

    private async Task ProcessMessageAsync(string msg)
    {
        try
        {
            using var doc  = JsonDocument.Parse(msg);
            var root       = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
                return;

            if (typeProp.GetString() == "git")
            {
                var id   = root.TryGetProperty("id",   out var idProp)   ? idProp.GetString()  : null;
                var args = root.TryGetProperty("args", out var argsProp)  ? argsProp            : default;
                await ProcessGitAction(args, id);
            }
        }
        catch (JsonException)
        {
            _logger.LogWarning("ProcessMessageAsync: invalid JSON received");
        }
    }

    // -------------------------------------------------------------------------
    // Background service
    // -------------------------------------------------------------------------

    public GitClientWorker(ILogger<GitClientWorker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ws = new ClientWebSocket();
                await _ws.ConnectAsync(new Uri("ws://localhost:5000"), stoppingToken);
                _logger.LogInformation("Git client connected");

                var buffer = new byte[8192];

                while (_ws.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                {
                    var result = await _ws.ReceiveAsync(buffer, stoppingToken);
                    var msg    = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await ProcessMessageAsync(msg);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Git client disconnected, retrying in 5s...");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }
}