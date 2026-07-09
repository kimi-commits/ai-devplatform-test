import * as vscode from "vscode";
import * as apiClient from "./apiClient";
import { RunStateStore } from "./runStateStore";

/**
 * Phase 5(規格書 v3 第 16 節)。Chat = 啟動並觀察 Workflow(見 ChatEndpoints.cs 開頭註解的
 * 「Chat 定位」決策):使用者在這裡輸入需求,送出後等同觸發一次 Workflow 執行,面板即時顯示
 * 每個 Step 的進度(透過 AI.Host 的 SSE 端點),不是自由對話某個 Agent。
 *
 * 除錯記錄(2026-07):曾經整個面板「送出按鈕完全沒反應」,查了很久才發現是把 CSS/JS 全部
 * 內嵌在 webview.html 的一條大字串裡,在某個 VS Code/Electron 版本組合下,只要這條字串
 * 總長度超過某個門檻(實測落在 5.9KB~6.1KB 之間,經過逐步排除已確認跟內容本身——emoji、
 * 中文字、跳脫字元、分支數——完全無關,單純是總長度)就會讓 webview 內部的
 * document.write 丟出 SyntaxError,導致整個 <script> 連解析都沒解析成功,按什麼都沒反應,
 * 也完全不會印出任何 console.log。改成把 CSS/JS 拆成獨立檔案(media/chatPanel.css、
 * media/chatPanel.js),用 webview.asWebviewUri() 產生的 URI 透過 <link>/<script src="">
 * 載入——這是 VS Code 官方文件建議的標準做法,外部資源走的是另一條載入路徑,不會整包塞進
 * document.write,徹底繞開這個大小限制問題。往後這個面板要加內容,一律加到那兩個檔案裡,
 * 不要再把 HTML 用內嵌 <style>/<script> 字串塞回 renderHtml()。
 */
export class ChatPanel {
  private static current: ChatPanel | undefined;

  private readonly panel: vscode.WebviewPanel;
  private activeStream: { dispose: () => void } | undefined;

  // Stage B(見 README「迭代開發迴圈」章節):選了「PM 規劃討論」模式之後,同一場對話要一直
  // 沿用同一個 planningSessionId(呼叫 /api/planning/{id}/message),直到使用者按下「確認規格,
  // 開始開發」(呼叫 /finalize)才清空、真正進入既有的 Workflow 執行/串流邏輯。
  private planningSessionId: string | undefined;

  private constructor(
    private readonly extensionUri: vscode.Uri,
    private readonly baseUrlProvider: () => string,
    private readonly runStateStore: RunStateStore,
    private readonly outputChannel: vscode.OutputChannel
  ) {
    this.panel = vscode.window.createWebviewPanel(
      "aiDevPlatformChat",
      "AI-DOS Chat",
      vscode.ViewColumn.Beside,
      {
        enableScripts: true,
        retainContextWhenHidden: true,
        // CSS/JS 現在從 media/ 資料夾以外部資源載入(見上面的除錯記錄),要明確允許
        // webview 讀取這個資料夾,否則 <link>/<script src=""> 會被擋掉。
        localResourceRoots: [vscode.Uri.joinPath(extensionUri, "media")]
      }
    );
    this.panel.webview.html = this.renderHtml();
    this.panel.onDidDispose(() => {
      this.activeStream?.dispose();
      ChatPanel.current = undefined;
    });
    this.panel.webview.onDidReceiveMessage((message) => {
      void this.handleMessage(message);
    });
  }

  static createOrShow(
    extensionUri: vscode.Uri,
    baseUrlProvider: () => string,
    runStateStore: RunStateStore,
    outputChannel: vscode.OutputChannel
  ): void {
    if (ChatPanel.current) {
      ChatPanel.current.panel.reveal(vscode.ViewColumn.Beside);
      return;
    }
    ChatPanel.current = new ChatPanel(extensionUri, baseUrlProvider, runStateStore, outputChannel);
  }

  private async handleMessage(message: any): Promise<void> {
    if (message?.command === "send") {
      await this.sendMessage(String(message.text ?? ""), String(message.workflow ?? "default"));
    } else if (message?.command === "showDiff") {
      await vscode.commands.executeCommand("aiDevPlatform.showDiff", String(message.artifactId));
    } else if (message?.command === "finalizePlanning") {
      await this.finalizePlanning();
    } else if (message?.command === "startDevelopment") {
      await this.startDevelopment();
    } else if (message?.command === "requestPrdList") {
      await this.loadPrdList();
    } else if (message?.command === "dispatchPm") {
      await this.dispatchPm(String(message.prdId ?? ""));
    } else if (message?.command === "requestReportList") {
      await this.loadReportList();
    } else if (message?.command === "reviseReport") {
      await this.reviseReport(String(message.reportId ?? ""));
    } else if (message?.command === "acceptReport") {
      await this.acceptReport(String(message.reportId ?? ""));
    }
  }

  private async sendMessage(text: string, workflow: string): Promise<void> {
    if (text.trim().length === 0) {
      return;
    }

    if (workflow === "pm") {
      await this.sendPlanningMessage(text);
      return;
    }

    // 選了「PM 規劃討論」之後又切回一般模式送出新需求,視為放棄原本那場還沒定案的討論,
    // 避免下次不小心又選回 pm 模式時,還接到一場語意上已經不相關的舊對話。
    this.planningSessionId = undefined;

    const baseUrl = this.baseUrlProvider();
    this.post({ command: "appendUser", text });
    this.setStatus("⏳ 正在啟動 Workflow……", true);

    try {
      const started = await apiClient.postChat(baseUrl, text, workflow);
      this.attachToRun(started);
    } catch (err) {
      const messageText = err instanceof Error ? err.message : String(err);
      this.post({
        command: "appendSystem",
        text: `⚠️ 啟動失敗:${messageText}(請確認 AI.Host 有用 AI_DEVPLATFORM_MODE=serve 啟動,` +
          `且 aiDevPlatform.apiBaseUrl 設定跟啟動的位址一致)`
      });
      this.setStatus("");
    }
  }

  /**
   * Stage B:PM 規劃討論模式。第一次送出(沒有 planningSessionId)呼叫 /api/planning 開新對話,
   * 之後每一輪呼叫 /api/planning/{id}/message 延續同一場對話。每次 PM 回覆之後都會顯示
   * 「確認規格,產生 PRD」按鈕(見 renderHtml 的 appendPm 處理),使用者隨時可以按,不是一定要等
   * PM 自己說「規格夠清楚了」才能按。
   */
  private async sendPlanningMessage(text: string): Promise<void> {
    const baseUrl = this.baseUrlProvider();
    this.post({ command: "appendUser", text });
    this.setStatus("🤔 PM 思考中……", true);

    try {
      if (!this.planningSessionId) {
        const started = await apiClient.startPlanning(baseUrl, text);
        this.planningSessionId = started.sessionId;
        this.post({ command: "appendPm", text: started.reply });
      } else {
        const result = await apiClient.continuePlanning(baseUrl, this.planningSessionId, text);
        this.post({ command: "appendPm", text: result.reply });
      }
    } catch (err) {
      const messageText = err instanceof Error ? err.message : String(err);
      this.post({ command: "appendSystem", text: `⚠️ PM 對話失敗:${messageText}` });
    } finally {
      this.setStatus("");
    }
  }

  /**
   * 使用者按下「確認規格,產生 PRD」:只請 PM 產生結構化規格書並存回 session,不啟動 Workflow
   * (使用者實測後要求分兩步,見 README「迭代開發迴圈」Stage B 調整紀錄:先看過 PRD 內容,
   * 確認沒問題再手動按「開始開發」)。刻意不清空 planningSessionId——PRD 產生後使用者可能還想
   * 繼續跟 PM 聊、修一下規格再重新產生一次,session 要留著。
   */
  private async finalizePlanning(): Promise<void> {
    if (!this.planningSessionId) {
      this.post({ command: "appendSystem", text: "⚠️ 目前沒有進行中的 PM 規劃對話,無法產生 PRD。" });
      return;
    }

    const baseUrl = this.baseUrlProvider();
    const sessionId = this.planningSessionId;
    this.setStatus("📋 PM 正在整理規格書……(通常要 30~60 秒)", true);

    try {
      const result = await apiClient.finalizePlanning(baseUrl, sessionId);
      // Stage F:origin 傳給 webview,revise 來源的新 PRD 不顯示「開始開發」按鈕(見
      // media/chatPanel.js 的 appendSpecReady 處理、PlanningSession.Origin 類別註解)。
      this.post({ command: "appendSpecReady", text: result.finalSpec, origin: result.origin });
    } catch (err) {
      const messageText = err instanceof Error ? err.message : String(err);
      this.post({ command: "appendSystem", text: `⚠️ 產生 PRD 失敗:${messageText}` });
    } finally {
      this.setStatus("");
    }
  }

  /** 使用者確認 PRD 沒問題、按下「開始開發」:用 session 裡已經存好的規格書啟動 Workflow。 */
  private async startDevelopment(): Promise<void> {
    if (!this.planningSessionId) {
      this.post({ command: "appendSystem", text: "⚠️ 目前沒有進行中的 PM 規劃對話,無法開始開發。" });
      return;
    }

    const baseUrl = this.baseUrlProvider();
    const sessionId = this.planningSessionId;
    this.planningSessionId = undefined; // 開始開發之後這場規劃討論就結束了,下次選 pm 模式要開新的一場。
    this.setStatus("⏳ 正在啟動 Workflow……", true);

    try {
      const started = await apiClient.startDevelopment(baseUrl, sessionId, "default");
      this.attachToRun(started);
    } catch (err) {
      const messageText = err instanceof Error ? err.message : String(err);
      this.post({ command: "appendSystem", text: `⚠️ 啟動開發失敗:${messageText}` });
      this.setStatus("");
    }
  }

  /**
   * Stage C:選了「Project Manager」模式時,webview 端把輸入框換成 PRD 下拉選單(見
   * media/chatPanel.js),並要求這裡回填選項清單——用 postMessage 往返而不是讓 webview
   * 直接呼叫 AI.Host,是延續整個專案「webview 只透過 vscode.postMessage 跟 extension host
   * 溝通,實際 HTTP 呼叫都在 extension host 這端」的既有模式(apiClient.ts 只在 Node 環境
   * 可用,webview 是瀏覽器環境沒有這些模組)。
   */
  private async loadPrdList(): Promise<void> {
    const baseUrl = this.baseUrlProvider();
    try {
      const prds = await apiClient.listPrds(baseUrl);
      this.post({ command: "prdList", prds });
    } catch (err) {
      const messageText = err instanceof Error ? err.message : String(err);
      this.post({ command: "appendSystem", text: `⚠️ 讀取 PRD 清單失敗:${messageText}` });
    }
  }

  /** 使用者在「Project Manager」模式選好一份 PRD、按下送出:交給 Project Manager Agent 動態拆解、分派給 CoderA/CoderB/CoderC。 */
  private async dispatchPm(prdId: string): Promise<void> {
    if (!prdId) {
      this.post({ command: "appendSystem", text: "⚠️ 請先選擇一份 PRD 檔案。" });
      return;
    }

    const baseUrl = this.baseUrlProvider();
    this.post({ command: "appendSystem", text: "🧑‍💼 已送出 PRD,Project Manager 正在拆解任務、分派給 CoderA(前端)/CoderB(後端)/CoderC(架構)……" });
    this.setStatus("⏳ Project Manager 正在拆解任務、啟動 Workflow……", true);

    try {
      const started = await apiClient.dispatchProjectManager(baseUrl, prdId);
      this.attachToRun(started);
    } catch (err) {
      const messageText = err instanceof Error ? err.message : String(err);
      this.post({ command: "appendSystem", text: `⚠️ 分派失敗:${messageText}` });
      this.setStatus("");
    }
  }

  /**
   * Stage E:選了「Product Manager(驗收)」模式時,webview 端把輸入框換成測試報告下拉選單
   * (見 media/chatPanel.js),做法跟 loadPrdList 一致。
   */
  private async loadReportList(): Promise<void> {
    const baseUrl = this.baseUrlProvider();
    try {
      const reports = await apiClient.listReports(baseUrl);
      this.post({ command: "reportList", reports });
    } catch (err) {
      const messageText = err instanceof Error ? err.message : String(err);
      this.post({ command: "appendSystem", text: `⚠️ 讀取測試報告清單失敗:${messageText}` });
    }
  }

  /**
   * Stage F:「修改規格」按鈕——帶著這份報告的 PRD+QA 結論開一場新的 PM 討論,沿用既有的
   * PM 對話 UI(appendPm、「確認規格,產生 PRD」按鈕)。收到第一句 PM 回覆後,把
   * workflowSelect 切回「pm」模式(見 media/chatPanel.js 的 switchMode 處理),這樣使用者才能
   * 繼續用文字輸入框跟 PM 來回討論——sendMessage/sendPlanningMessage 本來就是靠
   * this.planningSessionId 判斷要不要延續同一場對話,這裡先設好就會自動接上,不用另外處理。
   */
  private async reviseReport(reportId: string): Promise<void> {
    if (!reportId) {
      this.post({ command: "appendSystem", text: "⚠️ 請先選擇一份測試報告。" });
      return;
    }

    const baseUrl = this.baseUrlProvider();
    this.post({
      command: "appendSystem",
      text: "🧑‍💼 已帶著這份測試報告的規格書與 QA 結論,開始跟 Product Manager 討論要怎麼修改……"
    });
    this.setStatus("🤔 PM 思考中……", true);

    try {
      const started = await apiClient.reviseReport(baseUrl, reportId);
      this.planningSessionId = started.sessionId;
      this.post({ command: "appendPm", text: started.reply });
      this.post({ command: "switchMode", workflow: "pm" });
    } catch (err) {
      const messageText = err instanceof Error ? err.message : String(err);
      this.post({ command: "appendSystem", text: `⚠️ 開始修改規格討論失敗:${messageText}` });
    } finally {
      this.setStatus("");
    }
  }

  /** Stage G:「完成驗收」按鈕——手動觸發 Git commit/push + Deploy(accept-pipeline.json)。 */
  private async acceptReport(reportId: string): Promise<void> {
    if (!reportId) {
      this.post({ command: "appendSystem", text: "⚠️ 請先選擇一份測試報告。" });
      return;
    }

    const baseUrl = this.baseUrlProvider();
    this.post({ command: "appendSystem", text: "✅ 已確認驗收,開始執行 Git commit/push 與 Deploy……" });
    this.setStatus("⏳ 正在啟動 Git/Deploy Workflow……", true);

    try {
      const started = await apiClient.acceptReport(baseUrl, reportId);
      this.attachToRun(started);
    } catch (err) {
      const messageText = err instanceof Error ? err.message : String(err);
      this.post({ command: "appendSystem", text: `⚠️ 觸發驗收失敗:${messageText}` });
      this.setStatus("");
    }
  }

  /** 啟動 Workflow 後共用的收尾邏輯:寫入 RunStateStore、掛上 SSE 串流。一般直接送出跟 PM 定案後都會用到。 */
  private attachToRun(started: apiClient.ChatStartedResponse): void {
    const baseUrl = this.baseUrlProvider();
    this.runStateStore.startRun(started.runId, started.workflowId, started.steps);
    this.post({
      command: "appendSystem",
      text: `已啟動 Workflow '${started.workflowId}'(runId=${started.runId}),即時進度如下:`
    });

    this.activeStream?.dispose();
    this.activeStream = apiClient.streamChat(
      baseUrl,
      started.runId,
      (event) => this.handleStreamEvent(started.runId, event),
      () => {
        // 正常結束(伺服器端已在 completed 事件送出時關閉串流),不需要額外處理。
      },
      (err) => {
        this.outputChannel.appendLine(`[ChatPanel] SSE 錯誤:${err.message}`);
        this.post({ command: "appendSystem", text: `⚠️ 串流連線中斷:${err.message}` });
        // 修正:之前這裡沒有清狀態列,如果 SSE 連線失敗發生在第一個 stepStarted 事件送達之前
        // (代表輸入框/送出按鈕還處於「⏳ 正在啟動 Workflow……」的鎖定狀態),就會卡死——按送出
        // 完全沒反應,只能重開面板才能恢復。跟 finalizePlanning/startDevelopment 的 catch 一樣,
        // 任何讓這場 Run 提早結束的情況都要記得解鎖。
        this.setStatus("");
      }
    );
  }

  private handleStreamEvent(runId: string, event: any): void {
    switch (event?.type) {
      case "stepStarted": {
        this.runStateStore.updateStepStarted(runId, event.stepId);
        const agentNames = (event.agentNames ?? []).join(", ") || "—";
        this.post({
          command: "appendSystem",
          text: `▶ Step '${event.stepId}' 開始執行(${agentNames})`
        });
        // LLM 呼叫常常要 30~60 秒,中間完全沒有其他輸出,容易讓人以為卡住了——這裡持續顯示
        // 「目前正在跑哪一步」,直到下一個事件(成功/失敗/下一步開始)進來才會被蓋掉。
        this.setStatus(`⚙️ 執行中:Step '${event.stepId}'(${agentNames})……`);
        break;
      }
      case "stepSucceeded":
        this.runStateStore.updateStepSucceeded(runId, event.stepId, event.artifactIds ?? []);
        this.post({
          command: "appendStepSucceeded",
          stepId: event.stepId,
          artifactIds: event.artifactIds ?? []
        });
        break;
      case "stepFailed":
        this.runStateStore.updateStepFailed(runId, event.stepId, event.reason ?? "unknown");
        this.post({ command: "appendSystem", text: `❌ Step '${event.stepId}' 失敗:${event.reason ?? "unknown"}` });
        break;
      case "completed":
        this.runStateStore.completeRun(runId, Boolean(event.success));
        this.post({
          command: "appendSystem",
          text: `🏁 Workflow 執行完畢,結果:${event.success ? "成功" : "失敗/中止"}`
        });
        this.activeStream?.dispose();
        this.activeStream = undefined;
        this.setStatus("");
        break;
      default:
        break;
    }
  }

  private post(message: unknown): void {
    void this.panel.webview.postMessage(message);
  }

  /**
   * 更新輸入列上方的狀態列(見 renderHtml 的 #statusBar);傳空字串代表清空。
   * lockInput=true 時連輸入框跟送出按鈕都會停用,用在「等一個同步回覆」的情境(PM 對話、
   * 定案)避免手滑連點兩次；Workflow Step 執行中的狀態(見 handleStreamEvent)刻意不鎖輸入,
   * 因為使用者這時候可能想另外開一場新的對話。
   */
  private setStatus(text: string, lockInput = false): void {
    this.post({ command: "setStatus", text, lockInput });
  }

  /**
   * 產生 webview 的骨架 HTML:本身要保持很小(只有基本標籤結構),CSS/JS 都是外部檔案
   * 透過 <link>/<script src=""> 載入,不要再把樣式或邏輯用內嵌字串塞進來(見類別開頭的
   * 除錯記錄——這正是之前導致整個面板沒反應的原因)。
   */
  private renderHtml(): string {
    const webview = this.panel.webview;
    const styleUri = webview.asWebviewUri(vscode.Uri.joinPath(this.extensionUri, "media", "chatPanel.css"));
    const scriptUri = webview.asWebviewUri(vscode.Uri.joinPath(this.extensionUri, "media", "chatPanel.js"));
    return `<!DOCTYPE html>
<html lang="zh-Hant">
<head>
<meta charset="UTF-8" />
<link rel="stylesheet" href="${styleUri}" />
</head>
<body>
  <div id="transcript"></div>
  <div id="statusBar"></div>
  <div id="inputRow">
    <select id="workflowSelect">
      <option value="default">序列 Pipeline(default)</option>
      <option value="parallel">平行 Coder(parallel)</option>
      <option value="pm">PM 規劃討論(新,先對話確認規格)</option>
      <option value="pm-dispatch">Project Manager(選 PRD,動態分派給 CoderA/B/C)</option>
      <option value="acceptance">Product Manager(驗收,選測試報告)</option>
    </select>
    <textarea id="messageInput" rows="1" placeholder="輸入需求,支援換行;Enter 換行,Ctrl+Enter(Mac 是 Cmd+Enter)送出……"></textarea>
    <select id="prdSelect" style="display: none; flex: 1;">
      <option value="">(讀取 PRD 清單中……)</option>
    </select>
    <select id="reportSelect" style="display: none; flex: 1;">
      <option value="">(讀取測試報告清單中……)</option>
    </select>
    <button id="sendButton">送出</button>
    <button id="reviseButton" style="display: none;">修改規格</button>
    <button id="acceptButton" style="display: none;">完成驗收</button>
  </div>
  <script src="${scriptUri}"></script>
</body>
</html>`;
  }
}
