import { z } from "zod";
import { promises as fs } from "node:fs";
import path from "node:path";

/**
 * Search Tool: SearchText / SearchSymbol / SearchRegex(規格書 v1 第 10 節)。
 * Phase 2 最小實作:純 Node 檔案系統遞迴掃描 + 字串/正規比對,不依賴外部 ripgrep 執行檔,
 * 換取「不用另外安裝任何東西就能跑」的可攜性。之後若效能不夠,可以換成呼叫 ripgrep 而不影響
 * 這裡對外的 Tool 介面。
 */
export const searchToolSchemas = {
  searchText: z.object({ rootPath: z.string(), query: z.string(), maxResults: z.number().optional() }),
  searchSymbol: z.object({ rootPath: z.string(), symbol: z.string(), maxResults: z.number().optional() }),
  searchRegex: z.object({ rootPath: z.string(), pattern: z.string(), maxResults: z.number().optional() })
};

export interface SearchMatch {
  file: string;
  line: number;
  text: string;
}

const IGNORED_DIR_NAMES = new Set([
  "node_modules",
  "bin",
  "obj",
  ".git",
  ".artifacts",
  "dist",
  "out"
]);

async function* walkFiles(rootPath: string): AsyncGenerator<string> {
  let entries;
  try {
    entries = await fs.readdir(rootPath, { withFileTypes: true });
  } catch {
    return;
  }

  for (const entry of entries) {
    if (entry.name.startsWith(".") && entry.name !== ".") {
      // 允許遞迴進 . 開頭的檔案本身被搜尋到,但跳過像 .git / .artifacts 這種目錄。
      if (entry.isDirectory() && IGNORED_DIR_NAMES.has(entry.name)) {
        continue;
      }
    }
    const fullPath = path.join(rootPath, entry.name);
    if (entry.isDirectory()) {
      if (IGNORED_DIR_NAMES.has(entry.name)) {
        continue;
      }
      yield* walkFiles(fullPath);
    } else if (entry.isFile()) {
      yield fullPath;
    }
  }
}

async function searchByPredicate(
  rootPath: string,
  maxResults: number,
  predicate: (line: string) => boolean
): Promise<SearchMatch[]> {
  const matches: SearchMatch[] = [];

  for await (const file of walkFiles(rootPath)) {
    if (matches.length >= maxResults) {
      break;
    }

    let content: string;
    try {
      content = await fs.readFile(file, "utf-8");
    } catch {
      continue; // 可能是二進位檔或無讀取權限,略過。
    }

    const lines = content.split("\n");
    for (let i = 0; i < lines.length; i++) {
      if (predicate(lines[i])) {
        matches.push({ file, line: i + 1, text: lines[i].trim() });
        if (matches.length >= maxResults) {
          break;
        }
      }
    }
  }

  return matches;
}

export async function searchText(input: z.infer<typeof searchToolSchemas.searchText>) {
  const maxResults = input.maxResults ?? 50;
  const matches = await searchByPredicate(input.rootPath, maxResults, (line) => line.includes(input.query));
  return { matches };
}

export async function searchSymbol(input: z.infer<typeof searchToolSchemas.searchSymbol>) {
  // Phase 2 簡化版:符號搜尋用「單字邊界比對」近似,不是真正的語言 Server 語意搜尋
  // (規格書 v1 第 10 節的 SearchSymbol 留給日後接 LSP 時再升級,介面不需變動)。
  const maxResults = input.maxResults ?? 50;
  const boundaryRegex = new RegExp(`\\b${escapeRegExp(input.symbol)}\\b`);
  const matches = await searchByPredicate(input.rootPath, maxResults, (line) => boundaryRegex.test(line));
  return { matches };
}

export async function searchRegex(input: z.infer<typeof searchToolSchemas.searchRegex>) {
  const maxResults = input.maxResults ?? 50;
  let regex: RegExp;
  try {
    regex = new RegExp(input.pattern);
  } catch (err) {
    return { matches: [], error: `Invalid regex: ${(err as Error).message}` };
  }
  const matches = await searchByPredicate(input.rootPath, maxResults, (line) => regex.test(line));
  return { matches };
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
