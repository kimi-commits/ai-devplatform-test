import { z } from "zod";

/**
 * Unity Tool: Build / Addressables / Prefab / Scene / Animator / Material / Shader(規格書 v1 第 10 節)。
 *
 * Phase 5 真實實作已經完成,但走的是 Native Adapter,不是這個檔案——見
 * src/AI.Tools/Adapters/NativeUnityToolHandlers.cs 與 AI.Agents/BuildAgent.cs。原因跟這裡原本的
 * 註解一樣:Unity Editor Scripting API 本質上只能 in-process 呼叫,跨進程 MCP 會增加延遲與穩定性
 * 風險(規格書 v3 第 2、11 節)。AI.Host/Program.cs 的 mcpToolNames 清單刻意不含 "unity.build",
 * 所以下面這個函式實際上不會被 ToolRuntime 呼叫到,純粹保留當文件對照(如果之後真的要支援
 * 「沒有本機 Unity Editor、但有一個跨網路的建置伺服器」這種情境,MCP 版本才有機會派上用場)。
 */
export const unityToolSchemas = {
  build: z.object({ rootPath: z.string(), target: z.string() })
};

export async function build(_input: z.infer<typeof unityToolSchemas.build>) {
  return { success: false, error: "Unity build is implemented via the Native Adapter, not this MCP path. See NativeUnityToolHandlers.cs." };
}
