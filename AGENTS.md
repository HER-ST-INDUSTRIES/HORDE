# HORDE - Multi-Agent Session Manager

MCP server for coordinating multi-agent sessions using tmux worktrees.

## Structure

```
├── AgentManager.cs    # Session/agent lifecycle management
├── HordeTools.cs      # MCP tool definitions
└── Program.cs         # Server entry point
```

## Usage

```bash
# HTTP transport (default)
dotnet run
# → http://localhost:5123/mcp

# stdio transport
dotnet run -- --stdio

# Custom port
PORT=5050 dotnet run
```

## MCP Tools

- `create_horde` - Create multi-agent session with tmux worktrees
- `send_message` - Send message to agent
- `get_agent_state` - Get agent status
- `list_sessions` - List active sessions
- `remove_session` - Cleanup session and worktrees

## Environment

| Variable | Default | Description |
|----------|---------|-------------|
| `PORT` | `5123` | HTTP server port |
