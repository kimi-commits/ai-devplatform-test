import { z } from "zod";
import { spawn } from "node:child_process";

/** Terminal Tool: Run / Kill / ReadOutput(規格書 v1 第 10 節)。 */
export const terminalToolSchemas = {
  run: z.object({ command: z.string(), cwd: z.string().optional() })
};

export function run(input: z.infer<typeof terminalToolSchemas.run>): Promise<{ exitCode: number; output: string }> {
  return new Promise((resolve) => {
    let output = "";
    const proc = spawn(input.command, { shell: true, cwd: input.cwd });
    proc.stdout.on("data", (d) => (output += d.toString()));
    proc.stderr.on("data", (d) => (output += d.toString()));
    proc.on("close", (exitCode) => resolve({ exitCode: exitCode ?? -1, output }));
  });
}
