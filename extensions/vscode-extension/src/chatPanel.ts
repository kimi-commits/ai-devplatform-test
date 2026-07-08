import * as vscode from "vscode";
import * as apiClient from "./apiClient";
import { RunStateStore } from "./runStateStore";

/**
 * Phase 5(規格書 v3 第 16 節)。Chat = 啟動並觀察 Workflow(見 ChatEndpoints.cs 開頭註解的
 * 「Chat 定位」決策):使用者在這裡輸入需求,送出後等同觸發一次 Workflow 執行,面板即時顯示
 * 每個 Step 的進度(透過 AI.Host 的 SSE 端點),不是自由對話某個 Agent。
 */
export class ChatPanel {
  private static current: ChatPanel | undefined;

  private readonly panel: vscode.WebviewPanel;
  private activeStream: { dispose: () => void } | undefined;

  private constructor(
    private readonly baseUrlProvider: () => string,
    private readonly runStateStore: RunStateStore,
    private readonly outputChannel: vscode.OutputChannel
  ) {
    this.panel = vscode.window.createWebviewPanel(
      "aiDevPlatformChat",
      "AI-DOS Chat",
      vscode.ViewColumn.Beside,
      { enableScripts: true, retainContextWhenHidden: true }
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

  static createOrShow(baseUrlProvider: () => string, runStateStore: RunStateStore, outputChannel: vscode.OutputChannel): void {
    if (ChatPanel.current) {
      ChatPanel.current.panel.reveal(vscode.ViewColumn.Beside);
      return;
    }
    ChatPanel.current = new ChatPanel(baseUrlProvider, runStateStore, outputChannel);
  }

  private async handleMessage(message: any): Promise<void> {
    if (message?.command === "send") {
      await this.sendMessage(String(message.text ?? ""), String(message.workflow ?? "default"));
    } else if (message?.command === "showDiff") {
      await vscode.commands.executeCommand("aiDevPlatform.showDiff", String(message.artifactId));
    }
  }

  private async sendMessage(text: string, workflow: string): Promise<void> {
    if (text.trim().length === 0) {
      return;
    }

    const baseUrl = this.baseUrlProvider();
    this.post({ command: "appendUser", text });

    try {
      const started = await apiClient.postChat(baseUrl, text, workflow);
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
        }
      );
    } catch (err) {
      const messageText = err instanceof Error ? err.message : String(err);
      this.post({
        command: "appendSystem",
        text: `⚠️ 啟動失敗:${messageText}(請確認 AI.Host 有用 AI_DEVPLATFORM_MODE=serve 啟動,` +
          `且 aiDevPlatform.apiBaseUrl 設定跟啟動的位址一致)`
      });
    }
  }

  private handleStreamEvent(runId: string, event: any): void {
    switch (event?.type) {
      case "stepStarted":
        this.runStateStore.updateStepStarted(runId, event.stepId);
        this.post({
          command: "appendSystem",
          text: `▶ Step '${event.stepId}' 開始執行(${(event.agentNames ?? []).join(", ") || "—"})`
        });
        break;
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
        break;
      default:
        break;
    }
  }

  private post(message: unknown): void {
    void this.panel.webview.postMessage(message);
  }

  private renderHtml(): string {
    return `<!DOCTYPE html>
<html lang="zh-Hant">
<head>
<meta charset="UTF-8" />
<style>
  html, body { height: 100%; margin: 0; }
  body {
    font-family: var(--vscode-font-family);
    color: var(--vscode-foreground);
    padding: 8px;
    box-sizing: border-box;
    display: flex;
    flex-direction: column;
    height: 100vh;
  }
  /* 對話紀錄區佔滿剩餘空間、自己捲動;輸入列(下面 #inputRow)固定在視窗最底部,
     不會因為訊息變多而被推到畫面外,需要額外往下捲才看得到。 */
  #transcript { flex: 1 1 auto; min-height: 0; border: 1px solid var(--vscode-panel-border); border-radius: 4px; overflow-y: auto; padding: 8px; margin-bottom: 8px; }
  .msg { margin-bottom: 6px; white-space: pre-wrap; }
  .user { color: var(--vscode-textLink-foreground); }
  .system { color: var(--vscode-descriptionForeground); }
  #inputRow { flex: 0 0 auto; display: flex; gap: 6px; }
  #messageInput { flex: 1; }
  button { cursor: pointer; }
  .diffButton { margin-left: 6px; }
</style>
</head>
<body>
  <div id="transcript"></div>
  <div id="inputRow">
    <select id="workflowSelect">
      <option value="default">序列 Pipeline(default)</option>
      <option value="parallel">平行 Coder(parallel)</option>
    </select>
    <input id="messageInput" type="text" placeholder="輸入需求,按 Enter 送出……" />
    <button id="sendButton">送出</button>
  </div>
  <script>
    const vscode = acquireVsCodeApi();
    const transcript = document.getElementById("transcript");
    const input = document.getElementById("messageInput");
    const workflowSelect = document.getElementById("workflowSelect");
    const sendButton = document.getElementById("sendButton");

    function appendLine(text, cssClass) {
      const div = document.createElement("div");
      div.className = "msg " + cssClass;
      div.textContent = text;
      transcript.appendChild(div);
      transcript.scrollTop = transcript.scrollHeight;
    }

    function send() {
      const text = input.value;
      if (!text.trim()) { return; }
      vscode.postMessage({ command: "send", text, workflow: workflowSelect.value });
      input.value = "";
    }

    sendButton.addEventListener("click", send);
    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter") { send(); }
    });

    window.addEventListener("message", (event) => {
      const message = event.data;
      if (message.command === "appendUser") {
        appendLine("你: " + message.text, "user");
      } else if (message.command === "appendSystem") {
        appendLine(message.text, "system");
      } else if (message.command === "appendStepSucceeded") {
        const div = document.createElement("div");
        div.className = "msg system";
        div.textContent = "✅ Step '" + message.stepId + "' 完成";
        (message.artifactIds || []).forEach((id) => {
          const btn = document.createElement("button");
          btn.className = "diffButton";
          btn.textContent = "顯示內容/Diff (" + id.slice(0, 8) + ")";
          btn.addEventListener("click", () => {
            vscode.postMessage({ command: "showDiff", artifactId: id });
          });
          div.appendChild(btn);
        });
        transcript.appendChild(div);
        transcript.scrollTop = transcript.scrollHeight;
      }
    });
  </script>
</body>
</html>`;
  }
}
