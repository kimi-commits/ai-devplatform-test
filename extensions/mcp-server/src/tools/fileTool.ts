import { z } from "zod";
import { promises as fs } from "node:fs";

/**
 * File Tool: ReadFile / WriteFile / DeleteFile / Copy / Move(規格書 v1 第 10 節)。
 */
export const fileToolSchemas = {
  readFile: z.object({ path: z.string() }),
  writeFile: z.object({ path: z.string(), content: z.string() }),
  deleteFile: z.object({ path: z.string() }),
  copy: z.object({ from: z.string(), to: z.string() }),
  move: z.object({ from: z.string(), to: z.string() })
};

export async function readFile(input: z.infer<typeof fileToolSchemas.readFile>) {
  return await fs.readFile(input.path, "utf-8");
}

export async function writeFile(input: z.infer<typeof fileToolSchemas.writeFile>) {
  await fs.writeFile(input.path, input.content, "utf-8");
  return { success: true };
}

export async function deleteFile(input: z.infer<typeof fileToolSchemas.deleteFile>) {
  // High 風險操作:Runtime 層的 Capability Guard 應在呼叫此工具前完成人工核准(規格書 v3 第 6 節)。
  await fs.unlink(input.path);
  return { success: true };
}

export async function copy(input: z.infer<typeof fileToolSchemas.copy>) {
  await fs.copyFile(input.from, input.to);
  return { success: true };
}

export async function move(input: z.infer<typeof fileToolSchemas.move>) {
  await fs.rename(input.from, input.to);
  return { success: true };
}
