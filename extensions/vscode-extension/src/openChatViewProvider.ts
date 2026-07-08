import * as vscode from "vscode";

/**
 * Activity Bar 最上面的「Open Chat」捷徑(使用者反映 Cmd+Shift+P 找 "AI-DOS: Open Chat"
 * 太不直覺,希望在側邊欄就能直接點開)。
 *
 * 用一個只有單一節點的 TreeDataProvider 實作:沒有真正的樹狀資料,只是借 TreeView 的容器
 * 放一個「點下去就執行 aiDevPlatform.openChat」的項目,顯示順序在 package.json 的 views
 * 陣列裡排最前面,所以會出現在 Task Tree / Agent Status 上方。
 */
export class OpenChatViewProvider implements vscode.TreeDataProvider<string> {
  private static readonly ROOT_ITEM = "open-chat";

  private readonly _onDidChangeTreeData = new vscode.EventEmitter<void>();
  readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

  getTreeItem(element: string): vscode.TreeItem {
    const item = new vscode.TreeItem("開啟 Chat 面板", vscode.TreeItemCollapsibleState.None);
    item.iconPath = new vscode.ThemeIcon("comment-discussion");
    item.command = {
      command: "aiDevPlatform.openChat",
      title: "AI-DOS: Open Chat"
    };
    item.tooltip = "啟動並觀察 Workflow(等同 Cmd+Shift+P → \"AI-DOS: Open Chat\")";
    return item;
  }

  getChildren(element?: string): vscode.ProviderResult<string[]> {
    if (element) {
      return [];
    }
    return [OpenChatViewProvider.ROOT_ITEM];
  }
}
