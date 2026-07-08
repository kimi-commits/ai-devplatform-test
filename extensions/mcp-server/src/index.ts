import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import * as fileTool from "./tools/fileTool.js";
import * as searchTool from "./tools/searchTool.js";
import * as gitTool from "./tools/gitTool.js";
import * as buildTool from "./tools/buildTool.js";
import * as terminalTool from "./tools/terminalTool.js";
import * as browserTool from "./tools/browserTool.js";
import * as unityTool from "./tools/unityTool.js";

/**
 * AI Development Platform — MCP Tool Layer(規格書 v1 第 10 節,第一版)。
 * File / Search / Git / Build / Terminal / Browser / Unity。
 * 供 AI.Tools 專案的 McpToolAdapter 呼叫(規格書 v3 第 11 節)。
 */
const server = new McpServer({
  name: "ai-devplatform-mcp-server",
  version: "0.1.0"
});

function textResult(payload: unknown) {
  return { content: [{ type: "text" as const, text: JSON.stringify(payload) }] };
}

server.tool("file.readFile", fileTool.fileToolSchemas.readFile.shape, async (input) =>
  textResult(await fileTool.readFile(input))
);
server.tool("file.writeFile", fileTool.fileToolSchemas.writeFile.shape, async (input) =>
  textResult(await fileTool.writeFile(input))
);
server.tool("file.deleteFile", fileTool.fileToolSchemas.deleteFile.shape, async (input) =>
  textResult(await fileTool.deleteFile(input))
);
server.tool("file.copy", fileTool.fileToolSchemas.copy.shape, async (input) => textResult(await fileTool.copy(input)));
server.tool("file.move", fileTool.fileToolSchemas.move.shape, async (input) => textResult(await fileTool.move(input)));

server.tool("search.searchText", searchTool.searchToolSchemas.searchText.shape, async (input) =>
  textResult(await searchTool.searchText(input))
);
server.tool("search.searchSymbol", searchTool.searchToolSchemas.searchSymbol.shape, async (input) =>
  textResult(await searchTool.searchSymbol(input))
);
server.tool("search.searchRegex", searchTool.searchToolSchemas.searchRegex.shape, async (input) =>
  textResult(await searchTool.searchRegex(input))
);

server.tool("git.status", gitTool.gitToolSchemas.status.shape, async (input) => textResult(await gitTool.status(input)));
server.tool("git.diff", gitTool.gitToolSchemas.diff.shape, async (input) => textResult(await gitTool.diff(input)));
server.tool("git.commit", gitTool.gitToolSchemas.commit.shape, async (input) => textResult(await gitTool.commit(input)));
server.tool("git.checkout", gitTool.gitToolSchemas.checkout.shape, async (input) => textResult(await gitTool.checkout(input)));
server.tool("git.branch", gitTool.gitToolSchemas.branch.shape, async (input) => textResult(await gitTool.branch(input)));
server.tool("git.push", gitTool.gitToolSchemas.push.shape, async (input) => textResult(await gitTool.push(input)));

server.tool("build.run", buildTool.buildToolSchemas.build.shape, async (input) => textResult(await buildTool.build(input)));

server.tool("terminal.run", terminalTool.terminalToolSchemas.run.shape, async (input) =>
  textResult(await terminalTool.run(input))
);

server.tool("browser.open", browserTool.browserToolSchemas.open.shape, async (input) =>
  textResult(await browserTool.open(input))
);

server.tool("unity.build", unityTool.unityToolSchemas.build.shape, async (input) => textResult(await unityTool.build(input)));

async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error("AI-DevPlatform MCP server running on stdio");
}

main().catch((err) => {
  console.error("Fatal error starting MCP server:", err);
  process.exit(1);
});
