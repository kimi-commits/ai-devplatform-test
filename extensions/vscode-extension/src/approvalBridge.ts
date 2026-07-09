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

    // 修正(2026-07):這裡原本會把 .ai-devplatform/approvals/ 底下既有的 *.request.json
    // 都當成「新請求」跳出核准 Modal——用意是接住「Extension 比 AI.Host 晚啟動」這種正常
    // 情境,但代價是:如果 AI.Host 那個 process 中途被關掉(Ctrl+C、crash)、來不及在收到
    // response 後清掉 request/response 檔案,這些「孤兒」檔案就會永遠留在磁碟上,而且
    // 每次重開 VS Code / Extension 重新啟動時都會被 scanExisting() 重新掃到、重新跳出一次
        // 一模一樣的核准對話框——即使那個請求早就已經被回答過、發出請求的 process 也早就不在了。
    // 使用者實測時就是被這個問題困擾:移除了會產生新請求的 demo 程式碼之後,舊的孤兒檔案
    // 還是會在每次啟動時跳出來。
    //
    // 修正方式:啟動時既有的 request 檔案一律視為「上一輪留下的孤兒」直接靜默清掉(連同
    // 對應的 response 檔案,如果存在的話),不再跳 Modal——如果 AI.Host 真的還在跑、還在等
    // 這個請求的回應,它會在清掉 request 檔案後的下一次輪詢發現檔案消失,原本的邏輯
        // (VsCodeBridgeApprovalPrompt.AskAsync)只檢查 response 檔案是否出現,不會因為 request
        // 檔案被刪除而出錯,只是會繼續等到逾時、視為拒絕——這個 race window 在實務上極窄
    // (Extension 啟動通常遠早於任何後續的 High 風險操作),可以接受。
    this.cleanupOrphans();

    const pattern = new vscode.RelativePattern(this.approvalsDir, "*.request.json");
    this.watcher = vscode.workspace.createFileSystemWatcher(pattern);
    this.watcher.onDidCreate((uri) => this.handleRequestFile(uri.fsPath));

    this.outputChannel.appendLine(`[ApprovalBridge] 開始監看 ${this.approvalsDir}`);
  }

  private cleanupOrphans(): void {
    let entries: string[];
    try {
      entries = fs.readdirSync(this.approvalsDir);
    } catch {
      return;
    }

    for (const name of entries) {
      if (name.endsWith(".request.json")) {
        const requestPath = path.join(this.approvalsDir, name);
        const responsePath = requestPath.replace(/\.request\.json$/, ".response.json");
        try {
          fs.unlinkSync(requestPath);
        } catch {
          // 忽略——檔案可能剛好被別的地方清掉了。
        }
        try {
          if (fs.existsSync(responsePath)) {
            fs.unlinkSync(responsePath);
          }
        } catch {
          // 同上。
        }
        this.outputChannel.appendLine(
          `[ApprovalBridge] 清除啟動時遺留的孤兒核准請求檔案:${name}(不會跳出核准對話框)`
        );
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
