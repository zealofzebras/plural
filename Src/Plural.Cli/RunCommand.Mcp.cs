using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Plural.Core.Protocol;
using Plural.Core.Session;

/// <summary>
/// <c>mcp</c>: the <c>run</c> mixer as a Model Context Protocol server, so an AI client can drive mics, outputs and takes.
/// Stdio transport: newline-delimited JSON-RPC on stdin/stdout; everything the CLI prints goes to stderr instead.
/// Tool calls run one at a time; each returns the lines its action printed, plus events (state changes, saved takes)
/// that happened since the previous call.
/// </summary>
sealed partial class RunCommand
{
    private const string McpInstructions = """
        Plural controls Sabinetek SmartMike+ wireless mics and local microphones: live mixer (faders, mutes, direct outputs,
        a mix output) and multi-track recording. Start with "devices" and "status"; add mics with "add_mic".
        A "mic" argument is the number from "status", part of the mic's name, or "all".
        Results end with events since the previous call (connections, finished takes and their files).
        """;

    private const string MicArg = "Mic number from status (1-based), part of its name, or \"all\"";

    private static readonly string[] McpVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    private sealed class Capture
    {
        public readonly List<string> Lines = [];
        public bool Closed;
    }

    private sealed record McpTool(string Name, string Description, JsonObject Schema, Func<JsonObject, Task> Run);

    /// <summary>The tool call whose output is being collected; flows into whatever the call starts.</summary>
    private static readonly AsyncLocal<Capture?> CurrentCapture = new();
    private readonly Queue<string> _events = new();
    private List<McpTool>? _tools;

    /// <summary>Routes a printed line to the running tool call, or to the event queue. Called under <see cref="_consoleLock"/>.</summary>
    private void Captured(string line)
    {
        if (mcpOut is null)
        {
            return;
        }
        if (CurrentCapture.Value is { Closed: false } c)
        {
            c.Lines.Add(line);
            return;
        }
        _events.Enqueue(line);
        while (_events.Count > 500)
        {
            _events.Dequeue();
        }
    }

    private async Task McpLoopAsync()
    {
        using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        await using var writer = new StreamWriter(mcpOut!, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break; // client gone
                }
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                JsonObject? reply;
                JsonNode? id = null;
                try
                {
                    var msg = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("expected a JSON object");
                    id = msg["id"]?.DeepClone();
                    reply = await HandleAsync(msg, id).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    reply = McpError(null, -32700, ex.Message);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    reply = McpError(id, -32603, ex.Message);
                }
                if (reply is not null)
                {
                    await writer.WriteLineAsync(reply.ToJsonString()).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        _cts.Cancel(); // finishes a running take, disconnects and exits
    }

    private async Task<JsonObject?> HandleAsync(JsonObject msg, JsonNode? id)
    {
        if (id is null || msg["method"] is not JsonValue m || !m.TryGetValue(out string? method))
        {
            return null; // notifications (initialized, cancelled, …) need no answer; we send no requests
        }
        var p = msg["params"] as JsonObject;
        return method switch
        {
            "initialize" => McpResult(id, Initialize(p)),
            "ping" => McpResult(id, []),
            "tools/list" => McpResult(id, new JsonObject
            {
                ["tools"] = new JsonArray([.. Tools.Select(t => new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["inputSchema"] = t.Schema.DeepClone(),
                })]),
            }),
            "tools/call" => McpResult(id, await CallToolAsync(p).ConfigureAwait(false)),
            _ => McpError(id, -32601, $"method not found: {method}"),
        };
    }

    private static JsonObject Initialize(JsonObject? p)
    {
        var requested = p?["protocolVersion"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
        return new JsonObject
        {
            ["protocolVersion"] = requested is not null && McpVersions.Contains(requested) ? requested : McpVersions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "plural",
                ["version"] = typeof(RunCommand).Assembly.GetName().Version?.ToString(3) ?? "0",
            },
            ["instructions"] = McpInstructions,
        };
    }

    private async Task<JsonObject> CallToolAsync(JsonObject? p)
    {
        var name = p?["name"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
        var args = p?["arguments"] as JsonObject ?? [];
        var tool = Tools.FirstOrDefault(t => t.Name == name);
        var capture = new Capture();
        var failed = false;
        CurrentCapture.Value = capture;
        try
        {
            if (tool is null)
            {
                throw new ArgumentException($"unknown tool \"{name}\"");
            }
            await tool.Run(args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failed = true;
            Say($"{name}: {ex.Message}");
        }
        finally
        {
            CurrentCapture.Value = null;
        }

        string text;
        lock (_consoleLock)
        {
            capture.Closed = true; // later output from work this call started (timers, sessions) becomes an event
            text = capture.Lines.Count > 0 ? string.Join('\n', capture.Lines) : "ok";
            if (_events.Count > 0)
            {
                text += "\n\nevents since the previous call:\n" + string.Join('\n', _events);
                _events.Clear();
            }
        }
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = failed,
        };
    }

    private static JsonObject McpResult(JsonNode id, JsonObject result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject McpError(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    // ------------------------------------------------------------------ tools

    private List<McpTool> Tools => _tools ??=
    [
        new("devices", "List paired SmartMikes, local (OS) microphones, audio outputs, cameras, recording formats and "
            + "SmartMike setting names. Marks which devices are already added as mics.",
            Schema(), _ =>
            {
                PrintDevices();
                return Task.CompletedTask;
            }),
        new("status", "Each mic's number, name, state, peak level (dBFS), fader, mute, direct output, battery and errors; "
            + "the mix output and master level; recording format and folder; whether a take is running.",
            Schema(), _ =>
            {
                PrintStatus(brief: false);
                return Task.CompletedTask;
            }),
        new("add_mic", "Add a mic and connect it (SmartMikes start streaming). Takes a few seconds per SmartMike.",
            Schema(("mic", "string", "MAC address, or part of the name of a paired SmartMike or local microphone (see devices)", true)),
            a => AddAsync(Str(a, "mic"))),
        new("connect", "Reconnect mics that were added before (e.g. after disconnect or a lost link).",
            Schema(("mic", "string", MicArg, true)),
            a => Task.WhenAll(Find(Str(a, "mic")).Select(ConnectAsync))),
        new("disconnect", "Disconnect mics (they stay in the list and can be reconnected).",
            Schema(("mic", "string", MicArg, true)),
            a => DisconnectAsync(Str(a, "mic"))),
        new("record", "Start a take: every connected mic is recorded to its own track/file in the current format.",
            Schema(("seconds", "number", "Stop by itself after this many seconds; omit to record until stop", false)),
            a => RecordAsync(Num(a, "seconds") ?? 0)),
        new("stop", "Finish the take; returns once its files are saved, listing them.",
            Schema(), _ => StopRecordingAsync()),
        new("fader", "Set a channel's level (applies to recording, direct output and mix).",
            Schema(("mic", "string", MicArg, true), ("db", "number", $"Level in dB: {MikeChannel.MinFaderDb} (= off) to +12; 0 is unity", true)),
            a =>
            {
                Fader(Str(a, "mic"), Num(a, "db") ?? throw new ArgumentException("missing \"db\""));
                return Task.CompletedTask;
            }),
        new("mute", "Mute or unmute mics in recording and outputs.",
            Schema(("mic", "string", MicArg, true), ("muted", "boolean", "true to mute, false to unmute", true)),
            a =>
            {
                Mute(Str(a, "mic"), Bool(a, "muted") ?? throw new ArgumentException("missing \"muted\""));
                return Task.CompletedTask;
            }),
        new("set_output", "Play mics live on an audio output (direct out), e.g. a virtual cable.",
            Schema(("mic", "string", MicArg, true), ("output", "string", "Part of an output name (see devices), or \"off\"", true)),
            a =>
            {
                SetDirectOutput(Str(a, "mic"), Str(a, "output"));
                return Task.CompletedTask;
            }),
        new("set_mix", "Play the mix of all mics on an audio output.",
            Schema(("output", "string", "Part of an output name (see devices), or \"off\"", true)),
            a =>
            {
                SetMix(Str(a, "output"));
                return Task.CompletedTask;
            }),
        new("master", "Set the mix level and/or mute the mix.",
            Schema(("db", "number", $"Mix level in dB: {MikeChannel.MinFaderDb} (= off) to +12", false), ("muted", "boolean", "Mute or unmute the mix", false)),
            a =>
            {
                Master(Num(a, "db"), Bool(a, "muted"));
                return Task.CompletedTask;
            }),
        new("set_setting", "Write a SmartMike device setting (persists on the device). Typical: MicGain 0-100 (default 50), "
            + "Agc 0/1 (default 1), MonitorVolume 0-100 (default 80), NoiseSuppression 0/1.",
            Schema(("mic", "string", MicArg, true),
                ("setting", "string", $"One of {string.Join(", ", Enum.GetNames<MikeSetting>())}", true),
                ("value", "integer", "New value", true)),
            a =>
            {
                var value = Num(a, "value") ?? throw new ArgumentException("missing \"value\"");
                foreach (var ch in Find(Str(a, "mic")))
                {
                    ApplySetting(ch, FormattableString.Invariant($"{Str(a, "setting")}={(int)value}"));
                }
                return Task.CompletedTask;
            }),
        new("set_format", "Choose the recording format for the next take (not during a take). Video formats open a camera.",
            Schema(("format", "string", $"One of {string.Join(", ", hub.AvailableFormats).ToLowerInvariant()}", true)),
            a => SetFormatAsync(Str(a, "format"))),
    ];

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        foreach (var p in props)
        {
            properties[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray([.. props.Where(p => p.Required).Select(p => (JsonNode)p.Name)]),
        };
    }

    /// <summary>A string argument; numbers are accepted too (mic 2).</summary>
    private static string Str(JsonObject a, string name) => a[name] switch
    {
        JsonValue v when v.TryGetValue(out string? s) && s.Length > 0 => s,
        JsonValue v when v.TryGetValue(out double d) => d.ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentException($"missing \"{name}\""),
    };

    /// <summary>A number argument; strings like "-6", "-6 dB" or "off" are accepted too.</summary>
    private static double? Num(JsonObject a, string name) => a[name] switch
    {
        null => null,
        JsonValue v when v.TryGetValue(out double d) => d,
        JsonValue v when v.TryGetValue(out string? s) && TryDb(s, out var d) => d,
        _ => throw new ArgumentException($"\"{name}\" must be a number"),
    };

    private static bool? Bool(JsonObject a, string name) => a[name] switch
    {
        null => null,
        JsonValue v when v.TryGetValue(out bool b) => b,
        JsonValue v when v.TryGetValue(out string? s) && bool.TryParse(s, out var b) => b,
        _ => throw new ArgumentException($"\"{name}\" must be true or false"),
    };
}
