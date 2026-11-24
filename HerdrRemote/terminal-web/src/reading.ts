// Read terminal text through a chain of sentence spans (anchor.ts), without a separate captured document.
// Accept new sentences only after a span proven in this pass, excluding spans already in the chain and text before the start.
// This preserves stream order as the program writes without reading the same sentence twice.
//
// Send readstart, readadd and readend to C# for speech; readstate identifies the sentence being spoken.
// Highlight only that sentence's tagged cells. Reading never moves the viewport.
import type { Terminal } from '@xterm/xterm';
import { Anchors, type Holder, type Point, type Span } from './anchor';
import {
  bandAt, blockOf, bottomOf, isBlank, screenRows, splitsOf, splitsAt, stretchOf, type Area, type Block, type Char,
} from './rows';

export type ReadState = 'waiting' | 'playing' | 'paused' | 'listening';

// Accept stable row text after SETTLE_MS, or complete text once output pauses for QUIET_MS.
// A trailing fragment without a sentence ending requires OPEN_MS.
const SETTLE_MS = 1200;
const QUIET_MS = 300;
const OPEN_MS = 1500;
const MAX_UNSPOKEN = 400;
const KEEP_SPOKEN = 16;
const MARKERS_AHEAD = 8;
// Lose the location after LOST_MS without a provable span while output is quiet.
// After reset and initial paint, allow ATTACH_MS from attachment instead.
const LOST_MS = 2500;
const ATTACH_MS = 4000;
const CHUNK_ROWS = 160;
// Exclude box/block glyphs, geometric shapes, braille, arrows, technical symbols, dingbats, private-use characters
// (including planes 15-16 and image placeholders), and pictographs from speech.
const SYMBOLS = /[←-⇿⌀-⏿─-◿✀-➿⠀-⣿-\u{f0000}-\u{10ffff}]|\p{Extended_Pictographic}/gu;
const LETTER = /[\p{L}\p{N}]/u;
const ENDS = /[.!?…]["”')\]]*$/;

// A candidate sentence not yet accepted into the reading chain.
type Candidate = { text: string; first: Point; last: Point; left: number; right: number; terminal: boolean; continues: boolean; key: string };
type Entry = { seq: number; text: string; continues: boolean };

export interface ReadingDeps {
  term: Terminal;
  anchors: Anchors;
  // The caller adds the current reading lease before posting to C#.
  post(message: Record<string, unknown>): void;
  // Timestamp of the last terminal write, measured by performance.now().
  lastWriteAt(): number;
  // Milliseconds until synchronized output or an alternate-screen resize is considered complete; 0 means ready.
  // Do not inspect a partially painted frame.
  syncLeft(): number;
  // Exclude the cursor row from reading while the program displays its input cursor.
  cursorVisible(): boolean;
  // Request an overlay frame unless one is already drawing.
  redraw(): void;
}

export class Reader {
  private chain: Span[] = [];
  private readonly entries = new Map<Span, Entry>();
  private readonly bySeq = new Map<number, Span>();
  private readonly holder: Holder = { band: null };
  private nextSeq = 0;
  private current = 0;
  private state: ReadState = 'waiting';
  // C# assigns the reading ID in response to readstart; queue messages while the ID is 0.
  id = 0;
  private outbox: Record<string, unknown>[] = [];
  private started = false;
  // Keep the chain while its pane is hidden or the app is backgrounded; neither draw nor accept more sentences.
  private detached = false;
  private lost = false;
  private ended = false;
  // After reset and initial paint, recover spans only by full-band text alignment, within the reattachment deadline.
  private whole = false;
  private reattachBy = 0;
  private noHopeSince = 0;
  // Inspect the terminal in the next frame after output, resize or a speech response.
  private dirty = false;
  // One event-armed timeout for the next time-dependent transition; see arm.
  private timer = 0;
  private timerAt = 0;
  private seen = new Map<string, number>();
  // Cache pending highlight areas in buffer coordinates and the span-location signature that validates them.
  private shape: Area[] = [];
  private sig = new Int32Array(0);
  private sigNext = new Int32Array(0);
  private sigLen = -1;
  // Diagnostics for the test harness.
  stats = { passes: 0, unproven: 0, aligned: 0, ambiguous: 0, taken: 0, replaced: 0, lost: 0 };

  constructor(private readonly deps: ReadingDeps) {}

  get active(): boolean {
    return this.started && !this.lost;
  }

  get spans(): readonly Span[] {
    return this.chain;
  }

  // ---- starting and ending ------------------------------------------------------------------------------------------------------

  // Start at the selection head's first cell and accept the first sentence immediately.
  readFrom(head: Span): boolean {
    this.clear();
    const holder: Holder = { band: null };
    this.deps.anchors.resolve([head], holder, head);
    if (head.status !== 'found' || head.loc === null) { return false; }
    const start = head.loc.first;
    const found = this.candidates({ row: start.row, col: start.col - 1 }, null, 1, true);
    if (found.list.length === 0) { return false; }
    this.beginWith(found.list.slice(0, 1));
    return true;
  }

  // Keep the fixed selection bounded; look ahead one piece to detect truncation.
  readSelection(head: Span, tail: Span): boolean {
    this.clear();
    const holder: Holder = { band: null };
    this.deps.anchors.resolve([head, tail], holder, head);
    if (head.status !== 'found' || head.loc === null || tail.status !== 'found' || tail.loc === null) { return false; }
    const start = head.loc.first;
    const end = tail.loc.last;
    const found = this.candidates({ row: start.row, col: start.col - 1 }, { row: end.row, col: end.col + tail.loc.lastWidth }, MAX_UNSPOKEN + 1, true);
    if (found.list.length === 0) { return false; }
    const truncated = found.list.length > MAX_UNSPOKEN;
    if (truncated) { found.list.pop(); }
    this.beginWith(found.list);
    this.ended = true;
    this.send(truncated ? { type: 'readend', truncated: true } : { type: 'readend' });
    return true;
  }

  private beginWith(list: Candidate[]): void {
    this.started = true;
    this.lost = false;
    this.ended = false;
    this.detached = false;
    this.whole = false;
    this.nextSeq = 0;
    this.current = 0;
    this.state = 'waiting';
    const texts: string[] = [];
    let previous: Span | null = null;
    for (const c of list) {
      const span = this.take(c, previous, null);
      if (span !== null) { texts.push(c.text); previous = span; }
    }
    this.deps.post({ type: 'readstart', texts });
    this.outbox = [];
    // Snapshot the first sentence's surroundings now, so a repaint before the first pass can be measured.
    this.deps.anchors.resolve(this.chain, this.holder, this.chain[0] ?? null);
    this.dirty = true;
    this.deps.redraw();
  }

  // Attach C#'s reading ID; native-only reads may have no page-owned chain.
  attach(id: number): void {
    this.id = id;
    if (!this.started) { return; }
    if (this.detached) {
      this.detached = false;
      this.reattachBy = performance.now() + ATTACH_MS;
      this.noHopeSince = 0;
      this.dirty = true;
    }
    const held = this.outbox;
    this.outbox = [];
    for (const message of held) { this.deps.post({ ...message, id }); }
    this.deps.redraw();
  }

  // Detach when the pane is hidden or the app is backgrounded, preserving the chain.
  detach(): void {
    if (!this.started) { return; }
    this.detached = true;
    this.id = 0;
    this.disarm();
    this.deps.redraw();
  }

  // After reset, tags, markers and frame continuity are gone. Recover the chain only by aligning its band with the new frame.
  reset(): void {
    if (!this.started) { return; }
    this.deps.anchors.forgetPlaces(this.chain);
    this.whole = true;
    this.reattachBy = performance.now() + ATTACH_MS;
    this.noHopeSince = 0;
    this.seen.clear();
    this.dirty = true;
    this.deps.redraw();
  }

  // Release spans, tags and markers when reading stops, ends or is replaced.
  clear(): void {
    this.disarm();
    this.dirty = false;
    this.deps.anchors.dropAll(this.chain);
    this.chain = [];
    this.entries.clear();
    this.bySeq.clear();
    this.holder.band = null;
    this.started = this.detached = this.lost = this.ended = this.whole = false;
    this.id = 0;
    this.nextSeq = this.current = 0;
    this.state = 'waiting';
    this.outbox = [];
    this.seen.clear();
    this.sigLen = -1;
    this.deps.redraw();
  }

  private send(message: Record<string, unknown>): void {
    if (this.id === 0) { this.outbox.push(message); } else { this.deps.post({ ...message, id: this.id }); }
  }

  setState(seq: number, state: ReadState): void {
    if (!this.started) { return; }
    this.current = seq;
    this.state = state;
    this.refreshMarkers();
    this.trimSpoken();
    this.dirty = true;
    this.deps.redraw();
  }

  private lose(): void {
    this.stats.lost++;
    this.lost = true;
    this.disarm();
    this.send({ type: 'readlost' });
    this.deps.anchors.dropAll(this.chain);
    this.chain = [];
    this.entries.clear();
    this.bySeq.clear();
    this.deps.redraw();
  }

  // ---- the mark ---------------------------------------------------------------------------------------------------------------

  // Return visible row areas for the current sentence using its two cell tags and span length, without searching for text.
  // Return null when detached, listening, or unable to prove the sentence's current location.
  mark(): { areas: Area[]; state: ReadState } | null {
    if (!this.started || this.detached || this.lost || this.state === 'listening') { return null; }
    const span = this.bySeq.get(this.current);
    if (span === undefined) { return null; }
    const loc = this.deps.anchors.quick(span);
    if (loc === null) { return null; }
    const [left, right] = this.deps.anchors.panelOf(span);
    const top = this.deps.term.buffer.active.viewportY;
    const byRow = new Map<number, Area>();
    for (const cell of this.deps.anchors.cellsOfLoc(loc, left, right)) {
      const row = cell.row - top;
      if (row < 0 || row >= this.deps.term.rows) { continue; }
      const area = byRow.get(row);
      byRow.set(row, { top: row, bottom: row, left: Math.min(area?.left ?? cell.col, cell.col), right: Math.max(area?.right ?? 0, cell.col + cell.width) });
    }
    return byRow.size === 0 ? null : { areas: [...byRow.values()], state: this.state };
  }

  // Lightly highlight proven sentences from the current one onward, never previously spoken text.
  // Use one filled rectangle for a wholly pending paragraph; for partially read paragraphs or rows with later unrelated text,
  // highlight only pending sentence cells. Cache areas in buffer coordinates until span locations or status change.
  // Scrolling or unrelated spinner updates only translate cached areas into viewport coordinates.
  pending(): Area[] {
    if (!this.started || this.detached || this.lost || this.state === 'listening') { return []; }
    const term = this.deps.term;
    const buffer = term.buffer.active;
    const sig = this.signature(term.cols, term.rows, buffer.type === 'alternate' ? 1 : 0);
    if (!sig) { this.shape = this.build(); }
    const top = buffer.viewportY;
    const bottom = top + term.rows - 1;
    const out: Area[] = [];
    for (const a of this.shape) {
      if (a.bottom < top || a.top > bottom) { continue; }
      const first = Math.max(a.top, top);
      const last = Math.min(a.bottom, bottom);
      out.push({ top: first - top, bottom: last - top, left: a.left, right: a.right });
    }
    return out;
  }

  // Reuse signature arrays to detect changes in pending span locations and invalidate cached highlight areas.
  private signature(cols: number, rows: number, alt: number): boolean {
    const need = 3 + this.chain.length * 7;
    if (this.sigNext.length < need) { this.sigNext = new Int32Array(need * 2); }
    const next = this.sigNext;
    next[0] = cols; next[1] = rows; next[2] = alt;
    let n = 3;
    for (const span of this.chain) {
      const loc = span.loc;
      if (span.status !== 'found' || loc === null || (this.entries.get(span)?.seq ?? -1) < this.current) { continue; }
      next[n++] = loc.first.row; next[n++] = loc.first.col; next[n++] = loc.last.row; next[n++] = loc.last.col;
      next[n++] = loc.lastWidth; next[n++] = span.left; next[n++] = span.right;
    }
    let same = n === this.sigLen;
    for (let i = 0; same && i < n; i++) { if (next[i] !== this.sig[i]) { same = false; } }
    if (!same) { this.sigNext = this.sig; this.sig = next; this.sigLen = n; }
    return same;
  }

  // Build pending highlight areas in buffer coordinates, grouping consecutive sentences into blocks.
  private build(): Area[] {
    const anchors = this.deps.anchors;
    const out: Area[] = [];
    const spans = this.chain.filter((s) => s.status === 'found' && s.loc !== null && (this.entries.get(s)?.seq ?? -1) >= this.current);
    let i = 0;
    while (i < spans.length) {
      // Group consecutive sentences in one panel with no blank row between them.
      const [left, right] = anchors.panelOf(spans[i]);
      let end = i + 1;
      while (end < spans.length) {
        const [l, r] = anchors.panelOf(spans[end]);
        const gap = spans[end].loc!.first.row - spans[end - 1].loc!.last.row;
        if (l !== left || r !== right || gap < 0 || gap > 1) { break; }
        end++;
      }
      const run = spans.slice(i, end);
      i = end;
      const first = run[0].loc!;
      const last = run[run.length - 1].loc!;
      const extents = new Map<number, [number, number] | null>();
      const extent = (row: number): [number, number] | null => {
        let e = extents.get(row);
        if (e === undefined) { e = anchors.extent(row, left, right); extents.set(row, e); }
        return e;
      };
      // Find the block's left edge from continuation rows, excluding bullet or number prefixes.
      // If none exist, use sentence starts. The right edge is the widest row's end.
      const starts = new Set<number>();
      for (const span of run) { starts.add(span.loc!.first.row); }
      let blockLeft = Math.min(...run.map((s) => s.loc!.first.col));
      let edge = Number.MAX_SAFE_INTEGER;
      let blockRight = 0;
      for (let row = first.first.row; row <= last.last.row; row++) {
        const e = extent(row);
        if (e === null) { continue; }
        if (!starts.has(row)) { edge = Math.min(edge, e[0]); }
        blockRight = Math.max(blockRight, e[1]);
      }
      if (edge !== Number.MAX_SAFE_INTEGER) { blockLeft = Math.min(blockLeft, edge); }
      // A whole block contains only pending sentences: no other text before a sentence's first row or after its last row.
      let whole = true;
      for (let k = 0; whole && k < run.length; k++) {
        const loc = run[k].loc!;
        const before = k > 0 ? run[k - 1].loc! : null;
        const after = k + 1 < run.length ? run[k + 1].loc! : null;
        if ((before === null || before.last.row !== loc.first.row) && anchors.extent(loc.first.row, blockLeft, loc.first.col) !== null) { whole = false; }
        if ((after === null || after.first.row !== loc.last.row) && anchors.extent(loc.last.row, loc.last.col + loc.lastWidth, blockRight) !== null) { whole = false; }
      }
      if (whole) {
        out.push({ top: first.first.row, bottom: last.last.row, left: blockLeft, right: blockRight });
        continue;
      }
      // For partially read paragraphs, highlight pending cells row by row. Fill interior sentence rows to the block edges;
      // clip first and last rows to their endpoints, and merge adjacent rows with identical edges.
      let lastPlace: Area | null = null;
      for (const span of run) {
        const loc = span.loc!;
        for (let row = loc.first.row; row <= loc.last.row; row++) {
          const a = row === loc.first.row ? loc.first.col : blockLeft;
          const b = row === loc.last.row ? loc.last.col + loc.lastWidth : blockRight;
          if (b <= a) { continue; }
          if (lastPlace !== null && lastPlace.top === row && lastPlace.bottom === row && a >= lastPlace.right && a - lastPlace.right <= 2) {
            lastPlace.right = b;
          } else if (lastPlace !== null && lastPlace.bottom + 1 === row && lastPlace.left === a && lastPlace.right === b) {
            lastPlace.bottom = row;
          } else {
            lastPlace = { top: row, bottom: row, left: a, right: b };
            out.push(lastPlace);
          }
        }
      }
    }
    return out;
  }

  // terminal.ts hit-tests the rendered highlight elements.

  // ---- passes ---------------------------------------------------------------------------------------------------------------

  // Mark the next frame dirty after output, resize or buffer change. The caller already schedules that frame; no timer is needed.
  touch(): void {
    if (this.started && !this.detached && !this.lost) { this.dirty = true; }
  }

  // Resolve the chain immediately before drawing overlays, at most once per frame when dirty: tags, neighbors, then movement.
  // Draw only locations proven for this frame. Wait until partial frames close before inspecting them.
  frame(): void {
    if (!this.dirty || !this.started || this.detached || this.lost) { return; }
    this.pass();
  }

  // Schedule one timeout for the earliest settling, quiet-output, lost-location or partial-frame deadline.
  // Arm only from a pass or frame; never poll with a repeating timer.
  private arm(at: number): void {
    if (!this.started || this.detached || this.lost) { return; }
    if (this.timer !== 0) {
      if (this.timerAt <= at) { return; }
      window.clearTimeout(this.timer);
    }
    this.timerAt = at;
    this.timer = window.setTimeout(() => { this.timer = 0; this.timerAt = 0; this.pass(); }, Math.max(1, at - performance.now()));
  }

  private disarm(): void {
    window.clearTimeout(this.timer);
    this.timer = 0;
    this.timerAt = 0;
  }

  private currentSpan(): Span | null {
    return this.bySeq.get(this.current) ?? null;
  }

  private pass(): void {
    if (!this.started || this.detached || this.lost) { return; }
    const left = this.deps.syncLeft();
    if (left > 0) { this.arm(performance.now() + left); return; }
    this.dirty = false;
    const now = performance.now();
    const quietAt = this.deps.lastWriteAt() + QUIET_MS;
    const quiet = now >= quietAt;
    // Wait for quiet output before inspecting the initial repaint after reset.
    if (this.whole && !quiet) { this.arm(quietAt); return; }
    this.stats.passes++;
    const anchors = this.deps.anchors;
    const resolved = anchors.resolve(this.chain, this.holder, this.currentSpan(), this.whole);
    if (resolved.movement === 'stream') { this.stats.aligned++; }
    if (resolved.movement === 'ambiguous') { this.stats.ambiguous++; }
    if (resolved.found > 0) {
      this.whole = false;
      this.reattachBy = 0;
      this.noHopeSince = 0;
    }
    const gone = this.chain.filter((s) => s.status === 'gone');
    if (gone.length > 0) {
      this.chain = this.chain.filter((s) => s.status !== 'gone');
      for (const span of gone) { this.forget(span); }
    }
    if (!anchors.hope(this.chain)) {
      this.stats.unproven++;
      if (this.whole && this.reattachBy !== 0) {
        if (now >= this.reattachBy) { this.lose(); return; }
        this.arm(this.reattachBy);
      } else if (quiet) {
        this.noHopeSince = this.noHopeSince === 0 ? now : this.noHopeSince;
        if (now - this.noHopeSince >= LOST_MS) { this.lose(); return; }
        this.arm(this.noHopeSince + LOST_MS);
      } else {
        this.arm(quietAt);
      }
      this.deps.redraw();
      return;
    }
    this.noHopeSince = 0;
    this.replaceRewritten(resolved.changed);
    const due = this.ended ? 0 : this.intake(now, quiet, quietAt, resolved.movement === 'same' && resolved.shift !== 0);
    this.refreshMarkers();
    this.trimSpoken();
    this.deps.redraw();
    if (due !== 0) { this.arm(due); }
  }

  // Update a span rewritten in place, such as changing status numbers, without speaking it again.
  private replaceRewritten(changed: Span[]): void {
    for (const span of changed) {
      const was = span.was;
      if (was === null || span.status !== 'unproven') { continue; }
      const cells = this.deps.anchors.cells(was.first.row, was.last.row, span.left, span.right);
      if (cells.length === 0) { continue; }
      const index = this.chain.indexOf(span);
      const previous = index > 0 ? this.chain[index - 1] : null;
      const first = cells[0];
      const last = cells[cells.length - 1];
      if (this.deps.anchors.rekey(span, { row: first.row, col: first.col }, { row: last.row, col: last.col }, previous)) {
        this.stats.replaced++;
        const next = this.chain[index + 1];
        if (next !== undefined) { this.deps.anchors.regap(next, span); }
      }
    }
  }

  // Accept new sentences after proven spans in stream order. Return the next settling deadline if output stays unchanged,
  // or 0 when nothing is waiting.
  private intake(now: number, quiet: boolean, quietAt: number, moved = false): number {
    const anchors = this.deps.anchors;
    let unspoken = 0;
    for (const [, entry] of this.entries) { if (entry.seq > this.current) { unspoken++; } }
    if (unspoken >= MAX_UNSPOKEN) { return 0; }
    // Inspect gaps between proven spans only when no unproven span intervenes, plus the tail after the last proven span.
    const regions: { after: Span; before: Span | null }[] = [];
    let last: Span | null = null;
    let open = 0;
    for (const span of this.chain) {
      if (span.status === 'found') {
        if (last !== null && open === 0) { regions.push({ after: last, before: span }); }
        last = span;
        open = 0;
      } else {
        open++;
      }
    }
    if (last !== null && open === 0) { regions.push({ after: last, before: null }); }
    const seenNow = new Map<string, number>();
    let due = 0;
    const added: string[] = [];
    let firstSeq = -1;
    const lists = regions.map((region) => {
      const found = this.candidates(region.after.loc!.last, region.before === null ? null : region.before.loc!.first, MAX_UNSPOKEN - unspoken, false, this.entries.get(region.after)?.continues ?? false);
      // Retain the first-seen timestamp while a candidate's text stays unchanged.
      for (const c of found.list) { seenNow.set(c.key, this.seen.get(c.key) ?? now); }
      return found;
    });
    outer:
    for (let r = 0; r < regions.length; r++) {
      const region = regions[r];
      const found = lists[r];
      let previous: Span | null = region.after;
      for (let i = 0; i < found.list.length; i++) {
        const c = found.list[i];
        // A floating element may hide the rest of an unfinished sentence immediately above it.
        // Wait until the sentence continues past the element or ends before accepting it whole.
        if (found.barrier && !c.terminal && i === found.list.length - 1 && found.barrierRow - c.last.row <= 1) { continue; }
        const since = seenNow.get(c.key)!;
        const lastOfTail = region.before === null && i === found.list.length - 1 && !found.barrier;
        const needed = lastOfTail && !c.terminal ? OPEN_MS : SETTLE_MS;
        if (!(now - since >= needed || (region.before === null && (quiet || (moved && now - since >= QUIET_MS)) && (c.terminal || !lastOfTail)))) {
          // Accept after `needed` milliseconds unchanged, or sooner for eligible tail sentences when output is quiet
          // or QUIET_MS after first sight during measured movement.
          due = since + needed;
          if (region.before === null && (c.terminal || !lastOfTail)) {
            if (!quiet) { due = Math.min(due, quietAt); }
            if (moved) { due = Math.min(due, since + QUIET_MS); }
          }
          break outer;
        }
        const span = this.take(c, previous, region.before);
        if (span === null) { continue; }
        if (firstSeq < 0) { firstSeq = this.entries.get(span)!.seq; }
        added.push(c.text);
        previous = span;
        if (unspoken + added.length >= MAX_UNSPOKEN) { break outer; }
      }
    }
    // Retain first-seen times for current candidates; discard entries when candidates are accepted or disappear.
    this.seen = seenNow;
    if (added.length > 0) {
      this.stats.taken += added.length;
      this.send({ type: 'readadd', seq: firstSeq, texts: added });
    }
    return due;
  }

  // Insert an accepted candidate after its preceding span and update the next span's gap.
  private take(c: Candidate, after: Span | null, before: Span | null): Span | null {
    const anchors = this.deps.anchors;
    const span = anchors.make(c.first, c.last, c.left, c.right, after);
    if (span === null) { return null; }
    const index = after === null ? 0 : this.chain.indexOf(after) + 1;
    this.chain.splice(index, 0, span);
    const entry = { seq: this.nextSeq++, text: c.text, continues: c.continues };
    this.entries.set(span, entry);
    this.bySeq.set(entry.seq, span);
    if (before !== null) { anchors.regap(before, span); }
    return span;
  }

  private forget(span: Span): void {
    const entry = this.entries.get(span);
    if (entry !== undefined) { this.bySeq.delete(entry.seq); }
    this.entries.delete(span);
  }

  // Keep markers only near the current sentence: each marker costs xterm.js a callback for every trimmed line.
  private refreshMarkers(): void {
    const anchors = this.deps.anchors;
    for (const span of this.chain) {
      const seq = this.entries.get(span)?.seq ?? -1;
      if (seq >= this.current - KEEP_SPOKEN && seq <= this.current + MARKERS_AHEAD) { anchors.ensureMarker(span); } else { anchors.dropMarker(span); }
    }
  }

  // Retain the 16 most recently spoken spans and every unspoken span; release older ones.
  private trimSpoken(): void {
    const spoken = this.chain.filter((s) => (this.entries.get(s)?.seq ?? Infinity) < this.current);
    if (spoken.length <= KEEP_SPOKEN) { return; }
    spoken.sort((a, b) => this.entries.get(b)!.seq - this.entries.get(a)!.seq);
    const drop = spoken.slice(KEEP_SPOKEN);
    this.deps.anchors.dropAll(drop);
    this.chain = this.chain.filter((s) => !drop.includes(s));
    for (const span of drop) { this.forget(span); }
  }

  // ---- the sentences of the terminal ----------------------------------------------------------------------------------------------

  // Read at most limit sentences between exclusive cell bounds, in stream order within the starting panel.
  // A null end means the end of content. A floating element stops scanning; covered text waits until its row is clear.
  private candidates(after: Point, until: Point | null, limit: number, first = false, continuing = false): { list: Candidate[]; barrier: boolean; barrierRow: number } {
    const term = this.deps.term;
    const buffer = term.buffer.active;
    const alt = buffer.type === 'alternate';
    const list: Candidate[] = [];
    let barrier = false;
    let barrierRow = -1;
    const lastRow = Math.min(until === null ? buffer.length - 1 : until.row, buffer.length - 1);
    const cursor = this.deps.cursorVisible() ? buffer.baseY + buffer.cursorY : -1;
    let panelLeft = -1;
    let panelRight = term.cols;
    let panelTop = 0;
    let panelBottom = Number.MAX_SAFE_INTEGER;
    let panelSplits: string | null = null;
    let from = alt ? 0 : Math.max(0, after.row);
    while (from <= lastRow && list.length < limit && !barrier) {
      let to = alt ? term.rows - 1 : Math.min(lastRow, from + CHUNK_ROWS - 1);
      to = Math.min(to, lastRow);
      const rows = screenRows(from, to - from + 1);
      // A split needs four bars: keep three rows of context on both sides of an intake chunk.
      const contextFrom = Math.max(0, from - 3);
      const splits = splitsOf(screenRows(contextFrom, Math.min(buffer.length - 1, to + 3) - contextFrom + 1))
        .map((split) => ({ ...split, top: split.top + contextFrom - from, bottom: split.bottom + contextFrom - from }));
      // Blank cells outside the requested bounds, including the visible input cursor's row.
      for (let i = 0; i < rows.length; i++) {
        const row = from + i;
        const blank = row < after.row || row > (until?.row ?? Number.MAX_SAFE_INTEGER) || row === cursor;
        const cells = rows[i];
        for (let x = 0; x < cells.length; x++) {
          if (blank || (row === after.row && x <= after.col) || (until !== null && row === until.row && x >= until.col)) { cells[x] = ' '; }
        }
      }
      // Keep the starting cell's panel columns across every chunk.
      if (panelLeft < 0) {
        const startRow = Math.max(0, Math.min(rows.length - 1, after.row - from));
        const xs = splits.filter((s) => s.top <= startRow && startRow <= s.bottom).map((s) => s.x).sort((a, b) => a - b);
        const col = Math.max(0, after.col);
        panelLeft = Math.max(-1, ...xs.filter((x) => x < col)) + 1;
        panelRight = Math.min(term.cols, ...xs.filter((x) => x > col));
        const stretch = stretchOf(splits, startRow, rows.length);
        panelTop = stretch.top + from;
        panelSplits = splitsAt(splits, startRow).join(',');
        panelBottom = stretch.bottom === rows.length - 1 ? Number.MAX_SAFE_INTEGER : stretch.bottom + from;
      }
      if (panelBottom === Number.MAX_SAFE_INTEGER) {
        const startRow = Math.max(0, after.row - from);
        for (let row = startRow; row < rows.length; row++) {
          if (splitsAt(splits, row).join(',') !== panelSplits) { panelBottom = from + row - 1; break; }
        }
      }
      // Stop at floating elements; exclude background bands such as user messages and input boxes from speech.
      for (let i = 0; i < rows.length && !barrier; i++) {
        const kind = rows[i].some((c) => c.trim() !== '') ? bandAt(from + i, panelLeft, panelRight) : 'none';
        if (kind === 'float') {
          barrier = true;
          barrierRow = from + i;
          for (let k = i; k < rows.length; k++) { rows[k] = rows[k].map(() => ' '); }
        } else if (kind === 'row') {
          rows[i] = rows[i].map(() => ' ');
        }
      }
      const panel = rows.map((r, y) => (from + y < panelTop || from + y > panelBottom ? '' : r.slice(panelLeft, panelRight).join('')));
      for (let row = 0; row < panel.length && list.length < limit; row++) {
        if (isBlank(panel[row])) { continue; }
        const bottom = bottomOf(panel, row);
        const block = blockOf(rows, row, bottom, panelLeft, panelRight);
        for (const sentence of this.sentencesOf(block, from, panelLeft, panelRight, first && list.length === 0, continuing && list.length === 0)) { list.push(sentence); }
        row = bottom;
      }
      if (alt) { break; }
      // Start the next chunk after a nearby trailing blank row when possible, avoiding a paragraph split.
      let next = to + 1;
      for (let i = panel.length - 1; i > panel.length - 30 && i > 0; i--) {
        if (isBlank(panel[i]) && next < lastRow) { next = from + i + 1; break; }
      }
      from = Math.max(next, from + 1);
    }
    return { list: list.slice(0, limit), barrier, barrierRow };
  }

  // Speech pieces keep their original character-to-cell map; only the first piece of the reading gets the latency cut.
  private sentencesOf(block: Block, top: number, left: number, right: number, first: boolean, continuing: boolean): Candidate[] {
    const chars = block.chars;
    const pieces = speechPieces(block.text, first, continuing);
    const out: Candidate[] = [];
    for (const { start: a, end: b, terminal, continues } of pieces) {
      const slice: Char[] = chars.slice(a, b);
      const cells = slice.filter((c) => c.cell !== null);
      if (cells.length === 0) { continue; }
      const text = speakable(slice.map((c) => c.ch).join(''));
      if (!LETTER.test(text)) { continue; }
      const first = cells[0].cell!;
      const last = cells[cells.length - 1].cell!;
      const firstPoint = { row: top + first.row, col: first.col };
      const lastPoint = { row: top + last.row, col: last.col };
      out.push({ text, first: firstPoint, last: lastPoint, left, right, terminal, continues, key: this.rawKey(firstPoint, lastPoint, left, right) });
    }
    return out;
  }

  // Use the candidate's stream text to distinguish ongoing writes from settled sentences.
  private rawKey(first: Point, last: Point, left: number, right: number): string {
    const cells = this.deps.anchors.cells(first.row, last.row, left, right)
      .filter((c) => (c.row > first.row || c.col >= first.col) && (c.row < last.row || c.col <= last.col));
    return cells.map((c) => c.ch).join('');
  }
}

const SENTENCE_START = /\s+(?=[\p{Lu}\d¿¡"“'(])/gu;
const ABBREVIATION = /\b(?:mr|mrs|ms|dr|dra|prof|sr|sra|jr|st|vs|etc|e\.g|i\.e|p\.ej)\.["”')\]]*$/iu;

type SpeechPiece = { start: number; end: number; terminal: boolean; continues: boolean };

// Return offsets, not copied text, so the caller maps speech pieces back to their original terminal cells.
function speechPieces(text: string, first: boolean, continuing: boolean): SpeechPiece[] {
  const sentences: [number, number][] = [];
  let start = 0;
  for (const gap of text.matchAll(SENTENCE_START)) {
    const before = text.slice(start, gap.index);
    if (ENDS.test(before) && !ABBREVIATION.test(before)) {
      sentences.push([start, gap.index]);
      start = gap.index + gap[0].length;
    }
  }
  sentences.push([start, text.length]);
  const joined: [number, number][] = [];
  let pending: [number, number] | null = null;
  for (const piece of sentences) {
    if (piece[0] === piece[1]) { continue; }
    // Do not merge the remainder of an earlier artificial cut as if it were a new short sentence.
    if (continuing && joined.length === 0) { joined.push(piece); continue; }
    pending = pending === null ? piece : [pending[0], piece[1]];
    if (pending[1] - pending[0] >= 20) { joined.push(pending); pending = null; }
  }
  if (pending !== null) {
    if (joined.length > 0) { joined[joined.length - 1][1] = pending[1]; } else { joined.push(pending); }
  }
  const out: SpeechPiece[] = [];
  for (let group = 0; group < joined.length; group++) {
    const [a, b] = joined[group];
    const cuts: number[] = [];
    if (first && group === 0) {
      const words = [...text.slice(a, b).matchAll(/\S+/gu)];
      if (words.length > 10) {
        const tenth = words[9];
        const after = a + tenth.index + tenth[0].replace(/[,;:—–-]+$/u, '').length;
        for (let i = after; i < b; i++) { if (pieceMark(text, i)) { cuts.push(i + 1); break; } }
      }
    }
    const latencyCut = cuts[0] ?? -1;
    cuts.push(b);
    let from = a;
    for (const end of cuts) {
      while (end - from > 250) {
        let cut = -1;
        for (let i = from; i < from + 250; i++) { if (pieceMark(text, i)) { cut = i + 1; } }
        if (cut < 0) { for (let i = from + 249; i > from; i--) { if (/\s/u.test(text[i])) { cut = i; break; } } }
        if (cut <= from) { break; }
        out.push({ start: from, end: cut, terminal: true, continues: true });
        from = cut;
        while (from < end && /\s/u.test(text[from])) { from++; }
      }
      if (end > from) {
        const fragment = from > a || (continuing && group === 0);
        const whole = text.slice(from, end);
        out.push({ start: from, end, terminal: end === latencyCut || end < b || ((fragment || whole.length >= 20) && ENDS.test(whole) && !ABBREVIATION.test(whole)), continues: end === latencyCut || end < b });
      }
      from = end;
      while (from < b && /\s/u.test(text[from])) { from++; }
    }
  }
  return out;
}

function pieceMark(text: string, at: number): boolean {
  return /[,;:—–]/u.test(text[at]) || (text[at] === '-' && /\s/u.test(text[at - 1] ?? '') && /\s/u.test(text[at + 1] ?? ''));
}

// Strip symbols and collapse whitespace for speech.
export function speakable(text: string): string {
  return text.replace(SYMBOLS, ' ').replace(/\s+/g, ' ').trim();
}
