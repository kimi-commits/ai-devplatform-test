import * as vscode from "vscode";
import * as apiClient from "./apiClient";

/**
 * Phase 5(規格書 v3 第 16 節「Diff、Accept/Reject」)。
 *
 * 範疇說明(誠實記錄限制,見 AI.Host/Server/ChatEndpoints.cs 開頭註解「Diff 範疇說明」):
 * CoderAgent 目前是直接把建議文字寫成檔案,不是「先產生 patch、使用者按 Accept 才真正寫入」
 * 的模型,所以這裡顯示的不是對照既有檔案的真正 diff,而是 Coder 寫出來的建議內容本身。
 * Accept 只是確認保留(no-op,檔案已經在那了);Reject 會真的呼叫 file.deleteFile 把檔案刪掉
 * (會走 Capability Guard 的 High 風險核准流程)。
 */
export class DiffPanel {
  private static readonly panels = new Map<string, DiffPanel>();

  private readonly panel: vscode.WebviewPanel;

  private constructor(private readonly artifactId: string, private readonly baseUrlProvider: () => string) {
    this.panel = vscode.window.createWebviewPanel(
      "aiDevPlatformDiff",
      `AI-DOS Diff (${artifactId.slice(0, 8)})`,
      vscode.ViewColumn.Beside,
      { enableScripts: true }
    );
    this.panel.onDidDispose(() => DiffPanel.panels.delete(artifactId));
    this.panel.webview.onDidReceiveMessage((message) => {
      void this.handleMessage(message);
    });
    this.panel.webview.html = this.wrapHtml("<p>載入中……</p>");
    void this.load();
  }

  static show(artifactId: string, baseUrlProvider: () => string): void {
    const existing = DiffPanel.panels.get(artifactId);
    if (existing) {
      existing.panel.reveal(vscode.ViewColumn.Beside);
      return;
    }
    DiffPanel.panels.set(artifactId, new DiffPanel(artifactId, baseUrlProvider));
  }

  private async load(): Promise<void> {
    const baseUrl = this.baseUrlProvider();
    try {
      const { status, body } = await apiClient.getDiff(baseUrl, this.artifactId);
      if (status === 404) {
        this.panel.webview.html = this.wrapHtml(
          "<p>找不到這個 Artifact(Artifact Store 是檔案系統,AI.Host 重啟不會遺失,請確認 artifactId 正確)。</p>"
        );
        return;
      }
      if (status !== 200) {
        const errorBody = body as { error: string; type?: string };
        this.panel.webview.html = this.wrapHtml(
          `<p>此 Artifact 沒有可顯示的 Diff 內容(type=${escapeHtml(errorBody.type ?? "unknown")})。` +
            "目前只有 Coder 產出的 CodeArtifact 才有內容可看。</p>"
        );
        return;
      }
      this.panel.webview.html = this.renderDiffHtml(body as apiClient.DiffResponse);
    } catch (err) {
      const messageText = err instanceof Error ? err.message : String(err);
      this.panel.webview.html = this.wrapHtml(`<p>載入失敗:${escapeHtml(messageText)}</p>`);
    }
  }

  private async handleMessage(message: any): Promise<void> {
    const baseUrl = this.baseUrlProvider();
    if (message?.command === "accept") {
      await apiClient.acceptDiff(baseUrl, this.artifactId);
      vscode.window.showInformationMessage("AI-DOS: 已確認保留這份 Coder 建議(檔案已經寫入,沒有額外動作)。");
    } else if (message?.command === "reject") {
      const result = await apiClient.rejectDiff(baseUrl, this.artifactId);
      if (result.errors && result.errors.length > 0) {
        vscode.window.showWarningMessage(
          `AI-DOS: 部分檔案刪除失敗:${result.errors.map((e) => `${e.path}(${e.error})`).join(", ")}`
        );
      } else {
        vscode.window.showInformationMessage(
          `AI-DOS: 已刪除建議檔案:${(result.deletedFiles || []).join(", ") || "(無)"}`
        );
      }
    }
  }

  private renderDiffHtml(diff: apiClient.DiffResponse): string {
    const filesHtml = diff.files
      .map((file) => {
        if (file.error) {
          return `<h3>${escapeHtml(file.path)}</h3><p style="color:var(--vscode-errorForeground)">讀取失敗:${escapeHtml(file.error)}</p>`;
        }
        return `<h3>${escapeHtml(file.path)}</h3><pre>${escapeHtml(file.content ?? "")}</pre>`;
      })
      .join("\n");

    return this.wrapHtml(`
      <p><strong>Summary:</strong></p>
      <pre>${escapeHtml(diff.summary)}</pre>
      <p><strong>檔案內容</strong>(這是 Coder 直接寫入的建議文字,不是對照既有檔案的真正 diff——見類別註解說明):</p>
      ${filesHtml}
      <div style="margin-top:12px;">
        <button id="acceptBtn">保留(Accept)</button>
        <button id="rejectBtn">刪除這份建議(Reject)</button>
      </div>
      <script>
        const vscode = acquireVsCodeApi();
        document.getElementById("acceptBtn").addEventListener("click", () => vscode.postMessage({ command: "accept" }));
        document.getElementById("rejectBtn").addEventListener("click", () => vscode.postMessage({ command: "reject" }));
      </script>
    `);
  }

  private wrapHtml(body: string): string {
    return `<!DOCTYPE html>
<html lang="zh-Hant">
<head><meta charset="UTF-8" /><style>
  body { font-family: var(--vscode-font-family); color: var(--vscode-foreground); padding: 12px; }
  pre { background: var(--vscode-textCodeBlock-background); padding: 8px; overflow-x: auto; white-space: pre-wrap; }
  button { margin-right: 8px; cursor: pointer; }
</style></head>
<body>${body}</body>
</html>`;
  }
}

function escapeHtml(text: string): string {
  return text
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}
