// Shared paragraph detection for reading, double-tap selection and the selection menu.
// Inspect only terminal cells, not program identities: four or more vertical bars split panels; blank or rule rows end blocks;
// list, prompt and message prefixes start blocks; distinct backgrounds identify bands and floating elements.
import type { Terminal } from '@xterm/xterm';
import type { Point } from './anchor';

let term: Terminal;
export function bindTerminal(bound: Terminal): void { term = bound; }

export const PLACEHOLDER = '\u{10EEEE}';
// Include vim's default split separator (fillchars vert:|), a reverse-video column of ASCII bars.
export const BARS = ['│', '▎', '┃', '║', '▏', '▕', '|'];
export const WORD = /[\p{L}\p{N}]/u;
export const OPENER = /^\s*([❯›>⏺●•◦▪*+-]|\d+[.)])\s/;
export const LEAD = /^\s*([❯›>⏺●•◦▪*+-]|\d+[.)])\s+/;
export const MARKDOWN = /\*\*|__|`|^#+\s/g;
export const BOX = /[│▎┃║▏▕─━═╭╮╰╯┌┐└┘├┤┬┴┼╞╡╪]/;
// Wide cells span two columns. Characters retain their source cell; null denotes a space inserted between rows.
export type Cell = { row: number; col: number; width: number };
export type Char = { ch: string; cell: Cell | null };
export type Area = { top: number; bottom: number; left: number; right: number };
export type Block = Area & { text: string; chars: Char[] };
// Read rows from buffer line `from` (the viewport by default), with column x at index x.
// Use '' for the second half of wide cells and ' ' for empty cells. Omit trailing blanks so work scales with text, not grid width.
export function screenRows(from = term.buffer.active.viewportY, count = term.rows): string[][] {
  const buffer = term.buffer.active;
  const reuse = buffer.getNullCell();
  const lines = (term as any)._core.buffer.lines;
  return Array.from({ length: Math.max(0, count) }, (_, row) => {
    const line = buffer.getLine(from + row);
    const own = lines.get(from + row) as { getTrimmedLength(): number } | undefined;
    const text = line === undefined || own === undefined ? 0 : Math.min(term.cols, own.getTrimmedLength());
    const cells = new Array<string>(text);
    for (let x = 0; x < text; x++) {
      const cell = line!.getCell(x, reuse);
      // Treat image placeholders as blank: never read or highlight them.
      cells[x] = cell === undefined ? ' ' : cell.getWidth() === 0 ? '' : cell.getChars().startsWith(PLACEHOLDER) ? ' ' : cell.getChars() || ' ';
    }
    return cells;
  });
}
// A panel separator covering buffer rows top..bottom.
export type Split = { x: number; top: number; bottom: number };

// Four or more consecutive vertical bars in one column split only those rows into panels, including borderless tables.
// Rows outside the run retain their full width, so a table does not split surrounding prose. Inspect only cells containing bars.
export function splitsOf(rows: string[][]): Split[] {
  const splits: Split[] = [];
  const bars = new Map<number, number[]>();
  rows.forEach((cells, y) => {
    for (let x = 1; x < Math.min(cells.length, term.cols - 1); x++) {
      if (BARS.includes(cells[x])) {
        const at = bars.get(x);
        if (at === undefined) { bars.set(x, [y]); } else { at.push(y); }
      }
    }
  });
  for (const x of [...bars.keys()].sort((a, b) => a - b)) {
    const ys = bars.get(x)!;
    let top = ys[0];
    for (let i = 1; i <= ys.length; i++) {
      if (i < ys.length && ys[i] === ys[i - 1] + 1) { continue; }
      if (ys[i - 1] - top + 1 >= 4) { splits.push({ x, top, bottom: ys[i - 1] }); }
      top = ys[i];
    }
  }
  return splits;
}
export function splitsAt(splits: Split[], row: number): number[] {
  return splits.filter((s) => s.top <= row && row <= s.bottom).map((s) => s.x).sort((a, b) => a - b);
}
// Find the surrounding rows with identical panel separators; blocks cannot cross this boundary.
export function stretchOf(splits: Split[], row: number, count: number): { top: number; bottom: number } {
  const key = splitsAt(splits, row).join(',');
  let top = row;
  let bottom = row;
  while (top > 0 && splitsAt(splits, top - 1).join(',') === key) {
    top--;
  }
  while (bottom + 1 < count && splitsAt(splits, bottom + 1).join(',') === key) {
    bottom++;
  }
  return { top, bottom };
}

export function isBlank(line: string): boolean {
  return !WORD.test(line);
}

export function indentOf(line: string): number {
  return line.length - line.trimStart().length;
}
// On the alternate screen, a change between plain and distinct-background rows is a block boundary even without a blank row.
// This keeps prose separate from user messages, pinned messages and floating buttons detected by bandOf.
export function bandEdge(row: number): boolean {
  return term.buffer.active.type === 'alternate' && row > 0
    && (bandOf(row, 0, term.cols) === 'none') !== (bandOf(row - 1, 0, term.cols) === 'none');
}
// Stop at a blank or rule row, a new block prefix, or a background boundary.
export function bottomOf(panel: string[], top: number): number {
  let bottom = top;
  while (bottom + 1 < panel.length && !isBlank(panel[bottom + 1]) && !OPENER.test(panel[bottom + 1]) && !bandEdge(bottom + 1)) {
    bottom++;
  }
  return bottom;
}
// Map each UTF-16 code unit, as used by string indexes, to its source cell.
export function rowChars(cells: string[], row: number, left: number): Char[] {
  const chars: Char[] = [];
  cells.forEach((text, i) => {
    const cell = { row, col: left + i, width: cells[i + 1] === '' ? 2 : 1 };
    for (const ch of text.split('')) {
      chars.push({ ch, cell });
    }
  });
  return chars;
}
// Remove all matches of a global pattern, or only the first match of a non-global pattern.
export function without(chars: Char[], pattern: RegExp): Char[] {
  const text = chars.map((c) => c.ch).join('');
  const gone = new Set<number>();
  for (const match of text.matchAll(pattern.global ? pattern : new RegExp(pattern.source, `${pattern.flags}g`))) {
    const start = match.index ?? 0;
    for (let i = start; i < start + match[0].length; i++) {
      gone.add(i);
    }
    if (!pattern.global) {
      break;
    }
  }
  return chars.filter((_, i) => !gone.has(i));
}
export function trimmed(chars: Char[]): Char[] {
  let start = 0;
  let end = chars.length;
  while (start < end && /\s/.test(chars[start].ch)) {
    start++;
  }
  while (end > start && /\s/.test(chars[end - 1].ch)) {
    end--;
  }
  return chars.slice(start, end);
}
// Remove list, prompt, message and markdown markers; replace box glyphs with spaces and trim each row.
// Join rows with one space, except after '-' before a lowercase letter, then collapse whitespace. Retain source-cell mappings.
export function blockOf(rows: string[][], top: number, bottom: number, left: number, right: number): Block {
  const joined: Char[] = [];
  for (let i = top; i <= bottom; i++) {
    const line = trimmed(without(without(rowChars(rows[i].slice(left, right), i, left), LEAD), MARKDOWN)
      .map((c) => (BOX.test(c.ch) ? { ch: ' ', cell: c.cell } : c)));
    const first = line.map((c) => c.ch).join('');
    if (joined.length > 0 && !(joined[joined.length - 1].ch === '-' && /^\p{Ll}/u.test(first))) {
      joined.push({ ch: ' ', cell: null });
    }
    joined.push(...line);
  }
  const chars: Char[] = [];
  for (const c of joined) {
    if (!/\s/.test(c.ch)) {
      chars.push(c);
    } else if (chars.length > 0 && chars[chars.length - 1].ch !== ' ') {
      chars.push({ ch: ' ', cell: null });
    }
  }
  if (chars.length > 0 && chars[chars.length - 1].ch === ' ') {
    chars.pop();
  }
  return { text: chars.map((c) => c.ch).join(''), chars, top, bottom, left, right };
}
// Return the touched panel's columns and row text, leaving rows outside its separator range blank.
export function panelOf(rows: string[][], row: number, col: number): { panel: string[]; left: number; right: number } {
  const splits = splitsOf(rows);
  const xs = splitsAt(splits, row);
  const left = Math.max(-1, ...xs.filter((x) => x < col)) + 1;
  const right = Math.min(term.cols, ...xs.filter((x) => x > col));
  const { top, bottom } = stretchOf(splits, row, rows.length);
  return { panel: rows.map((r, y) => (y < top || y > bottom ? '' : r.slice(left, right).join(''))), left, right };
}
// Find the block's first row from a touched panel row; blank rows have no block.
export function blockTop(panel: string[], row: number): number | null {
  if (isBlank(panel[row])) {
    return null;
  }
  let top = row;
  while (top > 0 && !isBlank(panel[top - 1]) && !OPENER.test(panel[top]) && indentOf(panel[top - 1]) <= indentOf(panel[top]) + 3
    && !bandEdge(top)) {
    top--;
  }
  return top;
}
// Return a visible paragraph's first and last buffer cells, excluding list and prompt prefixes.
// Off-screen or blank cells have no paragraph. Double tap uses the same handles as word selection.
export function paragraphAt(point: Point): [Point, Point] | null {
  const top = term.buffer.active.viewportY;
  const row = point.row - top;
  if (row < 0 || row >= term.rows) {
    return null;
  }
  const rows = screenRows();
  const { panel, left, right } = panelOf(rows, row, point.col);
  const first = blockTop(panel, row);
  if (first === null) {
    return null;
  }
  const cells = blockOf(rows, first, bottomOf(panel, row), left, right).chars.map((c) => c.cell).filter((c): c is Cell => c !== null);
  if (cells.length === 0) {
    return null;
  }
  const a = cells[0];
  const b = cells[cells.length - 1];
  return [{ col: a.col, row: a.row + top }, { col: b.col + b.width - 1, row: b.row + top }];
}

// Classify backgrounds within a panel: 'row' if the first cell has a distinct background (such as a user message),
// 'float' if only later cells do (such as a jump-to-bottom button), otherwise 'none'. These are not paragraph text.
// Row indexes are relative to the viewport and may extend below it.
export function bandOf(row: number, left: number, right: number): 'none' | 'row' | 'float' {
  return bandAt(term.buffer.active.viewportY + row, left, right);
}

// Sample eight cells per screen row using raw background words (color mode and value; 0 means default).
// Use the default if any sample has it; otherwise use the most common background for programs that paint every cell.
let pageAt = -1e9;
let pageWord = 0;
export function pageBackground(): number {
  const now = performance.now();
  if (now - pageAt < 50) { return pageWord; }
  pageAt = now;
  const buffer = term.buffer.active;
  const lines = (term as any)._core.buffer.lines;
  const count = new Map<number, number>();
  pageWord = 0;
  for (let y = buffer.baseY; y < buffer.baseY + term.rows; y++) {
    const line = lines.get(y) as { _data: Uint32Array; length: number } | undefined;
    if (line === undefined) { continue; }
    for (let s = 0; s < 8; s++) {
      const x = Math.floor((s + 0.5) * term.cols / 8);
      if (x >= line.length) { return pageWord; }
      const word = line._data[x * 3 + 2];
      if ((word & 0x3000000) === 0) { return pageWord; }
      count.set(word & 0x3ffffff, (count.get(word & 0x3ffffff) ?? 0) + 1);
    }
  }
  let best = -1;
  for (const [word, n] of count) { if (n > best) { best = n; pageWord = word; } }
  return pageWord;
}

// Classify a buffer row using the same background rules as bandOf.
export function bandAt(row: number, left: number, right: number): 'none' | 'row' | 'float' {
  // Read background words directly: bits 24-25 of each cell's third word hold the color mode; 0 means default.
  // Avoid allocating a cell object per column on wide rows.
  const line = (term as any)._core.buffer.lines.get(row) as { _data: Uint32Array; length: number } | undefined;
  if (line === undefined) {
    return 'none';
  }
  const page = pageBackground();
  const own = (x: number): boolean => x < line.length && (line._data[x * 3 + 2] & 0x3000000) !== 0 && (line._data[x * 3 + 2] & 0x3ffffff) !== page;
  if (own(left)) {
    return 'row';
  }
  for (let x = left + 1; x < right; x++) {
    if (own(x)) {
      return 'float';
    }
  }
  return 'none';
}
