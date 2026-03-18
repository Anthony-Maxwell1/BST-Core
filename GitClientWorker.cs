using System.Net.WebSockets;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using System.Text;
using System.Text.Json;

public class GitClientWorker : BackgroundService
{
    private readonly ILogger<GitClientWorker> _logger;
    private ClientWebSocket _ws;

    private static Repository OpenOrClone(string path, string remoteUrl = null)
    {
        if (Repository.IsValid(path))
        {
            return new Repository(path);
        }

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
        {
            repo.Network.Remotes.Add(name, url);
        }
        else
        {
            repo.Network.Remotes.Update(name, r => r.Url = url);
        }
    }

    private static void Commit(Repository repo, string message, string name, string email)
    {
        var author = new Signature(name, email, DateTimeOffset.Now);
        var committer = author;

        if (!repo.RetrieveStatus().IsDirty)
            return;

        repo.Commit(message, author, committer);
    }

    public static CredentialsHandler GetCredentials(string username, string password)
    {
        return (_url, _user, _cred) =>
            new UsernamePasswordCredentials
            {
                Username = username,
                Password = password
            };
    }

    private static void Push(Repository repo, string remoteName, string branchName, CredentialsHandler creds)
    {
        var remote = repo.Network.Remotes[remoteName];
        var pushRefSpec = $"refs/heads/{branchName}";

        var options = new PushOptions
        {
            CredentialsProvider = creds
        };

        repo.Network.Push(remote, pushRefSpec, options);
    }

    private static void Pull(Repository repo, string name, string email, CredentialsHandler creds)
    {
        var signature = new Signature(name, email, DateTimeOffset.Now);

        var options = new PullOptions
        {
            FetchOptions = new FetchOptions
            {
                CredentialsProvider = creds
            }
        };

        Commands.Pull(repo, signature, options);
    }

    private static void StageAll(Repository repo)
    {
        Commands.Stage(repo, "*");
    }

    private static void StageFile(Repository repo, string path)
    {
        Commands.Stage(repo, path);
    }

    private async Task ProcessGitAction(JsonElement args, string id)
    {
    }

    private async Task ProcessMessageAsync(string msg)
    {
        try
        {
            using var doc = JsonDocument.Parse(msg);
            var root = doc.RootElement;

            if (root.TryGetProperty("id", out var idProp_) && idProp_.GetString().StartsWith("internalignore-"))
            {
                return;
            }

            if (!root.TryGetProperty("type", out var typeProp))
                return;
            
            if (typeProp.GetString() == "git")
            {
                var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                var args = root.TryGetProperty("args", out var argsProp) ? argsProp : default;
                await ProcessGitAction(args, id);
            }
        } catch (JsonException)
        {
            _logger.LogWarning("Invalid JSON received");
        }
    }

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
                    var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);

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