# Better Studio Tools (BST)

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
  "type": "cli" | "edit" | "response",
  "id": "<string>",         // Optional: request/response ID
  "command": "<string>",    // Required for CLI packets
  "args": { ... }           // Optional arguments
}
```

- `type` — distinguishes the packet purpose:
  - `"cli"` — control commands.
  - `"edit"` — edit actions for the unpacked project.
  - `"response"` — sent back by the client with status or results.

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
