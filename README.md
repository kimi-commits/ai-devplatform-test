# AI Development Platform(AI-DOS)骨架

依規格書 v3.0(`docs/AI-DevPlatform-Spec-v3.md`)產生的 Phase 1 專案骨架。

## 目錄結構

```
AI-DevPlatform.sln
src/
  AI.Host            — 啟動、DI 組裝(Program.cs)
  AI.Core            — 所有 Interface(IAgent、ITool、IWorkflowEngine、IEventBus、IArtifact...)
  AI.Models          — Model Registry + OpenAI Compatible Provider
  AI.Runtime         — Execution Engine(依 AgentKind 分派)+ 記憶體內 Event Bus
  AI.Workflow        — Workflow DSL 載入器、Workflow Engine(含分支/重試)、Agent Orchestrator
  AI.Tools           — Tool Runtime + MCP/Native/Plugin/REST 四種 Adapter 骨架
  AI.Agents          — Planner / Coder / Reviewer / QA / Build / Git / Deploy 七個 Agent
  AI.Artifacts       — CodeArtifact / ReviewArtifact / TestArtifact 等型別 + 檔案系統版 ArtifactStore
  AI.Knowledge       — Knowledge Base 介面 + Markdown 版最小實作
  AI.Memory          — Agent 私有 Memory(不共享)
  AI.MCP             — MCP Client 骨架(Phase 2 串接 extensions/mcp-server)
  AI.Plugin          — Agent Package Manifest 格式(Phase 7 才實作安裝機制)
  AI.Configuration   — appsettings.json 讀取
  AI.Logging         — Serilog 設定
extensions/
  vscode-extension   — VS Code Extension(TypeScript,已 npm install + tsc 型別檢查通過)
  mcp-server         — MCP Server(TypeScript,已 npm install + tsc 型別檢查通過)
config/
  appsettings.json   — Models / Workflow / CapabilityRisk 設定
workflows/
  default-pipeline.json — Workflow DSL:Planner → Coder → Reviewer → QA → Build → Git → Deploy
prompts/
  planner.v1.md / coder.v1.md / reviewer.v1.md / qa.v1.md(含 zh-TW / en 版本示範)
docs/
  AI-DevPlatform-Spec-v3.md — 完整規格書
samples/
  Phase0-MafDemo    — 驗證 Microsoft Agent Framework(+ NVIDIA NIM)的獨立最小 Demo
```

## 在 VS Code 開啟

1. 用 VS Code 開啟這個資料夾(`AI-DevPlatform/`)。
2. 安裝 C# Dev Kit 擴充套件(用來開啟 `AI-DevPlatform.sln`)。
3. 確認本機已安裝 **.NET 10 SDK**(所有專案已改為 `net10.0`,對應你機器上實際安裝的 Runtime;
   此骨架是在沒有 .NET SDK 的沙盒環境中手動撰寫 `.sln`/`.csproj` 產生的,已在使用者本機驗證
   `dotnet build` 全部 14 個專案成功):
   ```
   dotnet restore
   dotnet build
   ```
4. 執行 Host(會載入 `config/appsettings.json` 與 `workflows/default-pipeline.json`,
   目前只會印出初始化 Log,尚未跑真正的 Agent 邏輯):
   ```
   dotnet run --project src/AI.Host
   ```

## VS Code Extension / MCP Server

```
cd extensions/vscode-extension && npm install && npm run compile
cd extensions/mcp-server && npm install && npm run build
```

兩者已在沙盒環境中 `npm install` 並以 `tsc --noEmit` 驗證型別正確,可直接編譯。

## Phase 0:驗證 Microsoft Agent Framework

`samples/Phase0-MafDemo/` 是獨立的最小 Demo,驗證「一個 LLM Agent + 一個 Model + 一個 Tool」能
透過 Microsoft Agent Framework(`Microsoft.Agents.AI` + `Microsoft.Agents.AI.OpenAI`,目前穩定版
1.8.0 / 1.9.0)打通,供應商是 NVIDIA NIM(OpenAI-Compatible API)。

執行前先到 <https://build.nvidia.com> 申請 API Key,然後:

```
export NIM_API_KEY="nvapi-..."
# 以下兩個可省略,已有預設值:
export NIM_BASE_URL="https://integrate.api.nvidia.com/v1"
export NIM_MODEL_NAME="nvidia/llama-3.3-nemotron-super-49b-v1.5"

dotnet run --project samples/Phase0-MafDemo
```

模型清單會隨 NVIDIA 的 NIM Catalog 更新,若指定的模型下架,到 <https://build.nvidia.com> 查目前
可用的模型 ID 再覆寫 `NIM_MODEL_NAME`。

## Phase 1:單一序列 Pipeline 已可真實執行

`OpenAiCompatibleProvider.cs` 已改用 Phase 0 驗證過的 `OpenAIClient` + `GetChatClient(model).AsAIAgent(...)`
呼叫真正的模型。`Planner → Coder → Reviewer → QA → Build → Git → Deploy` 這條 Workflow DSL 現在
會由 `AgentOrchestrator` 真的逐步執行:Planner/Coder/Reviewer/QA 呼叫 NVIDIA NIM 產生內容,
Build 對 Workspace(目前指向這個 repo 本身)執行真正的 `dotnet build`,Git 執行 `git status`
(若目錄還不是 git repo,視為資訊性結果、不中斷流程,不會真的 commit/push)。

執行方式跟 Phase 0 一樣先設定環境變數,再跑 Host:

```
export NIM_API_KEY="nvapi-..."
dotnet run --project src/AI.Host
```

Log 會依序印出每個 Step 的執行結果與產出的 Artifact 數量,`.artifacts/` 資料夾(自動建立於
repo 根目錄)存放每個 Artifact 的 JSON 內容,可以打開來看 Planner/Coder/Reviewer/QA 實際生成
的文字。

## Phase 2:MCP Tool Runtime 與 Native Adapter 已可真實執行

`extensions/mcp-server` 的 Search / Git 工具已從 TODO 空殼改成真實實作(純 Node 檔案系統遞迴
掃描 + 字串/正規比對,`git.status` / `git.diff` / `git.commit` / `git.checkout` / `git.branch`
都是真的 spawn `git` 指令)。新增 `scripts/smoke-test.mjs`,用官方 `@modelcontextprotocol/sdk`
的 `Client` + `StdioClientTransport` 實際 spawn `dist/index.js`,走一次完整 MCP 協定(initialize
→ tools/list → tools/call),驗證 17 個工具(File/Search/Git/Build/Terminal/Browser/Unity)全部
可用:

```
cd extensions/mcp-server
npm install
npm run build
npm run smoke-test   # 14 個斷言全部通過
```

`AI.MCP` 的 `McpClient` 已改用官方 `ModelContextProtocol` C# SDK(1.3.0)的
`StdioClientTransport` + `McpClient.CreateAsync(...)`,用 `node dist/index.js` 把
`extensions/mcp-server` 當子行程啟動,`ListToolNamesAsync` / `CallToolAsync` 都是真的走 MCP 協定
(不是 TODO 骨架)。

Tool Runtime 現在同時掛了兩種後端,驗證規格書 v3 第 11 節「多後端」設計確實可行:

- **Native Adapter**(`NativeFileToolHandlers`):`file.readFile` / `writeFile` / `deleteFile` /
  `copy` / `move` 直接用 `System.IO`,不透過任何子行程。
- **MCP Adapter**(`McpToolAdapter` → `AI.Core.Tools.IMcpToolInvoker` → `AI.MCP`):
  Search / Git / Build / Terminal / Browser / Unity 這些工具透過 MCP 協定呼叫
  `extensions/mcp-server`。

`AI.Host/Program.cs` 啟動時會自動組裝這兩個 Adapter 並掛進同一個 `IToolRuntime`;若
`extensions/mcp-server/dist/index.js` 不存在,會印出警告並只註冊 Native Adapter,不會讓整個
Host 啟動失敗。

Agent 端也真的開始呼叫 Tool 了:

- **CoderAgent** 會把 LLM 的修改建議透過 `IToolRuntime`(`file.writeFile`,走 Native Adapter)
  寫成 workspace 底下 `.ai-suggestions/coder-{artifactId}.md` 的真實檔案,`CodeArtifact.Files`
  會記錄實際寫出的路徑。
- **GitAgent** 改成透過 `IToolRuntime`(`git.status`,走 MCP Adapter → `extensions/mcp-server`)
  取得工作目錄狀態,不再直接用 `Process` 呼叫 `git`——這樣 Pipeline 執行時會真的走一次
  `Agent → ToolRuntime → McpToolAdapter → AI.MCP → Node MCP Server` 的完整路徑。

執行方式跟 Phase 1 一樣(先 build MCP Server,再跑 Host):

```
cd extensions/mcp-server && npm install && npm run build && cd ../..
export NIM_API_KEY="nvapi-..."
dotnet run --project src/AI.Host
```

## 目前完成度(對應規格書 Roadmap Phase 2)

## Phase 3:Capability Guard + High 風險確認 UI(Console 與 VS Code 兩種)

規格書 v3 第 6 節的 `ICapabilityGuard`/`Capability` 介面在 Phase 1 就先定義好了,但一直沒有真正
的實作、也沒有任何地方真的呼叫它。Phase 3 把這個機制的後端做對、做穩,並且把「怎麼問人」這件事
拆成獨立的 `IApprovalPrompt` 介面,現在有兩種實作可以切換,`ToolRuntime`/Agent 完全不用改:

- **`AppConfigCapabilityGuard`**(`AI.Runtime/Capabilities/`):`GetRisk` 從 `config/appsettings.json`
  的 `CapabilityRisk` 查風險等級,支援 `"Docker.*"` 這種前綴通配;`RequestApprovalAsync` 委派給
  注入的 `IApprovalPrompt` 去問人。
- **`ToolCapabilityMap`**(`AI.Tools/Runtime/`):把實體 Tool 名稱(`git.push`、`file.deleteFile`、
  `terminal.run` 等)對應到抽象的 Capability 名稱,只列真正有風險的操作;`file.readFile`、
  `search.*`、`git.status` 這類唯讀操作沒有對應 Capability,一律自動執行。
- **`ToolRuntime.InvokeAsync`** 會先查 Capability 風險等級:Low 直接放行,Medium 放行但記一筆
  `LogWarning` 供事後審查,High 會先呼叫 `RequestApprovalAsync` 卡住流程,使用者拒絕就直接回傳
  失敗結果,完全不會呼叫到底層的 Adapter。
- `config/appsettings.json` 的 `CapabilityRisk` 新增兩筆:`File.Delete: High`(刪檔案風險比寫檔案
  高)、`Terminal.Execute: High`(`terminal.run` 是任意 shell 指令,風險等同 Docker/Deploy)。

`IApprovalPrompt` 的兩種實作,用環境變數 `AI_DEVPLATFORM_APPROVAL_MODE` 切換:

- **`console`(預設)**:`ConsoleApprovalPrompt`,在終端機印出提示,輸入 `y`/`yes` 才核准。已實測
  驗證過核准/拒絕兩條路徑都正確(見下方「已知限制 / 變更紀錄」)。
- **`vscode`**:`VsCodeBridgeApprovalPrompt`,把請求寫成
  `.ai-devplatform/approvals/{requestId}.request.json`,輪詢等待
  `{requestId}.response.json` 出現(逾時預設 10 分鐘,逾時視為拒絕);
  `extensions/vscode-extension/src/approvalBridge.ts` 用 `FileSystemWatcher` 監看這個目錄,
  偵測到新的 request 就跳出 `showWarningMessage` 的 Modal 對話框(規格書 v3 第 16 節「High 風險
  Capability 的確認 UI」),使用者按下「核准」/「拒絕」後寫回 response 檔案。兩個獨立 process
  (AI.Host 是 .NET Console App、VS Code Extension 是另一個 Node process)之間沒有現成的 IPC
  管道,用檔案系統當媒介是最簡單、不用額外套件、也最容易除錯的作法——這個專案本身也大量用
  「檔案即介面」(`.artifacts/`、`.ai-suggestions/`)。

**目前的 7 步 Pipeline 還不會真的觸發 High 風險確認**——GitAgent 只呼叫唯讀的 `git.status`,
DeployAgent 還是空殼,Coder 寫檔案走的是 Medium(`file.writeFile`)。為了能實際驗證這個機制擋不擋
得住,`AI.Host/Program.cs` 留了一段獨立的驗證路徑,預設不會執行,設定環境變數才會跑:

```
export AI_DEVPLATFORM_TEST_CAPABILITY_GUARD=1
dotnet run --project src/AI.Host
```

跑起來後,Host 會建立一個暫存測試檔案,然後呼叫 `file.deleteFile`(對應 `File.Delete`,High 風險)。

### 測試 Console 模式(已驗證過,直接用終端機就能測)

```
export AI_DEVPLATFORM_TEST_CAPABILITY_GUARD=1
dotnet run --project src/AI.Host
```

畫面上會跳出核准提示;輸入 `n` 會看到檔案沒被刪掉、`Success=False`,輸入 `y` 會看到檔案被
刪掉、`Success=True`。這段跑完之後,原本的 7 步 Pipeline 會照常繼續執行,不受影響。

### 測試 VS Code 模式(已在實機 VS Code 完整驗證過完整迴路)

1. 用 VS Code 開啟 `extensions/vscode-extension` 這個資料夾(不是整個 repo)。
2. `npm install && npm run compile`(如果還沒裝過依賴)。已內建 `.vscode/launch.json` +
   `tasks.json`,直接按 **F5**(或 Run and Debug 面板選 "Run AI-DOS Extension")應該會自動編譯
   並開一個新的「Extension Development Host」視窗。
3. 在這個新開的視窗裡,改用 **File → Open Folder** 開啟 `AI-DevPlatform` repo 的**根目錄**
   (不是 `extensions/vscode-extension`)——這一步很重要,因為 Extension 會用目前開啟的
   workspace 資料夾算出 `.ai-devplatform/approvals/` 的路徑,必須跟 AI.Host 算出來的路徑
   (repo 根目錄底下)一致才能對上。
4. 回到原本的終端機(在 repo 根目錄):
   ```
   export AI_DEVPLATFORM_APPROVAL_MODE=vscode
   export AI_DEVPLATFORM_TEST_CAPABILITY_GUARD=1
   dotnet run --project src/AI.Host
   ```
5. 預期會在 Extension Development Host 視窗跳出一個 Modal 對話框:「AI-DOS 要求核准 High 風險
   操作:File.Delete」,detail 顯示操作說明。按「核准」或「拒絕」,終端機那邊應該在 1 秒內
   (輪詢間隔 500ms)顯示對應結果,跟 Console 模式測到的行為一致。
6. 可以打開 Extension Development Host 視窗的 Output 面板(View → Output),下拉選單選
   "AI-DOS",會看到 `[ApprovalBridge]` 開頭的 Log,方便對照。

## Phase 4:平行 Coder、Workspace Snapshot、Merge Agent

規格書 v3 第 8 節的 Workflow DSL 從 Phase 1 就已經定義好 `WorkflowStep.Parallel`/`OnAllSuccess`
欄位、`WorkflowEngine.GetNextStep` 也早就支援 `current.OnSuccess ?? current.OnAllSuccess` 的轉移
邏輯,但一直沒有東西真的產生/消費「平行 Step」——`AgentOrchestrator.RunAsync` 原本強制要求每個
Step 必須有 `Agent`,遇到只有 `Parallel` 沒有 `Agent` 的 Step 會直接中止。Phase 4 把這一塊接起來:

- **`GitWorkspaceSnapshotProvider`**(`AI.Runtime/Workspace/`):`IWorkspaceSnapshotProvider`
  (規格書 v3 第 4 節)的第一個實作。`CreateSnapshotAsync` 用 `git rev-parse HEAD` 探測目前的
  commit sha(不是 git repo 就老實回報 `"unknown"`,不假裝有值),配一個新的 `SnapshotId`;
  `WorktreePath` 這次刻意留 `null`——真正的 git worktree 隔離需要 Workspace 本身是乾淨的 git
  repository 才能端到端驗證,而目前開發/驗證用的環境都不是 git repo(見 GitAgent 的
  `git.status` 呼叫結果),寫了也測不到,所以先老實留白,類別註解裡有完整說明。
- **`AgentOrchestrator`** 新增 `RunParallelBranchesAsync`/`RunSingleBranchAsync`:偵測到
  `Step.Parallel` 非空時,對每個具名 Agent 各自呼叫 `IWorkspaceSnapshotProvider.CreateSnapshotAsync`
  拿一個獨立 SnapshotId,用 `Task.WhenAll` 同時執行,整體成功與否是所有分支的 AND(任一分支
  失敗就整個 Step 視為失敗,交給 DSL 的 `onFailure`/`maxRetries` 處理,不做「部分成功也算過」
  這種模糊語意)。跑完之後接回原本同一套 `GetNextStep` 轉移邏輯,序列 Step 的行為完全不受影響。
- **`MergeAgent`**(`AI.Agents/`,`AgentKind.Tool`,不呼叫模型、決定性):把 `InputArtifacts`
  裡的 `CodeArtifact` 依 `SnapshotId` 分組,產出一份「各分支寫了哪些檔案、Summary 是什麣」的
  比較報告(`DocumentArtifact`)。「各分支寫了哪些檔案、Summary 是什麼」這部分刻意不做真正的
  `git merge`/自動選分支——那需要真正的 worktree 隔離才適合做,而且「選哪個分支」本身也該留給
  Reviewer/QA 或人來判斷,不該由 Merge 這一步自己決定。
- **`workflows/parallel-pipeline.json`**(新增,不影響原本的 `default-pipeline.json`):
  `plan → code(parallel: [CoderA, CoderB], onAllSuccess: merge) → merge → review → qa → build
  → git → deploy`。`CoderAgent` 從 Phase 1 就支援具名多實例(constructor 的 `name` 參數,原本
  就是為了 Phase 4 這個用途設計的),`AI.Host/Program.cs` 新增 `CoderA`/`CoderB` 兩個具名實例,
  `config/appsettings.json` 的 `Models` 也對應補了 `CoderA`/`CoderB` 兩筆(先指向跟 `Coder` 一樣
  的模型,避免需要額外申請/配置)。
- 用環境變數切換要跑哪個 Pipeline,不用改程式碼:

  ```
  export AI_DEVPLATFORM_WORKFLOW=parallel
  dotnet run --project src/AI.Host
  ```

  不設定這個環境變數(或設別的值)沿用原本的序列 Pipeline,兩者互不影響。

**這次的改動只在沙盒內做過語法層面檢查**(C# 括號配對、JSON 合法性),**還沒有實際
`dotnet build`/`dotnet run --project src/AI.Host` 跑過**,尤其請注意:平行執行需要
`OpenAiCompatibleProvider` 真的能同時處理兩個並發的 HTTP 請求(理論上沒問題,但沒實測過);
另外 `AI_DEVPLATFORM_WORKFLOW=parallel` 這條路徑會同時觸發兩次 LLM 呼叫,請確認 NVIDIA NIM 的
API Key 沒有速率限制問題。請在本機跑一次確認整條 Pipeline(尤其是 `merge` 這一步的報告內容跟
兩個 Coder 分支的 SnapshotId 有沒有對得上)。

## Phase 3 延伸:Chat、Diff、Streaming、Task Tree(VS Code Extension 完整功能,規格書第 16 節)

規格書 v3 第 16 節列的 VS Code Extension 功能(聊天、Diff、Accept/Reject、Streaming、Agent 狀態、
Task Tree)在 Phase 1~4 都還是最小骨架,沒有跟 AI.Host 串接。這裡把這幾塊接起來,過程中有兩個
先跟使用者確認過的架構決策:

1. **IPC 用 HTTP + Server-Sent Events,不是規格書寫的 gRPC。** 規格書 v3 第 20 節的技術選型寫
   gRPC,但這個沙盒沒有 .NET SDK,gRPC 需要的 `.proto` 工具鏈(雙邊 codegen)完全無法驗證產出的
   程式碼長什麼樣子,風險太高。改用 ASP.NET Core 內建的 Minimal API + Kestrel,Streaming 用 SSE
   表示,複雜度低很多,之後真的要換 gRPC 只需要換 Extension 的呼叫層(`apiClient.ts`),不影響
   Agent/Workflow 邏輯本身。
2. **Chat 的定位是「輸入需求 → 啟動一次 Workflow → 即時串流顯示各 Step 進度」**,不是自由對話
   某個 Agent。這樣 Chat/Diff/Task Tree 三個 UI 背後接的是同一個 Workflow 執行,不需要另外設計
   一套「單獨呼叫一個 Agent」的執行模型,跟現有 AgentOrchestrator/DSL 架構完全相容。

**後端(AI.Host)變更:**

- `AI.Host.csproj` 新增 `<FrameworkReference Include="Microsoft.AspNetCore.App" />`——刻意不把
  整個專案的 Sdk 換成 `Microsoft.NET.Sdk.Web`,只加這個 FrameworkReference,這是官方支援、對
  既有 Console App 影響最小的做法。
- `Program.cs` 改用 `WebApplicationBuilder` 取代原本單純的 `ServiceCollection`,DI 組裝內容完全
  不變。新增環境變數 `AI_DEVPLATFORM_MODE`:
  - `pipeline`(預設,不設定就是這個):跟 Phase 1~4 完全一樣,建好 DI 就跑一次 Workflow、
    印出結果、結束程式,這段邏輯**一行都沒有改**,確保已經驗證過的 CLI 行為不受影響。
  - `serve`:不跑 CLI pipeline,改成掛上 Chat/Diff/Task Tree 的 HTTP+SSE API 並常駐監聽
    (預設 `http://localhost:5170`,可用標準的 `ASPNETCORE_URLS` 環境變數覆寫)。
- 新增 `AI.Host/Server/RunTracker.cs`(`RunTracker`/`RunRegistry`/`StepState`):追蹤單一次
  Workflow 執行(RunId 直接沿用 `IWorkflowEngine.StartAsync` 產生的 workflowId)的即時狀態,用
  `System.Threading.Channels.Channel<string>` 實作簡易 Pub/Sub 給多個 SSE 連線共用,並保留
  History 讓比較晚連上的用戶端可以補播。
- 新增 `AI.Host/Server/ChatEndpoints.cs`,提供:
  - `POST /api/chat`:body `{ message, workflow? }`(workflow 預設 `default`,可填 `parallel`
    切換到 Phase 4 的平行 Pipeline),立刻回傳 `{ runId, workflowId, steps }`,實際執行在背景跑。
  - `GET /api/chat/{runId}/stream`(SSE):即時推送 `stepStarted`/`stepSucceeded`/`stepFailed`/
    `completed` 事件。
  - `GET /api/tasks/{runId}`:目前的 Step 狀態快照(給 Task Tree 用,也可以用來補上晚一步連線
    的狀態)。
  - `GET /api/diff/{artifactId}`:回傳 `CodeArtifact` 的 Summary 跟檔案內容;`POST
    /api/diff/{artifactId}/accept`(no-op,見下方範疇說明)、`POST /api/diff/{artifactId}/reject`
    (真的呼叫 `file.deleteFile` 刪掉建議檔案,會走 Capability Guard 的 High 風險核准流程)。
- **Diff 範疇說明(誠實記錄限制)**:`CoderAgent` 目前是直接把建議文字寫成檔案(Phase 2 就有的
  行為),不是「先產生 patch、使用者按 Accept 才真正寫入」的模型。所以這裡的 Accept 只是確認
  保留,沒有額外動作;Reject 則是真的刪除已寫入的檔案。要做到「先預覽、按下去才真的寫入」需要
  重新設計 CoderAgent 的執行時機,留待後續加強。
- `AI.Core.Events.AgentEvent` 新增 `StepStarted` 事件(Task Tree 需要即時顯示「目前跑到哪個
  Step」,不只是事後的成功/失敗),`AgentOrchestrator.RunAsync` 在每個 Step(含平行 Step)開始
  時發布這個事件。
- `IAgentOrchestrator.RunAsync` 新增選填參數 `seedArtifacts`,Chat 用它把使用者輸入的需求文字
  包成 `DocumentArtifact` 放進 Workflow 開始執行前的 Artifact 清單。`PlannerAgent` 改成讀取這個
  seed 當作真正的使用者需求(原本 Phase 1 是寫死的示範性任務,沒有 seed 時行為不變);
  `CoderAgent` 讀取任務規格的地方從 `FirstOrDefault` 改成 `LastOrDefault`——因為 InputArtifacts
  現在可能同時有「使用者原始需求」跟「Planner 消化過的任務規格」兩個 `DocumentArtifact`,Coder
  應該看後者。這是實作過程中發現、當場修正的一個小陷阱,不是額外的功能。

**前端(VS Code Extension)變更:**

- 新增 `apiClient.ts`:用 Node 內建的 `http`/`https` 模組跟上述 API 溝通(不依賴瀏覽器的
  fetch/EventSource,Extension Host 是 Node 環境沒有這兩個全域物件),手動解析 SSE
  (按 `\n\n` 切訊息、去掉 `data:` 前綴、`JSON.parse`)。
- 新增 `runStateStore.ts`:Chat/Task Tree/Agent Status 共用的記憶體內狀態(只保留「目前這一次」
  的 Run,跟現有 Orchestrator「同一時間只跑一個 Workflow instance」的既有限制一致),Chat 面板
  的 SSE 事件處理器寫入,兩個 TreeDataProvider 讀出畫面。
- 新增 `chatPanel.ts`:Webview 聊天面板,輸入需求(可選 default/parallel Pipeline)送出後即時
  顯示各 Step 進度,Step 成功時如果有產出 Artifact 會出現「顯示內容/Diff」按鈕。
- 新增 `diffPanel.ts`:Webview 顯示 Coder 產出的內容(誠實標註「不是對照既有檔案的真正 diff」)
  + Accept/Reject 按鈕。
- 新增 `taskTreeProvider.ts`(取代 `extension.ts` 原本的兩個 stub class):`AgentTaskTreeProvider`
  依 Step 分組顯示狀態(待執行/執行中/成功/失敗,對應圖示),`AgentStatusTreeProvider` 依 Agent
  分組顯示(平行 Step 的 CoderA/CoderB 會拆成兩筆)。
- `extension.ts` 接線:`aiDevPlatform.openChat` 開啟 Chat 面板;新增內部指令
  `aiDevPlatform.showDiff`(由 Chat 面板按鈕觸發,不出現在 Command Palette,因為需要 artifactId
  參數);原本的 `aiDevPlatform.acceptDiff`/`rejectDiff` 指令改成提示使用者到 Diff 面板操作
  (真正的 Accept/Reject 需要 artifactId 上下文,Command Palette 沒有這個上下文)。
- `package.json` 新增設定 `aiDevPlatform.apiBaseUrl`(預設 `http://localhost:5170`),對應
  AI.Host serve 模式監聽的位址。

**這次的改動已在沙盒內完整驗證 TypeScript 部分**(`npm run compile` 實際編譯過
`apiClient.ts`/`chatPanel.ts`/`diffPanel.ts`/`runStateStore.ts`/`taskTreeProvider.ts`/
`extension.ts`,全部通過,沒有型別錯誤);**C# 部分只做過語法層面檢查**(括號配對、XML 合法性),
**還沒有實際 `dotnet build`/`dotnet run` 驗證過**,請在本機:

```
# 一個終端機:啟動 serve 模式
export AI_DEVPLATFORM_MODE=serve
dotnet run --project src/AI.Host

# 另一個終端機或 VS Code:F5 開啟 Extension Development Host,開啟 repo 根目錄,
# 執行 "AI-DOS: Open Chat" 指令,輸入需求送出
```

請特別確認:(1) `dotnet build` 能不能正常抓到 `Microsoft.AspNetCore.App` 這個 FrameworkReference
(理論上 .NET SDK 內建就有,不需要額外安裝),(2) Chat 面板送出需求後,Task Tree / Agent Status
兩個側邊欄有沒有即時更新,(3) Step 成功後點「顯示內容/Diff」能不能正確開啟 Diff 面板並看到內容,
(4) Reject 按鈕會不會正確觸發 Capability Guard 的核准流程(用 `AI_DEVPLATFORM_APPROVAL_MODE=vscode`
測比較完整,console 模式的阻塞式 `Console.ReadLine()` 在常駐 Server 裡不是好的用法,見下方
「已知限制」)。

## Git Commit/Push 與 Deploy 真實實作

盤點 Phase 進度時發現 `GitAgent` 只呼叫唯讀的 `git.status`,commit/push 完全沒接;`DeployAgent`
是完全空的 no-op(`// TODO(Phase 8)`)。這兩個不屬於任何特定 Phase 的編號範圍,是規格書第 8 節
就定義好、但一直沒有真正做完的基礎缺口,獨立補上:

**Git commit/push**:

- `extensions/mcp-server/src/tools/gitTool.ts` 新增 `push`(以前註解寫「刻意不提供 push 操作」,
  現在把「不要繞過 Capability Guard」講清楚:push 該不該執行完全由 `ToolCapabilityMap`/
  `ICapabilityGuard` 在 `ToolRuntime` 那一層決定,工具層只負責把 `git push -u <remote> <branch>`
  真的跑出來;沒指定分支時自動用 `git rev-parse --abbrev-ref HEAD` 取得目前分支)。
- `GitAgent.ExecuteAsync` 改成三段式:`git.status` 查有沒有變更 → 有變更才 `git.commit`
  (commit 訊息取最新一份 `CodeArtifact.Summary` 的第一行,取不到就用通用訊息)→ commit 成功才
  `git.push`。三段都失敗一律視為資訊性結果(Success 仍是 true),原因寫進輸出的 Artifact 內容,
  不會讓 Workflow 中止——延續 Phase 2 就定下的容錯設計。
- `git.push` 是 High 風險 Capability,真的接上之後,`ToolRuntime` 會在執行前卡住等人工核准
  (Console y/n 或 VS Code Modal,跟 Phase 3 驗證過的機制完全相同,不需要新寫核准邏輯)。

**Deploy**:

- 這個專案沒有真實的雲端/Docker/Kubernetes 部署目標可以測試(規格書 Roadmap 把那些留到
  Phase 8),所以不假裝支援「依 Configuration 選擇 Docker/Azure/AWS/GCP 子流程」這種完整版本。
  改成最小可行版本:`config/appsettings.json` 新增 `Deploy.Command`(選填,對應新增的
  `AI.Configuration.DeployOptions`),有設定就透過新的 `deploy.execute` Native Tool
  (`NativeDeployToolHandlers.cs`,直接 `Process` 執行,跟 `BuildAgent` 跑 `dotnet build` 同一種
  做法)執行這句 shell 指令;沒設定就誠實回報「略過」,不假裝部署成功。
- `deploy.execute` 一樣是 High 風險 Capability(`ToolCapabilityMap` 從 Phase 3 就先定義好對應
  關係,這次終於有東西真的呼叫它),執行前一樣會卡住等人工核准。
- `DeployAgent` 的 `AgentKind` 順便從 `Workflow` 改成 `Tool`(跟 `GitAgent`/`BuildAgent` 一致)
  ——`ExecutionEngine` 對這兩種 Kind 的分派邏輯完全相同(都是直接呼叫 `ExecuteAsync`),只是
  Log 訊息會從「as Workflow」變成「as Tool」,單純讓分類更準確,沒有行為差異。
- Deploy 是 Pipeline 最後一步,所以刻意不像 GitAgent 那樣把失敗吞掉——部署指令真的失敗時,
  `DeployAgent` 會如實回報 `Success: false`,讓 Workflow 的最終執行結果反映真實情況。

**這批改動只在沙盒內做過語法層面檢查(括號配對、JSON/XML 合法性)跟 TypeScript 的 `tsc`/
`npm run compile`,還沒有實際 `dotnet build`/`dotnet run` 驗證過**,請在本機:

1. `dotnet build` 確認 `AI.Agents` 新增的 `AI.Configuration` 依賴沒有循環參照問題。
2. 在一個真正的 git repository 裡跑 Pipeline(這個專案目前的 outputs 資料夾本身不是 git repo,
   驗證不了 commit/push 的真實效果),確認有變更時會真的 commit,commit 成功後會跳出 High 風險
   核准(Console 或 VS Code),核准後真的 push。
3. 在 `config/appsettings.json` 設一句安全的測試指令(例如 `"Command": "echo deploy-test"`),
   確認 Deploy 步驟會走 `deploy.execute` → 卡核准 → 核准後執行 → Artifact 內容正確顯示指令輸出。

## 目前完成度(對應規格書 Roadmap Phase 0~4,VS Code Extension 已補完 Phase 3 範疇)

Execution Engine 分派邏輯、Event Bus、Workflow DSL(含分支/重試)、Capability 宣告、Artifact
型別與 Store、Prompt Template 外部化、Phase 0 的 MAF 驗證 Demo、**四個 LLM Agent 真實呼叫
NVIDIA NIM、Build/Git Agent 真實執行指令、AgentOrchestrator 完整事件路由迴圈**(Phase 1)、
**MCP Server 的 Search/Git 工具真實實作、AI.MCP 真的接官方 SDK、Native File Adapter + MCP
Adapter 雙後端接進 ToolRuntime、Coder/Git Agent 真的透過 ToolRuntime 呼叫 Tool**(Phase 2)、
**Capability Guard 後端機制、High 風險確認 UI(Console y/n 與 VS Code Modal 對話框兩條路徑都
已在使用者本機完整驗證過完整迴路)**(Phase 3)、**Workflow DSL 的 Parallel 節點正式啟用、
AgentOrchestrator 支援平行分支執行、WorkspaceSnapshot 真的被建立並標記在輸出 Artifact 上、
CoderA/CoderB 平行實例、Merge Agent 產出比較報告**(Phase 4,已在使用者本機完整驗證過,平行
Pipeline 8 個 Step 全部成功執行完畢),以及**AI.Host 的 HTTP+SSE serve 模式、Chat/Diff/Task
Tree/Agent 狀態四個 UI 真的接上 AI.Host**(Phase 3 延伸,規格書第 16 節的 VS Code Extension
完整功能,已在使用者本機 VS Code 完整驗證過 Chat → Workflow → Diff → Task Tree 整條迴路,細節見
下方變更紀錄),皆已完成並可端到端執行。**`GitAgent` 的 commit/push、`DeployAgent` 的部署指令
執行也已經真的接上**(見上方「Git Commit/Push 與 Deploy 真實實作」,尚待使用者本機驗證)。
VS Code Extension 的 Terminal 面板仍是尚未串接的部分。

**規格書 Roadmap 真正定義的 Phase 5——Unity Tool(採 Native Adapter)——完全沒有開始**:
`extensions/mcp-server/src/tools/unityTool.ts` 的 `build()` 函式目前直接回傳
`{ success: false, error: "Prefer Native Adapter for Unity; MCP path not implemented (Phase 5)." }`,
是骨架建立時留下的占位符;`NativeToolAdapter` 也還沒有任何真正的 Unity 處理常式,只有一句提到
Unity 的註解。這是這次盤點 Phase 進度時發現、之前誤把 Chat/Diff/Task Tree 標成「Phase 5」而蓋過去
的問題,已在下方變更紀錄記錄修正。

尚未實作(標記為 `TODO`,留在對應檔案內):

- **規格書 Roadmap 的 Phase 5(Unity Tool,採 Native Adapter)完全沒有實作**,見上方說明。
- VS Code Extension 的 Terminal 面板還是骨架,尚未串接 AI.Host。
- Deploy 目前只支援「執行一句 shell 指令」這種最小可行版本(見上方「Git Commit/Push 與 Deploy
  真實實作」),不是規格書設想的「依 Configuration 選擇 Docker/Azure/AWS/GCP 子流程」——那個完整
  版本規格書本來就排到 Phase 8,不在這次範疇內。
- Coder 目前只會把建議文字寫成單一 Markdown 檔案,還沒有讓模型輸出結構化的多檔案 diff/patch,
  Diff 面板顯示的也因此不是真正對照既有檔案的 diff(見上方 Phase 3 延伸「Diff 範疇說明」)。
- `WorkspaceSnapshot.WorktreePath` 尚未真正啟用(見上方 Phase 4 說明的範疇取捨)——目前平行
  Coder 共用同一個 `RootPath`,靠檔名帶 Guid 避免互相覆寫,不是真正的檔案系統隔離。等有真實
  git repository 的環境可以驗證 `git worktree` 指令之後再補上。
- Merge Agent 只產出比較報告,不做真正的 `git merge`/自動選分支(留給 Reviewer/QA 或人判斷)。
- Phase 3 延伸的 serve 模式如果搭配 `AI_DEVPLATFORM_APPROVAL_MODE=console`,Reject 觸發的 High 風險
  核准會呼叫阻塞式的 `Console.ReadLine()`,在一個要同時處理多個 HTTP 請求的常駐 Server 裡不是
  好的用法(會佔用一個執行緒等輸入)。serve 模式建議只用 `vscode` 核准模式。
- Phase 1 先讓 Planner/Coder/Reviewer/QA 四個 Agent 共用同一個模型(`nvidia/llama-3.3-nemotron-super-49b-v1.5`),
  規格書原本設想的「每個 Agent 配不同模型」之後可在 `config/appsettings.json` 的 `Models` 各自
  改成想用的模型 ID(先到 <https://build.nvidia.com> 確認 ID 存在)。

## 已知限制 / 變更紀錄

此骨架最初在沒有 .NET SDK 的沙盒環境中產生,`.sln`/`.csproj` 為手動撰寫。已在使用者本機
(.NET 10 SDK,arm64/macOS)驗證 `dotnet restore && dotnet build` 全部 14 個專案成功,過程中修正過:

- `AI.Logging` 的 Serilog 版本從 4.1.0 調整為 4.2.0(避免與 `Serilog.Extensions.Logging 9.0.0`
  的相依版本衝突,NU1605)。
- `AI.Host/Program.cs` 補上遺漏的 `using AI.Core.Models;`(`IModelRegistry` 找不到型別)。
- 所有專案的 `TargetFramework` 從 `net9.0` 改為 `net10.0`,對應使用者機器上實際安裝的 Runtime
  (機器上只有 .NET 10.0.3 / 10.0.5,沒有 .NET 9 Runtime;.NET 10 也是 LTS,不需額外安裝)。

接進真實 Microsoft Agent Framework 呼叫(`samples/Phase0-MafDemo` 已在使用者本機驗證跑通)之後,
又補了幾個修正:

- `Microsoft.Agents.AI.OpenAI` 需要 `Microsoft.Agents.AI >= 1.9.0`,原本釘 1.8.0 造成降級衝突,
  兩個都統一改成 1.9.0。
- `ChatClient` 專用的 `AsAIAgent` 多載其實在 `OpenAI.Chat` 命名空間下(不是 `Microsoft.Agents.AI.OpenAI`),
  已對照 Microsoft 官方範例原始碼確認並修正 using。
- `Microsoft.Agents.AI` 依賴 `Microsoft.Extensions.Logging.Abstractions` / `DependencyInjection` /
  `DependencyInjection.Abstractions` 都要 `>= 10.0.6`,原本 `AI.Runtime`/`AI.Tools`/`AI.Workflow`/
  `AI.Agents`/`AI.Logging`/`AI.Host` 都釘在 9.0.0,已預先統一升到 10.0.8(`Serilog.Extensions.Logging`
  也一併升到 10.0.0),避免又一次 NU1605。
- `AI.Core.Artifacts.CodeArtifact` 新增 `Summary` 欄位,讓 Coder Agent 的 LLM 輸出有地方放。

TypeScript 兩個子專案已在沙盒內 `npm install` + `tsc --noEmit` 驗證通過。C# 部分這次的改動
(AI.Models / AI.Agents / AI.Workflow / AI.Host / AI.Core)在沙盒內只做了語法層面檢查(XML/JSON
合法性、括號配對),**還沒有實際 `dotnet build` 驗證過**,請在本機跑一次確認。

Phase 2(MCP Tool Runtime)的變更與已知情況:

- `extensions/mcp-server/node_modules/typescript` 曾經處於損壞狀態(`package.json` 存在但
  `bin/` 目錄整個消失,推測是很早之前一次失敗的 `rm -rf node_modules` 留下的殘骸),導致
  `tsc` 誤觸發 npx 去抓一個不相關的過期套件。已用 `npm install typescript@5.9.3` 重新安裝修好
  (`extensions/vscode-extension` 也有同樣的問題,同樣修好了)。
- `AI.MCP` 新增 `ModelContextProtocol` 1.3.0 NuGet 套件。實際 API(`StdioClientTransport` +
  `McpClient.CreateAsync(...)` + `ListToolsAsync`/`CallToolAsync`)是直接對照官方 GitHub repo
  的 `v1.3.0` tag 原始碼確認過的,不是憑印象猜的(main 分支的 API 已經改版很多,直接抓 main
  會抓到不相容的簽章,這點特別注意過)。
- 新增 `AI.Core.Tools.IMcpToolInvoker` 介面,讓 `AI.Tools` 的 `McpToolAdapter` 依賴這個抽象,
  而不是直接參照 `AI.MCP`——因為 `AI.MCP` 本身要參照 `AI.Tools` 才能重用 `ToolRequest`/`ToolResult`
  型別,兩邊互相參照會變成循環參照。實作(`AI.MCP.Client.McpToolInvoker`)由 `AI.Host` 在組裝
  DI 時注入。
- `CoderAgent`、`GitAgent` 的建構子都多了 `IToolRuntime` 參數,`AI.Host/Program.cs` 的 DI 註冊
  已同步更新。
- `NativeFileToolHandlers.WriteFileAsync` 原本直接呼叫 `File.WriteAllTextAsync`,如果目標路徑的
  父目錄不存在會直接丟 `DirectoryNotFoundException`。`CoderAgent` 第一次執行時要寫的
  `.ai-suggestions/` 目錄本來就不存在,導致寫檔案在使用者本機上實測時靜默失敗(`CodeArtifact.Files`
  變成空陣列,但 Step 仍回報成功,因為例外被 catch 掉了)。已修正為寫入前先
  `Directory.CreateDirectory(...)` 確保父目錄存在。

已在使用者本機(.NET 10 SDK,arm64/macOS)完整驗證:`dotnet restore && dotnet build` 全部 15 個
專案(14 個 src + Phase0-MafDemo)成功,`dotnet run --project src/AI.Host` 端到端跑完 7 個 Step、
產出 6 個 Artifact。實測確認 Native Adapter(`.ai-suggestions/coder-{id}.md` 真的被寫出來,內容
與 `CodeArtifact.Summary` 一致)與 MCP Adapter(`git.status` 透過 `extensions/mcp-server` 正確
回報「不是 git repository」)兩條路徑都是真的在跑,不是骨架。

Phase 3(Capability Guard,Console 模式)的變更,**已在使用者本機完整驗證**:

- `AI.Runtime` 新增對 `AI.Configuration` 的 `ProjectReference`(`AppConfigCapabilityGuard` 需要讀
  `AppConfig.CapabilityRisk`),確認過不會造成循環參照(`AI.Configuration` 只參照 `AI.Core`)。
- `ToolRuntime` 的建構子多了 `ICapabilityGuard` 參數;因為只透過 DI(`services.AddSingleton<IToolRuntime, ToolRuntime>()`)
  建立,沒有任何地方是手動 `new ToolRuntime(...)`,所以這個簽章變動不會影響到其他呼叫端。
- 實測結果:設定 `AI_DEVPLATFORM_TEST_CAPABILITY_GUARD=1` 跑 Host,輸入 `y` 核准 → `file.deleteFile`
  真的執行、檔案被刪除;輸入 `n` 拒絕 → 回傳 `Success=False`、檔案原封不動。Medium 風險的
  `file.writeFile`(Coder 寫建議檔案)也正確自動放行並記錄 `LogWarning`。兩種情況下,原本的
  7 步 Pipeline 都照常執行完畢,不受影響。

Phase 3(VS Code Extension 確認 UI)的變更:

- 把「怎麼問人」從 `AppConfigCapabilityGuard` 抽成獨立的 `IApprovalPrompt` 介面,原本的 Console
  邏輯搬到新的 `ConsoleApprovalPrompt`,`AppConfigCapabilityGuard` 只留風險分級查詢,改成委派給
  注入的 `IApprovalPrompt`。這是單純的重構,Console 模式的行為(已如上驗證過)不受影響。
- 新增 `VsCodeBridgeApprovalPrompt`:用檔案系統跟 VS Code Extension 溝通(協定細節見程式碼開頭
  註解),`AI.Host/Program.cs` 依 `AI_DEVPLATFORM_APPROVAL_MODE` 環境變數決定注入哪一種
  `IApprovalPrompt`。
- `extensions/vscode-extension` 新增 `approvalBridge.ts`(`FileSystemWatcher` + Modal 確認對話框)、
  `.vscode/launch.json` + `tasks.json`(讓 F5 可以直接編譯並開 Extension Development Host)。
  TypeScript 部分已在沙盒內用 `tsc --noEmit` 型別檢查通過,也用 `npm run compile` 實際編譯出
  `out/extension.js`、`out/approvalBridge.js`。**整個 VS Code 端到端流程已在使用者本機完整驗證
  過一次**:F5 開啟 Extension Development Host → 開 repo 根目錄 → 觸發 High 風險操作 → 跳出
  Modal 對話框(內容正確顯示 `File.Delete`)→ 按下「核准」→ response 檔案寫回 →
  AI.Host 的輪詢在 ~3 秒內偵測到(實測時間戳記約 3 秒:18:44:47 送出請求,18:44:50 收到核准
  結果)→ `file.deleteFile` 真的執行 → Pipeline 繼續往下跑到 Planner 步驟,全程正常。
- C# 這批新檔案(`IApprovalPrompt`、`ConsoleApprovalPrompt`、`VsCodeBridgeApprovalPrompt`、
  `AppConfigCapabilityGuard` 的建構子簽章變動、`AI.Host/Program.cs` 的 DI 註冊)同樣只在沙盒內
  做過語法層面檢查(括號配對),`console` 模式因為只是把既有邏輯搬到新類別,風險較低;`vscode`
  模式(輪詢讀寫檔案那段)**還沒有實際 `dotnet build` 驗證過**,請在本機跑一次確認,尤其是
  `System.Text.Json` 用 `JsonNamingPolicy.CamelCase` 序列化/反序列化那段有沒有跟 TypeScript
  寫出來的 JSON 欄位名稱(`requestId`/`capabilityName`/`context`/`createdAt`/`approved`/`decidedAt`)
  對得上。

Phase 4(平行 Coder / Workspace Snapshot / Merge Agent)的變更:

- 新增 `AI.Runtime/Workspace/GitWorkspaceSnapshotProvider.cs`,實作規格書 v3 第 4 節從 Phase 1
  就定義好、但一直沒有實作的 `IWorkspaceSnapshotProvider`。範疇刻意收斂:`GitCommitSha` 用
  `git rev-parse HEAD` 老實探測(非 git repo 就回 `"unknown"`),`WorktreePath` 先留 `null`
  ——因為目前的開發/驗證環境都不是 git repository,真正的 `git worktree` 隔離寫了也無法端到端
  驗證,詳細取捨寫在類別註解裡。
- 新增 `AI.Agents/MergeAgent.cs`(`AgentKind.Tool`,不呼叫模型):依 `SnapshotId` 分組
  `CodeArtifact`,產出比較報告,不自動選分支/合併。
- `AgentOrchestrator.RunAsync` 新增對 `WorkflowStep.Parallel` 的處理(`RunParallelBranchesAsync`/
  `RunSingleBranchAsync`),原本遇到沒有 `Agent` 的 Step 會直接中止,現在會先檢查有沒有
  `Parallel` 清單。建構子多了 `IWorkspaceSnapshotProvider` 參數,只透過 DI 建立,沒有手動
  `new AgentOrchestrator(...)` 的呼叫端,簽章變動不影響其他地方。
- 新增 `workflows/parallel-pipeline.json`,`AI.Host/Program.cs` 新增 `CoderA`/`CoderB` 具名
  `CoderAgent` 實例與 `MergeAgent` 的 DI 註冊,`config/appsettings.json` 的 `Models` 補上
  `CoderA`/`CoderB` 兩筆(對應 `ModelRegistry` 要求「每個具名 Agent 都要有註冊」的既有限制,
  否則會丟 `InvalidOperationException`)。新增環境變數 `AI_DEVPLATFORM_WORKFLOW=parallel`
  切換到平行 Pipeline,不設定則沿用原本序列 Pipeline 的選擇邏輯。
- **已在使用者本機完整驗證**:`AI_DEVPLATFORM_WORKFLOW=parallel dotnet run --project src/AI.Host`
  跑完整條 8 步 Pipeline 成功。實測確認:(1) 兩個 Coder 分支確實各自拿到不同的 SnapshotId
  (`291baf98...` / `2c64ae7d...`),(2) 兩個分支平行呼叫 NVIDIA NIM 沒有互相干擾,各自的
  `file.writeFile` 都正確走 Medium 風險 Capability 自動放行,(3) `merge` 步驟正常執行並產出
  1 個 `DocumentArtifact`,(4) 後續 review/qa/build/git/deploy 都照常執行完畢,全程共產出 8 個
  Artifact,Workflow 執行結果為「成功」。同一次執行也一併驗證了 VS Code 核准模式(`git` 步驟前的
  Capability Guard 測試),核准後 ~20 秒內完成整個核准迴路,跟 Phase 3 驗證過的行為一致。

Phase 3 延伸(Chat/Diff/Streaming/Task Tree)的變更與已知情況:

**標號更正**:這批工作原本在文件裡誤標成「Phase 5」,經使用者要求逐一核對規格書第 19 節的
Roadmap 原文後發現:規格書的 Phase 5 明確定義是「Unity Tool(採 Native Adapter)」,跟
Chat/Diff/Task Tree 完全無關;Chat/Diff/Streaming/Task Tree/Agent 狀態屬於規格書第 16 節
「VS Code Extension」的完整功能描述,Roadmap 只把「High 風險確認 UI」列為 Phase 3 的必達項目,
其餘部分沒有明確指定所屬 Phase,最合理的定位是「補完 Phase 3 的 VS Code Extension 範疇」。
已把文件內所有「Phase 5」相關標示改成「Phase 3 延伸」,並在上方「目前完成度」新增規格書真正
Phase 5(Unity Tool)尚未開始的說明。

- 架構決策(已跟使用者確認,詳見上方 Phase 3 延伸章節):IPC 用 HTTP+SSE 取代規格書寫的 gRPC;
  Chat 的定位是「啟動並觀察 Workflow」而不是自由對話。
- `AI.Host.csproj` 新增 `Microsoft.AspNetCore.App` FrameworkReference,`Program.cs` 改用
  `WebApplicationBuilder`;新增 `AI_DEVPLATFORM_MODE=serve` 分支,`pipeline`(預設)模式的邏輯
  逐行比對過沒有變動。
- 新增 `AI.Host/Server/RunTracker.cs`、`ChatEndpoints.cs`(Chat/Tasks/Diff 的 HTTP+SSE API)。
- `AI.Core.Events` 新增 `StepStarted` 事件;`IAgentOrchestrator.RunAsync`/`AgentOrchestrator`
  新增選填的 `seedArtifacts` 參數,用來把 Chat 的使用者需求文字送進 Workflow。
- 實作過程中發現一個小陷阱並當場修正:`CoderAgent` 原本用
  `InputArtifacts.OfType<DocumentArtifact>().FirstOrDefault()` 找任務規格,Chat 功能上線之後
  InputArtifacts 可能同時有「使用者原始需求」(seedArtifacts)跟「Planner 消化過的任務規格」
  兩個 `DocumentArtifact`,`FirstOrDefault` 會誤取到使用者原始需求而不是 Planner 的輸出。已改成
  `LastOrDefault`(Planner 的輸出在清單裡總是排在 seed 後面)。這是寫 Chat 端點時透過推演資料
  流程發現的,不是使用者回報的 bug。
- VS Code Extension 新增 `apiClient.ts`(Node `http`/`https` 手動實作 HTTP + SSE 客戶端)、
  `runStateStore.ts`(Chat/Task Tree/Agent Status 共用狀態)、`chatPanel.ts`、`diffPanel.ts`、
  `taskTreeProvider.ts`(取代原本 `extension.ts` 內的兩個 stub class)。**TypeScript 部分已在
  沙盒內用 `npm run compile` 實際編譯過,全部 6 個檔案(含既有的 `approvalBridge.ts`/
  `extension.ts`)都通過,沒有型別錯誤。**
- `package.json` 新增 `aiDevPlatform.apiBaseUrl` 設定。
- **已在使用者本機驗證**:`dotnet build` 全部 15 個專案成功;`AI_DEVPLATFORM_MODE=serve` 啟動後
  正確監聽 `http://localhost:5170`,Chat 面板(輸入框 + Pipeline 下拉選單)也確認能正常開啟。
  Build 過程中出現 `NU1510` 警告(`Microsoft.Extensions.DependencyInjection` 這個手動加的
  PackageReference 跟 `Microsoft.AspNetCore.App` FrameworkReference 帶進來的重複了),已移除
  多餘的 PackageReference 修掉這個警告。
- **修了一個 Phase 1 就留下的既有缺口**:`package.json` 的 `viewsContainers.activitybar` 宣告了
  圖示 `media/icon.svg`,但這個檔案從骨架建立以來就不存在,導致 Activity Bar 上的 AI-DOS 圖示
  在使用者實測時找不到(Task Tree / Agent Status 兩個側邊欄因此也看不到)。已補上
  `extensions/vscode-extension/media/icon.svg`。這不是這批工作新增的問題,是這批工作第一次
  有人真的去點 Activity Bar 才暴露出來的舊缺口。
- **已在使用者本機 VS Code 完整驗證整條 Chat → Workflow → Diff → Task Tree 迴路**:
  Chat 面板輸入需求送出後,依序看到 `plan → code → review → qa → build → git → deploy` 七個
  Step 的「開始執行」「完成」訊息,最後顯示「🏁 Workflow 執行完畢,結果:成功」;`code`/`review`
  等每個有產出的 Step 都出現「顯示內容/Diff」按鈕,點開能正確顯示該次 Coder/Reviewer 產出的
  完整內容(內容確實針對使用者在 Chat 輸入的需求生成,證明 `seedArtifact` 有正確把 Chat 訊息
  傳給 Planner,不是走 Phase 1 的示範性任務文字);Diff 面板的「保留(Accept)」按鈕點下去正確
  跳出「已確認保留這份 Coder 建議」的通知;Activity Bar 的 Task Tree 跟 Agent Status 兩個面板
  都正確顯示七個 Step/Agent 的即時狀態(全部成功打勾)。這是 Phase 3 延伸這批工作唯一一次修正
  (`media/icon.svg`)之後的完整驗證,沒有再發現其他問題。

Git Commit/Push 與 Deploy 的變更與已知情況:

- `extensions/mcp-server/src/tools/gitTool.ts` 新增 `push`,`src/index.ts` 註冊 `git.push` 工具;
  `AI.Host/Program.cs` 的 `mcpToolNames` 補上 `"git.push"`。
- `GitAgent.cs` 全面重寫:從「只查狀態」改成「狀態 → 有變更才 commit → commit 成功才 push」
  三段式,commit 訊息取最新一份 `CodeArtifact.Summary`。三段任何一段失敗都視為資訊性結果,
  不中斷 Workflow(延續 Phase 2 的既有容錯設計)。
- 新增 `AI.Configuration.DeployOptions`(`AppConfig.Deploy.Command`)、
  `AI.Tools/Adapters/NativeDeployToolHandlers.cs`(`deploy.execute` Native Tool,直接 `Process`
  執行使用者設定的 shell 指令)。`DeployAgent.cs` 全面重寫,改成呼叫這個 Tool,沒設定 Command
  時誠實回報略過;`AgentKind` 順便從 `Workflow` 改成 `Tool`(跟 `ExecutionEngine` 的分派邏輯
  無關,純粹分類更準確)。
- `AI.Agents.csproj` 新增對 `AI.Configuration` 的 `ProjectReference`(`DeployAgent` 需要讀
  `AppConfig.Deploy`),確認過不會造成循環參照(`AI.Configuration` 只參照 `AI.Core`)。
- `config/appsettings.json` 新增 `"Deploy": { "Command": null }`(預設不設定,DeployAgent 會
  誠實回報略過,不會假裝部署成功)。
- **這批改動只在沙盒內做過語法層面檢查(括號配對、JSON/XML 合法性)跟 TypeScript 的
  `tsc --noEmit`,還沒有實際 `dotnet build`/`dotnet run` 驗證過**,請在本機跑一次確認,尤其是
  在真正的 git repository 裡測 commit/push 的完整核准流程,以及設定 `Deploy.Command` 之後
  Deploy 步驟的完整核准流程。
