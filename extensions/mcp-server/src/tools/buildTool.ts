import { z } from "zod";
import { spawn } from "node:child_process";

/** Build Tool: dotnet / go / Unity / Node(規格書 v1 第 10 節)。 */
export const buildToolSchemas = {
  build: z.object({
    rootPath: z.string(),
    kind: z.enum(["dotnet", "go", "unity", "node"])
  })
};

export function build(input: z.infer<typeof buildToolSchemas.build>): Promise<{ exitCode: number; log: string }> {
  const commandByKind: Record<string, [string, string[]]> = {
    dotnet: ["dotnet", ["build"]],
    go: ["go", ["build", "./..."]],
    node: ["npm", ["run", "build"]],
    unity: ["unity-editor", ["-batchmode", "-quit", "-executeMethod", "BuildScript.Build"]]
  };

  const [cmd, args] = commandByKind[input.kind];
  return new Promise((resolve) => {
    let log = "";
    const proc = spawn(cmd, args, { cwd: input.rootPath });
    proc.stdout.on("data", (d) => (log += d.toString()));
    proc.stderr.on("data", (d) => (log += d.toString()));
    proc.on("close", (exitCode) => resolve({ exitCode: exitCode ?? -1, log }));
  });
}
