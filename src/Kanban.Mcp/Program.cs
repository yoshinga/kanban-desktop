using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kanban.Mcp;

// Minimal MCP (Model Context Protocol) stdio server for Kanban.
// It proxies tool calls to the local REST API hosted by the desktop app,
// so the desktop app must be running. Protocol messages are newline-delimited
// JSON-RPC 2.0 over stdin/stdout; logs go to stderr.

var client = new ApiClient();
var server = new McpServer(client);

string? line;
var stdin = Console.OpenStandardInput();
using var reader = new StreamReader(stdin, Encoding.UTF8);
var stdout = Console.OpenStandardOutput();
using var writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };

while ((line = await reader.ReadLineAsync()) is not null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    JsonNode? msg;
    try { msg = JsonNode.Parse(line); }
    catch (JsonException) { continue; }
    if (msg is not JsonObject obj) continue;

    server.Handle(obj, writer);
}
return 0;

// =====================================================================

sealed class McpServer
{
    private readonly ApiClient _api;
    public McpServer(ApiClient api) => _api = api;

    public void Handle(JsonObject msg, TextWriter output)
    {
        var method = msg["method"]?.GetValue<string>();
        var id = msg["id"];
        var isRequest = id is not null;

        if (method is null) return;

        switch (method)
        {
            case "initialize":
            {
                var requested = msg["params"]?["protocolVersion"]?.GetValue<string>() ?? "2024-11-05";
                Respond(output, id, new JsonObject
                {
                    ["protocolVersion"] = requested,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "kanban", ["version"] = "1.0.0" },
                });
                break;
            }
            case "notifications/initialized":
            case "notifications/cancelled":
                break;
            case "ping":
                Respond(output, id, new JsonObject());
                break;
            case "tools/list":
                Respond(output, id, new JsonObject { ["tools"] = ToolCatalog.Build() });
                break;
            case "tools/call":
                HandleToolCall(msg, id, output).GetAwaiter().GetResult();
                break;
            default:
                if (isRequest)
                    RespondError(output, id, -32601, $"Method not found: {method}");
                break;
        }
    }

    private async Task HandleToolCall(JsonObject msg, JsonNode? id, TextWriter output)
    {
        var name = msg["params"]?["name"]?.GetValue<string>();
        var args = msg["params"]?["arguments"] as JsonObject ?? new JsonObject();
        if (name is null)
        {
            RespondError(output, id, -32602, "Missing tool name");
            return;
        }

        try
        {
            var text = await ToolCatalog.InvokeAsync(_api, name, args);
            Respond(output, id, new JsonObject
            {
                ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
                ["isError"] = false,
            });
        }
        catch (ApiException e)
        {
            Respond(output, id, new JsonObject
            {
                ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = e.Message } },
                ["isError"] = true,
            });
        }
        catch (Exception e)
        {
            Respond(output, id, new JsonObject
            {
                ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "Error: " + e.Message } },
                ["isError"] = true,
            });
        }
    }

    private static void Respond(TextWriter output, JsonNode? id, JsonNode result)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };
        output.WriteLine(msg.ToJsonString());
    }

    private static void RespondError(TextWriter output, JsonNode? id, int code, string message)
    {
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
        output.WriteLine(msg.ToJsonString());
    }
}
