using AI.Agents;
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

/// <summary>Stage B 規劃對話的請求體(見下方 /api/planning* 端點)。</summary>
public sealed record PlanningMessageRequest(string Message);

/// <summary>Stage B 規劃對話定案時的請求體,Workflow 語意跟 ChatRequest 一致。</summary>
public sealed record PlanningFinalizeRequest(string? Workflow);

/// <summary>Stage C:使用者在「Project Manager」模式下選好一份 PRD 檔案後送出的請求體。</summary>
public sealed record PmDispatchRequest(string PrdId);

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

        // Stage B(見 README「迭代開發迴圈」章節):Product Manager 多輪對話規格確認。
        // 跟 /api/chat 的差異是「不會立刻啟動 Workflow」——先在 planningSessions 這個記憶體內
        // session(AI.Host 重啟就會遺失,跟 RunTracker 的 MVP 取捨一致)裡來回對話,使用者按下
        // 「確認規格,開始開發」(對應 /finalize)才真的啟動 Workflow,啟動邏輯刻意跟 /api/chat
        // 保持一致(同一個 RunRegistry、同一個 seedArtifacts 模式),讓 Chat 面板可以直接複用
        // 現有的 /api/chat/{runId}/stream、/api/tasks/{runId}、/api/diff/{artifactId} 這些端點,
        // 不需要另外做一套。
        var planningSessions = new PlanningSessionRegistry();
        var productManager = app.Services.GetRequiredService<ProductManagerAgent>();
        // Stage C:PRD 落地成檔案,見 PrdStore.cs 類別註解。跟 approvals/、.ai-suggestions/ 同一層,
        // 放在 repoRoot 底下的 .ai-devplatform/ 目錄。
        var prdStore = new PrdStore(Path.Combine(repoRoot, ".ai-devplatform", "prds"));
        // Stage E(使用者自訂擴充):測試報告落地成檔案,見 AI.Agents/TestReportStore.cs 類別註解
        // (為什麼放在 AI.Agents 而不是這裡)。TestReportAgent 寫入的目錄跟這裡指向同一個路徑,
        // 兩邊各自 new 一個 TestReportStore 就能讀到彼此的檔案,不需要共用實例。
        var testReportStore = new AI.Agents.TestReportStore(Path.Combine(repoRoot, ".ai-devplatform", "test-reports"));

        app.MapPost("/api/planning", async (PlanningMessageRequest request, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.BadRequest(new { error = "message 不可為空" });
            }

            var session = planningSessions.Create();
            session.Append("user", request.Message);

            var reply = await productManager.ReplyAsync(session.Snapshot(), cancellationToken);
            session.Append("pm", reply);

            logger.LogInformation("[Planning] 開始新的規劃對話,session={SessionId}", session.SessionId);

            return Results.Ok(new { sessionId = session.SessionId, reply });
        });

        app.MapPost("/api/planning/{sessionId}/message", async (string sessionId, PlanningMessageRequest request, CancellationToken cancellationToken) =>
        {
            var session = planningSessions.Get(sessionId);
            if (session is null)
            {
                return Results.NotFound(new { error = "找不到這個規劃對話 session(AI.Host 重啟過的話會遺失,重新開始一個新的對話即可)。" });
            }

            if (string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.BadRequest(new { error = "message 不可為空" });
            }

            session.Append("user", request.Message);
            var reply = await productManager.ReplyAsync(session.Snapshot(), cancellationToken);
            session.Append("pm", reply);

            return Results.Ok(new { reply });
        });

        app.MapGet("/api/planning/{sessionId}", (string sessionId) =>
        {
            var session = planningSessions.Get(sessionId);
            if (session is null)
            {
                return Results.NotFound(new { error = "not_found" });
            }

            return Results.Ok(new { sessionId, turns = session.Snapshot() });
        });

        // 使用者實測後要求把原本「一鍵定案並啟動開發」拆成兩步(見 README「迭代開發迴圈」章節的
        // Stage B 調整紀錄):按「確認規格,產生 PRD」只產生規格書、不動 Workflow,讓使用者可以先
        // 看過 PRD 內容;確認 PRD 沒問題之後,再按另一個「開始開發」按鈕才真的啟動 Workflow。
        app.MapPost("/api/planning/{sessionId}/finalize", async (string sessionId, CancellationToken cancellationToken) =>
        {
            var session = planningSessions.Get(sessionId);
            if (session is null)
            {
                return Results.NotFound(new { error = "找不到這個規劃對話 session(AI.Host 重啟過的話會遺失,重新開始一個新的對話即可)。" });
            }

            var history = session.Snapshot();
            if (history.Count == 0)
            {
                return Results.BadRequest(new { error = "這個規劃對話還沒有任何內容,無法定案。" });
            }

            var finalSpec = await productManager.FinalizeSpecAsync(history, cancellationToken);
            session.SetFinalSpec(finalSpec);

            // Stage C:同時把這份 PRD 存成檔案,讓 Chat 面板的「Project Manager」模式之後可以
            // 從下拉選單選到它(見 GET /api/prds)。用 try/catch 包起來、失敗只記 log 不擋主流程,
            // 因為就算存檔失敗,使用者原本要的「產生 PRD 給我看」這件事已經做到了,不應該因為
            // 這個額外功能失敗就讓整個 /finalize 回傳錯誤。
            string? prdId = null;
            try
            {
                prdId = await prdStore.SaveAsync(finalSpec, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Planning] Session {SessionId} 的 PRD 存檔失敗,不影響本次回應。", sessionId);
            }

            logger.LogInformation(
                "[Planning] Session {SessionId} 產生 PRD(尚未啟動開發,PrdId={PrdId}):\n{Spec}",
                sessionId, prdId, finalSpec);

            // Stage F:origin 讓 Chat 面板知道這個 session 是不是從「修改規格」按鈕開始的
            // (見 PlanningSession.Origin 類別註解),藉此決定要不要顯示「🚀 開始開發」按鈕。
            return Results.Ok(new { finalSpec, prdId, origin = session.Origin });
        });

        // Stage C(使用者自訂擴充):列出所有已產生的 PRD 檔案,給 Chat 面板「Project Manager」
        // 模式的下拉選單用。
        app.MapGet("/api/prds", () => Results.Ok(prdStore.List()));

        // Stage C:使用者在「Project Manager」模式選好一份 PRD、按下送出——對應使用者描述的流程
        // 「Project Manager Agent 動態分派給多個 Coder 開始開發」。跟 /api/chat、
        // /api/planning/{sessionId}/start-development 的啟動邏輯刻意保持一致(同一個
        // RunRegistry、同一個 seedArtifacts 模式),讓 Chat 面板可以直接複用既有的
        // /api/chat/{runId}/stream、/api/tasks/{runId} 這些端點。
        app.MapPost("/api/pm/dispatch", async (
            PmDispatchRequest request,
            IWorkflowEngine workflowEngine,
            IAgentOrchestrator orchestrator,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.PrdId))
            {
                return Results.BadRequest(new { error = "prdId 不可為空" });
            }

            var prdContent = prdStore.GetContent(request.PrdId);
            if (prdContent is null)
            {
                return Results.NotFound(new { error = $"找不到 PRD:{request.PrdId}" });
            }

            var workflowPath = Path.Combine(repoRoot, "workflows", "pm-dispatch-pipeline.json");
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
                Content: prdContent);

            logger.LogInformation(
                "[PM Dispatch] 使用 PRD '{PrdId}' 啟動 Workflow '{WorkflowId}'(RunId={RunId})",
                request.PrdId, definition.WorkflowId, runId);

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
                    logger.LogError(ex, "[PM Dispatch] Workflow '{RunId}' 執行時發生未預期例外", runId);
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

        // Stage E~G(使用者自訂擴充,見 README「迭代開發迴圈」章節):QA 產生的測試報告落地後,
        // 使用者在 Chat 面板「Product Manager(驗收)」模式挑一份報告,決定「完成驗收」還是
        // 「修改規格」——這兩個都是使用者主動觸發的「新的一次」動作,不是原本那個 Run 的延續
        // (見 TestReportAgent.cs 類別註解「人工驗收關卡的實作方式」)。
        app.MapGet("/api/reports", () => Results.Ok(testReportStore.List()));

        app.MapGet("/api/reports/{id}", (string id) =>
        {
            var detail = testReportStore.GetDetail(id);
            return detail is null
                ? Results.NotFound(new { error = $"找不到測試報告:{id}" })
                : Results.Ok(detail);
        });

        // Stage F:「修改規格」——帶著這份報告的 PRD 內容 + QA 結論開一場新的 PM 規劃討論
        // session,回傳形狀刻意跟 /api/planning(startPlanning)一致,讓 Chat 面板可以直接沿用
        // 既有的 PM 對話 UI(appendPm、確認規格按鈕、finalize/start-development 這一整條既有
        // 邏輯),差別只在於這場對話一開始已經有 seed 內容,不是從使用者輸入開始;而且
        // session.Origin="revise",定案後不會顯示「開始開發」按鈕(見上面 /finalize 的註解)。
        app.MapPost("/api/reports/{id}/revise", async (string id, CancellationToken cancellationToken) =>
        {
            var detail = testReportStore.GetDetail(id);
            if (detail is null)
            {
                return Results.NotFound(new { error = $"找不到測試報告:{id}" });
            }

            var session = planningSessions.Create("revise");
            var seedMessage =
                "以下是先前確認過、已經照這份規格開發完成的規格書:\n\n" + detail.PrdContent +
                "\n\n這是驗收測試(QA)的結論:\n\n" + detail.QaSummary +
                "\n\n請根據這份測試結果,和我討論這份規格書需要怎麼修改(哪裡沒做對、要補什麼、" +
                "要拿掉什麼),不用重新從零規劃。";
            session.Append("user", seedMessage);

            var reply = await productManager.ReplyAsync(session.Snapshot(), cancellationToken);
            session.Append("pm", reply);

            logger.LogInformation(
                "[Acceptance] 針對測試報告 {ReportId} 開始「修改規格」討論,session={SessionId}",
                id, session.SessionId);

            return Results.Ok(new { sessionId = session.SessionId, reply });
        });

        // Stage G:「完成驗收」——手動觸發 Git commit/push + Deploy(見 workflows/accept-pipeline.json,
        // 拆出來的獨立小 Workflow),啟動邏輯刻意跟 /api/pm/dispatch 保持一致(同一個 RunRegistry),
        // 讓 Chat 面板可以直接複用既有的 /api/chat/{runId}/stream、/api/tasks/{runId} 端點。
        app.MapPost("/api/reports/{id}/accept", async (
            string id,
            IWorkflowEngine workflowEngine,
            IAgentOrchestrator orchestrator,
            CancellationToken cancellationToken) =>
        {
            var detail = testReportStore.GetDetail(id);
            if (detail is null)
            {
                return Results.NotFound(new { error = $"找不到測試報告:{id}" });
            }

            var workflowPath = Path.Combine(repoRoot, "workflows", "accept-pipeline.json");
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

            logger.LogInformation(
                "[Acceptance] 使用者確認驗收(測試報告 {ReportId}),啟動 Workflow '{WorkflowId}'(RunId={RunId})",
                id, definition.WorkflowId, runId);

            _ = Task.Run(async () =>
            {
                try
                {
                    // 不把這個 HTTP 請求的 cancellationToken 傳進去(跟 /api/pm/dispatch 等其他
                    // fire-and-forget 端點一致):這是背景執行的 Task.Run,生命週期不該綁在
                    // 這次 HTTP 請求/連線上,否則使用者一收到 202 回應、連線結束,背景工作就會
                    // 被跟著取消。
                    var success = await orchestrator.RunAsync(definition, workspace, runId, seedArtifacts: null);
                    tracker.Complete(success);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[Acceptance] Workflow '{RunId}' 執行時發生未預期例外", runId);
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

        app.MapPost("/api/planning/{sessionId}/start-development", async (
            string sessionId,
            PlanningFinalizeRequest request,
            IWorkflowEngine workflowEngine,
            IAgentOrchestrator orchestrator,
            CancellationToken cancellationToken) =>
        {
            var session = planningSessions.Get(sessionId);
            if (session is null)
            {
                return Results.NotFound(new { error = "找不到這個規劃對話 session(AI.Host 重啟過的話會遺失,重新開始一個新的對話即可)。" });
            }

            var finalSpec = session.GetFinalSpec();
            if (string.IsNullOrWhiteSpace(finalSpec))
            {
                return Results.BadRequest(new { error = "這個 session 還沒有產生規格書,請先呼叫 /finalize(對應 Chat 面板的「確認規格,產生 PRD」按鈕)。" });
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
                Content: finalSpec);

            logger.LogInformation(
                "[Planning] Session {SessionId} 啟動 Workflow '{WorkflowId}'(RunId={RunId})",
                sessionId, definition.WorkflowId, runId);

            // 跟 /api/chat 一樣是 fire-and-forget,進度一樣靠 /api/chat/{runId}/stream 這個既有的
            // SSE 端點推送,不需要另外做一套串流。
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
                    logger.LogError(ex, "[Planning] Workflow '{RunId}' 執行時發生未預期例外", runId);
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
    }
}
