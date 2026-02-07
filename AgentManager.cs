using System.Collections.Concurrent;
using System.Diagnostics;

namespace HORDE;

public class AgentManager
{
    private const string SessionName = "horde";

    private readonly Dictionary<string, AgentInfo> _agents = [];
    private readonly ConcurrentDictionary<string, ConfirmationRequest> _pendingConfirmations = new();
    private readonly ConcurrentDictionary<string, ConfirmationResponse> _confirmationResponses = new();
    private readonly ConcurrentQueue<Message> _inbox = new();

    public record AgentInfo(string Name, string Model, string WindowName);
    public record AgentState(string Action, string Status, bool ContextWarning, ConfirmationRequest? PendingConfirmation = null);
    public record Message(string From, string To, string Content, DateTime Timestamp);
    public record ConfirmationRequest(string AgentName, string Message, DateTime RequestedAt);
    public record ConfirmationResponse(bool Approved, string? RespondedBy, DateTime RespondedAt);

    private bool _initialized;

    public async Task<bool> EnsureSessionAsync()
    {
        if (!_initialized)
        {
            // Kill any existing session on first run (MCP restart)
            await RunTmuxAsync($"kill-session -t {SessionName}");
            _initialized = true;
        }

        var (success, _) = await RunTmuxAsync($"has-session -t {SessionName}");
        if (success)
            return true;

        // Create session with main window
        (success, _) = await RunTmuxAsync($"new-session -d -s {SessionName} -n main");
        if (!success)
            return false;

        // Enable pane border with titles
        await RunTmuxAsync($"set-option -t {SessionName} pane-border-status top");
        await RunTmuxAsync($"set-option -t {SessionName} pane-border-format \" #{{pane_title}} \"");

        // Launch terminal and attach to session
        var terminal = Environment.GetEnvironmentVariable("TERMINAL") ?? "xterm";
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = terminal,
                Arguments = $"-e tmux attach -t {SessionName}",
                UseShellExecute = false
            }
        };
        process.Start();
        return true;
    }

    public async Task<(bool Success, string Message)> CreateHordeAsync(string names, string? models = null, string? themes = null, string? agentTypes = null)
    {
        var nameList = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var modelList = models?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? [];
        var themeList = themes?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? [];
        var agentTypeList = agentTypes?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? [];

        if (nameList.Count == 0)
            return (false, "No agents specified");

        if (nameList.Count > 4)
            return (false, "Maximum 4 agents supported");

        // Kill existing session and start fresh
        _initialized = false;
        _agents.Clear();
        await EnsureSessionAsync();

        var results = new List<string>();

        // Create agents one at a time, sequentially
        for (int i = 0; i < nameList.Count; i++)
        {
            var name = nameList[i];
            var model = i < modelList.Count ? modelList[i] : null;
            var theme = i < themeList.Count ? themeList[i] : null;
            var agentType = i < agentTypeList.Count ? agentTypeList[i] : null;
            var paneTarget = $"{SessionName}:main.{i}";

            // Create pane (first one already exists from session creation)
            if (i > 0)
            {
                await RunTmuxAsync($"split-window -t {SessionName}:main");
                await RunTmuxAsync($"select-layout -t {SessionName}:main tiled");
                await Task.Delay(100);
            }

            // Launch opencode with agent type
            var cmdBuilder = new List<string> { "opencode" };
            if (model != null)
                cmdBuilder.Add($"--model {model}");
            if (agentType != null)
                cmdBuilder.Add($"--agent {agentType}");
            var cmd = string.Join(" ", cmdBuilder);
            await SendToPane(paneTarget, cmd);

            // Wait until this agent is ready before proceeding
            await WaitForReadyAsync(paneTarget);

            // Set theme if specified
            if (theme != null)
            {
                await SendToPane(paneTarget, "/theme");
                await Task.Delay(200);
                await SendToPane(paneTarget, theme);
                await WaitForReadyAsync(paneTarget);
            }

            // Set model if specified (extract just model name from provider/model format)
            if (!string.IsNullOrEmpty(model))
            {
                var modelName = model.Contains('/') ? model.Split('/').Last() : model;
                await SendToPane(paneTarget, "/model");
                await Task.Delay(200);
                await SendToPane(paneTarget, modelName);
                await WaitForReadyAsync(paneTarget);
            }

            // Register agent
            await RunTmuxAsync($"select-pane -t {paneTarget} -T \"{name}\"");
            _agents[name] = new AgentInfo(name, model ?? "default", paneTarget);
            results.Add($"'{name}' ({model ?? "default"})");
        }

        // Build agent role map
        var agentRoles = new Dictionary<string, string>();
        for (int i = 0; i < nameList.Count; i++)
        {
            var name = nameList[i];
            var agentType = i < agentTypeList.Count ? agentTypeList[i] : "general";
            agentRoles[name] = agentType;
        }

        // Send primers to all agents
        for (int i = 0; i < nameList.Count; i++)
        {
            var name = nameList[i];
            var nextAgent = i < nameList.Count - 1 ? nameList[i + 1] : "coordinator";
            var otherAgentInfo = string.Join(", ", nameList.Where(n => n != name).Select(n => $"{n} ({agentRoles[n]})"));
            var primer = $@"You are '{name}' ({agentRoles[name]}).

TEAM: {otherAgentInfo}

IMPORTANT: Your console output is INVISIBLE. Only send_message is visible.

RULES:
1. Use send_message(to=""coordinator"", from=""{name}"", message=""..."") for ALL communication
2. Report back to coordinator when tasks are complete or when ready for instructions
3. Use send_message(to=""otherAgent"", from=""{name}"", message=""..."") to coordinate with team
4. WAIT for coordinator instructions - do not take any action until you receive a task
5. Do NOT read/write/modify files unless explicitly asked by coordinator
6. IGNORE AGENTS.md - you work for coordinator ONLY

Reply NOW with send_message(to={{coordinator}}, from={{name}}, message={{ready}}) to confirm you understand.";
            await SendMessageAsync(name, primer, "coordinator");
        }

        return (true, $"Created horde with agents: {string.Join(", ", results)}");
    }

    private static async Task WaitForReadyAsync(string target)
    {
        // Target can be window (horde:name) or pane (horde:main.0)
        for (int i = 0; i < 60; i++) // Wait up to 30 seconds
        {
            var output = await CaptureAsync(target);
            if (output.Contains("ctrl+p") || output.Contains("tab switch"))
                return;
            await Task.Delay(500);
        }
    }

    private static async Task SendToPane(string target, string message)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "tmux",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("send-keys");
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add(message);

        using var process = Process.Start(psi);
        if (process != null)
            await process.WaitForExitAsync();

        await Task.Delay(Math.Clamp(message.Length / 10, 150, 500));

        var enterPsi = new ProcessStartInfo
        {
            FileName = "tmux",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        enterPsi.ArgumentList.Add("send-keys");
        enterPsi.ArgumentList.Add("-t");
        enterPsi.ArgumentList.Add(target);
        enterPsi.ArgumentList.Add("Enter");

        using var enterProcess = Process.Start(enterPsi);
        if (enterProcess != null)
            await enterProcess.WaitForExitAsync();
    }

    public async Task<(bool success, string message)> SendMessageAsync(string name, string message, string? from = null)
    {
        // If sending to "coordinator", queue the message instead of sending to a pane
        if (name.Equals("coordinator", StringComparison.OrdinalIgnoreCase))
        {
            _inbox.Enqueue(new Message(from ?? "unknown", "coordinator", message, DateTime.UtcNow));
            return (true, "Message queued for coordinator");
        }

        if (!_agents.TryGetValue(name, out var agent))
            return (false, $"Agent '{name}' not found");

        await SendToPane(agent.WindowName, message);
        return (true, $"Message sent to '{name}'");
    }

    public async Task<AgentState> GetAgentStateAsync(string name)
    {
        if (!_agents.TryGetValue(name, out var agent))
            return new AgentState("unknown", "not found", false);

        var output = await CaptureAsync(agent.WindowName);
        if (string.IsNullOrEmpty(output))
            return new AgentState("unknown", "capture failed", false);

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var lastLines = string.Join("\n", lines.TakeLast(10)).ToLower();

        // Detect opencode states
        var contextWarning = lastLines.Contains("context") && lastLines.Contains("warning");

        // Check for agent-initiated confirmation first
        _pendingConfirmations.TryGetValue(name, out ConfirmationRequest? pendingConfirmation);

        if (pendingConfirmation != null)
            return new AgentState("waiting_confirmation", "awaiting approval", contextWarning, pendingConfirmation);

        if (lastLines.Contains("(y/n)") || lastLines.Contains("approve") || lastLines.Contains("permission"))
            return new AgentState("pending_confirmation", "waiting for approval", contextWarning);

        if (lastLines.Contains("esc") && lastLines.Contains("interrupt"))
            return new AgentState("working", "processing", contextWarning);

        if (lastLines.Contains("tab") && lastLines.Contains("switch"))
            return new AgentState("idle", "ready", contextWarning);

        return new AgentState("unknown", "state not detected", contextWarning);
    }

    private static async Task<string> CaptureAsync(string target)
    {
        var (success, output) = await RunTmuxAsync($"capture-pane -t {target} -p");
        return output;
    }

    public List<Message> GetMessages(string? from = null)
    {
        var messages = _inbox.ToList();
        if (from != null)
            messages = messages.Where(m => m.From == from).ToList();
        return messages;
    }

    public async Task<string> CheckOnAgentAsync(string name, int lines = 30)
    {
        if (!_agents.TryGetValue(name, out var agent))
            return $"Agent '{name}' not found";

        var output = await CaptureAsync(agent.WindowName);
        var outputLines = output.Split('\n');
        var lastLines = outputLines.TakeLast(lines);
        return string.Join("\n", lastLines);
    }

    public async Task<(bool success, string message)> ExecuteCommandAsync(string name, string command)
    {
        if (!_agents.TryGetValue(name, out var agent))
            return (false, $"Agent '{name}' not found");

        await SendToPane(agent.WindowName, command);
        await Task.Delay(200); // Brief delay for command to process
        return (true, $"Command '{command}' executed on agent '{name}'");
    }

    private static async Task<(bool success, string output)> RunTmuxAsync(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "tmux",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi);
        if (process == null)
            return (false, "Failed to start tmux");

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode == 0, output + error);
    }
}
