import * as vscode from "vscode";
import { RunStateStore, StepInfo, StepStatus } from "./runStateStore";

const STATUS_ICON: Record<StepStatus, vscode.ThemeIcon> = {
  pending: new vscode.ThemeIcon("circle-outline"),
  running: new vscode.ThemeIcon("sync~spin"),
  succeeded: new vscode.ThemeIcon("check"),
  failed: new vscode.ThemeIcon("error")
};

const STATUS_LABEL: Record<StepStatus, string> = {
  pending: "待執行",
  running: "執行中",
  succeeded: "成功",
  failed: "失敗"
};

const PLACEHOLDER_STEP: StepInfo = {
  id: "(尚未啟動任何 Workflow,請先用 AI-DOS: Open Chat 送出需求)",
  agentNames: [],
  status: "pending",
  artifactIds: []
};

/**
 * Phase 5(規格書 v3 第 16 節「Task Tree」)。資料來源是 RunStateStore(由 ChatPanel 的 SSE
 * 事件處理器寫入),不是獨立輪詢 AI.Host——三個 UI(Chat/Task Tree/Agent Status)看的是同一個
 * Workflow 執行狀態(見 ChatEndpoints.cs「Chat 定位」的架構決策)。
 */
export class AgentTaskTreeProvider implements vscode.TreeDataProvider<StepInfo> {
  private readonly _onDidChangeTreeData = new vscode.EventEmitter<void>();
  readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

  constructor(private readonly runStateStore: RunStateStore) {
    this.runStateStore.onDidChange(() => this._onDidChangeTreeData.fire());
  }

  getTreeItem(step: StepInfo): vscode.TreeItem {
    const item = new vscode.TreeItem(
      `${step.id}${step.agentNames.length > 0 ? ` (${step.agentNames.join(", ")})` : ""}`,
      vscode.TreeItemCollapsibleState.None
    );
    item.description = STATUS_LABEL[step.status] ?? step.status;
    item.iconPath = STATUS_ICON[step.status];
    if (step.reason) {
      item.tooltip = step.reason;
    }
    return item;
  }

  getChildren(): vscode.ProviderResult<StepInfo[]> {
    const run = this.runStateStore.getCurrentRun();
    if (!run || run.steps.length === 0) {
      return [PLACEHOLDER_STEP];
    }
    return run.steps;
  }
}

interface AgentStatusEntry {
  agentName: string;
  status: StepStatus;
}

/**
 * 依 Agent(而不是依 Step)列一次,平行 Step(例如 CoderA/CoderB)在這裡會拆成兩筆各自的狀態,
 * 跟 Task Tree 依 Step 分組是互補的兩種檢視角度。
 */
export class AgentStatusTreeProvider implements vscode.TreeDataProvider<AgentStatusEntry> {
  private readonly _onDidChangeTreeData = new vscode.EventEmitter<void>();
  readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

  constructor(private readonly runStateStore: RunStateStore) {
    this.runStateStore.onDidChange(() => this._onDidChangeTreeData.fire());
  }

  getTreeItem(entry: AgentStatusEntry): vscode.TreeItem {
    const item = new vscode.TreeItem(entry.agentName, vscode.TreeItemCollapsibleState.None);
    item.description = STATUS_LABEL[entry.status] ?? entry.status;
    item.iconPath = STATUS_ICON[entry.status];
    return item;
  }

  getChildren(): vscode.ProviderResult<AgentStatusEntry[]> {
    const run = this.runStateStore.getCurrentRun();
    if (!run || run.steps.length === 0) {
      return [{ agentName: "(尚未啟動任何 Workflow,請先用 AI-DOS: Open Chat 送出需求)", status: "pending" }];
    }

    const entries: AgentStatusEntry[] = [];
    for (const step of run.steps) {
      for (const agentName of step.agentNames) {
        entries.push({ agentName, status: step.status });
      }
    }
    return entries;
  }
}
