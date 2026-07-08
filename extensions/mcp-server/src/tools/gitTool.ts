import { z } from "zod";
import { spawn } from "node:child_process";

/**
 * Git Tool: Status / Diff / Commit / Checkout / Branch / Push(規格書 v1 第 10 節)。
 * push 屬於 High 風險 Capability,由 AI.Core.Capabilities.ICapabilityGuard 在呼叫前核准
 * (見 AI.Tools/Runtime/ToolCapabilityMap.cs 的 "git.push" → "Git.Push" 對應),這裡只負責把
 * 實際的 git 指令跑出來,核准與否的邏輯完全不在這層——這樣才能保證「不管哪個後端(MCP/Native)
 * 實作 push,Capability Guard 都一定會先擋一次」,不會因為工具層自己加了一個判斷而繞過去。
 * 若 rootPath 不是 git repository,回傳 success:false 而不是拋例外,讓呼叫端(GitAgent)自行
 * 決定要不要把它當成資訊性結果(規格書 v3 GitAgent 的容錯設計)。
 */
export const gitToolSchemas = {
  status: z.object({ rootPath: z.string() }),
  diff: z.object({ rootPath: z.string() }),
  commit: z.object({ rootPath: z.string(), message: z.string() }),
  checkout: z.object({ rootPath: z.string(), branch: z.string() }),
  branch: z.object({ rootPath: z.string(), name: z.string() }),
  push: z.object({ rootPath: z.string(), remote: z.string().optional(), branch: z.string().optional() })
};

function runGit(rootPath: string, args: string[]): Promise<{ exitCode: number; stdout: string; stderr: string }> {
  return new Promise((resolve) => {
    let stdout = "";
    let stderr = "";
    let proc;
    try {
      proc = spawn("git", args, { cwd: rootPath });
    } catch (err) {
      resolve({ exitCode: -1, stdout: "", stderr: (err as Error).message });
      return;
    }

    proc.stdout.on("data", (d) => (stdout += d.toString()));
    proc.stderr.on("data", (d) => (stderr += d.toString()));
    proc.on("error", (err) => resolve({ exitCode: -1, stdout, stderr: err.message }));
    proc.on("close", (exitCode) => resolve({ exitCode: exitCode ?? -1, stdout, stderr }));
  });
}

export async function status(input: z.infer<typeof gitToolSchemas.status>) {
  const result = await runGit(input.rootPath, ["status", "--porcelain"]);
  if (result.exitCode !== 0) {
    return { success: false, changes: [] as string[], error: result.stderr.trim() };
  }
  const changes = result.stdout.split("\n").map((l) => l.trim()).filter((l) => l.length > 0);
  return { success: true, changes };
}

export async function diff(input: z.infer<typeof gitToolSchemas.diff>) {
  const result = await runGit(input.rootPath, ["diff"]);
  if (result.exitCode !== 0) {
    return { success: false, diff: "", error: result.stderr.trim() };
  }
  return { success: true, diff: result.stdout };
}

export async function commit(input: z.infer<typeof gitToolSchemas.commit>) {
  const add = await runGit(input.rootPath, ["add", "-A"]);
  if (add.exitCode !== 0) {
    return { success: false, error: add.stderr.trim() };
  }
  const result = await runGit(input.rootPath, ["commit", "-m", input.message]);
  return { success: result.exitCode === 0, error: result.exitCode === 0 ? undefined : (result.stderr || result.stdout).trim() };
}

export async function checkout(input: z.infer<typeof gitToolSchemas.checkout>) {
  const result = await runGit(input.rootPath, ["checkout", input.branch]);
  return { success: result.exitCode === 0, error: result.exitCode === 0 ? undefined : result.stderr.trim() };
}

export async function branch(input: z.infer<typeof gitToolSchemas.branch>) {
  const result = await runGit(input.rootPath, ["branch", input.name]);
  return { success: result.exitCode === 0, error: result.exitCode === 0 ? undefined : result.stderr.trim() };
}

export async function push(input: z.infer<typeof gitToolSchemas.push>) {
  const remote = input.remote ?? "origin";
  let branchName = input.branch;

  if (!branchName) {
    const branchResult = await runGit(input.rootPath, ["rev-parse", "--abbrev-ref", "HEAD"]);
    if (branchResult.exitCode !== 0) {
      return { success: false, remote, branch: undefined, error: `無法取得目前分支:${branchResult.stderr.trim()}` };
    }
    branchName = branchResult.stdout.trim();
  }

  // -u 會在分支還沒設定 upstream 追蹤時自動設定,對第一次 push 的新分支比較友善,
  // 已經有 upstream 的分支加這個參數也不會有副作用。
  const result = await runGit(input.rootPath, ["push", "-u", remote, branchName]);
  return {
    success: result.exitCode === 0,
    remote,
    branch: branchName,
    error: result.exitCode === 0 ? undefined : (result.stderr || result.stdout).trim()
  };
}
