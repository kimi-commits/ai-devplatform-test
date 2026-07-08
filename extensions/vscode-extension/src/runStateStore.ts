import * as vscode from "vscode";

export type StepStatus = "pending" | "running" | "succeeded" | "failed";

export interface StepInfo {
  id: string;
  agentNames: string[];
  status: StepStatus;
  reason?: string;
  artifactIds: string[];
}

export interface RunInfo {
  runId: string;
  workflowId: string;
  steps: StepInfo[];
  completed: boolean;
  success?: boolean;
}

/**
 * Phase 5(規格書 v3 第 16 節)。Chat 送出需求 = 啟動一次 Workflow(見
 * AI.Host/Server/ChatEndpoints.cs 開頭註解的「Chat 定位」決策),Chat 面板、Task Tree、
 * Agent Status 這三個 UI 都是同一次 Workflow 執行的不同觀察角度,所以共用這個記憶體內狀態:
 * ChatPanel 收到 SSE 事件時寫入,Task Tree / Agent Status 這兩個 TreeDataProvider 讀出畫面。
 *
 * 只保留「目前這一次」的 Run,沒有做歷史多筆 Run 的清單——這跟現有 AgentOrchestrator
 * 「同一時間只跑一個 Workflow instance」的既有限制一致,不是額外的簡化。
 */
export class RunStateStore {
  private current: RunInfo | undefined;
  private readonly _onDidChange = new vscode.EventEmitter<void>();
  readonly onDidChange = this._onDidChange.event;

  startRun(runId: string, workflowId: string, steps: { id: string; agentNames: string[] }[]): void {
    this.current = {
      runId,
      workflowId,
      steps: steps.map((s) => ({ id: s.id, agentNames: s.agentNames, status: "pending" as StepStatus, artifactIds: [] })),
      completed: false
    };
    this._onDidChange.fire();
  }

  updateStepStarted(runId: string, stepId: string): void {
    this.withStep(runId, stepId, (step) => {
      step.status = "running";
    });
  }

  updateStepSucceeded(runId: string, stepId: string, artifactIds: string[]): void {
    this.withStep(runId, stepId, (step) => {
      step.status = "succeeded";
      step.artifactIds = artifactIds;
    });
  }

  updateStepFailed(runId: string, stepId: string, reason: string): void {
    this.withStep(runId, stepId, (step) => {
      step.status = "failed";
      step.reason = reason;
    });
  }

  completeRun(runId: string, success: boolean): void {
    if (this.current?.runId === runId) {
      this.current.completed = true;
      this.current.success = success;
      this._onDidChange.fire();
    }
  }

  getCurrentRun(): RunInfo | undefined {
    return this.current;
  }

  private withStep(runId: string, stepId: string, mutate: (step: StepInfo) => void): void {
    if (this.current?.runId !== runId) {
      return;
    }
    const step = this.current.steps.find((s) => s.id === stepId);
    if (step) {
      mutate(step);
      this._onDidChange.fire();
    }
  }
}
