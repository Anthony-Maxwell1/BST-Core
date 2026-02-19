# Better Studio Tools
## Core & Integrated client

- The core is a basic websocket server that accepts local connections and rebroadcasts them.
- The integrated client listens in and accepts cli type packets, e.g. unpacking the project for a client, or commiting to git. It also gracefully shuts down and has power recovery (recovering from shutoff without save, via the unpacked project). It also listens in to edits when a project is open/unpacked, to edit the unpacked version as well if it has not been edited directly in the folder.

### Packet format
TODO

### Packet types and arguments
TODO

### Current clients
- Integrated client, bundled in BST-Core
