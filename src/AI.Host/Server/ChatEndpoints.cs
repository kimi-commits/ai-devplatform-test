using AI.Core.Artifacts;
using AI.Core.Events;
using AI.Core.Tools;
using AI.Core.Workflow;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AI.Host.Server;

/// <summary>Chat 送出訊息的請求體。Workflow 不填時預設用 default-pipeline.json(序列 Pipeline)。</summary>
public sealed record ChatRequest(string Message, string? Workflow);

/// <summary>
/// Phase 5:Chat、Diff、Streaming、Task Tree(規格書 v3 第 16 節)的後端 API。
///
/// 架構決策(已跟使用者確認過,詳見 README「已知限制 / 變更紀錄」):
/// 1. IPC 用 HTTP + Server-Sent Events,不是規格書寫的 gRPC——這個沙盒沒有 .NET SDK,
///    gRPC 需要的 .proto 工具鏈完全無法驗證,風險太高;HTTP+SSE 用 ASP.NET Core 內建的
///    Minimal API + Kestrel 就能做到,之後真的要換 gRPC 只需要換 Extension 的呼叫層,
///    不影響 Agent/Workflow 邏輯本身。
/// 2. Chat = 啟動並觀察 Workflow——使用者在 Chat 輸入需求,等同觸發一次 Workflow 執行(跟
///    `dotnet run --project src/AI.Host` 的 CLI 模式跑的是同一套 Orchestrator/DSL),訊息串流
///    顯示各 Step 的即時進度;Diff 顯示該次執行 Coder 產出的 CodeArtifact;Task Tree 即時顯示
///    目前跑到哪個 Step。三者是同一個 Workflow 執行的三種觀察角度,不是各自獨立的東西。
///
/// Diff 範疇說明:CoderAgent 目前是直接把建議文字寫成檔案(Phase 2 的既有行為,見
/// CoderAgent.WriteSuggestionToFileAsync),不是「產生一個 patch、等使用者按 Accept 才真正寫入」
/// 的模型。所以這裡的 Accept 只是確認保留(no-op),Reject 則是真的呼叫 file.deleteFile 把已經
/// 寫入的建議檔案刪掉(會走 Capability Guard 的 High 風險核准流程,跟手動刪檔案一樣)。要做到
/// 「先預覽、使用者按下去才真的寫入檔案」,需要重新設計 CoderAgent 的執行時機,留待後續加強,
/// 這裡誠實記錄這個限制,不假裝有更完整的 patch-apply 機制。
/// </summary>
public static class ChatEndpoints
{
    public static void MapAiDevPlatformApi(
        this WebApplication app,
        string repoRoot,
        string defaultWorkflowPath,
        string parallelWorkflowPath)
    {
        var runRegistry = new RunRegistry();
        var eventBus = app.Services.GetRequiredService<IEventBus>();
        var logger = app.Services.GetRequiredService<ILogger<RunRegistry>>();

        // 全域訂閱一次即可:事件本身帶 WorkflowId,由 RunRegistry 依 WorkflowId 找出對應的
        // RunTracker 轉發,不需要每次啟動新 Run 都重新訂閱(也不會累積訂閱者洩漏)。
        eventBus.Subscribe<StepStarted>((e, _) =>
        {
            runRegistry.Get(e.WorkflowId)?.OnStepStarted(e.StepId, e.AgentNames);
            return Task.CompletedTask;
        });
        eventBus.Subscribe<StepSucceeded>((e, _) =>
        {
            runRegistry.Get(e.WorkflowId)?.OnStepSucceeded(e.StepId, e.ArtifactIds);
            return Task.CompletedTask;
        });
        eventBus.Subscribe<StepFailed>((e, _) =>
        {
            runRegistry.Get(e.WorkflowId)?.OnStepFailed(e.StepId, e.Reason);
            return Task.CompletedTask;
        });

        app.MapPost("/api/chat", async (ChatRequest request, IWorkflowEngine workflowEngine, IAgentOrchestrator orchestrator) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.BadRequest(new { error = "message 不可為空" });
            }

            var workflowPath = string.Equals(request.Workflow, "parallel", StringComparison.OrdinalIgnoreCase)
                ? parallelWorkflowPath
                : defaultWorkflowPath;

            if (!File.Exists(workflowPath))
            {
                return Results.NotFound(new { error = $"找不到 Workflow DSL:{workflowPath}" });
            }

            var definition = await workflowEngine.LoadAsync(workflowPath);
            var workspace = new AI.Core.Workspace.Workspace(
                Name: "AI-DevPlatform",
                RootPath: repoRoot,
                Language: "C#",
                Framework: ".NET",
                GitBranch: "main",
                BuildProfile: null);

            var runId = await workflowEngine.StartAsync(definition, workspace);
            var tracker = runRegistry.Create(runId, definition.WorkflowId, definition.Steps);

            var seedArtifact = new DocumentArtifact(
                ArtifactId: Guid.NewGuid().ToString("N"),
                WorkflowId: runId,
                SnapshotId: null,
                CreatedAt: DateTimeOffset.UtcNow,
                Content: request.Message);

            logger.LogInformation(
                "[Chat] 收到新需求,啟動 Workflow '{WorkflowId}'(RunId={RunId}):{Message}",
                definition.WorkflowId, runId, request.Message);

            // Fire-and-forget:Chat 的 POST 立刻回應 runId,實際執行在背景跑,進度靠 SSE 推送。
            _ = Task.Run(async () =>
            {
                try
                {
                    var success = await orchestrator.RunAsync(
                        definition, workspace, runId, seedArtifacts: new IArtifact[] { seedArtifact });
                    tracker.Complete(success);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[Chat] Workflow '{RunId}' 執行時發生未預期例外", runId);
                    tracker.Fail(ex.Message);
                }
            });

            return Results.Accepted(value: new
            {
                runId,
                workflowId = definition.WorkflowId,
                steps = tracker.Steps.Select(s => new { id = s.Id, agentNames = s.AgentNames })
            });
        });

        app.MapGet("/api/chat/{runId}/stream", async (string runId, HttpContext context) =>
        {
            var tracker = runRegistry.Get(runId);
            if (tracker is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.Headers["Content-Type"] = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            var reader = tracker.Subscribe(out var replay);

            try
            {
                foreach (var line in replay)
                {
                    await context.Response.WriteAsync($"data: {line}\n\n", context.RequestAborted);
                }
                await context.Response.Body.FlushAsync(context.RequestAborted);

                await foreach (var line in reader.ReadAllAsync(context.RequestAborted))
                {
                    await context.Response.WriteAsync($"data: {line}\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
                // 用戶端斷線或關掉面板,正常結束,不是錯誤。
            }
        });

        app.MapGet("/api/tasks/{runId}", (string runId) =>
        {
            var tracker = runRegistry.Get(runId);
            if (tracker is null)
            {
                return Results.NotFound(new { error = "找不到這個 runId(可能還沒開始,或 AI.Host 重啟過)" });
            }

            return Results.Ok(new
            {
                runId = tracker.RunId,
                workflowId = tracker.WorkflowDefinitionId,
                completed = tracker.Completed,
                success = tracker.Success,
                steps = tracker.Steps.Select(s => new
                {
                    id = s.Id,
                    agentNames = s.AgentNames,
                    status = s.Status.ToString().ToLowerInvariant(),
                    reason = s.Reason,
                    artifactIds = s.ArtifactIds
                })
            });
        });

        app.MapGet("/api/diff/{artifactId}", async (string artifactId, IArtifactStore artifactStore) =>
        {
            var artifact = await artifactStore.GetAsync(artifactId);
            if (artifact is null)
            {
                return Results.NotFound(new { error = "not_found" });
            }

            if (artifact is not CodeArtifact codeArtifact)
            {
                return Results.BadRequest(new { error = "unsupported_artifact_type", type = artifact.Type });
            }

            var files = codeArtifact.Files.Select(relativePath =>
            {
                var absolutePath = Path.Combine(repoRoot, relativePath);
                try
                {
                    return new { path = relativePath, content = (string?)File.ReadAllText(absolutePath), error = (string?)null };
                }
                catch (Exception ex)
                {
                    return new { path = relativePath, content = (string?)null, error = (string?)ex.Message };
                }
            }).ToList();

            return Results.Ok(new
            {
                artifactId = codeArtifact.ArtifactId,
                type = codeArtifact.Type,
                summary = codeArtifact.Summary,
                files
            });
        });

        app.MapPost("/api/diff/{artifactId}/accept", (string artifactId) =>
        {
            // 見類別註解「Diff 範疇說明」:檔案已經由 CoderAgent 直接寫入,Accept 只是確認保留,
            // 沒有額外動作——老實回報,不假裝有真正的 patch-apply 邏輯。
            return Results.Ok(new
            {
                accepted = true,
                note = "檔案已由 Coder 直接寫入,Accept 只是確認保留,沒有額外動作。"
            });
        });

        app.MapPost("/api/diff/{artifactId}/reject", async (string artifactId, IArtifactStore artifactStore, IToolRuntime toolRuntime) =>
        {
            var artifact = await artifactStore.GetAsync(artifactId);
            if (artifact is not CodeArtifact codeArtifact)
            {
                return Results.NotFound(new { error = "not_found_or_unsupported" });
            }

            var deleted = new List<string>();
            var errors = new List<object>();

            foreach (var relativePath in codeArtifact.Files)
            {
                var absolutePath = Path.Combine(repoRoot, relativePath);
                var result = await toolRuntime.InvokeAsync(
                    "file.deleteFile",
                    new ToolRequest("file.deleteFile", new Dictionary<string, object?> { ["path"] = absolutePath }));

                if (result.Success)
                {
                    deleted.Add(relativePath);
                }
                else
                {
                    errors.Add(new { path = relativePath, error = result.Error });
                }
            }

            return Results.Ok(new { rejected = true, deletedFiles = deleted, errors });
        });
    }
}
