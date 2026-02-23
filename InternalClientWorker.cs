using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RobloxFiles;
using RobloxFiles.DataTypes;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

public class InternalClientWorker : BackgroundService
{
    private readonly ILogger<InternalClientWorker> _logger;
    private ClientWebSocket _ws;

    private bool _projectOpen = false;
    private string _currentProject = null;
    private string _unpackedPath = "./unpacked";
    private RobloxFile _currentPlace = null; // RBXL in-memory

    private bool _closing = false;

    private readonly IDeserializer _yamlDeserializer;
    private readonly ISerializer _yamlSerializer;

    private FileSystemWatcher _watcher;

    private void StartWatchingUnpacked()
    {
        if (!Directory.Exists(_unpackedPath)) return;

        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(_unpackedPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            // Only watch the specific files we care about
        };

        _watcher.Changed += OnUnpackedFileChanged;
        _watcher.Created += OnUnpackedFileChanged;
        _watcher.Deleted += OnUnpackedFileChanged;
        _watcher.Renamed += OnUnpackedFileChanged;

        _watcher.EnableRaisingEvents = true;
    }

    private async void OnUnpackedFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_currentPlace == null || _closing) return;

        // Ignore events that aren't for a specific file we care about
        var fileName = Path.GetFileName(e.FullPath);
        if (fileName != "properties.yaml" && fileName != "code.lua")
            return;

        try
        {
            var relativePath = Path.GetRelativePath(_unpackedPath, e.FullPath);
            var parts = relativePath.Split(Path.DirectorySeparatorChar);

            // We expect: FolderName/properties.yaml or FolderName/code.lua
            // parts[0] = instance folder, parts[1] = filename
            if (parts.Length < 2) return;

            var folderName = parts[0];
            var nameParts = folderName.Split('.');
            if (nameParts.Length < 2) return;

            var name = nameParts[0];
            var className = nameParts[1];

            var instance = _currentPlace
                .GetDescendants()
                .FirstOrDefault(x => x.Name == name && x.ClassName == className);

            if (instance == null) return;

            // Small delay to let the file finish writing
            await Task.Delay(100);

            if (fileName == "properties.yaml")
            {
                var yamlText = await File.ReadAllTextAsync(e.FullPath);
                var props = _yamlDeserializer.Deserialize<Dictionary<string, object>>(yamlText);

                if (props != null)
                {
                    foreach (var kvp in props)
                    {
                        if (instance.Properties.TryGetValue(kvp.Key, out var prop))
                        {
                            // Coerce the YAML string value to the property's actual type
                            prop.Value = CoerceValue(kvp.Value, prop.Value);
                        }
                    }
                }
            }
            else if (fileName == "code.lua")
            {
                var code = await File.ReadAllTextAsync(e.FullPath);
                if (instance.Properties.TryGetValue("Source", out var prop))
                    prop.Value = code;
            }

            _logger.LogInformation("Updated instance {name}.{className} from {file}", name, className, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update instance from file change: {file}", e.FullPath);
        }
    }

    // Coerce a YAML-deserialized value (usually string) to match the existing property type
    private static object CoerceValue(object yamlValue, object existingValue)
    {
        if (yamlValue == null) return existingValue;
        if (existingValue == null) return yamlValue;

        var targetType = existingValue.GetType();
        var strVal = yamlValue.ToString();

        try
        {
            if (targetType == typeof(bool) && bool.TryParse(strVal, out var b)) return b;
            if (targetType == typeof(int) && int.TryParse(strVal, out var i)) return i;
            if (targetType == typeof(float) && float.TryParse(strVal, out var f)) return f;
            if (targetType == typeof(double) && double.TryParse(strVal, out var d)) return d;
            if (targetType == typeof(long) && long.TryParse(strVal, out var l)) return l;
            if (targetType == typeof(string)) return strVal;
        }
        catch { /* fall through */ }

        return yamlValue; // best effort
    }

    private async Task RestoreUnpackedProject()
    {
        if (Directory.Exists(_unpackedPath))
        {
            var projectYaml = Path.Combine(_unpackedPath, "project.yaml");
            if (File.Exists(projectYaml))
            {
                var meta = await File.ReadAllTextAsync(projectYaml);
                var info = _yamlDeserializer.Deserialize<Dictionary<string, object>>(meta);

                if (info != null && info.TryGetValue("name", out var nameObj))
                {
                    _currentProject = nameObj.ToString();
                    _projectOpen = true;

                    var projectFile = Path.Combine("./projects", _currentProject + ".rbxl");
                    if (File.Exists(projectFile))
                    {
                        _currentPlace = RobloxFile.Open(projectFile);
                    }

                    StartWatchingUnpacked(); // ← was missing
                    _logger.LogInformation("Restored unpacked project: {project}", _currentProject);
                }
            }
        }
    }


    public InternalClientWorker(ILogger<InternalClientWorker> logger)
    {
        _logger = logger;

        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        _yamlSerializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
    }

    private async Task SendStatus(string id)
    {
        await SendAsync(_ws, json: new Dictionary<string, object>
        {
            { "type", "response" },
            { "id", id },
            { "projectOpen", _projectOpen },
            { "currentProject", _currentProject ?? "" },
            { "unpackedPath", _projectOpen ? _unpackedPath : "" }
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RestoreUnpackedProject();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ws = new ClientWebSocket();
                await _ws.ConnectAsync(new Uri("ws://localhost:5000"), stoppingToken);
                _logger.LogInformation("Internal client connected");

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
                _logger.LogError(ex, "Internal client disconnected, retrying in 5s...");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task ListProjects(string id)
    {
        var projectDir = "./projects";
        var projects = new List<string>();

        if (Directory.Exists(projectDir))
        {
            projects = Directory.GetFiles(projectDir, "*.rbxl")
                                .Select(f => Path.GetFileNameWithoutExtension(f))
                                .ToList();
        }

        await SendAsync(_ws, json: new Dictionary<string, object>
        {
            { "type", "response" },
            { "id", id },
            { "projects", projects }
        });
    }

    private async Task ProcessMessageAsync(string msg)
    {
        try
        {
            using var doc = JsonDocument.Parse(msg);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
                return;

            if (typeProp.GetString() == "edit")
            {
                var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                var args = root.TryGetProperty("args", out var argsProp) ? argsProp : default;
                if (_projectOpen)
                    await ApplyEdit(args, id);
                return;
            }

            if (typeProp.GetString() != "cli")
                return;

            await HandleCliPacketAsync(root);
        }
        catch (JsonException)
        {
            // ignore invalid JSON
        }
    }

    private async Task HandleCliPacketAsync(JsonElement packet)
    {
        var command = packet.GetProperty("command").GetString();
        var id = packet.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        var args = packet.TryGetProperty("args", out var argsProp) ? argsProp : default;

        switch (command)
        {
            case "status":
                await SendStatus(id);
                break;

            case "list-projects":
                await ListProjects(id);
                break;

            case "open-project":
                if (args.ValueKind != JsonValueKind.Undefined && args.TryGetProperty("name", out var nameProp))
                {
                    await OpenProject(nameProp.GetString(), id);
                }
                break;

            case "close-project":
                await CloseCurrentProject(id);
                break;
        }
    }

    private static Dictionary<string, object> GetSafePropertyValues(Instance instance)
    {
        var safeProps = new Dictionary<string, object>();

        foreach (var kvp in instance.Properties)
        {
            var value = kvp.Value.Value;

            switch (value)
            {
                case null:
                case string:
                case bool:
                case int:
                case float:
                case double:
                case decimal:
                case long:
                    safeProps[kvp.Key] = value;
                    break;

                // Special Roblox data types
                case Vector3 v3:
                    safeProps[kvp.Key] = new { v3.X, v3.Y, v3.Z };
                    break;

                case CFrame cf:
                    safeProps[kvp.Key] = new { cf.Position.X, cf.Position.Y, cf.Position.Z };
                    break;

                case ContentId cid:
                    safeProps[kvp.Key] = cid.Url;
                    break;

                default:
                    // Skip anything else (Parent, Instance references, complex objects)
                    break;
            }
        }

        return safeProps;
    }

    private static string SanitizeFolderName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_'); // replace invalid chars with underscore
        }
        return name;
    }

    private async Task UnpackInstanceAsync(Instance obj, string parentPath)
    {
        var folderName = $"{SanitizeFolderName(obj.Name)}.{obj.ClassName}.{Guid.NewGuid()}";
        var folderPath = Path.Combine(parentPath, folderName);
        Directory.CreateDirectory(folderPath);

        // Serialize safe properties
        var propsYaml = _yamlSerializer.Serialize(GetSafePropertyValues(obj));
        await File.WriteAllTextAsync(Path.Combine(folderPath, "properties.yaml"), propsYaml);

        // Save script if any
        if (obj.ClassName.EndsWith("Script") && obj.Properties.TryGetValue("Source", out var source))
        {
            await File.WriteAllTextAsync(Path.Combine(folderPath, "code.lua"), source.Value?.ToString() ?? "");
        }

        // Recurse into children
        foreach (var child in obj.GetChildren())
        {
            await UnpackInstanceAsync(child, folderPath);
        }
    }

    private async Task OpenProject(string projectName, string id)
    {
        var projectFile = Path.Combine("./projects", projectName + ".rbxl");
        _logger.LogInformation("Opening project {projectName}", projectName);
        if (!File.Exists(projectFile))
        {
            _logger.LogWarning("Project {projectName} not found", projectName);
            return;
        }

        // Clear previous unpack
        if (Directory.Exists(_unpackedPath))
            Directory.Delete(_unpackedPath, recursive: true);

        Directory.CreateDirectory(_unpackedPath);

        // Load RBXL
        _currentPlace = RobloxFile.Open(projectFile);

        // Start recursion at root
        foreach (var child in _currentPlace.GetChildren())
        {
            await UnpackInstanceAsync(child, _unpackedPath);
        }

        // Save project metadata
        await File.WriteAllTextAsync(Path.Combine(_unpackedPath, "project.yaml"),
            _yamlSerializer.Serialize(new Dictionary<string, object> { { "name", projectName } }));

        _currentProject = projectName;
        _projectOpen = true;

        StartWatchingUnpacked();

        await SendAsync(_ws, json: new Dictionary<string, object>
        {
            { "type", "response" },
            { "id", id },
            { "status", "opened" },
            { "project", projectName }
        });
    }

    private async Task CloseCurrentProject(string id)
    {
        if (_projectOpen)
        {
            _closing = true;
            // Repack RBXL
            var projectFile = Path.Combine("./projects", _currentProject + ".rbxl");
            _currentPlace.Save(projectFile);

            // Clear unpacked
            if (Directory.Exists(_unpackedPath))
                Directory.Delete(_unpackedPath, recursive: true);

            _projectOpen = false;
            _currentProject = null;
            _currentPlace = null;
            _closing = false;
        }

        await SendAsync(_ws, json: new Dictionary<string, object>
        {
            { "type", "response" },
            { "id", id },
            { "status", "closed" }
        });
    }

    private async Task ApplyEdit(JsonElement args, string id)
    {
        // args: path = folder name under _unpackedPath
        // action: modify/create/delete
        // target: property or script
        // value: new value

        if (args.ValueKind != JsonValueKind.Object) return;

        var folder = args.GetProperty("path").GetString();
        var action = args.GetProperty("action").GetString();
        var target = args.GetProperty("target").GetString();
        var value = args.TryGetProperty("value", out var valProp) ? valProp.GetString() : null;

        var objDir = Path.Combine(_unpackedPath, folder);
        if (!Directory.Exists(objDir)) return;

        // Find the instance in _currentPlace
        var name = folder.Split('.')[0];
        var className = folder.Split('.')[1];

        var instance = _currentPlace
            .GetDescendants()
            .FirstOrDefault(x => x.Name == name && x.ClassName == className);
        if (instance == null) return;

        switch (action)
        {
            case "modify":
                if (target == "property" && !string.IsNullOrEmpty(value))
                {
                    var propName = args.GetProperty("property").GetString();

                    if (instance.Properties.TryGetValue(propName, out var prop))
                    {
                        prop.Value = value; // ✅ correct way
                    }

                    // Update YAML
                    var propsYaml = _yamlSerializer.Serialize(
                        instance.Properties.ToDictionary(k => k.Key, v => v.Value.Value)
                    );
                    await File.WriteAllTextAsync(Path.Combine(objDir, "properties.yaml"), propsYaml);
                }

                else if (target == "script" && !string.IsNullOrEmpty(value))
                {
                    if (instance.Properties.TryGetValue("Source", out var prop))
                    {
                        prop.Value = value; // ✅ correct way
                    }
                    await File.WriteAllTextAsync(Path.Combine(objDir, "code.lua"), value);
                }
                break;

            case "delete":
                instance.Destroy();
                if (Directory.Exists(objDir))
                    Directory.Delete(objDir, recursive: true); // ← only once
                break;

            case "create":
                // optionally implement
                break;
        }

        await SendAsync(_ws, json: new Dictionary<string, object>
        {
            { "type", "response" },
            { "id", id },
            { "status", "edited" },
            { "path", folder },
            { "action", action }
        });
    }

    private static async Task SendAsync(ClientWebSocket ws, string message = null, Dictionary<string, object> json = null)
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
}
