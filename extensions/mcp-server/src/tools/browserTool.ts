import { z } from "zod";

/** Browser Tool: Open / Screenshot / Click / Input(規格書 v1 第 10 節)。Phase 2 起串接實際瀏覽器自動化。 */
export const browserToolSchemas = {
  open: z.object({ url: z.string() }),
  screenshot: z.object({ path: z.string() })
};

export async function open(_input: z.infer<typeof browserToolSchemas.open>) {
  return { success: false, error: "Browser automation not yet wired (Phase 2)." };
}
