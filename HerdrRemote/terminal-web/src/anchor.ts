// Shared cell-identity tracking for selection and reading, without guessing text locations.
//
// The stream is the sequence of non-blank buffer cells in row order, optionally limited to panel columns.
// Whitespace, line folds and kitty image placeholders do not contribute to it.
//
// A Span identifies a stream range, such as a sentence or selection endpoint, using:
// - Invisible tags on its first and last eligible cells. An extended-attributes object stores the span ID as an RGB underline
//   color with no underline style, leaving rendering and Copy unchanged. Tags follow scrollback, trimming, IL/DL/SU/SD,
//   scroll-region shifts and reflow (zoom, rotation, keyboard). Rewriting, erasing or trimming a cell removes its tag.
// - A marker on the first logical row of the first tagged cell, used only to detect removal from the normal buffer.
// Tags rely on xterm.js internals (_core.buffer.lines, _data, _extendedAttrs), pinned at 6.0.0. Verify each tag write through the
// public cell API; a failed readback disables tag lookup for the session.
//
// Resolution returns found, unproven (rewritten but not relocated), or gone for each span. A new copy is accepted only if a
// neighbor proven in this pass has matching intervening text, or surrounding rows have one unambiguous measured movement.
// Never choose text merely because it is unique on-screen or closest to the previous location.
import type { IMarker, Terminal } from '@xterm/xterm';

export type Point = { col: number; row: number };
// A non-blank stream cell with its characters, buffer location and column width.
export type Cell = { ch: string; row: number; col: number; width: number };
// First and last cells of a span, using each cell's starting column.
export type Loc = { first: Point; last: Point; lastWidth: number };

export type Status = 'found' | 'unproven' | 'hidden' | 'gone';

export interface Span {
  id: number;
  key: string;
  // Character offsets and lengths of the tagged cells within key; tagLast is null for a single-tag span.
  tagFirst: { off: number; len: number } | null;
  tagLast: { off: number; len: number } | null;
  marker: IMarker | null;
  // Record the gap from the previous span when created or relocated: always its character count, and its text when short.
  gap: string | null;
  gapLen: number;
  // Panel bounds and the grid width at capture; after a width change, read the full row instead.
  left: number;
  right: number;
  cols: number;
  alt: boolean;
  loc: Loc | null;
  status: Status;
  // Track off-screen alternate-buffer cells in screen-relative rows while every subsequent movement remains proven.
  virtual: Loc | null;
  // Retain the last proven location to detect sentences rewritten in place.
  was: Loc | null;
}

// Snapshot surrounding rows after a proven pass as the reference for measuring subsequent movement.
export interface Band {
  cols: number;
  alt: boolean;
  top: number;
  // Whitespace-free text of each row (full width).
  rows: string[];
  // Concatenated row text and each row's starting offset.
  stream: string;
  starts: number[];
  // Stream offset of each span fully inside the band.
  offsets: Map<Span, number>;
  // Span locations at capture.
  locs: Map<Span, Loc>;
}

export interface Holder {
  band: Band | null;
}

export type Resolved = {
  found: number;
  // Movement this pass: 'none' needs no comparison or lacks a band; 'same' shifts rows by k at unchanged width;
  // 'stream' aligns text across the frame; 'ambiguous' cannot prove a movement.
  movement: 'none' | 'same' | 'stream' | 'ambiguous';
  shift: number;
  // Spans rewritten with different text while the surrounding frame remains stationary.
  changed: Span[];
};

const HAS_EXT = 0x10000000;
const CM_MASK = 0x3000000;
const CM_RGB = 0x3000000;
const ID_MASK = 0xffffff;
const PLACEHOLDER = '\u{10EEEE}';
const PLACEHOLDER_RUN = /\u{10EEEE}\p{M}*/gu;
const FRAME = /^[\u2500-\u259f]/;
const GAP_TEXT_MAX = 160;
const WALK_ROWS = 600;
const BAND_ROWS = 96;
const BAND_MARGIN = 8;
const MIN_OTHER_ROWS = 3;
const MIN_OTHER_CHARS = 256;

type Line = { _data: Uint32Array; _extendedAttrs: Record<string, { underlineColor: number; underlineStyle: number; urlId: number; clone(): unknown } | undefined> };
type TagMap = Map<number, Point[]>;

export type CellTags = { ids: number[]; owned: Set<number>; valid: boolean };

export class Anchors {
  // Disable tag lookup after any tag fails public-API readback, indicating incompatible xterm.js internals.
  tagsOn = true;
  // Per-pass lookup diagnostics for the test harness; these do not affect behavior.
  lastMove: unknown = null;
  readonly stats = { passes: 0, suspectPasses: 0, byTag: 0, byNeighbour: 0, byMovement: 0, byStream: 0, byBlock: 0, byLocal: 0, byCut: 0, unproven: 0, ambiguous: 0, noBand: 0 };
  private nextId = 2;
  private cuts: { span: Span; k: number; rows: number }[] = [];
  // Hold resolution and band snapshots during partial frames: a ?2026 block or an alternate-screen resize awaiting repaint.
  held: () => boolean = () => false;
  private readonly core: any;
  // Markers track removal from the buffer without a separate trimmed-line counter.
  constructor(readonly term: Terminal) {
    this.core = (term as any)._core;
  }

  get alternate(): boolean {
    return this.term.buffer.active.type === 'alternate';
  }

  // ---- the stream -------------------------------------------------------------------------------------------------------------

  private lineAt(row: number): Line | undefined {
    return this.core.buffer.lines.get(row);
  }

  // Read non-blank cells from left to right within panel bounds, stopping at trimmed length.
  // Use line methods directly so mostly blank wide rows cost only their text.
  private rowCells(row: number, left: number, right: number, out: Cell[], includeDecorations = false): void {
    const line = this.core.buffer.lines.get(row) as { getTrimmedLength(): number; getWidth(i: number): number; hasContent(i: number): number; getString(i: number): string } | undefined;
    if (line === undefined) { return; }
    const end = Math.min(right, this.term.cols, line.getTrimmedLength());
    for (let col = Math.max(0, left); col < end; col++) {
      const width = line.getWidth(col);
      if (width === 0 || line.hasContent(col) === 0) { continue; }
      const ch = line.getString(col);
      if (ch !== '' && ch.trim() !== '' && (includeDecorations || (!ch.startsWith(PLACEHOLDER) && !FRAME.test(ch)))) { out.push({ ch, row, col, width }); }
    }
  }

  // Read inclusive buffer rows, full width unless panel bounds are supplied.
  cells(from: number, to: number, left = 0, right = this.term.cols, includeDecorations = false): Cell[] {
    const out: Cell[] = [];
    const last = Math.min(to, this.term.buffer.active.length - 1);
    for (let row = Math.max(0, from); row <= last; row++) { this.rowCells(row, left, right, out, includeDecorations); }
    return out;
  }

  panelOf(span: Span): [number, number] {
    return this.term.cols === span.cols ? [span.left, span.right] : [0, this.term.cols];
  }

  // Read exactly count characters forward, including the starting cell only when requested.
  // Return null if the stream ends early or a cell crosses the character boundary.
  private forward(from: Point, inclusive: boolean, count: number, left: number, right: number): Cell[] | null {
    const out: Cell[] = [];
    let total = 0;
    const buffer = this.term.buffer.active;
    for (let row = from.row; row < buffer.length && row < from.row + WALK_ROWS && total < count; row++) {
      const row_: Cell[] = [];
      this.rowCells(row, left, right, row_);
      for (const c of row_) {
        if (row === from.row && (inclusive ? c.col < from.col : c.col <= from.col)) { continue; }
        if (total >= count) { break; }
        out.push(c);
        total += c.ch.length;
      }
    }
    return total === count ? out : null;
  }

  // Read exactly count characters before the point, excluding its cell, in stream order; return null if impossible.
  private backward(from: Point, count: number, left: number, right: number): Cell[] | null {
    const out: Cell[] = [];
    let total = 0;
    for (let row = from.row; row >= 0 && row > from.row - WALK_ROWS && total < count; row--) {
      const row_: Cell[] = [];
      this.rowCells(row, left, right, row_);
      for (let i = row_.length - 1; i >= 0 && total < count; i--) {
        const c = row_[i];
        if (row === from.row && c.col >= from.col) { continue; }
        out.push(c);
        total += c.ch.length;
      }
    }
    return total === count ? out.reverse() : null;
  }

  // Count characters strictly between cells a and b; retain the text only up to GAP_TEXT_MAX.
  gapBetween(a: Point, b: Point, left: number, right: number): { text: string | null; len: number } {
    let len = 0;
    let text = '';
    for (let row = a.row; row <= b.row; row++) {
      const row_: Cell[] = [];
      this.rowCells(row, left, right, row_);
      for (const c of row_) {
        if (row === a.row && c.col <= a.col) { continue; }
        if (row === b.row && c.col >= b.col) { break; }
        len += c.ch.length;
        if (len <= GAP_TEXT_MAX) { text += c.ch; }
      }
    }
    return { text: len <= GAP_TEXT_MAX ? text : null, len };
  }

  // Read the inclusive span range.
  cellsOfLoc(loc: Loc, left = 0, right = this.term.cols, includeDecorations = false): Cell[] {
    const all = this.cells(loc.first.row, loc.last.row, left, right, includeDecorations);
    return all.filter((c) => (c.row > loc.first.row || c.col >= loc.first.col) && (c.row < loc.last.row || c.col <= loc.last.col));
  }

  private textOf(cells: Cell[]): string {
    return cells.map((c) => c.ch).join('');
  }

  // ---- tags -------------------------------------------------------------------------------------------------------------------

  private writeTag(row: number, col: number, id: number): boolean {
    const line = this.lineAt(row);
    if (line === undefined || (line._data[col * 3 + 2] & HAS_EXT) !== 0) { return false; }
    const extended = (this.core.buffer.getNullCell().extended.clone()) as { underlineColor: number };
    extended.underlineColor = CM_RGB | id;
    line._extendedAttrs[col] = extended as never;
    line._data[col * 3 + 2] |= HAS_EXT;
    // Verify through the public API to detect changes to xterm.js attribute storage.
    const read = this.term.buffer.active.getLine(row)?.getCell(col) as any;
    if (read === undefined || !read.isUnderlineColorRGB() || read.isUnderline() !== 0 || (read.getUnderlineColor() & ID_MASK) !== id) {
      delete line._extendedAttrs[col];
      line._data[col * 3 + 2] &= ~HAS_EXT;
      this.tagsOn = false;
      return false;
    }
    return true;
  }

  // Return the cell's tag ID, or 0. Program underline colors, OSC 8 links and plain cells yield 0 or an unowned ID;
  // validate every apparent match against the span's text.
  private tagAt(line: Line, col: number): number {
    if ((line._data[col * 3 + 2] & HAS_EXT) === 0) { return 0; }
    const e = line._extendedAttrs[col];
    if (e === undefined || e.underlineStyle !== 0 || e.urlId !== 0 || (e.underlineColor & CM_MASK) !== CM_RGB) { return 0; }
    return e.underlineColor & ID_MASK;
  }

  // Index active-buffer tags by ID. Sparse extended attributes avoid scanning every cell in each line.
  scan(): TagMap {
    const map: TagMap = new Map();
    if (!this.tagsOn) { return map; }
    const lines = this.core.buffer.lines;
    for (let row = 0; row < lines.length; row++) {
      const line = lines.get(row) as Line | undefined;
      if (line === undefined) { continue; }
      for (const key in line._extendedAttrs) {
        const col = Number(key);
        const id = this.tagAt(line, col);
        if (id !== 0) {
          const at = map.get(id);
          if (at === undefined) { map.set(id, [{ row, col }]); } else { at.push({ row, col }); }
        }
      }
    }
    return map;
  }

  // Look up only the requested tag IDs within an inclusive row range, such as the viewport or one logical line.
  private scanRows(ids: number[], from: number, to: number): TagMap {
    const map: TagMap = new Map();
    const lines = this.core.buffer.lines;
    for (let row = Math.max(0, from); row <= Math.min(to, lines.length - 1); row++) {
      const line = lines.get(row) as Line | undefined;
      if (line === undefined) { continue; }
      for (const key in line._extendedAttrs) {
        const id = this.tagAt(line, Number(key));
        if (id !== 0 && ids.includes(id)) {
          const at = map.get(id);
          if (at === undefined) { map.set(id, [{ row, col: Number(key) }]); } else { at.push({ row, col: Number(key) }); }
        }
      }
    }
    return map;
  }

  // Remove only tags that still belong to this span.
  private untag(span: Span, map?: TagMap): void {
    if (span.id === 0) { return; }
    const found = map ?? this.scan();
    for (const id of [span.id, span.id + 1]) {
      for (const p of found.get(id) ?? []) {
        const line = this.lineAt(p.row);
        if (line !== undefined && this.tagAt(line, p.col) === id) {
          delete line._extendedAttrs[p.col];
          line._data[p.col * 3 + 2] &= ~HAS_EXT;
        }
      }
    }
  }

  private retag(span: Span, cells: Cell[]): void {
    if (span.id === 0 || !this.tagsOn) { return; }
    // Reuse the first and last eligible cells from the proven range.
    const a = this.eligible(cells, false);
    const b = this.eligible(cells, true);
    if (a !== null) { this.writeTag(cells[a].row, cells[a].col, span.id); }
    if (b !== null && b !== a) { this.writeTag(cells[b].row, cells[b].col, span.id + 1); }
  }

  // Find the first or last cell without its own extended attributes.
  private eligible(cells: Cell[], last: boolean): number | null {
    for (let k = 0; k < cells.length; k++) {
      const i = last ? cells.length - 1 - k : k;
      const line = this.lineAt(cells[i].row);
      if (line !== undefined && (line._data[cells[i].col * 3 + 2] & HAS_EXT) === 0) { return i; }
    }
    return null;
  }

  // Tag selection cells end to end without replacing reader tags or program attributes.
  cover(cells: Cell[]): CellTags {
    const tags: CellTags = { ids: [], owned: new Set(), valid: this.tagsOn };
    for (const cell of cells) {
      const line = this.lineAt(cell.row);
      if (line === undefined) { tags.valid = false; continue; }
      let id = this.tagAt(line, cell.col);
      if (id === 0) {
        id = this.nextId;
        this.nextId = this.nextId + 2 > ID_MASK - 2 ? 2 : this.nextId + 2;
        if (!this.writeTag(cell.row, cell.col, id)) { tags.valid = false; }
        else { tags.owned.add(id); }
      }
      tags.ids.push(id);
    }
    return tags;
  }

  covered(tags: CellTags, cells: Cell[]): boolean {
    return tags.valid && this.tagsOn && cells.length === tags.ids.length && cells.every((cell, i) => {
      const line = this.lineAt(cell.row);
      return line !== undefined && this.tagAt(line, cell.col) === tags.ids[i];
    });
  }

  uncover(tags: CellTags): void {
    const map = this.scan();
    for (const id of tags.owned) {
      for (const point of map.get(id) ?? []) {
        const line = this.lineAt(point.row);
        if (line !== undefined && this.tagAt(line, point.col) === id) {
          delete line._extendedAttrs[point.col];
          line._data[point.col * 3 + 2] &= ~HAS_EXT;
        }
      }
    }
  }

  // ---- spans ------------------------------------------------------------------------------------------------------------------

  // Create a span over the inclusive cell range within panel bounds. Record the gap from the previous span for neighbor lookup.
  // Return null when the range contains no text.
  make(first: Point, last: Point, left = 0, right = this.term.cols, previous: Span | null = null): Span | null {
    const all = this.cells(first.row, last.row, left, right);
    const cells = all.filter((c) => (c.row > first.row || c.col >= first.col) && (c.row < last.row || c.col <= last.col));
    if (cells.length === 0) { return null; }
    const key = this.textOf(cells);
    const id = this.nextId;
    this.nextId = this.nextId + 2 > ID_MASK - 2 ? 2 : this.nextId + 2;
    const span: Span = {
      id: 0, key, tagFirst: null, tagLast: null, marker: null, gap: null, gapLen: 0, left, right, cols: this.term.cols,
      alt: this.alternate, loc: this.locOf(cells), status: 'found', virtual: null, was: null,
    };
    if (this.tagsOn) {
      const a = this.eligible(cells, false);
      const b = this.eligible(cells, true);
      let off = 0;
      const offsets = cells.map((c) => { const at = off; off += c.ch.length; return at; });
      if (a !== null && this.writeTag(cells[a].row, cells[a].col, id)) {
        span.id = id;
        span.tagFirst = { off: offsets[a], len: cells[a].ch.length };
        if (b !== null && b !== a && this.writeTag(cells[b].row, cells[b].col, id + 1)) {
          span.tagLast = { off: offsets[b], len: cells[b].ch.length };
        }
      }
    }
    span.marker = this.markerFor(cells[0].row);
    if (previous?.loc != null) {
      const gap = this.gapBetween(previous.loc.last, span.loc!.first, left, right);
      span.gap = gap.text;
      span.gapLen = gap.len;
    }
    return span;
  }

  private locOf(cells: Cell[]): Loc {
    const a = cells[0];
    const b = cells[cells.length - 1];
    return { first: { row: a.row, col: a.col }, last: { row: b.row, col: b.col }, lastWidth: b.width };
  }

  // Anchor the first row of the logical line containing this row.
  private markerFor(row: number): IMarker | null {
    const buffer = this.term.buffer.active;
    let top = row;
    while (top > 0 && buffer.getLine(top)?.isWrapped === true) { top--; }
    return this.term.registerMarker(top - (buffer.baseY + buffer.cursorY)) ?? null;
  }

  // Release the span's tags and marker.
  drop(span: Span, map?: TagMap): void {
    this.untag(span, map);
    span.marker?.dispose();
    span.marker = null;
    span.status = 'gone';
  }

  // Release all spans with one tag scan of the buffer.
  dropAll(spans: Span[]): void {
    if (spans.length === 0) { return; }
    const map = this.scan();
    for (const span of spans) { this.drop(span, map); }
  }

  // Restore a marker when needed; reading keeps markers only for a window of its spans.
  ensureMarker(span: Span): void {
    if (span.marker !== null && !span.marker.isDisposed) { return; }
    span.marker = span.loc !== null && span.status === 'found' ? this.markerFor(span.loc.first.row) : null;
  }

  dropMarker(span: Span): void {
    span.marker?.dispose();
    span.marker = null;
  }

  // ---- finding by tags ----------------------------------------------------------------------------------------------------------

  // Validate the span's text around a tagged cell, including the prefix before the tag and the remaining stream characters.
  // Return null if the complete key does not match.
  private around(span: Span, at: Point, tag: { off: number; len: number }): Loc | null {
    const [left, right] = this.panelOf(span);
    let first: Cell | null = null;
    if (tag.off > 0) {
      first = this.matchBackward(at, span.key, tag.off, left, right);
      if (first === null) { return null; }
    }
    const from = this.matchForward(at, span.key, tag.off, left, right);
    if (from === null) { return null; }
    const a = first ?? from.first;
    return { first: { row: a.row, col: a.col }, last: { row: from.last.row, col: from.last.col }, lastWidth: from.last.width };
  }

  // Match key[pos..] forward from this cell, stopping at the first mismatching stream cell.
  // Work scales with the span's text, not row width. Return the first and last cells only after the entire key matches.
  private matchForward(at: Point, key: string, pos: number, left: number, right: number): { first: Cell; last: Cell } | null {
    const buffer = this.term.buffer.active;
    let first: Cell | null = null;
    for (let row = at.row; row < buffer.length && row < at.row + WALK_ROWS; row++) {
      const line = this.core.buffer.lines.get(row) as { getTrimmedLength(): number; getWidth(i: number): number; hasContent(i: number): number; getString(i: number): string } | undefined;
      if (line === undefined) { continue; }
      const end = Math.min(right, this.term.cols, line.getTrimmedLength());
      for (let col = Math.max(0, left, row === at.row ? at.col : 0); col < end; col++) {
        const width = line.getWidth(col);
        if (width === 0 || line.hasContent(col) === 0) { continue; }
        const ch = line.getString(col);
        // A matching cell belongs to the stream; keys contain no blanks, box glyphs or placeholders.
        // Only inspect a mismatching cell further to distinguish ignored decoration from different text.
        if (ch === '' || !key.startsWith(ch, pos)) {
          if (ch === '' || ch.trim() === '' || ch.startsWith(PLACEHOLDER) || FRAME.test(ch)) { continue; }
          return null;
        }
        const cell = { ch, row, col, width };
        first ??= cell;
        pos += ch.length;
        if (pos === key.length) { return { first, last: cell }; }
      }
    }
    return null;
  }

  // Match key[0..pos) backward, excluding this cell; return the first matching cell or null.
  private matchBackward(at: Point, key: string, pos: number, left: number, right: number): Cell | null {
    let first: Cell | null = null;
    for (let row = at.row; row >= 0 && row > at.row - WALK_ROWS; row--) {
      const line = this.core.buffer.lines.get(row) as { getTrimmedLength(): number; getWidth(i: number): number; hasContent(i: number): number; getString(i: number): string } | undefined;
      if (line === undefined) { continue; }
      const end = Math.min(right, this.term.cols, line.getTrimmedLength());
      for (let col = (row === at.row ? Math.min(at.col, end) : end) - 1; col >= Math.max(0, left); col--) {
        const width = line.getWidth(col);
        if (width === 0 || line.hasContent(col) === 0) { continue; }
        const ch = line.getString(col);
        if (ch === '' || pos - ch.length < 0 || !key.startsWith(ch, pos - ch.length)) {
          if (ch === '' || ch.trim() === '' || ch.startsWith(PLACEHOLDER) || FRAME.test(ch)) { continue; }
          return null;
        }
        first = { ch, row, col, width };
        pos -= ch.length;
        if (pos === 0) { return first; }
      }
    }
    return null;
  }

  private sameLoc(a: Loc, b: Loc): boolean {
    return a.first.row === b.first.row && a.first.col === b.first.col && a.last.row === b.last.row && a.last.col === b.last.col;
  }

  // Locate the span by its tags. Copied tags yielding different valid locations are ambiguous and return null.
  private byTags(span: Span, map: TagMap): Loc | null {
    if (span.id === 0 || span.tagFirst === null) { return null; }
    const options: Loc[] = [];
    const add = (loc: Loc | null): void => { if (loc !== null && !options.some((o) => this.sameLoc(o, loc))) { options.push(loc); } };
    for (const p of map.get(span.id) ?? []) { add(this.around(span, p, span.tagFirst)); }
    if (span.tagLast !== null) {
      const atEnd = span.tagLast.off + span.tagLast.len === span.key.length;
      for (const p of map.get(span.id + 1) ?? []) {
        // The first tag already proves this location, including the final tagged cell; avoid a second text walk.
        if (atEnd && options.length === 1 && options[0].last.row === p.row && options[0].last.col === p.col) { continue; }
        add(this.around(span, p, span.tagLast));
      }
    }
    return options.length === 1 ? options[0] : null;
  }

  // Locate a visible mark using its marker's logical line or the viewport, without scanning the whole buffer each frame.
  quick(span: Span): Loc | null {
    if (span.id === 0 || span.tagFirst === null || span.status !== 'found' || span.alt !== this.alternate) { return null; }
    const buffer = this.term.buffer.active;
    const ids = span.tagLast === null ? [span.id] : [span.id, span.id + 1];
    let map: TagMap = new Map();
    if (span.marker !== null && !span.marker.isDisposed) {
      let end = span.marker.line;
      while (end + 1 < buffer.length && buffer.getLine(end + 1)?.isWrapped === true) { end++; }
      map = this.scanRows(ids, span.marker.line, end);
    }
    if (map.size === 0) { map = this.scanRows(ids, buffer.viewportY, buffer.viewportY + this.term.rows - 1); }
    return this.byTags(span, map);
  }

  // Return the first column and exclusive end of a row's text within panel bounds; return null for blank rows.
  // Exclude box drawing and image placeholders. Read raw cell words only up to trimmed length.
  extent(row: number, left: number, right: number): [number, number] | null {
    const line = this.lineAt(row) as (Line & { getTrimmedLength(): number }) | undefined;
    if (line === undefined) { return null; }
    const data = line._data;
    const end = Math.min(right, this.term.cols, line.getTrimmedLength());
    const text = (col: number): boolean => {
      const word = data[col * 3];
      const cp = word & 0x1fffff;
      // Match rowCells: exclude trimmed whitespace, box/block glyphs, image placeholders and zero-width cells.
      return cp > 32 && !/^\s$/u.test(String.fromCodePoint(cp)) && (cp < 0x2500 || cp > 0x259f) && cp !== 0x10eeee && (word >> 22) !== 0;
    };
    let first = Math.max(0, left);
    while (first < end && !text(first)) { first++; }
    if (first >= end) { return null; }
    let last = end - 1;
    while (last > first && !text(last)) { last--; }
    return [first, last + Math.max(1, data[last * 3] >> 22)];
  }

  // ---- the band -------------------------------------------------------------------------------------------------------------------

  // Read one native string per row, excluding whitespace, box/block glyphs and image placeholders as the stream does.
  private rowKey(row: number): string {
    const text = this.term.buffer.active.getLine(row)?.translateToString(true, 0, this.term.cols) ?? '';
    return text.replace(/[\s\u2500-\u259f]+/g, '').replace(PLACEHOLDER_RUN, '');
  }

  // Capture rows around proven spans for the next movement comparison.
  snapshot(chain: Span[], focus: Span | null): Band | null {
    const found = chain.filter((s) => s.status === 'found' && s.loc !== null);
    const buffer = this.term.buffer.active;
    const alt = this.alternate;
    const low = alt ? 0 : Math.max(0, buffer.viewportY - this.term.rows);
    const high = alt ? this.term.rows - 1 : buffer.length - 1;
    if (found.length === 0 && !(alt && chain.some((s) => s.virtual !== null))) { return null; }
    let top = low;
    let bottom = Math.min(high, low + BAND_ROWS - 1);
    if (found.length > 0) {
      const mid = (focus !== null && focus.status === 'found' ? focus : found[0]).loc!;
      top = Math.max(low, Math.min(...found.map((s) => s.loc!.first.row)) - BAND_MARGIN);
      bottom = Math.min(high, Math.max(...found.map((s) => s.loc!.last.row)) + BAND_MARGIN);
      if (bottom - top + 1 > BAND_ROWS) {
        top = Math.max(low, Math.min(high - BAND_ROWS + 1, mid.first.row - BAND_ROWS / 2));
        bottom = Math.min(high, top + BAND_ROWS - 1);
      }
    }
    const rows: string[] = [];
    const starts: number[] = [];
    let stream = '';
    for (let row = top; row <= bottom; row++) {
      starts.push(stream.length);
      const key = this.rowKey(row);
      rows.push(key);
      stream += key;
    }
    const offsets = new Map<Span, number>();
    const locs = new Map<Span, Loc>();
    for (const s of found) {
      const loc = s.loc!;
      if (loc.first.row < top || loc.last.row > bottom) { continue; }
      // Compute the span's stream offset from its row start and preceding characters on that row.
      const before: Cell[] = [];
      this.rowCells(loc.first.row, 0, loc.first.col, before);
      offsets.set(s, starts[loc.first.row - top] + this.textOf(before).length);
      locs.set(s, loc);
    }
    return { cols: this.term.cols, alt, top, rows, stream, starts, offsets, locs };
  }

  // Find exactly one row shift that explains the old band at the current width. All overlapping rows must agree, supported by
  // at least MIN_OTHER_ROWS distinct non-blank rows or MIN_OTHER_CHARS characters. If none matches, try stationary rewrites by
  // excluding suspect rows at shift 0. `agree` records supporting non-blank rows outside each span's own rows.
  private moveSame(band: Band, suspects: Span[]): { shift: number; agree: number[]; strict: boolean } | 'ambiguous' | null {
    const buffer = this.term.buffer.active;
    const alt = this.alternate;
    const n = band.rows.length;
    const low = alt ? 0 : Math.max(0, band.top - this.term.rows);
    const high = alt ? this.term.rows - 1 : Math.min(buffer.length - 1, band.top + n - 1 + this.term.rows);
    const skip = new Set<number>();
    for (const s of suspects) {
      const loc = band.locs.get(s);
      if (loc !== undefined) { for (let r = loc.first.row; r <= loc.last.row; r++) { skip.add(r - band.top); } }
    }
    const picture = new Map<number, string>();
    const pic = (row: number): string => {
      let key = picture.get(row);
      if (key === undefined) { key = this.rowKey(row); picture.set(row, key); }
      return key;
    };
    // Record the best partial shift and first disagreement for test diagnostics.
    let best = { k: 0, agreed: -1, oldRow: -1, oldText: '', newText: '', overlap: 0 };
    const scan = (left: Set<number>, only: number | null): { k: number; agree: number[] }[] => {
      const valid: { k: number; agree: number[] }[] = [];
      for (let k = only ?? low - (band.top + n - 1); k <= (only ?? high - band.top); k++) {
        let chars = 0;
        const distinct = new Set<string>();
        const agree: number[] = [];
        let ok = true;
        let overlap = 0;
        let agreed = 0;
        for (let i = 0; i < n; i++) {
          const row = band.top + i + k;
          if (row < low || row > high) { continue; }
          overlap++;
          if (left.has(i)) { continue; }
          if (band.rows[i] !== pic(row)) {
            ok = false;
            if (only === null && agreed > best.agreed) { best = { k, agreed, oldRow: band.top + i, oldText: band.rows[i].slice(0, 40), newText: pic(row).slice(0, 40), overlap }; }
            break;
          }
          agreed++;
          if (band.rows[i] !== '') { distinct.add(band.rows[i]); chars += band.rows[i].length; agree.push(i); }
        }
        if (ok && overlap > 0 && (distinct.size >= MIN_OTHER_ROWS || chars >= MIN_OTHER_CHARS)) { valid.push({ k, agree }); }
      }
      return valid;
    };
    let valid = scan(new Set(), null);
    let strict = true;
    if (valid.length === 0 && skip.size > 0) { valid = scan(skip, 0); strict = false; }
    this.lastMove = { valid: valid.map((v) => v.k), strict, best, band: [band.top, band.top + n - 1], suspects: suspects.length };
    return valid.length === 1 ? { shift: valid[0].k, agree: valid[0].agree, strict } : valid.length === 0 ? null : 'ambiguous';
  }

  // If a whole-band shift fails, compare contiguous row blocks at the same width. Each block ends at a mismatch or edge.
  // A span needs exactly one sufficiently supported block covering its old rows, and no competing shift covering its destination.
  // This proves both where the old rows went and where the new rows came from.
  private moveBlocks(band: Band, open: Span[]): Map<Span, number> {
    const out = new Map<Span, number>();
    this.cuts = [];
    const buffer = this.term.buffer.active;
    const alt = this.alternate;
    const n = band.rows.length;
    const low = alt ? 0 : Math.max(0, band.top - this.term.rows);
    const high = alt ? this.term.rows - 1 : Math.min(buffer.length - 1, band.top + n - 1 + this.term.rows);
    const pics: string[] = [];
    const index = new Map<string, number[]>();
    for (let row = low; row <= high; row++) {
      const key = this.rowKey(row);
      pics.push(key);
      if (key !== '') { const at = index.get(key); if (at === undefined) { index.set(key, [row]); } else { at.push(row); } }
    }
    const shifts = new Set<number>();
    for (let i = 0; i < n; i++) {
      if (band.rows[i] !== '') { for (const row of index.get(band.rows[i]) ?? []) { shifts.add(row - band.top - i); } }
    }
    const blocks: { k: number; from: number; to: number }[] = [];
    for (const k of shifts) {
      let from = -1;
      for (let i = 0; i <= n; i++) {
        const row = band.top + i + k;
        const agree = i < n && row >= low && row <= high && band.rows[i] === pics[row - low];
        if (agree) { if (from < 0) { from = i; } } else if (from >= 0) { blocks.push({ k, from, to: i - 1 }); from = -1; }
      }
    }
    const enough = (b: { from: number; to: number }, skipFrom: number, skipTo: number): boolean => {
      const seen = new Set<string>();
      let chars = 0;
      for (let i = b.from; i <= b.to; i++) {
        if (i >= skipFrom && i <= skipTo) { continue; }
        const key = band.rows[i];
        if (key !== '' && !seen.has(key)) { seen.add(key); chars += key.length; }
      }
      return seen.size >= MIN_OTHER_ROWS || chars >= MIN_OTHER_CHARS;
    };
    for (const span of open) {
      const loc = band.locs.get(span);
      if (loc === undefined) { continue; }
      const a = loc.first.row - band.top;
      const z = loc.last.row - band.top;
      const mine = blocks.filter((b) => b.from <= a && b.to >= z && enough(b, a, z));
      if (mine.length === 0) {
        // The span starts in an upward-moving block but extends past it into newly arriving rows (k < 0).
        const part = blocks.filter((b) => b.k < 0 && b.from <= a && b.to >= a && b.to < z && enough(b, a, b.to));
        if (part.length === 1 && !blocks.some((b) => b.k !== part[0].k && b.from + b.k <= a + part[0].k && b.to + b.k >= part[0].to + part[0].k && enough(b, a + part[0].k - b.k, part[0].to + part[0].k - b.k))
          && !blocks.some((b) => b !== part[0] && b.from <= a && b.to >= a && enough(b, a, Math.min(z, b.to)))) {
          this.cuts.push({ span, k: part[0].k, rows: part[0].to - a + 1 });
        }
        continue;
      }
      if (mine.length !== 1) { continue; }
      const k = mine[0].k;
      const rival = blocks.some((b) => b.k !== k && b.from + b.k <= a + k && b.to + b.k >= z + k && enough(b, a + k - b.k, z + k - b.k));
      if (!rival) { out.set(span, k); }
    }
    this.lastMove = { ...(this.lastMove as object), blocks: blocks.filter((b) => enough(b, -1, -1)).map((b) => [b.k, b.from, b.to]), proven: out.size };
    return out;
  }

  // After a width change or full repaint, align the band's text against the screen or whole buffer as one stream.
  // Require exactly one location whose surrounding text agrees both ways to a band or picture edge.
  // Return matching cells, null for no match, or 'ambiguous' for competing matches.
  private alignStream(band: Band, span: Span, whole: boolean, picture: { text: string; starts: number[]; from: number }): Cell[] | null | 'ambiguous' {
    const offset = band.offsets.get(span);
    if (offset === undefined) { return null; }
    const { text, starts, from } = picture;
    const B = band.stream;
    const found: number[] = [];
    for (let at = text.indexOf(span.key); at >= 0; at = text.indexOf(span.key, at + 1)) {
      const d = at - offset;
      let jl = offset;
      while (jl > 0 && d + jl - 1 >= 0 && B[jl - 1] === text[d + jl - 1]) { jl--; }
      let jr = offset + span.key.length;
      while (jr < B.length && d + jr < text.length && B[jr] === text[d + jr]) { jr++; }
      const reachesLeft = jl === 0 || d + jl === 0;
      const reachesRight = jr === B.length || d + jr === text.length;
      if (!reachesLeft || !reachesRight) { continue; }
      const besides = jr - jl - span.key.length;
      // Count supporting band rows outside the span itself.
      const own = band.locs.get(span);
      let rows = 0;
      const seen = new Set<string>();
      band.rows.forEach((key, i) => {
        const row = band.top + i;
        if (key !== '' && band.starts[i] >= jl && band.starts[i] + key.length <= jr && !(own !== undefined && row >= own.first.row && row <= own.last.row)
          && !seen.has(key)) { seen.add(key); rows++; }
      });
      if (besides >= MIN_OTHER_CHARS || rows >= MIN_OTHER_ROWS) { found.push(at); }
    }
    if (found.length > 1) { return 'ambiguous'; }
    if (found.length === 0) {
      // If no alignment reaches the edges, accept a locally matching stretch with enough surrounding evidence only when
      // it occurs once in both the current picture and the old band.
      const local: { at: number; jl: number; jr: number }[] = [];
      for (let at = text.indexOf(span.key); at >= 0; at = text.indexOf(span.key, at + 1)) {
        const d = at - offset;
        let jl = offset;
        while (jl > 0 && d + jl - 1 >= 0 && B[jl - 1] === text[d + jl - 1]) { jl--; }
        let jr = offset + span.key.length;
        while (jr < B.length && d + jr < text.length && B[jr] === text[d + jr]) { jr++; }
        const own = band.locs.get(span);
        let rows = 0;
        const seen = new Set<string>();
        band.rows.forEach((key, i) => {
          const row = band.top + i;
          if (key !== '' && band.starts[i] >= jl && band.starts[i] + key.length <= jr && !(own !== undefined && row >= own.first.row && row <= own.last.row)
            && !seen.has(key)) { seen.add(key); rows++; }
        });
        if (jr - jl - span.key.length >= MIN_OTHER_CHARS || rows >= MIN_OTHER_ROWS) { local.push({ at, jl, jr }); }
      }
      if (local.length === 0) { return null; }
      if (local.length > 1) { return 'ambiguous'; }
      const stretch = B.slice(local[0].jl, local[0].jr);
      if (B.indexOf(stretch) !== local[0].jl || B.indexOf(stretch, local[0].jl + 1) >= 0) { return 'ambiguous'; }
      found.push(local[0].at);
      this.stats.byLocal++;
    }
    // Map the matched text range back to its cells.
    const at = found[0];
    let first = 0;
    while (first + 1 < starts.length && starts[first + 1] <= at) { first++; }
    let last = first;
    while (last + 1 < starts.length && starts[last + 1] < at + span.key.length) { last++; }
    const cells = this.cells(from + first, from + last);
    let skipped = 0;
    let i = 0;
    while (i < cells.length && skipped < at - starts[first]) { skipped += cells[i].ch.length; i++; }
    let taken = 0;
    let j = i;
    while (j < cells.length && taken < span.key.length) { taken += cells[j].ch.length; j++; }
    const own = cells.slice(i, j);
    return skipped === at - starts[first] && this.textOf(own) === span.key ? own : null;
  }

  // Build the alignment target from concatenated whitespace-free rows and their starting offsets.
  private picture(whole: boolean): { text: string; starts: number[]; from: number } {
    const buffer = this.term.buffer.active;
    const from = this.alternate ? 0 : whole ? 0 : Math.max(0, buffer.viewportY - this.term.rows);
    const to = this.alternate ? this.term.rows - 1 : buffer.length - 1;
    const starts: number[] = [];
    let text = '';
    for (let row = from; row <= to; row++) {
      starts.push(text.length);
      text += this.rowKey(row);
    }
    return { text, starts, from };
  }

  // ---- resolving ----------------------------------------------------------------------------------------------------------------

  private setFound(span: Span, loc: Loc): void {
    span.loc = loc;
    span.was = loc;
    span.status = 'found';
    span.virtual = null;
    if (span.cols !== this.term.cols) {
      span.cols = this.term.cols;
      span.left = 0;
      span.right = this.term.cols;
    }
  }

  // Locate a span next to a neighbor proven in this pass, requiring the same key and intervening characters as before.
  // Check forward from the previous span or backward from the next span.
  private byNeighbour(span: Span, before: Span | undefined, after: Span | undefined): Cell[] | null {
    const [left, right] = this.panelOf(span);
    if (before?.status === 'found' && before.loc !== null) {
      const gap = span.gapLen;
      const next = this.forward(before.loc.last, false, gap + span.key.length, left, right);
      if (next !== null) {
        const gapCells = this.cutChars(next, gap);
        if (gapCells !== null) {
          const own = next.slice(gapCells);
          if (this.textOf(own) === span.key && (span.gap === null || this.textOf(next.slice(0, gapCells)) === span.gap)) { return own; }
        }
      }
    }
    if (after?.status === 'found' && after.loc !== null) {
      const gap = after.gapLen;
      const prev = this.backward(after.loc.first, gap + span.key.length, left, right);
      if (prev !== null) {
        const keyCells = this.cutChars(prev, span.key.length);
        if (keyCells !== null) {
          const own = prev.slice(0, keyCells);
          if (this.textOf(own) === span.key && (after.gap === null || this.textOf(prev.slice(keyCells)) === after.gap)) { return own; }
        }
      }
    }
    return null;
  }

  // Return the number of cells holding exactly count characters; return null if a cell crosses the boundary.
  private cutChars(cells: Cell[], count: number): number | null {
    let total = 0;
    for (let i = 0; i < cells.length; i++) {
      if (total === count) { return i; }
      total += cells[i].ch.length;
    }
    return total === count ? cells.length : null;
  }

  // Resolve the chain in stream order using tags, then neighbors proven in this pass, then measured band movement.
  // Retain proven continuity for off-screen alternate-buffer spans. Update each status and location, retag spans found without
  // tags, and refresh holder.band after the pass.
  resolve(chain: Span[], holder: Holder, focus: Span | null, whole = false): Resolved {
    const result: Resolved = { found: 0, movement: 'none', shift: 0, changed: [] };
    if (this.held()) { return result; }
    this.stats.passes++;
    const map = this.scan();
    const nowAlt = this.alternate;
    const suspects = new Set<Span>();
    for (const span of chain) {
      if (span.status === 'gone') { continue; }
      if (span.alt !== nowAlt) {
        // Leaving the alternate screen clears its spans permanently; normal-buffer spans are only hidden during the switch.
        span.status = span.alt ? 'gone' : 'hidden';
        span.virtual = null;
        continue;
      }
      const loc = this.byTags(span, map);
      if (loc !== null) {
        this.setFound(span, loc);
        this.stats.byTag++;
        continue;
      }
      if (!nowAlt && span.marker !== null && span.marker.isDisposed && span.id !== 0) {
        span.status = 'gone';
        continue;
      }
      if (span.status === 'found') { span.was = span.loc; }
      span.status = 'unproven';
      suspects.add(span);
    }
    const rewritten: [Span, Cell[]][] = [];
    const neighbours = (): void => {
      let progress = true;
      while (progress) {
        progress = false;
        for (let i = 0; i < chain.length; i++) {
          const span = chain[i];
          if (span.status !== 'unproven' || !suspects.has(span)) { continue; }
          const cells = this.byNeighbour(span, chain[i - 1], chain[i + 1]);
          if (cells !== null) {
            this.stats.byNeighbour++;
            this.setFound(span, this.locOf(cells));
            rewritten.push([span, cells]);
            progress = true;
          }
        }
      }
    };
    if (suspects.size > 0) { this.stats.suspectPasses++; neighbours(); }
    const open = [...suspects].filter((s) => s.status === 'unproven');
    const band = holder.band;
    if (open.length > 0 && band !== null && band.alt === nowAlt) {
      if (!whole && band.cols === this.term.cols) {
        const moved = this.moveSame(band, open);
        if (moved === 'ambiguous') {
          result.movement = 'ambiguous';
        } else if (moved === null) {
          const proven = this.moveBlocks(band, open);
          const rows = nowAlt ? this.term.rows : this.term.buffer.active.length;
          const place = (old: Loc, k: number): Loc => ({ first: { row: old.first.row + k, col: old.first.col }, last: { row: old.last.row + k, col: old.last.col }, lastWidth: old.lastWidth });
          const used = new Set<number>();
          for (const [span, k] of proven) {
            const shifted = place(band.locs.get(span)!, k);
            const [left, right] = this.panelOf(span);
            const cells = this.cellsOfLoc(shifted, left, right);
            if (this.textOf(cells) === span.key) {
              this.stats.byBlock++;
              this.setFound(span, shifted);
              rewritten.push([span, cells]);
              used.add(k);
            }
          }
          for (const cut of this.cuts) {
            const { span, k, rows: keep } = cut;
            if (span.status !== 'unproven') { continue; }
            const old = band.locs.get(span)!;
            const [left, right] = this.panelOf(span);
            const all = this.cells(old.first.row + k, old.first.row + k + keep - 1, left, right).filter((c) => c.row > old.first.row + k || c.col >= old.first.col);
            const text = this.textOf(all);
            if (all.length > 0 && text.length < span.key.length && span.key.startsWith(text)) {
              const index = chain.indexOf(span);
              if (this.rekey(span, { row: all[0].row, col: all[0].col }, { row: all[all.length - 1].row, col: all[all.length - 1].col }, index > 0 ? chain[index - 1] : null)) {
                this.stats.byCut++;
                span.was = span.loc;
                used.add(k);
              }
            }
          }
          if (used.size === 1) {
            // One movement proved the chain's rows: what it did not hold keeps its place by continuity (off the block or off the screen).
            const k = [...used][0];
            result.movement = 'same';
            result.shift = k;
            for (const span of open) {
              if (span.status !== 'unproven') { continue; }
              const old = band.locs.get(span) ?? span.virtual;
              span.virtual = nowAlt && old !== null && old !== undefined ? place(old, k) : null;
            }
          }
        } else {
          result.movement = 'same';
          result.shift = moved.shift;
          for (const span of open) {
            const old = band.locs.get(span) ?? span.virtual;
            if (old === null || old === undefined) { continue; }
            // Evidence besides the span's own rows: other rows that agreed, at least MIN_OTHER_ROWS different (or MIN_OTHER_CHARS characters).
            const ownRows = new Set<number>();
            for (let r = old.first.row; r <= old.last.row; r++) { ownRows.add(r - band.top); }
            const others = new Set<string>();
            let chars = 0;
            for (const i of moved.agree) { if (!ownRows.has(i) && !others.has(band.rows[i])) { others.add(band.rows[i]); chars += band.rows[i].length; } }
            if (others.size < MIN_OTHER_ROWS && chars < MIN_OTHER_CHARS && band.locs.has(span)) { span.virtual = null; continue; }
            const shifted: Loc = { first: { row: old.first.row + moved.shift, col: old.first.col }, last: { row: old.last.row + moved.shift, col: old.last.col },
              lastWidth: old.lastWidth };
            const [left, right] = this.panelOf(span);
            const rows = nowAlt ? this.term.rows : this.term.buffer.active.length;
            if (shifted.first.row < 0 || shifted.last.row >= rows) {
              // Preserve the off-screen alternate-buffer location by continuity until a movement cannot be proven.
              span.virtual = nowAlt ? shifted : null;
              continue;
            }
            const cells = this.cellsOfLoc(shifted, left, right);
            if (this.textOf(cells) === span.key) {
              this.stats.byMovement++;
              this.setFound(span, shifted);
              rewritten.push([span, cells]);
            } else if (!band.locs.has(span)) {
              // The span has not reappeared under this stationary row; retain its proven virtual location.
              span.virtual = nowAlt ? shifted : null;
            } else if (moved.shift === 0 && cells.length > 0) {
              span.virtual = null;
              result.changed.push(span);
            } else {
              span.virtual = null;
            }
          }
        }
      } else {
        const picture = this.picture(whole);
        let any = false;
        for (const span of open) {
          const aligned = this.alignStream(band, span, whole, picture);
          if (aligned === 'ambiguous') { result.movement = 'ambiguous'; } else if (aligned !== null) {
            any = true;
            this.stats.byStream++;
            this.setFound(span, this.locOf(aligned));
            rewritten.push([span, aligned]);
          }
        }
        if (any) { result.movement = 'stream'; }
      }
      if (rewritten.length > 0) { neighbours(); }
    }
    // Unproven movement or a width change breaks virtual-location continuity.
    if (result.movement !== 'same') {
      for (const span of open) { if (span.status === 'unproven') { span.virtual = null; } }
    }
    // Recover a span brought back on-screen only when its text matches at its continuously tracked location.
    for (const span of chain) {
      if (span.status === 'unproven' && span.virtual !== null && nowAlt && span.virtual.first.row >= 0 && span.virtual.last.row < this.term.rows) {
        const [left, right] = this.panelOf(span);
        const cells = this.cellsOfLoc(span.virtual, left, right);
        if (this.textOf(cells) === span.key) {
          this.setFound(span, span.virtual);
          rewritten.push([span, cells]);
        }
      }
    }
    // Restore tags erased by a rewrite only after another method proves the span's location.
    for (const [span, cells] of rewritten) {
      if (span.status === 'found') { this.retag(span, cells); }
    }
    for (const span of chain) {
      if (span.status === 'found') { result.found++; }
    }
    for (const span of suspects) { if (span.status === 'unproven') { this.stats.unproven++; } }
    if (result.movement === 'ambiguous') { this.stats.ambiguous++; }
    if (suspects.size > 0 && band === null) { this.stats.noBand++; }
    // Keep the last proven snapshot when this pass proves nothing, for comparison in a later pass.
    holder.band = this.snapshot(chain, focus) ?? holder.band;
    return result;
  }

  // Retarget a rewritten span in place, preserving its object identity for existing references.
  rekey(span: Span, first: Point, last: Point, previous: Span | null): boolean {
    const next = this.make(first, last, span.left, span.right, previous);
    if (next === null) { return false; }
    this.untag(span);
    span.marker?.dispose();
    Object.assign(span, next);
    return true;
  }

  // Recompute the gap from the previous span after inserting another span in the chain.
  regap(span: Span, previous: Span): void {
    if (span.loc === null || previous.loc === null) { return; }
    const gap = this.gapBetween(previous.loc.last, span.loc.first, span.left, span.right);
    span.gap = gap.text;
    span.gapLen = gap.len;
  }

  // A chain can still recover if any span is found, hidden in the other buffer, or tracked by proven continuity.
  hope(chain: Span[]): boolean {
    return chain.some((s) => s.status === 'found' || s.status === 'hidden' || (s.status === 'unproven' && s.virtual !== null));
  }

  // After a reset, keep the chain's text but discard markers and virtual locations; tags and frame continuity are gone.
  forgetPlaces(chain: Span[]): void {
    for (const span of chain) {
      span.marker?.dispose();
      span.marker = null;
      span.virtual = null;
      if (span.status !== 'gone') { span.status = 'unproven'; }
    }
  }
}
