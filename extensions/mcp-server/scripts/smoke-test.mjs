// Phase 2 MCP smoke test.
// 用官方 @modelcontextprotocol/sdk 的 Client + StdioClientTransport,
// 實際 spawn dist/index.js 這個 MCP Server 子行程,走一次真正的 MCP 協定
// (initialize -> tools/list -> tools/call),驗證 Server 的工具是否可用。
// 用途:在 sandbox 沒有 .NET runtime 可以跑 AI.MCP/AI.Host 的情況下,
// 先用 Node 端獨立驗證 MCP Server 本身的協定正確性。
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { mkdtemp, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const serverEntry = path.join(__dirname, "..", "dist", "index.js");

let passed = 0;
let failed = 0;

function check(label, condition, detail) {
  if (condition) {
    passed++;
    console.log(`PASS  ${label}`);
  } else {
    failed++;
    console.log(`FAIL  ${label}${detail ? ` -- ${detail}` : ""}`);
  }
}

async function callTool(client, name, args) {
  const result = await client.callTool({ name, arguments: args });
  const text = result.content?.[0]?.text ?? "";
  try {
    return { raw: result, parsed: JSON.parse(text) };
  } catch {
    return { raw: result, parsed: null };
  }
}

async function main() {
  const workDir = await mkdtemp(path.join(tmpdir(), "mcp-smoke-"));
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [serverEntry]
  });
  const client = new Client({ name: "smoke-test-client", version: "0.1.0" });

  try {
    await client.connect(transport);
    console.log(`Connected. Test workspace: ${workDir}\n`);

    // 1) tools/list
    const toolsResult = await client.listTools();
    const toolNames = toolsResult.tools.map((t) => t.name).sort();
    const expected = [
      "file.readFile",
      "file.writeFile",
      "file.deleteFile",
      "file.copy",
      "file.move",
      "search.searchText",
      "search.searchSymbol",
      "search.searchRegex",
      "git.status",
      "git.diff",
      "git.commit",
      "git.checkout",
      "git.branch",
      "build.run",
      "terminal.run",
      "browser.open",
      "unity.build"
    ].sort();
    check(
      "tools/list 回傳所有預期工具",
      expected.every((name) => toolNames.includes(name)),
      `got: ${toolNames.join(", ")}`
    );

    // 2) file.writeFile / file.readFile round-trip
    const testFilePath = path.join(workDir, "hello.txt");
    const writeRes = await callTool(client, "file.writeFile", {
      path: testFilePath,
      content: "Hello from MCP smoke test\nLine 2 contains SEARCHTOKEN\n"
    });
    check("file.writeFile 成功", writeRes.parsed?.success === true, JSON.stringify(writeRes.parsed));

    // file.readFile 直接回傳原始字串內容(不是包一層物件),所以 parsed 本身就是字串。
    const readRes = await callTool(client, "file.readFile", { path: testFilePath });
    check(
      "file.readFile 讀回寫入內容",
      typeof readRes.parsed === "string" && readRes.parsed.includes("SEARCHTOKEN"),
      JSON.stringify(readRes.parsed)
    );

    // 3) search.searchText 在剛寫入的檔案中找到字串
    const searchRes = await callTool(client, "search.searchText", {
      rootPath: workDir,
      query: "SEARCHTOKEN"
    });
    check(
      "search.searchText 找到剛寫入的字串",
      Array.isArray(searchRes.parsed?.matches) && searchRes.parsed.matches.length > 0,
      JSON.stringify(searchRes.parsed)
    );

    // 4) search.searchRegex 驗證正規比對
    const regexRes = await callTool(client, "search.searchRegex", {
      rootPath: workDir,
      pattern: "^Hello.*test$"
    });
    check(
      "search.searchRegex 正規比對成功",
      Array.isArray(regexRes.parsed?.matches) && regexRes.parsed.matches.length > 0,
      JSON.stringify(regexRes.parsed)
    );

    // 5) git 系列:在非 git repo 目錄下呼叫,預期 success:false 而不是丟例外
    const gitStatusRes = await callTool(client, "git.status", { rootPath: workDir });
    check(
      "git.status 在非 git repo 下回傳 success:false(不丟例外)",
      gitStatusRes.parsed?.success === false,
      JSON.stringify(gitStatusRes.parsed)
    );

    // 6) 在同一目錄初始化 git repo,驗證 git.branch / git.status 正常運作
    const { execFileSync } = await import("node:child_process");
    execFileSync("git", ["init", "-q"], { cwd: workDir });
    execFileSync("git", ["config", "user.email", "smoke@test.local"], { cwd: workDir });
    execFileSync("git", ["config", "user.name", "Smoke Test"], { cwd: workDir });

    const gitStatusAfterInit = await callTool(client, "git.status", { rootPath: workDir });
    check(
      "git.status 在初始化過的 repo 下回傳 success:true 且看得到未追蹤檔案",
      gitStatusAfterInit.parsed?.success === true && gitStatusAfterInit.parsed.changes.length > 0,
      JSON.stringify(gitStatusAfterInit.parsed)
    );

    // git branch/checkout 需要至少一個 commit(master 才有 ref 可以分支),先透過 MCP 的
    // git.commit 工具建立第一個 commit,順便驗證 commit 工具本身。
    const commitRes = await callTool(client, "git.commit", {
      rootPath: workDir,
      message: "smoke test: initial commit"
    });
    check("git.commit 建立初始 commit 成功", commitRes.parsed?.success === true, JSON.stringify(commitRes.parsed));

    const branchRes = await callTool(client, "git.branch", { rootPath: workDir, name: "smoke-test-branch" });
    check("git.branch 建立分支成功", branchRes.parsed?.success === true, JSON.stringify(branchRes.parsed));

    const checkoutRes = await callTool(client, "git.checkout", {
      rootPath: workDir,
      branch: "smoke-test-branch"
    });
    check("git.checkout 切換分支成功", checkoutRes.parsed?.success === true, JSON.stringify(checkoutRes.parsed));

    // 修改已追蹤的檔案後,git.diff 應該能看到差異
    await writeFile(testFilePath, "Hello from MCP smoke test\nLine 2 contains SEARCHTOKEN\nAppended line.\n");
    const diffRes = await callTool(client, "git.diff", { rootPath: workDir });
    check(
      "git.diff 偵測到已追蹤檔案的變更",
      diffRes.parsed?.success === true && typeof diffRes.parsed.diff === "string" && diffRes.parsed.diff.includes("Appended line"),
      JSON.stringify(diffRes.parsed)
    );

    // 7) file.copy / file.move / file.deleteFile
    const copyTarget = path.join(workDir, "hello-copy.txt");
    const copyRes = await callTool(client, "file.copy", { from: testFilePath, to: copyTarget });
    check("file.copy 成功", copyRes.parsed?.success === true, JSON.stringify(copyRes.parsed));

    const moveTarget = path.join(workDir, "hello-moved.txt");
    const moveRes = await callTool(client, "file.move", { from: copyTarget, to: moveTarget });
    check("file.move 成功", moveRes.parsed?.success === true, JSON.stringify(moveRes.parsed));

    const deleteRes = await callTool(client, "file.deleteFile", { path: moveTarget });
    check("file.deleteFile 成功", deleteRes.parsed?.success === true, JSON.stringify(deleteRes.parsed));
  } finally {
    await client.close().catch(() => {});
    await rm(workDir, { recursive: true, force: true }).catch(() => {});
  }

  console.log(`\n${passed} passed, ${failed} failed`);
  if (failed > 0) {
    process.exitCode = 1;
  }
}

main().catch((err) => {
  console.error("Smoke test crashed:", err);
  process.exitCode = 1;
});
