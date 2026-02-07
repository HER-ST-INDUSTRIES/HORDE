using System.ComponentModel;
using ModelContextProtocol.Server;

namespace HORDE;

[McpServerToolType]
public class HordeTools(AgentManager manager)
{
    [McpServerTool(Name = "create_horde", Destructive = true, ReadOnly = false, OpenWorld = true), Description("Create a HORDE session with multiple agents at once. Arrays are positional - names[0] gets models[0], themes[0], and agentTypes[0].")]
    public async Task<string> CreateHorde(
    [Description("Comma-separated agent names (e.g. 'agent1,agent2,agent3')")] string names,
    [Description("Comma-separated models, positional (optional, e.g. 'anthropic/claude-sonnet-4,google/gemini-2.5-pro')")] string? models = null,
    [Description("Comma-separated themes, positional (optional, e.g. 'dracula,nord')")] string? themes = null,
    [Description("Comma-separated agent, positional names (optional, e.g 'Build, Plan')")] string? agentTypes = null)
    {
        var (success, message) = await manager.CreateHordeAsync(names, models, themes, agentTypes);
        return message;
    }

    [McpServerTool(Name = "send_message", Destructive = true, ReadOnly = false, OpenWorld = true), Description("Send a message to an agent or coordinator")]
    public async Task<string> SendMessage(
    [Description("Recipient name (agent name or 'coordinator')")] string to,
    [Description("Message to send")] string message,
    [Description("Sender name (your agent name)")] string from)
    {
        // Direct message without wrapping
        var (success, result) = await manager.SendMessageAsync(to, message, from);
        return result;
    }

    [McpServerTool(Name = "get_messages", Destructive = false, ReadOnly = true, OpenWorld = true), Description("Get all messages in the inbox")]
    public string GetMessages([Description("Filter by sender (optional)")] string? from = null)
    {
        var messages = manager.GetMessages(from);
        if (messages.Count == 0)
            return "No messages in inbox";

        var formatted = messages.Select(m => $"[{m.Timestamp:HH:mm:ss}] {m.From} -> {m.To}: {m.Content}");
        return string.Join("\n\n", formatted);
    }

    [McpServerTool(Name = "get_agent_state", Destructive = false, ReadOnly = true, OpenWorld = true), Description("Get the current state of an agent")]
    public async Task<string> GetAgentState([Description("Name of the agent")] string name)
    {
        var state = await manager.GetAgentStateAsync(name);
        var result = $"Agent '{name}': {state.Status} (action: {state.Action}, context warning: {state.ContextWarning})";
        if (state.PendingConfirmation != null)
            result += $"\nPending confirmation: {state.PendingConfirmation.Message}";
        return result;
    }

    [McpServerTool(Name = "execute_command", Destructive = true, ReadOnly = false, OpenWorld = true), Description("Execute a command directly in an agent's OpenCode session")]
    public async Task<string> ExecuteCommand(
    [Description("Name of the agent")] string name,
    [Description("Command to execute (e.g. '/compact', '/new', '/session', '/clear', '/accept', '/deny')")] string command)
    {
        var (success, result) = await manager.ExecuteCommandAsync(name, command);
        return result;
    }

    [McpServerTool(Name = "check_on_agent", Destructive = false, ReadOnly = true, OpenWorld = true), Description("LAST RESORT: Check on an agent by reading their pane content. Not for normal workflow - only use when communication is broken or agent is unresponsive")]
    public async Task<string> CheckOnAgent(
    [Description("Name of agent")] string name,
    [Description("Number of lines to read (default 30)")] int lines = 30)
    => await manager.CheckOnAgentAsync(name, lines);
}
