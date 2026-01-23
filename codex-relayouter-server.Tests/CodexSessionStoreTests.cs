using System.Text;
using codex_bridge_server.Bridge;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace codex_bridge_server.Tests;

public sealed class CodexSessionStoreTests
{
    [Fact]
    public void ListRecent_FiltersTaskTitleGeneratorPromptSessions()
    {
        const string taskTitlePromptPrefix =
            "You are a helpful assistant. You will be presented with a user prompt, and your job is to provide a short title for a task that will be created from that prompt.";

        var normalSessionId = Guid.NewGuid().ToString();
        var filteredSessionId = Guid.NewGuid().ToString();

        var sessionsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
        var dir = Path.Combine(sessionsRoot, "tests", "listrecent", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var normalPath = Path.Combine(dir, $"rollout-test-{normalSessionId}.jsonl");
        var filteredPath = Path.Combine(dir, $"rollout-test-{filteredSessionId}.jsonl");

        try
        {
            File.WriteAllLines(
                normalPath,
                new[]
                {
                    BuildSessionMetaLine(normalSessionId),
                    BuildUserMessageLine("hello"),
                },
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            File.WriteAllLines(
                filteredPath,
                new[]
                {
                    BuildSessionMetaLine(filteredSessionId),
                    BuildUserMessageLine($"{taskTitlePromptPrefix}\n\nUser prompt:\nhello"),
                },
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var future = DateTime.UtcNow.AddYears(50);
            File.SetLastWriteTimeUtc(filteredPath, future.AddMinutes(2));
            File.SetLastWriteTimeUtc(normalPath, future.AddMinutes(1));

            var store = CreateStore();
            var sessions = store.ListRecent(limit: 10);

            Assert.Contains(sessions, s => string.Equals(s.Id, normalSessionId, StringComparison.Ordinal));
            Assert.DoesNotContain(sessions, s => string.Equals(s.Id, filteredSessionId, StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteFile(filteredPath);
            TryDeleteFile(normalPath);
        }
    }

    [Fact]
    public void ReadMessages_FlushesTrailingTraceAsAssistantMessage()
    {
        var sessionId = Guid.NewGuid().ToString();
        var sessionsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
        var dir = Path.Combine(sessionsRoot, "tests", DateTimeOffset.UtcNow.ToString("yyyyMMdd"));
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, $"rollout-test-{sessionId}.jsonl");

        try
        {
            File.WriteAllLines(
                filePath,
                new[]
                {
                    BuildSessionMetaLine(sessionId),
                    BuildUserMessageLine("hello"),
                    BuildFunctionCallLine(callId: "call_1", tool: "shell_command", argsJson: "{\"command\":\"echo hi\"}"),
                    BuildFunctionCallOutputLine(callId: "call_1", output: "Exit code: 0\nWall time: 0.0 seconds\nOutput:\nhi\n"),
                },
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var store = CreateStore();
            var messages = store.ReadMessages(sessionId, limit: 200);

            Assert.NotNull(messages);
            var list = messages!.ToArray();

            Assert.Equal(2, list.Length);
            Assert.Equal("user", list[0].Role, ignoreCase: true);
            Assert.Equal("assistant", list[1].Role, ignoreCase: true);
            Assert.Equal("（未输出正文）", list[1].Text);

            Assert.NotNull(list[1].Trace);
            Assert.NotEmpty(list[1].Trace!);
            Assert.Contains(list[1].Trace!, t => string.Equals(t.Kind, "command", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteFile(filePath);
        }
    }

    [Fact]
    public void ReadMessages_UsesAgentMessageWhenAssistantMessageMissing()
    {
        var sessionId = Guid.NewGuid().ToString();
        var sessionsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
        var dir = Path.Combine(sessionsRoot, "tests", DateTimeOffset.UtcNow.ToString("yyyyMMdd"));
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, $"rollout-test-{sessionId}.jsonl");

        const string agentText = "agent says hello";

        try
        {
            File.WriteAllLines(
                filePath,
                new[]
                {
                    BuildSessionMetaLine(sessionId),
                    BuildUserMessageLine("hello"),
                    BuildAgentMessageLine(agentText),
                },
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var store = CreateStore();
            var messages = store.ReadMessages(sessionId, limit: 200);

            Assert.NotNull(messages);
            var list = messages!.ToArray();

            Assert.Equal(2, list.Length);
            Assert.Equal("user", list[0].Role, ignoreCase: true);
            Assert.Equal("assistant", list[1].Role, ignoreCase: true);
            Assert.Equal(agentText, list[1].Text);
        }
        finally
        {
            TryDeleteFile(filePath);
        }
    }

    [Fact]
    public void ReadMessages_DoesNotDuplicateAgentMessageAndResponseItemAssistantMessage()
    {
        var sessionId = Guid.NewGuid().ToString();
        var sessionsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
        var dir = Path.Combine(sessionsRoot, "tests", DateTimeOffset.UtcNow.ToString("yyyyMMdd"));
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, $"rollout-test-{sessionId}.jsonl");

        const string agentText = "hello from agent";

        try
        {
            File.WriteAllLines(
                filePath,
                new[]
                {
                    BuildSessionMetaLine(sessionId),
                    BuildUserMessageLine("hello"),
                    BuildAgentMessageLine(agentText),
                    BuildAssistantMessageLine(agentText),
                },
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var store = CreateStore();
            var messages = store.ReadMessages(sessionId, limit: 200);

            Assert.NotNull(messages);
            var list = messages!.ToArray();

            Assert.Equal(2, list.Length);
            Assert.Equal("user", list[0].Role, ignoreCase: true);
            Assert.Equal("assistant", list[1].Role, ignoreCase: true);
            Assert.Equal(agentText, list[1].Text);
        }
        finally
        {
            TryDeleteFile(filePath);
        }
    }

    private static CodexSessionStore CreateStore()
    {
        var options = Options.Create(new CodexOptions());
        var cliInfo = new CodexCliInfo(options, NullLogger<CodexCliInfo>.Instance);
        return new CodexSessionStore(NullLogger<CodexSessionStore>.Instance, cliInfo);
    }

    private static string BuildSessionMetaLine(string sessionId) =>
        $"{{\"timestamp\":\"{DateTimeOffset.UtcNow:O}\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{sessionId}\",\"timestamp\":\"{DateTimeOffset.UtcNow:O}\",\"cwd\":\"C:\\\\test\",\"originator\":\"codex-bridge-test\",\"cli_version\":\"0.0.0\",\"instructions\":\"\"}}}}";

    private static string BuildUserMessageLine(string text) =>
        $"{{\"timestamp\":\"{DateTimeOffset.UtcNow:O}\",\"type\":\"response_item\",\"payload\":{{\"type\":\"message\",\"role\":\"user\",\"content\":[{{\"type\":\"input_text\",\"text\":{JsonString(text)}}}]}}}}";

    private static string BuildAssistantMessageLine(string text) =>
        $"{{\"timestamp\":\"{DateTimeOffset.UtcNow:O}\",\"type\":\"response_item\",\"payload\":{{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{{\"type\":\"output_text\",\"text\":{JsonString(text)}}}]}}}}";

    private static string BuildAgentMessageLine(string text) =>
        $"{{\"timestamp\":\"{DateTimeOffset.UtcNow:O}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"agent_message\",\"message\":{JsonString(text)}}}}}";

    private static string BuildFunctionCallLine(string callId, string tool, string argsJson) =>
        $"{{\"timestamp\":\"{DateTimeOffset.UtcNow:O}\",\"type\":\"response_item\",\"payload\":{{\"type\":\"function_call\",\"name\":{JsonString(tool)},\"arguments\":{JsonString(argsJson)},\"call_id\":{JsonString(callId)}}}}}";

    private static string BuildFunctionCallOutputLine(string callId, string output) =>
        $"{{\"timestamp\":\"{DateTimeOffset.UtcNow:O}\",\"type\":\"response_item\",\"payload\":{{\"type\":\"function_call_output\",\"call_id\":{JsonString(callId)},\"output\":{JsonString(output)}}}}}";

    private static string JsonString(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
        }
    }
}
