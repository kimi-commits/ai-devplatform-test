import * as vscode from "vscode";
import * as path from "node:path";
import { ApprovalBridge } from "./approvalBridge";
import { ChatPanel } from "./chatPanel";
import { DiffPanel } from "./diffPanel";
import { RunStateStore } from "./runStateStore";
import { AgentStatusTreeProvider, AgentTaskTreeProvider } from "./taskTreeProvider";
import { OpenChatViewProvider } from "./openChatViewProvider";

/**
 * AI Development Platform VS Code Extension.
 * 提供:聊天、Diff、Accept/Reject、Streaming、Log、Agent 狀態、Task Tree、Terminal,
 * 以及 High 風險 Capability 的確認 UI(規格書 v3 第 16 節)。
 *
 * Phase 3 完成:High 風險 Capability 的確認 UI,透過 ApprovalBridge 監看
 * .ai-devplatform/approvals/ 目錄,跟另一個獨立 process(AI.Host)用檔案系統交換核准請求/回應
 * (見 approvalBridge.ts 開頭註解的完整協定說明)。
 *
 * Phase 5 完成:Chat、Diff、Streaming、Task Tree、Agent 狀態,透過 HTTP+SSE 跟 AI.Host 的
 * serve 模式溝通(見 apiClient.ts / chatPanel.ts / diffPanel.ts / runStateStore.ts /
 * taskTreeProvider.ts,以及 AI.Host/Server/ChatEndpoints.cs 開頭註解的架構決策說明)。
 * Terminal 面板仍是尚未串接的部分。
 */
export function activate(context: vscode.ExtensionContext): void {
  const outputChannel = vscode.window.createOutputChannel("AI-DOS");
  outputChannel.appendLine("AI Development Platform extension activated.");

  const runStateStore = new RunStateStore();

  // 排在 Task Tree / Agent Status 上方的「Open Chat」捷徑,避免使用者得先找到
  // Cmd+Shift+P → "AI-DOS: Open Chat" 才能開面板。
  const openChatViewProvider = new OpenChatViewProvider();
  vscode.window.registerTreeDataProvider("aiDevPlatform.openChatView", openChatViewProvider);

  const taskTreeProvider = new AgentTaskTreeProvider(runStateStore);
  vscode.window.registerTreeDataProvider("aiDevPlatform.taskTree", taskTreeProvider);

  const agentStatusProvider = new AgentStatusTreeProvider(runStateStore);
  vscode.window.registerTreeDataProvider("aiDevPlatform.agentStatus", agentStatusProvider);

  const approvalBridge = new ApprovalBridge(resolveApprovalsDir(), outputChannel);
  approvalBridge.start();

  context.subscriptions.push(
    outputChannel,
    approvalBridge,
    vscode.commands.registerCommand("aiDevPlatform.openChat", () => {
      ChatPanel.createOrShow(resolveApiBaseUrl, runStateStore, outputChannel);
    }),
    // 內部指令,由 ChatPanel 的「顯示內容/Diff」按鈕觸發,不對外出現在 Command Palette
    // (需要 artifactId 這個參數,直接從 Palette 呼叫沒有意義)。
    vscode.commands.registerCommand("aiDevPlatform.showDiff", (artifactId: string) => {
      DiffPanel.show(artifactId, resolveApiBaseUrl);
    }),
    vscode.commands.registerCommand("aiDevPlatform.acceptDiff", () => {
      vscode.window.showInformationMessage(
        "AI-DOS: 請在 Chat 面板點「顯示內容/Diff」開啟該次產出的 Diff 面板,裡面有 Accept/Reject 按鈕。"
      );
    }),
    vscode.commands.registerCommand("aiDevPlatform.rejectDiff", () => {
      vscode.window.showInformationMessage(
        "AI-DOS: 請在 Chat 面板點「顯示內容/Diff」開啟該次產出的 Diff 面板,裡面有 Accept/Reject 按鈕。"
      );
    }),
    vscode.commands.registerCommand("aiDevPlatform.approveHighRiskAction", async () => {
      // 獨立的手動示範指令,方便在沒有 AI.Host 跑起來的情況下,單獨看一下這個 Modal
      // 對話框長什麼樣子。真正的 High 風險核准流程是由上面的 ApprovalBridge 自動觸發,
      // 不需要使用者手動叫這個指令。
      const choice = await vscode.window.showWarningMessage(
        "此操作屬於高風險 Capability(例如 git push / Deploy),是否核准執行?",
        { modal: true },
        "核准",
        "取消"
      );
      outputChannel.appendLine(`[Manual Demo] High-risk action decision: ${choice ?? "cancelled"}`);
    })
  );
}

export function deactivate(): void {
  // no-op(ApprovalBridge 透過 context.subscriptions 自動 dispose)
}

/** 依目前開啟的 workspace 資料夾算出 .ai-devplatform/approvals/ 的絕對路徑。 */
function resolveApprovalsDir(): string {
  const workspaceFolders = vscode.workspace.workspaceFolders;
  const root = workspaceFolders && workspaceFolders.length > 0 ? workspaceFolders[0].uri.fsPath : process.cwd();
  return path.join(root, ".ai-devplatform", "approvals");
}

/**
 * Phase 5:AI.Host 用 AI_DEVPLATFORM_MODE=serve 啟動時監聽的 HTTP 位址(見
 * AI.Host/Program.cs)。用設定值而不是寫死,方便使用者改連 port 或遠端位址。
 */
function resolveApiBaseUrl(): string {
  return vscode.workspace.getConfiguration("aiDevPlatform").get<string>("apiBaseUrl", "http://localhost:5170");
}
