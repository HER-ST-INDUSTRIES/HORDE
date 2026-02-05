using System.Collections.Concurrent;
using System.Diagnostics;

namespace HORDE;

public class AgentManager
{
    const string SessionName = "horde";

    readonly Dictionary<string, AgentInfo> _agents = [];
    readonly ConcurrentQueue<Message> _inbox = new();

    public record AgentInfo(string Name, string Model, string WindowName);

    public record AgentState(string Action, string Status, bool ContextWarning, ConfirmationRequest? PendingConfirmation = null);

    public record Message(string From, string To, string Content, DateTime Timestamp);

    public record ConfirmationRequest(string AgentName, string Message, DateTime RequestedAt);

    public record ConfirmationResponse(bool Approved, string? RespondedBy, DateTime RespondedAt);

    readonly ConcurrentDictionary<string, ConfirmationRequest> _pendingConfirmations = new();
    readonly ConcurrentDictionary<string, ConfirmationResponse> _confirmationResponses = new();

    bool _initialized;

    public async Task<bool> EnsureSessionAsync()
    {
        if (!_initialized)
        {
            // Kill any existing session on first run (MCP restart)
            await RunTmuxAsync($"kill-session -t {SessionName}");
            _initialized = true;
        }

        var exists = await RunTmuxAsync($"has-session -t {SessionName}");
        if (!exists.Success)
        {
            // Create session with main window
            var result = await RunTmuxAsync($"new-session -d -s {SessionName} -n main");
            if (!result.Success) return false;

            // Enable pane border with titles
            await RunTmuxAsync($"set-option -t {SessionName} pane-border-status top");
            await RunTmuxAsync($"set-option -t {SessionName} pane-border-format \" #{{pane_title}} \"");

            // Launch terminal and attach to session
            var terminal = "alacritty";
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
        }
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
            else
                cmdBuilder.Add("--model zai-coding-plan/glm-4.7");
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

    public async Task<(bool Success, string Message)> AddAgentAsync(string name, string? model = null, string? theme = null)
    {
        if (_agents.ContainsKey(name))
            return (false, $"Agent '{name}' already exists");

        if (_agents.Count >= 4)
            return (false, "Maximum 4 agents supported");

        await EnsureSessionAsync();

        // Always split to create a new pane for each agent
        // First get current pane count
        var paneCountResult = await RunTmuxAsync($"list-panes -t {SessionName}:main -F '#{{pane_index}}'");
        var paneCount = paneCountResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        string paneTarget;

        if (paneCount == 1)
        {
            // Check if pane 0 is empty (just a shell prompt)
            var pane0Content = await CaptureAsync($"{SessionName}:main.0");
            var isEmptyPane = string.IsNullOrWhiteSpace(pane0Content) ||
                              !pane0Content.Contains("opencode") &&
                              !pane0Content.Contains("ctrl+p");

            if (isEmptyPane)
            {
                paneTarget = $"{SessionName}:main.0";
            }
            else
            {
                // Pane 0 is occupied, split
                await RunTmuxAsync($"split-window -t {SessionName}:main");
                await RunTmuxAsync($"select-layout -t {SessionName}:main tiled");
                paneTarget = $"{SessionName}:main.1";
            }
        }
        else
        {
            // Split from last pane
            await RunTmuxAsync($"split-window -t {SessionName}:main");
            await RunTmuxAsync($"select-layout -t {SessionName}:main tiled");
            paneTarget = $"{SessionName}:main.{paneCount}";
        }

        var cmd = model != null ? $"opencode --model {model}" : "opencode";
        await SendToPane(paneTarget, cmd);

        // Wait for opencode to be ready
        await WaitForReadyAsync(paneTarget);

        await WaitForReadyAsync(paneTarget);

        // Set theme if provided (must be done before primer)
        if (theme != null)
        {
            await SendToPane(paneTarget, "/theme");
            await Task.Delay(200);
            await SendToPane(paneTarget, theme);
            await WaitForReadyAsync(paneTarget);
        }

        // Set pane title for identification
        await RunTmuxAsync($"select-pane -t {paneTarget} -T \"{name}\"");

        _agents[name] = new AgentInfo(name, model ?? "default", paneTarget);

        // Send primer message
        var otherAgents = string.Join(", ", _agents.Keys.Where(k => k != name));
        var primer = $@"You are '{name}' in a multi-agent session. Other agents: {(string.IsNullOrEmpty(otherAgents) ? "none yet" : otherAgents)}.

CRITICAL: Your console output is NOT visible to anyone. Only messages sent via send_message tool can be seen by other agents and the coordinator.

When responding, ALWAYS use: send_message(to=""coordinator"", from=""{name}"", message=""..."")

Rules:
- Keep all tool outputs to an absolute minimum
- Do NOT explain your reasoning in console - just act
- Use send_message to report progress/results
- ALWAYS report back when you complete a task
- NO smalltalk, NO chatter - be concise and direct
- Only communicate what is necessary for the task";
        await SendMessageAsync(name, primer, "coordinator");

        return (true, $"Agent '{name}' created with model '{model ?? "default"}'");
    }

    static async Task WaitForReadyAsync(string target)
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

    static async Task SendToPane(string target, string message)
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

    public async Task<(bool Success, string Message)> RemoveAgentAsync(string name)
    {
        if (!_agents.TryGetValue(name, out var agent))
            return (false, $"Agent '{name}' not found");

        // Kill the agent's pane
        await RunTmuxAsync($"kill-pane -t {agent.WindowName}");
        _agents.Remove(name);
        return (true, $"Agent '{name}' removed");
    }

    public IEnumerable<AgentInfo> ListAgents() => _agents.Values;

    public async Task<(bool Success, string Message)> SendMessageAsync(string name, string message, string? from = null)
    {
        // If sending to "coordinator", queue the message instead of sending to a pane
        if (name.Equals("coordinator", StringComparison.OrdinalIgnoreCase))
        {
            QueueMessage(from ?? "unknown", "coordinator", message);
            return (true, "Message queued for coordinator");
        }

        if (!_agents.TryGetValue(name, out var agent))
            return (false, $"Agent '{name}' not found");

        await SendToPane(agent.WindowName, message);
        return (true, $"Message sent to '{name}'");
    }

    public async Task<(bool Success, string Message)> RespondConfirmationAsync(string name, bool approve, string? respondedBy = null)
    {
        if (!_agents.TryGetValue(name, out var agent))
            return (false, $"Agent '{name}' not found");

        // Check if there's a pending agent-initiated confirmation
        if (_pendingConfirmations.TryRemove(name, out var pending))
        {
            var response = new ConfirmationResponse(approve, respondedBy, DateTime.UtcNow);
            _confirmationResponses[name] = response;
            return (true, $"Confirmation {(approve ? "approved" : "denied")} for '{name}'");
        }

        // Fallback: send y/n to OpenCode's built-in confirmations
        var key = approve ? "y" : "n";
        await RunTmuxAsync($"send-keys -t {agent.WindowName} {key}");
        return (true, $"Sent '{key}' to '{name}'");
    }

    public (bool Success, string Message) RequestConfirmation(string agentName, string message)
    {
        if (!_agents.ContainsKey(agentName))
            return (false, $"Agent '{agentName}' not found");

        var request = new ConfirmationRequest(agentName, message, DateTime.UtcNow);
        _pendingConfirmations[agentName] = request;

        // Queue message to coordinator about pending confirmation
        QueueMessage(agentName, "coordinator", $"[CONFIRMATION REQUIRED] {message}");

        return (true, "Confirmation requested. Waiting for response...");
    }

    public async Task<ConfirmationResponse?> WaitForConfirmationResponseAsync(string agentName, int timeoutSeconds = 300)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            if (_confirmationResponses.TryRemove(agentName, out var response))
                return response;

            await Task.Delay(500);
        }

        // Timeout - remove pending confirmation
        _pendingConfirmations.TryRemove(agentName, out _);
        return null;
    }

    public List<ConfirmationRequest> GetPendingConfirmations()
    {
        return _pendingConfirmations.Values.ToList();
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
        ConfirmationRequest? pendingConfirmation = null;
        _pendingConfirmations.TryGetValue(name, out pendingConfirmation);

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

    public async Task<(bool Success, string Response)> WaitForResponseAsync(string name, int timeoutSeconds = 120)
    {
        if (!_agents.TryGetValue(name, out var agent))
            return (false, $"Agent '{name}' not found");

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            var state = await GetAgentStateAsync(name);
            if (state.Status == "idle")
            {
                // Agent is idle, capture their response
                var output = await CaptureAsync(agent.WindowName);
                return (true, output);
            }

            if (state.Status == "pending_confirmation")
                return (false, $"Agent '{name}' is waiting for confirmation");

            await Task.Delay(1000);
        }

        return (false, $"Timeout waiting for agent '{name}'");
    }

    static async Task<string> CaptureAsync(string target)
    {
        var result = await RunTmuxAsync($"capture-pane -t {target} -p");
        return result.Output;
    }

    // Message queue methods
    public void QueueMessage(string from, string to, string content)
    {
        _inbox.Enqueue(new Message(from, to, content, DateTime.UtcNow));
    }

    public List<Message> GetMessages(string? from = null)
    {
        var messages = _inbox.ToList();
        if (from != null)
            messages = messages.Where(m => m.From == from).ToList();
        return messages;
    }

    public async Task<Message?> WaitForMessageAsync(string? from = null, int timeoutSeconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            var messages = GetMessages(from);
            // Find any message that arrived after we started waiting
            var newMessage = messages.FirstOrDefault(m => m.Timestamp >= startTime);
            if (newMessage != null)
                return newMessage;

            await Task.Delay(500);
        }
        return null;
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

    public async Task<(bool Success, string Message)> ExecuteCommandAsync(string name, string command)
    {
        if (!_agents.TryGetValue(name, out var agent))
            return (false, $"Agent '{name}' not found");

        await SendToPane(agent.WindowName, command);
        await Task.Delay(200); // Brief delay for command to process
        return (true, $"Command '{command}' executed on agent '{name}'");
    }

    static async Task ResizeAllPanesAsync()
    {
        // Use tiled layout to equalize all panes, then resize coordinator
        await RunTmuxAsync($"select-layout -t {SessionName}:main tiled");
        await Task.Delay(50);
        await RunTmuxAsync($"resize-pane -t {SessionName}:main.0 -x 55");
    }

    static async Task<(bool Success, string Output)> RunTmuxAsync(string args)
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
