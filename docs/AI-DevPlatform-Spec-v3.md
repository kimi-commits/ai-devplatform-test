# AI Development Platform 規格書 v3.0 — AI Development Operating System (AI-DOS)

> 本版將定位從「AI Coding Tool」提升為「AI Development Operating System」:Runtime/Execution Engine 相當於 Kernel,Tool Runtime 相當於 Driver 層,Event Bus 相當於 IPC,Artifact Store + Snapshot 相當於檔案系統。取代 v2.0。

## 1. 專案目標與定位

建立一套可擴充的 AI Development Operating System,而非單純呼叫模型的 Coding Tool。

特色:

- 可接任何 OpenAI Compatible API
- 支援 Microsoft Agent Framework(僅作為 LLM Agent 的執行後端之一)
- 支援 MCP,並可混用 Native / Plugin / REST 工具後端
- 可支援任何程式語言、任何 IDE
- Agent 可自行讀寫專案、Build、Review、修正 Build Error、執行 Test
- 事件驅動、能力宣告、Workflow 可設定化,新增 Agent 不需改動既有程式碼

## 2. 系統架構

```
                   VS Code / CLI / Web UI
                          │
                       AI Host
                          │
──────────────────────────────────────────────
                      Runtime
                          │
                  Execution Engine        ← 統一分派 LLM / Tool / Script / Workflow Agent
                          │
                      Event Bus           ← Pub/Sub,任何 Plugin 可訂閱
                          │
                   Workflow Engine        ← 解析 Workflow DSL(JSON),含分支與重試
                          │
                  Agent Orchestrator      ← 依 Workflow 決定下一個要啟動的 Agent
                          │
        ┌─────────────────┼──────────────────┐
        ▼                 ▼                  ▼
    Planner            Coder              Reviewer   ...  QA / Build / Git / Deploy
        │                 │                  │
        └─────────────────┴──────────────────┘
                          │
──────────────────────────────────────────────
                    Tool Runtime
        MCP  │  Native  │  Plugin  │  REST
──────────────────────────────────────────────
   Workspace │ Snapshot │ Artifact │ Knowledge Base │ Memory │ Configuration │ Logging
```

Execution Engine 是本版最關鍵的新增層:並非所有 Agent 都需要 LLM。例如 Build Agent 只是「收到 `BuildRequested` → 執行 `dotnet build` → 回報結果」,不需要 ChatClient、Memory、Prompt、Tool Calling。Execution Engine 統一分派四種 Agent 類型:

| Agent 類型 | 說明 | 範例 |
|---|---|---|
| LLM Agent | 需要呼叫模型、有 Prompt、可能需要 Tool Calling | Planner、Coder、Reviewer |
| Tool Agent | 直接呼叫單一 Tool,不需要模型 | Build、Git |
| Script Agent | 純程式邏輯,無模型無 Tool 呼叫協商 | 格式檢查、版本號遞增 |
| Workflow Agent | 本身是一段子 Workflow 的封裝 | Deploy(內含多步驟) |

Microsoft Agent Framework 只服務 LLM Agent 這一種類型,不是全局唯一路徑,與 Agent Orchestrator 職責明確分開:

| 層級 | 職責 |
|---|---|
| Microsoft Agent Framework | LLM Agent 內部:呼叫 Model、Prompt 組裝、Tool Calling 協商 |
| Execution Engine | 依 Agent 類型分派到正確的執行後端(MAF / 直接 Tool 呼叫 / 純程式碼 / 子 Workflow) |
| Agent Orchestrator | 跨 Agent:根據事件與 Workflow DSL 決定下一步 |

## 3. Solution 結構

新元件對應到既有專案,不新增過多頂層專案:

```
AI-DevPlatform.sln
src/
  AI.Host              — 啟動、DI、Logging、初始化(不變)
  AI.Core              — 所有 Interface(不變)
  AI.Models            — Model Registry(不變)
  AI.Runtime           — Execution Engine + Event Bus(新增內容)
  AI.Workflow          — Workflow Engine + Workflow DSL + Agent Orchestrator(新增內容)
  AI.Tools             — Tool Runtime:MCP / Native / Plugin / REST Adapter(新增內容)
  AI.Agents            — Planner / Coder / Reviewer / QA / Build / Git / Deploy
  AI.Artifacts         — Artifact 定義與 Artifact Store(新增專案)
  AI.Knowledge         — Knowledge Base(新增專案,Phase 6 才實作,Phase 1 先定介面)
  AI.Memory            — Agent 私有 Memory(不變)
  AI.MCP               — MCP Server/Client 實作
  AI.Plugin            — Plugin System / Agent Package(擴充內容)
  AI.Configuration     — 設定檔(不變)
  AI.Logging           — Serilog(不變)
extensions/
  vscode-extension
  mcp-server
docs/
samples/
```

## 4. Workspace 與 Snapshot

```
Workspace
  Name / RootPath / Language / Framework / GitBranch / BuildProfile / Settings
```

新增 **Workspace Snapshot**,解決「Agent A 改完、Agent B 看到不同版本」的問題:

```
WorkspaceSnapshot
  SnapshotId
  WorkspaceId
  GitCommitSha
  WorktreePath      ← 若為平行 Coder,指向該 Coder 專屬 worktree
  CreatedAt
```

每個 Artifact(第 12 節)都必須標記自己是基於哪個 SnapshotId 產生,確保 Reviewer / QA 永遠審查同一份狀態。單一 Coder(Phase 1)階段先定義介面,不強制使用;進入平行 Coder(Phase 4)後正式啟用。

## 5. Model Registry

不變,集中管理各 Agent 對應模型(Planner→Nemotron、Coder→DeepSeek、Reviewer→Nemotron、QA→Kimi、Summary→GPT、Translator→Gemini),換模型不需改 Agent。

## 6. Capability(能力宣告,取代直接依賴 Tool)

Agent 不直接知道 Tool 是什麼,而是宣告需要的 Capability,由 Runtime 在執行時注入對應的 Tool Runtime 實作。Capability 同時攜帶風險等級,取代 v2 獨立的 Guardrail 清單,審批邏輯統一掛在這裡:

```json
{
  "agent": "Coder",
  "capabilities": [
    { "name": "File.Read",  "risk": "Low" },
    { "name": "File.Write", "risk": "Medium" },
    { "name": "Git.Commit", "risk": "Medium" },
    { "name": "Git.Push",   "risk": "High" }
  ]
}
```

風險等級對應執行方式:

| 風險等級 | 執行方式 |
|---|---|
| Low | 全自動執行 |
| Medium | 自動執行,記錄於 Log 供事後審查 |
| High | 需使用者在 VS Code / CLI 明確確認才能執行(例如 git push、Deploy、Delete、Docker) |

## 7. Event Bus

Agent 之間不直接呼叫,而是發布事件(`TaskCreated`、`CodeGenerated`、`BuildFailed`...),由訂閱者處理。任何 Plugin(例如未來的 Documentation Agent)都可以訂閱事件,不需修改既有 Agent 程式碼。

```
Planner → Publish(TaskCreated) → Event Bus → Coder 訂閱 → 執行
                                            → (未來)Documentation Agent 訂閱 → 執行
```

MVP 階段用 Host 內的記憶體 Pub/Sub(例如 .NET Channel)即可,不需要 Kafka/NATS;介面設計成可替換,未來要跨行程時再升級後端,不影響 Agent 程式碼。事件需保證同一 WorkflowId 內的處理順序。

## 8. Workflow Engine 與 Workflow DSL

Workflow 定義從程式碼移到 JSON,且必須支援分支與重試(單純有序清單無法表達 `BuildFailed → CoderRetry`):

```json
{
  "workflowId": "default-pipeline",
  "steps": [
    { "id": "plan",   "agent": "Planner",  "onSuccess": "code" },
    { "id": "code",   "agent": "Coder",    "onSuccess": "review", "onFailure": "code", "maxRetries": 3 },
    { "id": "review", "agent": "Reviewer", "onSuccess": "qa",     "onFailure": "code" },
    { "id": "qa",     "agent": "QA",       "onSuccess": "build",  "onFailure": "code" },
    { "id": "build",  "agent": "Build",    "onSuccess": "git",    "onFailure": "code", "maxRetries": 3 },
    { "id": "git",    "agent": "Git",      "onSuccess": "deploy" },
    { "id": "deploy", "agent": "Deploy" }
  ],
  "onRetryExceeded": "EscalateToHuman"
}
```

Phase 4 平行 Coder 啟用後,`steps` 支援 `parallel` 節點:

```json
{ "id": "code", "parallel": ["coderA", "coderB"], "onAllSuccess": "merge" }
```

新增 Agent(例如 Security)只需在 DSL 加一個 step,不需重新編譯。

## 9. Agent Orchestrator

依 Workflow Engine 解析出的狀態機,監聽 Event Bus 上的事件,決定下一個要啟動的 Agent。Orchestrator 本身不含業務邏輯,只做路由。失敗重試上限與升級人工介入的規則定義在 DSL 的 `maxRetries` / `onRetryExceeded`(見第 8 節),取代原本寫死在程式碼裡的邏輯。

## 10. Artifact(取代鬆散的 WorkflowContext.Payload)

Workflow 之間永遠傳遞強型別 Artifact,而不是 string 或鬆散物件:

```
Artifact(base)
  ArtifactId / Type / WorkflowId / SnapshotId / CreatedAt / RefPath

CodeArtifact    { Files[] }
DiffArtifact    { Diff }
ReviewArtifact  { Findings[], Verdict }
TestArtifact    { Results[], Coverage }
BuildLogArtifact{ Log, ExitCode }
ScreenshotArtifact { ImagePath }
PRArtifact      { Url, Branch }
DocumentArtifact{ Content }
```

事件本身只攜帶 `ArtifactId`(指標),實際內容存放於 Artifact Store(檔案系統 + SQLite metadata),避免大型內容(Build Log、截圖)塞爆 Event Bus。範例流程:

```
Coder → CodeArtifact → Reviewer → ReviewArtifact → QA → (ReviewArtifact + TestArtifact) → Build
```

## 11. Tool Runtime

Tool 不強制走 MCP,依場景選擇最適合的後端:

```
Tool Runtime
  ├─ MCP Adapter      (跨進程、標準化,例如 File/Search/Git)
  ├─ Native Adapter   (in-process,例如 Unity Tool——Unity Editor API 本質只能 in-process 呼叫)
  ├─ Plugin Adapter   (第三方語言 Plugin,例如 Rust/Go/Python Plugin)
  └─ REST Adapter     (呼叫外部服務,例如公司內部 API)
```

Agent 只透過 Capability(第 6 節)取得 Tool,不知道背後是哪個 Adapter,更換後端不影響 Agent 程式碼。

## 12. Knowledge Base

與 Agent 私有 Memory(第 13 節)分開,存放靜態、跨 Agent 共用的知識:

```
Knowledge Base
  Unity Coding Guideline / Architecture / Company Rule / API Doc
```

實作上包裝成一個 Capability(`Knowledge.Query`),透過第 6 節機制注入給 Planner / Coder / Reviewer。MVP 階段用 Markdown 文件 + Search Tool 即可,Phase 6 視需要升級為向量檢索。

## 13. Memory

Agent 私有、跨次執行的長期知識,與 Artifact(單次 Workflow 內傳遞)、Knowledge Base(靜態共用知識)三者職責分開,不可混用:

- Planner:需求歷史
- Coder:過去修改脈絡
- Reviewer:曾 Review 過的 Diff
- QA:歷史 Build Error 模式

Memory 不跨 Agent 共享。

## 14. Prompt Template

System Prompt 全部抽離程式碼,存成獨立檔案,支援多語系與版本號,修改不需重新編譯:

```
prompts/
  planner.v1.md
  coder.v1.md
  reviewer.v1.md
  qa.v1.md
  zh-TW/planner.v1.md
  en/planner.v1.md
```

## 15. Plugin System / Agent Package

任何人可新增 Rust / Python / Unity / Go Plugin。進一步將「Tool + Prompt + Workflow + Configuration」打包成可安裝的 **Agent Package**(取代單純 Plugin 的概念),Phase 7 才實作安裝機制,但 Manifest 格式建議現在先定:

```json
{
  "name": "unity-agent-package",
  "version": "1.0.0",
  "agents": ["Coder", "Build"],
  "tools": ["UnityTool"],
  "prompts": "prompts/unity/",
  "workflow": "workflows/unity-pipeline.json"
}
```

## 16. VS Code Extension

提供聊天、Diff、Accept/Reject、Streaming、Log、Agent 狀態、Task Tree、Terminal,以及第 6 節 High 風險 Capability 的確認 UI。

## 17. Logging

全部 Agent 記錄:開始、結束、Tool 呼叫、耗時、Token、Cost。

## 18. Configuration

```json
{
  "Models": {
    "Planner": "nemotron",
    "Coder": "deepseek",
    "QA": "kimi"
  },
  "Workflow": "workflows/default-pipeline.json",
  "CapabilityRisk": {
    "File.Read": "Low",
    "File.Write": "Medium",
    "Git.Commit": "Medium",
    "Git.Push": "High",
    "Deploy.*": "High",
    "Docker.*": "High"
  }
}
```

## 19. Roadmap

**Phase 0 — 框架驗證**
最小 Demo:一個 LLM Agent + 一個 Model + 一個 Tool,驗證 Microsoft Agent Framework 在 .NET 10 下的穩定度與文件完整度。

**Phase 1 — 單一序列 Pipeline + 核心骨架**
Execution Engine(四種 Agent 類型分派)、Event Bus(記憶體內)、Workflow Engine + DSL(含分支/重試,不含 parallel)、Capability 宣告 + 風險分級、Artifact 基本型別 + Store、Prompt Template 外部化。跑通 `Planner → Coder → Reviewer → QA → Build`(單一 Coder)。Workspace Snapshot 僅定義介面,尚不啟用。

**Phase 2 — MCP 與多後端 Tool**
File / Search / Git MCP Tool,Tool Runtime 至少實作一種 Native Adapter 驗證可行性。

**Phase 3 — VS Code Extension**
含 High 風險 Capability 的確認 UI。

**Phase 4 — 平行 Coder**
Workspace Snapshot 正式啟用、Merge Agent、Workflow DSL 的 `parallel` 節點。

**Phase 5 — Unity Tool**
採 Native Adapter。

**Phase 6 — Knowledge Base 與 Multi Workspace**
Knowledge Base 正式導入(視需要升級向量檢索)。

**Phase 7 — Agent Marketplace**
Agent Package 打包與安裝機制。

**Phase 8 — Cloud / Docker / Kubernetes**

## 20. 技術選型

| 模組 | 技術 |
|---|---|
| Runtime | .NET 10(依實際部署環境的 Runtime 調整,Phase 0 骨架已驗證 net10.0 可正常建置) |
| LLM Agent 執行後端 | Microsoft Agent Framework(Phase 0 驗證) |
| LLM SDK | OpenAI .NET SDK |
| Model Provider | NVIDIA NIM / OpenAI / OpenRouter / Ollama |
| Tool Protocol | MCP + Native + Plugin + REST |
| Event Bus(MVP) | 記憶體內 Pub/Sub(.NET Channel),介面可替換 |
| Extension | VS Code Extension API(TypeScript) |
| DI | Microsoft.Extensions.DependencyInjection |
| Logging | Serilog |
| Config | Microsoft.Extensions.Configuration |
| Storage | SQLite(MVP,含 Artifact Metadata)→ PostgreSQL(可選) |
| IPC | gRPC(Host ↔ Extension) |
| Build | MSBuild、Unity BatchMode、Go CLI |
| 並發控制 | Git Worktree + Workspace Snapshot |
