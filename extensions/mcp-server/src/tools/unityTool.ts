import { z } from "zod";

/**
 * Unity Tool: Build / Addressables / Prefab / Scene / Animator / Material / Shader(規格書 v1 第 10 節)。
 * 建議走 Native Adapter 而非此 MCP 後端——Unity Editor Scripting API 本質上只能 in-process 呼叫,
 * 跨進程 MCP 會增加延遲與穩定性風險(規格書 v3 第 2、11 節)。這裡先留 MCP 版本作為對照/備援。
 */
export const unityToolSchemas = {
  build: z.object({ rootPath: z.string(), target: z.string() })
};

export async function build(_input: z.infer<typeof unityToolSchemas.build>) {
  return { success: false, error: "Prefer Native Adapter for Unity; MCP path not implemented (Phase 5)." };
}
