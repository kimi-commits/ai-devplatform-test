import * as http from "node:http";
import * as https from "node:https";
import { URL } from "node:url";

/**
 * Phase 5(規格書 v3 第 16 節 Chat/Diff/Task Tree)。跟 AI.Host 的 HTTP+SSE API 溝通
 * (見 AI.Host/Server/ChatEndpoints.cs 開頭註解:用 HTTP+SSE 取代規格書原寫的 gRPC)。
 * 用 Node 內建的 http/https 模組直接實作,不依賴瀏覽器的 fetch/EventSource——
 * Extension Host 是 Node 環境,沒有這兩個全域物件,用內建模組最不容易踩到相容性問題。
 */

export interface ChatStartedResponse {
  runId: string;
  workflowId: string;
  steps: { id: string; agentNames: string[] }[];
}

export interface TaskTreeStepResponse {
  id: string;
  agentNames: string[];
  status: string;
  reason: string | null;
  artifactIds: string[];
}

export interface TaskTreeResponse {
  runId: string;
  workflowId: string;
  completed: boolean;
  success: boolean | null;
  steps: TaskTreeStepResponse[];
}

export interface DiffFile {
  path: string;
  content: string | null;
  error: string | null;
}

export interface DiffResponse {
  artifactId: string;
  type: string;
  summary: string;
  files: DiffFile[];
}

export interface RejectDiffResponse {
  rejected: boolean;
  deletedFiles: string[];
  errors: { path: string; error: string }[];
}

/** Stage B:Product Manager 多輪對話規格確認,見 AI.Host/Server/ChatEndpoints.cs 的 /api/planning* 端點。 */
export interface PlanningStartedResponse {
  sessionId: string;
  reply: string;
}

export interface PlanningMessageResponse {
  reply: string;
}

/** 只產生 PRD,不啟動 Workflow(見下方 finalizePlanning)。prdId 可能是 undefined(存檔失敗時)。 */
export interface PlanningFinalizeResponse {
  finalSpec: string;
  prdId?: string;
  /** Stage F:"fresh" 一般規劃討論;"revise" 是從「修改規格」按鈕開始的討論,見 PlanningSession.Origin。 */
  origin?: string;
}

/**
 * Stage C(見 AI.Host/Server/PrdStore.cs):PRD 落地成檔案之後的摘要,給「Project Manager」
 * 模式的下拉選單用。
 */
export interface PrdSummary {
  id: string;
  title: string;
  createdAt: string;
}

/**
 * Stage E(見 AI.Agents/TestReportStore.cs):測試報告落地成檔案之後的摘要,給「Product
 * Manager(驗收)」模式的下拉選單用。
 */
export interface TestReportSummary {
  id: string;
  title: string;
  createdAt: string;
  passed: boolean;
}

function request(method: string, url: string, body?: unknown): Promise<{ status: number; json: any }> {
  return new Promise((resolve, reject) => {
    const target = new URL(url);
    const lib = target.protocol === "https:" ? https : http;
    const payload = body === undefined ? undefined : Buffer.from(JSON.stringify(body), "utf-8");

    const req = lib.request(
      target,
      {
        method,
        headers: payload
          ? { "Content-Type": "application/json", "Content-Length": payload.length }
          : undefined
      },
      (res) => {
        const chunks: Buffer[] = [];
        res.on("data", (chunk) => chunks.push(chunk as Buffer));
        res.on("end", () => {
          const text = Buffer.concat(chunks).toString("utf-8");
          let json: any = undefined;
          if (text.length > 0) {
            try {
              json = JSON.parse(text);
            } catch {
              json = { raw: text };
            }
          }
          resolve({ status: res.statusCode ?? 0, json });
        });
      }
    );

    req.on("error", reject);
    if (payload) {
      req.write(payload);
    }
    req.end();
  });
}

export async function postChat(baseUrl: string, message: string, workflow: string): Promise<ChatStartedResponse> {
  const { status, json } = await request("POST", `${baseUrl}/api/chat`, { message, workflow });
  if (status !== 202) {
    throw new Error((json && json.error) || `POST /api/chat 失敗(HTTP ${status})`);
  }
  return json as ChatStartedResponse;
}

export async function getTasks(baseUrl: string, runId: string): Promise<TaskTreeResponse> {
  const { status, json } = await request("GET", `${baseUrl}/api/tasks/${encodeURIComponent(runId)}`);
  if (status !== 200) {
    throw new Error((json && json.error) || `GET /api/tasks 失敗(HTTP ${status})`);
  }
  return json as TaskTreeResponse;
}

export async function getDiff(
  baseUrl: string,
  artifactId: string
): Promise<{ status: number; body: DiffResponse | { error: string; type?: string } }> {
  const { status, json } = await request("GET", `${baseUrl}/api/diff/${encodeURIComponent(artifactId)}`);
  return { status, body: json };
}

export async function acceptDiff(baseUrl: string, artifactId: string): Promise<void> {
  await request("POST", `${baseUrl}/api/diff/${encodeURIComponent(artifactId)}/accept`);
}

export async function rejectDiff(baseUrl: string, artifactId: string): Promise<RejectDiffResponse> {
  const { json } = await request("POST", `${baseUrl}/api/diff/${encodeURIComponent(artifactId)}/reject`);
  return json as RejectDiffResponse;
}

export async function startPlanning(baseUrl: string, message: string): Promise<PlanningStartedResponse> {
  const { status, json } = await request("POST", `${baseUrl}/api/planning`, { message });
  if (status !== 200) {
    throw new Error((json && json.error) || `POST /api/planning 失敗(HTTP ${status})`);
  }
  return json as PlanningStartedResponse;
}

export async function continuePlanning(baseUrl: string, sessionId: string, message: string): Promise<PlanningMessageResponse> {
  const { status, json } = await request("POST", `${baseUrl}/api/planning/${encodeURIComponent(sessionId)}/message`, { message });
  if (status !== 200) {
    throw new Error((json && json.error) || `POST /api/planning/{sessionId}/message 失敗(HTTP ${status})`);
  }
  return json as PlanningMessageResponse;
}

/** 只產生 PRD、存回 session,不啟動 Workflow。要真的開始開發,使用者確認 PRD 之後另外呼叫 startDevelopment。 */
export async function finalizePlanning(baseUrl: string, sessionId: string): Promise<PlanningFinalizeResponse> {
  const { status, json } = await request("POST", `${baseUrl}/api/planning/${encodeURIComponent(sessionId)}/finalize`);
  if (status !== 200) {
    throw new Error((json && json.error) || `POST /api/planning/{sessionId}/finalize 失敗(HTTP ${status})`);
  }
  return json as PlanningFinalizeResponse;
}

/** 使用者確認 PRD 沒問題後才呼叫:用 session 裡已經存好的規格書啟動 Workflow。 */
export async function startDevelopment(baseUrl: string, sessionId: string, workflow: string): Promise<ChatStartedResponse> {
  const { status, json } = await request("POST", `${baseUrl}/api/planning/${encodeURIComponent(sessionId)}/start-development`, { workflow });
  if (status !== 202) {
    throw new Error((json && json.error) || `POST /api/planning/{sessionId}/start-development 失敗(HTTP ${status})`);
  }
  return json as ChatStartedResponse;
}

/** Stage C:列出所有已產生的 PRD 檔案,給「Project Manager」模式的下拉選單用。 */
export async function listPrds(baseUrl: string): Promise<PrdSummary[]> {
  const { status, json } = await request("GET", `${baseUrl}/api/prds`);
  if (status !== 200) {
    throw new Error((json && json.error) || `GET /api/prds 失敗(HTTP ${status})`);
  }
  return json as PrdSummary[];
}

/** Stage C:選好一份 PRD 後送出,交給 Project Manager Agent 動態拆解、分派給 CoderA/CoderB/CoderC。 */
export async function dispatchProjectManager(baseUrl: string, prdId: string): Promise<ChatStartedResponse> {
  const { status, json } = await request("POST", `${baseUrl}/api/pm/dispatch`, { prdId });
  if (status !== 202) {
    throw new Error((json && json.error) || `POST /api/pm/dispatch 失敗(HTTP ${status})`);
  }
  return json as ChatStartedResponse;
}

/** Stage E:列出所有已產生的測試報告,給「Product Manager(驗收)」模式的下拉選單用。 */
export async function listReports(baseUrl: string): Promise<TestReportSummary[]> {
  const { status, json } = await request("GET", `${baseUrl}/api/reports`);
  if (status !== 200) {
    throw new Error((json && json.error) || `GET /api/reports 失敗(HTTP ${status})`);
  }
  return json as TestReportSummary[];
}

/**
 * Stage F:「修改規格」按鈕——帶著這份報告的 PRD+QA 結論開一場新的 PM 討論,回傳形狀刻意跟
 * startPlanning 一致,讓 Chat 面板可以直接沿用既有的 PM 對話 UI(appendPm/finalize 等)。
 */
export async function reviseReport(baseUrl: string, reportId: string): Promise<PlanningStartedResponse> {
  const { status, json } = await request("POST", `${baseUrl}/api/reports/${encodeURIComponent(reportId)}/revise`);
  if (status !== 200) {
    throw new Error((json && json.error) || `POST /api/reports/{id}/revise 失敗(HTTP ${status})`);
  }
  return json as PlanningStartedResponse;
}

/** Stage G:「完成驗收」按鈕——手動觸發 Git commit/push + Deploy(workflows/accept-pipeline.json)。 */
export async function acceptReport(baseUrl: string, reportId: string): Promise<ChatStartedResponse> {
  const { status, json } = await request("POST", `${baseUrl}/api/reports/${encodeURIComponent(reportId)}/accept`);
  if (status !== 202) {
    throw new Error((json && json.error) || `POST /api/reports/{id}/accept 失敗(HTTP ${status})`);
  }
  return json as ChatStartedResponse;
}

/**
 * 手動解析 Server-Sent Events(`text/event-stream`),依 "\n\n" 切訊息、去掉 "data: " 前綴、
 * JSON.parse 之後回呼。回傳一個 dispose() 讓呼叫端可以主動關閉連線(例如 Chat 面板關閉時)。
 */
export function streamChat(
  baseUrl: string,
  runId: string,
  onEvent: (event: any) => void,
  onEnd: () => void,
  onError: (err: Error) => void
): { dispose: () => void } {
  const target = new URL(`${baseUrl}/api/chat/${encodeURIComponent(runId)}/stream`);
  const lib = target.protocol === "https:" ? https : http;
  let buffer = "";
  let disposed = false;

  const req = lib.get(target, (res) => {
    if (res.statusCode !== 200) {
      if (!disposed) {
        onError(new Error(`SSE 連線失敗(HTTP ${res.statusCode})`));
      }
      res.resume();
      return;
    }

    res.setEncoding("utf-8");
    res.on("data", (chunk: string) => {
      buffer += chunk;
      let separatorIndex: number;
      while ((separatorIndex = buffer.indexOf("\n\n")) !== -1) {
        const rawMessage = buffer.slice(0, separatorIndex);
        buffer = buffer.slice(separatorIndex + 2);
        const dataLine = rawMessage.split("\n").find((line) => line.startsWith("data:"));
        if (!dataLine) {
          continue;
        }
        const jsonText = dataLine.slice("data:".length).trim();
        try {
          onEvent(JSON.parse(jsonText));
        } catch {
          // 忽略單一事件解析失敗,不中斷整條串流。
        }
      }
    });
    res.on("end", () => {
      if (!disposed) {
        onEnd();
      }
    });
  });

  req.on("error", (err) => {
    if (!disposed) {
      onError(err);
    }
  });

  return {
    dispose: () => {
      disposed = true;
      req.destroy();
    }
  };
}
