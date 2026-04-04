using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RoSharp;
using RoSharp.API;
using RoSharp.API.Assets;
using RoSharp.API.Assets.Experiences;
using RoSharp.API.Communities;

public class CloudClientWorker(ILogger<CloudClientWorker> logger) : BackgroundService
{
    private readonly ILogger<CloudClientWorker> _logger = logger;
    private ClientWebSocket? _ws;
    private Session session = new Session();

    // State held between the uploadRemote control message and the binary frame that follows it.
    private record PendingUpload(
        string Name,
        string AssetType,
        ulong CreatorId,
        bool CreatorIsCommunity
    );

    private PendingUpload? _pendingUpload;
    private string APIKey = string.Empty;

    private async Task<bool> Auth(string? apiKey = null, string? code = null)
    {
        if (apiKey != null)
        {
            session.SetAPIKey(apiKey);
            APIKey = apiKey;
            _logger.LogInformation("Authenticated with API key");
            return true;
        }
        else if (code != null)
        {
            await session.LoginAsync(code);
            _logger.LogInformation("Authenticated with code");
            return true;
        }
        else
        {
            _logger.LogWarning("No authentication method provided");
            return false;
        }
    }

    private async Task<User?> FetchUser(ulong? id = null, string? username = null)
    {
        if (id != null)
        {
            return await User.FromId(id ?? 0, session);
        }
        else if (username != null)
        {
            return await User.FromUsername(username, session);
        }
        else
        {
            return null;
        }
    }

    private async Task<Community?> FetchCommunity(ulong? id = null, string? name = null)
    {
        if (id != null)
        {
            return await Community.FromId(id ?? 0, session);
        }
        else if (name != null)
        {
            return await Community.FromName(name, session);
        }
        else
        {
            return null;
        }
    }

    // Asset.FromId is the only factory — Assets have no name-based lookup in RoSharp.
    private async Task<Asset?> FetchAsset(ulong? id = null)
    {
        if (id != null)
        {
            return await Asset.FromId(id ?? 0, session);
        }
        else
        {
            return null;
        }
    }

    // Experience can be fetched by universe ID or by any place ID within it.
    private async Task<Experience?> FetchExperience(ulong? universeId = null, ulong? placeId = null)
    {
        if (universeId != null)
        {
            return await Experience.FromId(universeId ?? 0, session);
        }
        else if (placeId != null)
        {
            return await Experience.FromPlaceId(placeId ?? 0, session);
        }
        else
        {
            return null;
        }
    }

    private async Task<MemberManager> CommunityFetchMemberManager(Community community)
    {
        return await community.GetMemberManagerAsync();
    }

    // GetRoleInCommunityAsync returns HttpResult<Role?> — the implicit operator lets us
    // assign directly to Role? without referencing the RoSharp.Http namespace.
    private async Task<Role?> CommunityFetchRole(
        MemberManager members,
        User? user = null,
        ulong? id = null,
        string? name = null
    )
    {
        Role? result;
        if (user != null)
        {
            result = await members.GetRoleInCommunityAsync(user);
        }
        else if (id != null)
        {
            result = await members.GetRoleInCommunityAsync(id ?? 0);
        }
        else if (name != null)
        {
            result = await members.GetRoleInCommunityAsync(name);
        }
        else
        {
            throw new ArgumentException("No role identifier provided");
        }
        return result;
    }

    private async Task<bool> CommunitySetRole(
        MemberManager members,
        Role role,
        User? user = null,
        ulong? id = null,
        string? name = null
    )
    {
        if (user != null)
        {
            await members.SetRankAsync(user, role);
            return true;
        }
        else if (id != null)
        {
            await members.SetRankAsync(id ?? 0, role);
            return true;
        }
        else if (name != null)
        {
            await members.SetRankAsync(name, role);
            return true;
        }
        else
        {
            return false;
        }
    }

    // Reused across all upload calls — one client for the lifetime of the worker.
    private static readonly HttpClient _uploadClient = new HttpClient();

    // ---------------------------------------------------------------------------
    // Asset upload
    // ---------------------------------------------------------------------------

    // Calls the Roblox Open Cloud Assets v1 API directly — RoSharp has no built-in
    // upload method. Requires an API key with the "asset:write" permission.
    // assetType must be the string recognised by the API, e.g. "Image", "Audio", "Decal".
    // creatorId is either a userId or a groupId depending on creatorIsCommunity.
    // Returns the new asset ID on success, or null if the asset is queued for moderation.
    private async Task<ulong?> UploadAssetAsync(
        string name,
        string assetType,
        ulong creatorId,
        bool creatorIsCommunity,
        byte[] fileData
    )
    {
        if (fileData.Length == 0)
            throw new InvalidOperationException("fileData is empty.");

        var requestJson = JsonSerializer.Serialize(
            new
            {
                assetType,
                displayName = name,
                description = "",
                creationContext = new
                {
                    creator = new
                    {
                        userId = creatorIsCommunity ? (ulong?)null : creatorId,
                        groupId = creatorIsCommunity ? (ulong?)creatorId : (ulong?)null,
                    },
                },
            }
        );

        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(requestJson, Encoding.UTF8, "application/json"), "request");
        multipart.Add(new ByteArrayContent(fileData), "fileContent", name);

        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            "https://apis.roblox.com/assets/v1/assets"
        );
        req.Headers.Add("x-api-key", APIKey);
        req.Content = multipart;

        HttpResponseMessage resp = await _uploadClient.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Upload failed ({resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("assetId", out var assetIdEl))
            return assetIdEl.GetUInt64();

        // Some asset types go through moderation and return an operation path instead.
        if (doc.RootElement.TryGetProperty("path", out var pathEl))
            _logger.LogInformation(
                "Upload queued for moderation, operation path: {path}",
                pathEl.GetString()
            );

        return null;
    }

    // Reads a file from disk and uploads it.
    private async Task<ulong?> UploadByFile(
        string filePath,
        string name,
        string assetType,
        ulong creatorId,
        bool creatorIsCommunity
    )
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentNullException(nameof(filePath));
        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found.", filePath);

        byte[] fileData = await File.ReadAllBytesAsync(filePath);
        return await UploadAssetAsync(name, assetType, creatorId, creatorIsCommunity, fileData);
    }

    // Receives raw file bytes over the websocket (the next binary frame after the control
    // message) and uploads them. Sets _pendingUpload so ExecuteAsync can hand off the frame.
    private async Task<ulong?> UploadRemote(byte[] fileData)
    {
        if (_pendingUpload == null)
            throw new InvalidOperationException(
                "No pending upload — send an uploadRemote control message first."
            );

        PendingUpload p = _pendingUpload;
        _pendingUpload = null;

        return await UploadAssetAsync(
            p.Name,
            p.AssetType,
            p.CreatorId,
            p.CreatorIsCommunity,
            fileData
        );
    }

    // ---------------------------------------------------------------------------

    private async Task ProcessMessageAsync(string msg)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(msg);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse message as JSON: {msg}", msg);
            return;
        }

        var root = doc.RootElement;

        // --- TYPE (optional, but recommended) ---
        string? type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

        if (type != "cloud")
        {
            _logger.LogWarning("Unsupported or missing type: {type}", type);
            return;
        }

        // --- COMMAND ---
        if (!root.TryGetProperty("command", out var commandEl))
        {
            _logger.LogWarning("Message missing 'command' field");
            return;
        }

        string command = commandEl.GetString() ?? string.Empty;
        string returnId;
        if (root.TryGetProperty("id", out var idEl))
        {
            returnId = idEl.GetString() ?? string.Empty;
        }
        else
        {
            returnId = string.Empty;
        }

        // --- ARGS ---
        JsonElement args = root.TryGetProperty("args", out var argsEl) ? argsEl : default;

        switch (command)
        {
            case "auth":
            {
                string? apiKey = args.TryGetProperty("apiKey", out var k) ? k.GetString() : null;
                string? code = args.TryGetProperty("code", out var c) ? c.GetString() : null;
                _logger.LogInformation("Authenticating...");

                await SendAsync(
                    _ws,
                    json: new Dictionary<string, object>
                    {
                        { "type", "response" },
                        { "id", returnId },
                        { "success", await Auth(apiKey, code) },
                    }
                );
                break;
            }

            case "fetchUser":
            {
                ulong? id = args.TryGetProperty("id", out var i) ? i.GetUInt64() : null;
                string? username = args.TryGetProperty("username", out var u)
                    ? u.GetString()
                    : null;

                User? user = await FetchUser(id, username);
                _logger.LogInformation("Fetched user: {user}", user?.Username ?? "(null)");
                await SendAsync(
                    _ws,
                    json: new Dictionary<string, object>
                    {
                        { "type", "response" },
                        { "id", returnId },
                        { "userId", user?.Id },
                        { "username", user?.Username },
                        { "displayName", user?.DisplayName },
                    }
                );
                break;
            }

            case "fetchAsset":
            {
                ulong? id = args.TryGetProperty("id", out var i) ? i.GetUInt64() : null;

                Asset? asset = await FetchAsset(id);
                _logger.LogInformation("Fetched asset: {asset}", asset?.Name ?? "(null)");
                await SendAsync(
                    _ws,
                    json: new Dictionary<string, object>
                    {
                        { "type", "response" },
                        { "id", returnId },
                        { "assetId", asset?.Id },
                        { "name", asset?.Name },
                        { "assetType", asset?.AssetType },
                    }
                );
                break;
            }

            case "fetchExperience":
            {
                ulong? universeId = args.TryGetProperty("universeId", out var u)
                    ? u.GetUInt64()
                    : null;
                ulong? placeId = args.TryGetProperty("placeId", out var p) ? p.GetUInt64() : null;

                Experience? exp = await FetchExperience(universeId, placeId);
                _logger.LogInformation("Fetched experience: {exp}", exp?.Name ?? "(null)");
                await SendAsync(
                    _ws,
                    json: new Dictionary<string, object>
                    {
                        { "type", "response" },
                        { "id", returnId },
                        { "universeId", exp?.UniverseId },
                        { "name", exp?.Name },
                    }
                );
                break;
            }

            case "fetchCommunity":
            {
                ulong? id = args.TryGetProperty("id", out var i) ? i.GetUInt64() : null;
                string? name = args.TryGetProperty("name", out var n) ? n.GetString() : null;

                Community? community = await FetchCommunity(id, name);
                _logger.LogInformation(
                    "Fetched community: {community}",
                    community?.Name ?? "(null)"
                );
                await SendAsync(
                    _ws,
                    json: new Dictionary<string, object>
                    {
                        { "type", "response" },
                        { "id", returnId },
                        { "communityId", community?.Id },
                        { "name", community?.Name },
                    }
                );
                break;
            }

            case "communityGetRole":
            {
                ulong? communityId = args.TryGetProperty("communityId", out var ci)
                    ? ci.GetUInt64()
                    : null;
                ulong? userId = args.TryGetProperty("userId", out var ui) ? ui.GetUInt64() : null;
                string? username = args.TryGetProperty("username", out var un)
                    ? un.GetString()
                    : null;

                Community? community = await FetchCommunity(communityId);
                if (community == null)
                {
                    _logger.LogWarning("communityGetRole: community not found");
                    break;
                }

                MemberManager members = await CommunityFetchMemberManager(community);

                User? user =
                    userId.HasValue ? await FetchUser(userId)
                    : username != null ? await FetchUser(username: username)
                    : null;

                Role? role = await CommunityFetchRole(members, user, userId, username);
                _logger.LogInformation("Role in community: {role}", role?.Name ?? "(null)");
                await SendAsync(
                    _ws,
                    json: new Dictionary<string, object>
                    {
                        { "type", "response" },
                        { "id", returnId },
                        { "roleName", role?.Name },
                        { "roleRank", role?.Rank },
                    }
                );
                break;
            }

            case "communitySetRole":
            {
                ulong? communityId = args.TryGetProperty("communityId", out var ci)
                    ? ci.GetUInt64()
                    : null;
                ulong? userId = args.TryGetProperty("userId", out var ui) ? ui.GetUInt64() : null;
                string? username = args.TryGetProperty("username", out var un)
                    ? un.GetString()
                    : null;
                string? roleName = args.TryGetProperty("roleName", out var rn)
                    ? rn.GetString()
                    : null;

                Community? community = await FetchCommunity(communityId);
                if (community == null)
                {
                    _logger.LogWarning("communitySetRole: community not found");
                    break;
                }

                MemberManager members = await CommunityFetchMemberManager(community);

                User? user =
                    userId.HasValue ? await FetchUser(userId)
                    : username != null ? await FetchUser(username: username)
                    : null;

                var roleManager = await community.GetRoleManagerAsync();
                Role? role = roleName != null ? roleManager.GetRole(roleName) : null;

                if (role == null)
                {
                    _logger.LogWarning("communitySetRole: role '{roleName}' not found", roleName);
                    break;
                }

                bool ok = await CommunitySetRole(members, role, user, userId, username);
                _logger.LogInformation("Set role result: {ok}", ok);
                await SendAsync(
                    _ws,
                    json: new Dictionary<string, object>
                    {
                        { "type", "response" },
                        { "id", returnId },
                        { "success", ok },
                    }
                );
                break;
            }

            case "uploadByFile":
            {
                string? filePath = args.TryGetProperty("filePath", out var fp)
                    ? fp.GetString()
                    : null;
                string? name = args.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? assetType = args.TryGetProperty("assetType", out var at)
                    ? at.GetString()
                    : null;
                ulong? creatorId = args.TryGetProperty("creatorId", out var ci)
                    ? ci.GetUInt64()
                    : null;
                bool creatorIsCommunity =
                    args.TryGetProperty("creatorIsCommunity", out var cc) && cc.GetBoolean();

                if (filePath == null || name == null || assetType == null || creatorId == null)
                {
                    _logger.LogWarning("uploadByFile: missing required fields");
                    break;
                }

                ulong? assetId = await UploadByFile(
                    filePath,
                    name,
                    assetType,
                    creatorId.Value,
                    creatorIsCommunity
                );
                _logger.LogInformation(
                    "uploadByFile: new asset ID = {assetId}",
                    assetId?.ToString() ?? "(pending moderation)"
                );
                await SendAsync(
                    _ws,
                    json: new Dictionary<string, object>
                    {
                        { "type", "response" },
                        { "id", returnId },
                        { "assetId", assetId },
                    }
                );
                break;
            }

            case "uploadRemote":
            {
                string? name = args.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? assetType = args.TryGetProperty("assetType", out var at)
                    ? at.GetString()
                    : null;
                ulong? creatorId = args.TryGetProperty("creatorId", out var ci)
                    ? ci.GetUInt64()
                    : null;
                bool creatorIsCommunity =
                    args.TryGetProperty("creatorIsCommunity", out var cc) && cc.GetBoolean();

                if (name == null || assetType == null || creatorId == null)
                {
                    _logger.LogWarning("uploadRemote: missing required fields");
                    break;
                }

                _pendingUpload = new PendingUpload(
                    name,
                    assetType,
                    creatorId.Value,
                    creatorIsCommunity
                );

                _logger.LogInformation("uploadRemote: ready — send binary next");
                await SendAsync(
                    _ws,
                    json: new Dictionary<string, object>
                    {
                        { "type", "response" },
                        { "id", returnId },
                        { "readyForFile", true },
                    }
                );
                break;
            }

            default:
                _logger.LogWarning("Unknown command: {command}", command);
                break;
        }
    }

    private static async Task SendAsync(
        ClientWebSocket ws,
        string message = null,
        Dictionary<string, object> json = null
    )
    {
        string payload;

        if (json != null)
        {
            payload = JsonSerializer.Serialize(json);
        }
        else if (!string.IsNullOrEmpty(message))
        {
            payload = message;
        }
        else
        {
            throw new ArgumentException("Either message or json must be provided");
        }

        var bytes = Encoding.UTF8.GetBytes(payload);
        var segment = new ArraySegment<byte>(bytes);

        await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ws = new ClientWebSocket();
                await _ws.ConnectAsync(new Uri("ws://localhost:5000"), stoppingToken);
                _logger.LogInformation("Cloud client connected");

                var buffer = new byte[65536];
                while (_ws.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                {
                    // Accumulate fragments into a single message — the WebSocket protocol
                    // allows any message to be split across multiple frames.
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _ws.ReceiveAsync(buffer, stoppingToken);
                        if (result.MessageType == WebSocketMessageType.Close)
                            break;
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    byte[] payload = ms.ToArray();

                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        // Binary frame: expected to be the file data for an uploadRemote.
                        if (_pendingUpload != null)
                        {
                            ulong? assetId = await UploadRemote(payload);
                            _logger.LogInformation(
                                "uploadRemote: new asset ID = {assetId}",
                                assetId?.ToString() ?? "(pending moderation)"
                            );
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Received binary frame with no pending uploadRemote — ignoring."
                            );
                        }
                    }
                    else
                    {
                        // Text frame: JSON control message.
                        string msg = Encoding.UTF8.GetString(payload);
                        await ProcessMessageAsync(msg);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cloud client disconnected, retrying in 5s...");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }
}
