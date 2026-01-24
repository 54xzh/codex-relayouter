using System;
using System.IO;
using System.Net;
using codex_bridge_server.Bridge;
using codex_bridge_server.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace codex_bridge_server.Tests;

public sealed class TurnPlanTests
{
    [Fact]
    public void TurnPlanStore_UpsertAndGet()
    {
        var store = new CodexTurnPlanStore();
        var snapshot = new TurnPlanSnapshot(
            SessionId: "thread_test",
            TurnId: "turn_test",
            Explanation: "explain",
            Plan: new[] { new TurnPlanStep("step", "pending") },
            UpdatedAt: DateTimeOffset.UtcNow);

        store.Upsert(snapshot);

        Assert.True(store.TryGet("thread_test", out var loaded));
        Assert.Equal(snapshot, loaded);
    }

    [Fact]
    public void SessionsController_GetLatestPlan_ReturnsUnauthorizedWhenNotLoopback()
    {
        var controller = CreateController(remoteIp: IPAddress.Parse("8.8.8.8"), out _);

        var result = controller.GetLatestPlan("thread_test");

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public void SessionsController_GetLatestPlan_ReturnsNotFoundWhenMissing()
    {
        var controller = CreateController(remoteIp: IPAddress.Loopback, out _);

        var result = controller.GetLatestPlan("thread_missing");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void SessionsController_GetLatestPlan_ReturnsSnapshot()
    {
        var controller = CreateController(remoteIp: IPAddress.Loopback, out var planStore);
        var snapshot = new TurnPlanSnapshot(
            SessionId: "thread_test",
            TurnId: "turn_test",
            Explanation: null,
            Plan: new[] { new TurnPlanStep("step 1", "inProgress") },
            UpdatedAt: DateTimeOffset.UtcNow);

        planStore.Upsert(snapshot);

        var result = controller.GetLatestPlan("thread_test");

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<TurnPlanSnapshot>(ok.Value);
        Assert.Equal("thread_test", payload.SessionId);
        Assert.Equal("turn_test", payload.TurnId);
        Assert.NotNull(payload.Plan);
        Assert.Single(payload.Plan);
    }

    private static SessionsController CreateController(IPAddress remoteIp, out CodexTurnPlanStore planStore)
    {
        var securityOptions = Options.Create(new BridgeSecurityOptions
        {
            RemoteEnabled = false,
            BearerToken = null,
        });

        var deviceStore = new PairedDeviceStore(NullLogger<PairedDeviceStore>.Instance, filePath: GetTempFilePath());
        var authorizer = new BridgeRequestAuthorizer(securityOptions, deviceStore);

        var cliInfo = new CodexCliInfo(
            Options.Create(new CodexOptions { Executable = "codex" }),
            NullLogger<CodexCliInfo>.Instance);

        var sessionStore = new CodexSessionStore(NullLogger<CodexSessionStore>.Instance, cliInfo);

        planStore = new CodexTurnPlanStore();

        var translationCache = new TranslationCacheStore(NullLogger<TranslationCacheStore>.Instance, filePath: GetTempTranslationsPath());
        var translation = new BridgeTranslationService(
            Options.Create(new BridgeTranslationOptions { Enabled = false }),
            translationCache,
            new NoopTranslator(),
            NullLogger<BridgeTranslationService>.Instance);

        var controller = new SessionsController(authorizer, sessionStore, planStore, translation);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                Connection =
                {
                    RemoteIpAddress = remoteIp,
                },
            },
        };

        return controller;
    }

    private static string GetTempFilePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codex-relayouter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "paired-devices.json");
    }

    private static string GetTempTranslationsPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codex-relayouter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "translations.json");
    }

    private sealed class NoopTranslator : ITextTranslator
    {
        public Task<string?> TranslateToZhCnAsync(string input, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
