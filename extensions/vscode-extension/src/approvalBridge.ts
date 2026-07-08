import * as vscode from "vscode";
import * as fs from "node:fs";
import * as path from "node:path";

/**
 * 對應 AI.Runtime/Capabilities/VsCodeBridgeApprovalPrompt.cs 的協定。
 * AI.Host(獨立的 .NET process)在需要 High 風險 Capability 核准時,
 * 會把請求寫成 .ai-devplatform/approvals/{requestId}.request.json;
 * 這個類別用 FileSystemWatcher 偵測到之後跳出 Modal 確認對話框,
 * 使用者按下「核准」/「拒絕」後寫回 {requestId}.response.json 給 AI.Host 讀。
 *
 * 兩個獨立 process 之間沒有現成的 IPC 管道,選檔案系統當媒介是最簡單、
 * 不需要額外安裝套件、也最容易在使用者本機除錯的作法(規格書 v3 第 16 節)。
 */
interface ApprovalRequest {
  requestId: string;
  capabilityName: string;
  context: string;
  createdAt: string;
}

interface ApprovalResponse {
  requestId: string;
  approved: boolean;
  decidedAt: string;
}

export class ApprovalBridge implements vscode.Disposable {
  private watcher: vscode.FileSystemWatcher | undefined;
  private readonly processedIds = new Set<string>();

  constructor(
    private readonly approvalsDir: string,
    private readonly outputChannel: vscode.OutputChannel
  ) {}

  start(): void {
    fs.mkdirSync(this.approvalsDir, { recursive: true });

    // Extension 啟動時,.ai-devplatform/approvals/ 底下可能已經躺著 AI.Host 更早寫入、
    // 還沒被處理的 request 檔案(例如 Extension 比 AI.Host 晚啟動),先掃一次補上。
    this.scanExisting();

    const pattern = new vscode.RelativePattern(this.approvalsDir, "*.request.json");
    this.watcher = vscode.workspace.createFileSystemWatcher(pattern);
    this.watcher.onDidCreate((uri) => this.handleRequestFile(uri.fsPath));

    this.outputChannel.appendLine(`[ApprovalBridge] 開始監看 ${this.approvalsDir}`);
  }

  private scanExisting(): void {
    let entries: string[];
    try {
      entries = fs.readdirSync(this.approvalsDir);
    } catch {
      return;
    }

    for (const name of entries) {
      if (name.endsWith(".request.json")) {
        this.handleRequestFile(path.join(this.approvalsDir, name));
      }
    }
  }

  private async handleRequestFile(filePath: string): Promise<void> {
    let request: ApprovalRequest;
    try {
      const raw = fs.readFileSync(filePath, "utf-8");
      request = JSON.parse(raw) as ApprovalRequest;
    } catch (err) {
      this.outputChannel.appendLine(`[ApprovalBridge] 讀取 ${filePath} 失敗:${String(err)}`);
      return;
    }

    // FileSystemWatcher 在某些平台上可能對同一個檔案觸發多次 onDidCreate,
    // 用 requestId 去重,避免同一個請求跳出兩次對話框。
    if (this.processedIds.has(request.requestId)) {
      return;
    }
    this.processedIds.add(request.requestId);

    this.outputChannel.appendLine(
      `[ApprovalBridge] 收到 High 風險核准請求 ${request.requestId}:${request.capabilityName} — ${request.context}`
    );

    const choice = await vscode.window.showWarningMessage(
      `AI-DOS 要求核准 High 風險操作:${request.capabilityName}`,
      { modal: true, detail: request.context },
      "核准",
      "拒絕"
    );

    const approved = choice === "核准";
    const response: ApprovalResponse = {
      requestId: request.requestId,
      approved,
      decidedAt: new Date().toISOString()
    };

    const responsePath = path.join(this.approvalsDir, `${request.requestId}.response.json`);
    try {
      fs.writeFileSync(responsePath, JSON.stringify(response, null, 2), "utf-8");
    } catch (err) {
      this.outputChannel.appendLine(`[ApprovalBridge] 寫入 ${responsePath} 失敗:${String(err)}`);
      return;
    }

    this.outputChannel.appendLine(
      `[ApprovalBridge] ${request.capabilityName} 核准結果:${approved ? "核准" : "拒絕"}` +
        `(requestId=${request.requestId})`
    );

    // request 檔案留給 AI.Host 端在讀到 response 之後自己清掉,這裡不主動刪除,
    // 避免 AI.Host 還沒讀到 request 內容就被清空(競態條件)。
  }

  dispose(): void {
    this.watcher?.dispose();
    this.processedIds.clear();
  }
}
