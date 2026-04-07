# Better Studio Tools (BST)

We recommend:

1. Installing the VS Code client [here](https://github.com/Anthony-Maxwell1/BST-VS-Code)
2. Installing the Roblox Studio Client [here](https://github.com/Anthony-Maxwell1/BST-Studio)
3. Installing the CLI, that manages the core (note that the VS Code client installs this for you, there is no need to install this if you are using the VS Code Cliente). [here](https://github.com/Anthony-Maxwell1/BST-Cli)

Related Projects:

- [Roblox-File-Format, ported to .NET 10 by me](https://github.com/Anthony-Maxwell1/Roblox-File-Format)
- [Original Project by MaximumADHD before .NET 5.0 (not cross-platform), updated alongside Roblox's clients via Roblox Client Tracker](https://github.com/MaximumADHD/Roblox-File-Format)
- [VS Code Client](https://github.com/Anthony-Maxwell1/BST-VS-Code)
- [Roblox Studio Client](https://github.com/Anthony-Maxwell1/BST-Studio)
- [CLI (Small client, doesn't support most features that a normal client would. Rather, it can control git features, and manages the core for you)](https://github.com/Anthony-Maxwell1/BST-Cli)

## Technical Overview

Below is the technical overview, not needed for most users, however provides insight into how the project works.

## Internal Client & Packet Documentation

### Overview

The Internal Client is a background service that connects to the local BST-Core WebSocket server (`ws://localhost:5000`) and manages Roblox projects in memory and on disk. It supports:

- **Opening/unpacking RBXL projects** into a folder structure for editing.
- **Monitoring live edits** to the unpacked project and updating in-memory RBXL.
- **Applying edits** (modify, delete, create) to properties or scripts.
- **Project recovery** after shutdown by restoring the unpacked project.
- **CLI commands** for project management (status, list, open, close).

---

## Packet Format

All communication uses JSON packets with the following general structure:

```json
{
  "type": "cli" | "edit" | "edit-relay" | "response" | "git" | "cloud", // For a regular client that has access to the file system on which unpacked is available, edit-relay should be of no concern. More on that later
  "id": "<string>",         // Optional: request/response ID
  "command": "<string>",    // Required for CLI packets
  "args": { ... }           // Optional arguments
}
```

- `type` — distinguishes the packet purpose:
  - `"cli"` — control commands.
  - `"edit"` — edit actions for the unpacked project.
  - `"response"` — sent back by the client with status or results.
  - `"edit-relay"` — when an Instance is modified, the internal client will relay the change.
  - `"git"` — triggers git actions in the GitClientWorker

- `id` — used to match responses to requests.
- `command` — specifies the CLI operation.
- `args` — contains command-specific or edit-specific arguments.

---

## CLI Packets (`type: "cli"`)

| Command         | Args                          | Description                                                                                                  |
| --------------- | ----------------------------- | ------------------------------------------------------------------------------------------------------------ |
| `status`        | _none_                        | Returns the current client status, including whether a project is open, the project name, and unpacked path. |
| `list-projects` | _none_                        | Returns an array of available project names in `./projects`.                                                 |
| `open-project`  | `{ "name": "<projectName>" }` | Opens and unpacks the specified project. Creates folder structure and YAML metadata.                         |
| `close-project` | _none_                        | Closes the current project, saves changes back to RBXL, and deletes unpacked files.                          |

**Example Response Packet** (status):

```json
{
  "type": "response",
  "id": "abc123",
  "projectOpen": true,
  "currentProject": "ExampleProject",
  "unpackedPath": "./unpacked"
}
```

---

## Edit Packets (`type: "edit"`)

Used to apply live edits to an open project. The `args` object must include:

- `path` — folder name under `_unpackedPath` corresponding to the instance.
- `action` — `"modify" | "delete" | "create"`.
- `target` — `"property" | "script"` (for modify actions).
- `property` — property name (required for property edits).
- `value` — new value for the property or script.

| Action   | Target     | Args Example                                                                                                         | Description                                                                     |
| -------- | ---------- | -------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------- |
| `modify` | `property` | `{ "path": "Part.Model.123", "action": "modify", "target": "property", "property": "Transparency", "value": "0.5" }` | Modifies a property of the specified instance. Updates YAML in unpacked folder. |
| `modify` | `script`   | `{ "path": "Script.Script.456", "action": "modify", "target": "script", "value": "print('Hello')" }`                 | Modifies the `Source` of a script. Updates `code.lua`.                          |
| `delete` | n/a        | `{ "path": "Part.Model.123", "action": "delete" }`                                                                   | Deletes the instance and its corresponding unpacked folder.                     |
| `create` | n/a        | TBD                                                                                                                  | Optional: create a new instance (not yet implemented).                          |

## Edit Relay Packets (`type: "edit-relay"`)

Used for clients without access to the file system, such as the roblox studio client.

- `uuid` — The uuid. This can then be looked up, if no access to the file system (WIP)
- `value` — Full new value of file (For properties, this is a dictionary, for script this is string.)
  The rest of the values (excluding path and value) are shared with Edit Packets

**Example Response Packet** (edit applied):

```json
{
  "type": "response",
  "id": "xyz789",
  "status": "edited",
  "path": "Part.Model.123",
  "action": "modify"
}
```

## Git packets

Used to control the git repository.

- `action` - The action. Can be "clone", "commit" or "pull".
- `remoteUrl` - The git url. e.g. `https://github.com/Anthony-Maxwell1/game.git`
- `username` - The git username.
- `passsword` - The git password. In the case of github, this is your PAT.
- `authorName` & `authorEmail` - What should be shown as user details.
- `projectName` - The project name that corresponds to the git repository.
- `message`? - Only for commit, the message to commit and push.
Note that commiting automatically stages all changes and pushes, and this can not be done any other way due to the way in which the system works, without complex setups that have multiple project files at once tracking the changes in each.
**Example Packet** (commit):

```json
{
    "type": "git",
    "id": "test27",
    "args": {
        "action": "commit",
        "remoteUrl": "https://github.com/Anthony-Maxwell1/game.git",
        "username": "Anthony-Maxwell1",
        "password": "my-totally-secure-pat-here",
        "authorName": "Anthony-Maxwell1",
        "authorEmail": "anthony@thatdev.org",
        "projectName": "Place1",
        "message": "Added a lobby"
    }
}
```

Response packets are provided with type response.

**Note**: Credential storing is planned, when I get around to adding it.

---

## Folder Structure for Unpacked Projects

```
unpacked/
  Part.Model.<guid>/
    properties.yaml      # YAML serialized safe properties
    code.lua             # Script source (if applicable)
  Script.Script.<guid>/
    properties.yaml
    code.lua
  project.yaml           # Metadata (project name)
```

- Folders are named as `<InstanceName>.<ClassName>.<GUID>` for uniqueness.
- Only safe property types are serialized (`string`, `bool`, numeric types, `Vector3`, `CFrame`, `ContentId`).
- Scripts have their source saved as `code.lua`.

---

## Notes

- The client automatically restores the last unpacked project if present on startup.
- Edits to properties or scripts are applied to both the in-memory RBXL (`_currentPlace`) and the unpacked folder.
- CLI responses are always sent with `type: "response"` and include the original `id` if provided.

## Testing
Is performed using postman. Using postman, you can open this local directory, and it will automatically find the collection and load it. You should then create an environment and insert your api keys and usernames, etc. for testing.
