# KhaozEngine.TileEdit.Tool

The `ke-tileedit` dotnet tool: an MCP (Model Context Protocol) server that opens, queries, mutates,
validates, renders, and saves KhaozEngine tile worlds over stdio, so an AI client can author a world
before any GUI editor exists. Every mutation runs through `KhaozEngine.TileWorld.Editing`, the same
command layer the later GUI editor uses, so an MCP edit and a GUI edit are the same undoable operation.
Author-time dev tool, not a runtime package.

## Install

```bash
dotnet tool install --global KhaozEngine.TileEdit.Tool
```

Installs the `ke-tileedit` command. This README ships inside the tool's nupkg (`PackageReadmeFile`).

## Wiring into an MCP client

Register it as an MCP server. Repo-local, for development against the tool's own source:

```bash
claude mcp add ke-tileedit -- dotnet run --project /path/to/KhaozEngine.TileEdit.Tool -c Debug
```

Against the packaged tool, once installed globally:

```bash
claude mcp add ke-tileedit -- ke-tileedit
```

Or run it ephemerally with `dnx`, no install required:

```bash
claude mcp add ke-tileedit -- dnx KhaozEngine.TileEdit.Tool
```

Equivalent `.mcp.json` entry (repo-local form):

```json
{
  "mcpServers": {
    "ke-tileedit": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/KhaozEngine.TileEdit.Tool", "-c", "Debug"]
    }
  }
}
```

Swap `command`/`args` for `"ke-tileedit"` with no args once it is installed as a global tool.

## Composition

`Tools/McpBootstrap.cs` is the one place the server's services and verb classes are registered, shared
by the stdio host in `Program.cs` and by the in-process wire-level tests, so the two can never disagree
on which verbs exist. The verb families (world, tile, height, object, marker, prefab, collision, render)
land on top of it.

Renders are the only thing here that touches a GPU, through `TileWorldSnapshot` over
`Render3DSnapshot`. Every other verb runs on a machine with no display or graphics device.

Full document format and the render arm sharing this same model:
[`KhaozEngine.TileWorld`](../KhaozEngine.TileWorld/README.md),
[`KhaozEngine.TileWorld.Editing`](../KhaozEngine.TileWorld.Editing/README.md), and
[`docs/design/TILE-WORLD-DESIGN-2026-08-15.md`](../docs/design/TILE-WORLD-DESIGN-2026-08-15.md).

Part of [KhaozEngine](https://github.com/APKiwiOrg/KhaozEngine).
