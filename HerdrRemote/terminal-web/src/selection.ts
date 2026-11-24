// Long press selects a word, as in Termux, Termius and ConnectBot; dragging before release extends the selection.
// Use xterm.js selection rendering and page-owned handles on both platforms. The native edit menu uses UIEditMenuInteraction
// on iOS and floating ActionMode on Android; deps.menu sends its position and capabilities, and act receives the chosen action.
// xterm.js has no touch selection: its mouse-based selection cannot run because page handlers suppress synthesized mouse events.
//
// follow runs during the shared repaint to keep selection attached through output, scrolling, trimming, reflow and redraws.
// xterm.js clears selection when row counts change and retains stale columns after reflow, so both endpoints must be restored.
// Only a tap elsewhere without scrolling or pinching, or removal of the text from the buffer, ends selection.
//
// On release, anchor.ts tracks a chain of spans: the nearest text row above, the first 64 characters, the last 64, and the nearest
// text row below. Cell tags retain identity through zoom, rotation, keyboard changes and redraws. Hide rewritten endpoints until
// proven again; never jump to another copy of the text, and do not offer Copy or Read while their location is unproven.
import type { Terminal } from '@xterm/xterm';
import { Anchors, type CellTags, type Holder, type Point, type Span } from './anchor';

export type { Point } from './anchor';

// The native menu's capabilities and rectangle in page CSS pixels.
export type MenuState = { id: number; text: boolean; readable: boolean; image: 'none' | 'open' | 'reload'; x: number; y: number; width: number; height: number } | null;

export type MenuAction = 'read' | 'readfrom' | 'copy' | 'paste' | 'all' | 'image' | 'reloadimage' | 'clear';

export type SelectionDeps = {
  term: Terminal;
  anchors: Anchors;
  revision: () => number;
  // Map page coordinates to a cell with viewport-relative rows; return null outside the grid.
  cellAt: (clientX: number, clientY: number) => { row: number; col: number } | null;
  // Return the visible paragraph's first and last buffer cells, using the reader's block rules; otherwise null.
  paragraphAt: (point: Point) => [Point, Point] | null;
  copy: (text: string) => void;
  paste: () => void;
  // Read only between the selection's first and last spans.
  read: (head: Span, tail: Span) => void;
  // Read onward from the selection's first cell.
  readFrom: (head: Span) => void;
  context: (point: Point) => { readable: boolean; image: 'none' | 'open' | 'reload'; id?: number };
  action: (action: 'image' | 'reloadimage', point: Point, image?: number) => void;
  // Notify C# when a hold starts selection or opens the menu, for haptic feedback.
  started: () => void;
  // Send menu state only when it changes.
  menu: (state: MenuState) => void;
};

// Handle hit radius; the knob's visual size is set separately below.
const HANDLE_HIT_PX = 28;
// Keep at most 64 characters at each selection endpoint.
const SPAN_CHARS = 64;
const KNOB_PX = 18;

export class Selection {
  private start: Point | null = null;
  private end: Point | null = null;
  // A hold on a blank cell opens a Paste-only menu at this page coordinate.
  private at: { x: number; y: number } | null = null;
  private dragging: 'start' | 'end' | null = null;
  // Track the above/head/tail/below span chain for reading. Hide rewritten endpoints until they are proven again.
  private head: Span | null = null;
  private tail: Span | null = null;
  private chain: Span[] = [];
  private holder: Holder = { band: null };
  private coverage: CellTags | null = null;
  private coveredRevision = -1;
  private coverageProven = false;
  private hasText = false;
  private lost = false;
  private readonly handles: Record<'start' | 'end', HTMLElement>;
  // Hide the menu during scrolling or pinching; retain the last reported state to avoid duplicate messages.
  private moving = false;
  private menuSent = 'null';
  private menuId = 0;
  private menuStart: Point | null = null;
  private menuEnd: Point | null = null;
  private menuAt: { x: number; y: number } | null = null;
  private menuText = false;
  private menuReadable = false;
  private menuImage = 'none';
  private capability: { readable: boolean; image: 'none' | 'open' | 'reload'; id?: number } = { readable: false, image: 'none' };

  constructor(private readonly deps: SelectionDeps) {
    this.handles = { start: newHandle(), end: newHandle() };
  }

  setTheme(accent: string): void {
    for (const handle of Object.values(this.handles)) {
      for (const part of Array.from(handle.children)) {
        (part as HTMLElement).style.background = accent;
      }
    }
  }

  // Hide the menu during movement; restore it at the selection when movement ends.
  setMoving(moving: boolean): void {
    this.moving = moving;
    this.place();
  }

  get active(): boolean {
    return (this.start !== null && !this.lost) || this.at !== null;
  }

  // Select the word under a hold, or show only the menu on a blank cell. Dragging extends the endpoint until release.
  begin(clientX: number, clientY: number): void {
    const point = this.pointAt(clientX, clientY);
    const word = point === null ? null : this.wordAt(point);
    this.deps.started();
    if (word === null) {
      this.start = null;
      this.end = null;
      this.at = { x: clientX, y: clientY };
    } else {
      [this.start, this.end] = word;
      this.at = null;
    }
    this.dragging = 'end';
    this.show();
  }

  // Double tap uses the same editable range and menu as a hold.
  beginParagraph(clientX: number, clientY: number): void {
    const point = this.pointAt(clientX, clientY);
    const range = point === null ? null : this.deps.paragraphAt(point);
    this.deps.started();
    [this.start, this.end] = range ?? [null, null];
    this.at = range === null ? { x: clientX, y: clientY } : null;
    this.dragging = 'end';
    this.lost = false;
    this.show();
    this.release();
  }

  // Touching a handle starts a drag. Elsewhere, clear selection only if the touch ends as a tap; scrolling or pinching preserves it.
  touchStart(clientX: number, clientY: number): 'handle' | 'outside' {
    for (const which of ['end', 'start'] as const) {
      const handle = this.handles[which];
      const box = handle.getBoundingClientRect();
      if (handle.style.display !== 'none'
        && Math.hypot(clientX - (box.left + box.width / 2), clientY - (box.bottom - KNOB_PX / 2)) <= HANDLE_HIT_PX) {
        this.dragging = which;
        this.place();
        return 'handle';
      }
    }
    return 'outside';
  }

  // Move the active endpoint while dragging a handle or the initial long press. Return whether selection consumes the move.
  move(clientX: number, clientY: number): boolean {
    if (this.dragging === null) {
      return false;
    }
    if (this.start === null || this.end === null) {
      return true;
    }
    // Aim half a cell above the finger, which sits below the row under the handle knob.
    const cellHeight = this.screen().getBoundingClientRect().height / this.deps.term.rows;
    const point = this.pointAt(clientX, clientY - cellHeight / 2, true);
    if (point !== null) {
      this[this.dragging] = point;
      this.show();
    }
    return true;
  }

  // Release the drag and show the menu. Return whether selection consumes the release.
  release(): boolean {
    if (this.dragging === null) {
      return false;
    }
    this.dragging = null;
    this.remember();
    const [first] = this.ordered();
    const point = first ?? (this.at === null ? null : this.pointAt(this.at.x, this.at.y));
    this.capability = point === null ? { readable: false, image: 'none' } : this.deps.context(point);
    this.place();
    return true;
  }

  // Actions re-read the live range; an obsolete or hidden native context cannot act.
  act(action: MenuAction, id?: number): void {
    this.follow();
    if (id === undefined || id !== this.menuId || this.menuSent === 'null') {
      return;
    }
    const term = this.deps.term;
    if (action === 'all') {
      const top = term.buffer.active.viewportY;
      [this.start, this.end] = [{ col: 0, row: top }, { col: term.cols - 1, row: top + term.rows - 1 }];
      this.at = null;
      this.lost = false;
      this.capability = this.deps.context(this.start);
      this.show();
      this.remember();
      this.place();
      return;
    }
    const text = term.getSelection();
    if (action === 'copy' && text !== '') {
      this.deps.copy(text);
    } else if (action === 'read' && text.trim() !== '' && this.head !== null && this.tail !== null) {
      this.uncover();
      this.deps.read(this.head, this.tail);
    } else if (action === 'paste') {
      this.deps.paste();
    } else if (action === 'readfrom' || action === 'image' || action === 'reloadimage') {
      const [first] = this.ordered();
      const point = first ?? (this.at === null ? null : this.pointAt(this.at.x, this.at.y));
      if (point !== null) {
        const current = this.deps.context(point);
        if (action === 'readfrom') {
          if (current.readable && text.trim() !== '' && this.head !== null) { this.uncover(); this.deps.readFrom(this.head); }
        } else if (current.id === this.capability.id && current.image === this.capability.image && current.image !== 'none') {
          this.deps.action(action, point, current.id);
        }
      }
    }
    this.clear();
  }

  clear(): void {
    this.start = null;
    this.end = null;
    this.at = null;
    this.dragging = null;
    this.forget();
    this.deps.term.clearSelection();
    this.place();
  }

  // Restore selection, handles and menu from the head and tail cell tags after each screen change. Tags survive reflow, scrolling,
  // trimming and row moves. If overwritten, resolve spans against a neighbor proven in this pass or measured screen movement.
  // Hide unproven endpoints, never jump to duplicate text, and clear selection when its endpoints leave normal-buffer scrollback.
  follow(): void {
    if (this.head !== null && this.tail !== null && this.dragging === null && this.start !== null && this.end !== null) {
      const anchors = this.deps.anchors;
      let first = this.head.status === 'found' ? anchors.quick(this.head) : null;
      let last = this.tail.status === 'found' ? anchors.quick(this.tail) : null;
      if (first === null || last === null) {
        // Resolve overwritten tags in this drawing frame, not later. During partial frames, keep endpoints hidden until the frame closes.
        if (anchors.held()) {
          this.hide();
          return;
        }
        anchors.resolve(this.chain, this.holder, this.head);
        if (this.head.status === 'gone' || this.tail.status === 'gone') {
          this.clear();
          return;
        }
        first = this.head.status === 'found' ? this.head.loc : null;
        last = this.tail.status === 'found' ? this.tail.loc : null;
      }
      const term = this.deps.term;
      if (first === null || last === null) {
        this.hide();
        return;
      }
      const start = { col: first.first.col, row: first.first.row };
      const end = { col: last.last.col + last.lastWidth - 1, row: last.last.row };
      const moved = this.start.row !== start.row || this.start.col !== start.col || this.end.row !== end.row || this.end.col !== end.col;
      const revision = this.deps.revision();
      if (this.coverage !== null && (moved || revision !== this.coveredRevision)) {
        this.coveredRevision = revision;
        const cells = anchors.cellsOfLoc({ first: start, last: last.last, lastWidth: last.lastWidth }, 0, term.cols, true);
        this.coverageProven = anchors.covered(this.coverage, cells);
      }
      if (this.coverage !== null && !this.coverageProven) { this.hide(); return; }
      const wasLost = this.lost;
      this.lost = false;
      [this.start, this.end] = [start, end];
      const drawn = term.getSelectionPosition();
      if (wasLost || moved || drawn === undefined || drawn.start.x !== start.col || drawn.start.y !== start.row) {
        this.show();
        return;
      }
    }
    this.place();
  }

  // Capture endpoint spans on release, each holding at most 64 characters. Track intervening cells by tags, not copied text.
  private remember(): void {
    this.forget();
    const [first, last] = this.ordered();
    if (first === null || last === null) { return; }
    const anchors = this.deps.anchors;
    const term = this.deps.term;
    const cells = anchors.cells(first.row, last.row)
      .filter((c) => (c.row > first.row || c.col >= first.col) && (c.row < last.row || c.col <= last.col));
    this.hasText = cells.length > 0;
    if (!this.hasText) { return; }
    let headEnd = 0;
    let chars = 0;
    while (headEnd < cells.length - 1 && chars + cells[headEnd].ch.length < SPAN_CHARS) { chars += cells[headEnd].ch.length; headEnd++; }
    let tailStart = cells.length - 1;
    chars = cells[tailStart].ch.length;
    while (tailStart > headEnd + 1 && chars + cells[tailStart - 1].ch.length <= SPAN_CHARS) { tailStart--; chars += cells[tailStart].ch.length; }
    const point = (i: number): Point => ({ row: cells[i].row, col: cells[i].col });
    const above = this.witness(first.row, -1);
    this.head = anchors.make(point(0), point(headEnd), 0, term.cols, above);
    this.tail = tailStart > headEnd ? anchors.make(point(tailStart), point(cells.length - 1), 0, term.cols, this.head) : this.head;
    const below = this.witness(last.row, 1, this.tail);
    this.chain = [above, this.head, this.tail === this.head ? null : this.tail, below].filter((span): span is Span => span !== null);
    this.holder = { band: null };
    this.lost = false;
    if (this.head !== null) { anchors.resolve(this.chain, this.holder, this.head); }
    this.coverage = this.head?.loc !== null && this.head?.loc !== undefined && this.tail?.loc !== null && this.tail?.loc !== undefined
      ? anchors.cover(anchors.cellsOfLoc({ first: this.head.loc.first, last: this.tail.loc.last, lastWidth: this.tail.loc.lastWidth }, 0, term.cols, true)) : null;
    this.coveredRevision = -1;
    this.follow();
  }

  // Use the nearest text row above (-1) or below (1) as a witness for resolving adjacent endpoint spans.
  private witness(row: number, direction: -1 | 1, previous: Span | null = null): Span | null {
    const anchors = this.deps.anchors;
    const length = this.deps.term.buffer.active.length;
    for (let y = row + direction, steps = 0; y >= 0 && y < length && steps < 200; y += direction, steps++) {
      const cells = anchors.cells(y, y);
      if (cells.length > 0) {
        return anchors.make({ row: y, col: cells[0].col }, { row: y, col: cells[cells.length - 1].col }, 0, this.deps.term.cols, previous);
      }
    }
    return null;
  }

  private uncover(): void {
    if (this.coverage !== null) { this.deps.anchors.uncover(this.coverage); this.coverage = null; }
  }

  private forget(): void {
    this.uncover();
    const spans = [...new Set(this.chain)];
    this.deps.anchors.dropAll(spans);
    this.head = null;
    this.tail = null;
    this.chain = [];
    this.holder = { band: null };
    this.hasText = false;
    this.lost = false;
  }

  // Hide the highlight, handles and menu until both endpoints are proven in the current frame.
  private hide(): void {
    this.lost = true;
    if (this.deps.term.hasSelection()) { this.deps.term.clearSelection(); }
    this.place();
  }

  // Position handles and menu at the current selection; hide them while its location is unproven or off-screen.
  place(): void {
    const [first, last] = this.lost ? [null, null] : this.ordered();
    const screen = this.screen().getBoundingClientRect();
    const term = this.deps.term;
    const cellWidth = screen.width / term.cols;
    const cellHeight = screen.height / term.rows;
    const top = term.buffer.active.viewportY;
    const put = (handle: HTMLElement, point: Point | null, after: boolean): void => {
      const row = point === null ? -1 : point.row - top;
      if (point === null || row < 0 || row >= term.rows) {
        handle.style.display = 'none';
        return;
      }
      handle.style.display = 'block';
      handle.style.left = `${screen.left + (point.col + (after ? 1 : 0)) * cellWidth - KNOB_PX / 2}px`;
      handle.style.top = `${screen.top + row * cellHeight}px`;
      handle.style.height = `${cellHeight + KNOB_PX}px`;
    };
    put(this.handles.start, first, false);
    put(this.handles.end, last, true);
    // Hide the menu during dragging, scrolling or zooming, and while selection is off-screen after user scrolling or new output.
    // Restore it with the selection so it never floats over unrelated text.
    const away = first !== null && last !== null && (last.row < top || first.row >= top + term.rows);
    let state: MenuState = null;
    if (this.dragging === null && !this.moving && !away && this.at !== null) {
      state = { id: 0, text: false, readable: false, image: this.capability.image, x: this.at.x - 1, y: this.at.y - 1, width: 2, height: 2 };
    } else if (this.dragging === null && !this.moving && !away && first !== null && last !== null) {
      // For one row, use the selected columns; across multiple rows, use the full screen width.
      const from = Math.max(first.row, top) - top;
      const to = Math.min(last.row, top + term.rows - 1) - top;
      const one = first.row === last.row;
      state = {
        id: 0,
        text: this.hasText,
        readable: this.hasText && this.capability.readable && first.row >= top && first.row < top + term.rows,
        image: this.capability.image,
        x: screen.left + (one ? first.col : 0) * cellWidth,
        y: screen.top + from * cellHeight,
        width: (one ? last.col + 1 - first.col : term.cols) * cellWidth,
        height: (to - from + 1) * cellHeight,
      };
    }
    if ((state === null) !== (this.menuSent === 'null') || first?.row !== this.menuStart?.row || first?.col !== this.menuStart?.col
      || last?.row !== this.menuEnd?.row || last?.col !== this.menuEnd?.col || this.at?.x !== this.menuAt?.x || this.at?.y !== this.menuAt?.y
      || (state?.text ?? false) !== this.menuText || (state?.readable ?? false) !== this.menuReadable || (state?.image ?? 'none') !== this.menuImage) {
      this.menuId++;
      this.menuStart = first;
      this.menuEnd = last;
      this.menuAt = this.at;
      this.menuText = state?.text ?? false;
      this.menuReadable = state?.readable ?? false;
      this.menuImage = state?.image ?? 'none';
    }
    if (state !== null) { state.id = this.menuId; }
    const shown = JSON.stringify(state === null ? null : { ...state, x: Math.round(state.x), y: Math.round(state.y),
      width: Math.round(state.width), height: Math.round(state.height) });
    if (shown !== this.menuSent) {
      this.menuSent = shown;
      this.deps.menu(JSON.parse(shown) as MenuState);
    }
  }

  private show(): void {
    const [first, last] = this.ordered();
    const term = this.deps.term;
    if (first === null || last === null) {
      term.clearSelection();
    } else {
      term.select(first.col, first.row, (last.row - first.row) * term.cols + last.col - first.col + 1);
    }
    this.place();
  }

  private ordered(): [Point | null, Point | null] {
    const a = this.start;
    const b = this.end;
    if (a === null || b === null) {
      return [null, null];
    }
    return a.row < b.row || (a.row === b.row && a.col <= b.col) ? [a, b] : [b, a];
  }

  // Map page coordinates to a buffer cell. Clamp drags past the grid edge when requested; otherwise return null outside the grid.
  private pointAt(clientX: number, clientY: number, clamp = false): Point | null {
    const term = this.deps.term;
    let cell = this.deps.cellAt(clientX, clientY);
    if (cell === null && clamp) {
      const box = this.screen().getBoundingClientRect();
      const row = Math.floor((clientY - box.top) / (box.height / term.rows));
      const col = Math.floor((clientX - box.left) / (box.width / term.cols));
      cell = { row: Math.min(term.rows - 1, Math.max(0, row)), col: Math.min(term.cols - 1, Math.max(0, col)) };
    }
    if (cell === null) { return null; }
    const row = cell.row + term.buffer.active.viewportY;
    const col = cell.col > 0 && term.buffer.active.getLine(row)?.getCell(cell.col)?.getWidth() === 0 ? cell.col - 1 : cell.col;
    return { col, row };
  }

  // A word is a run of non-blank cells, including the second half of wide characters. Blank cells have no word.
  private wordAt(point: Point): [Point, Point] | null {
    const line = this.deps.term.buffer.active.getLine(point.row);
    if (line === undefined) {
      return null;
    }
    const blank = (col: number): boolean => {
      const cell = line.getCell(col);
      return cell === undefined || (cell.getWidth() !== 0 && cell.getChars().trim() === '');
    };
    if (blank(point.col)) {
      return null;
    }
    let left = point.col;
    let right = point.col;
    while (left > 0 && !blank(left - 1)) {
      left--;
    }
    while (right < this.deps.term.cols - 1 && !blank(right + 1)) {
      right++;
    }
    return [{ col: left, row: point.row }, { col: right, row: point.row }];
  }

  private screen(): HTMLElement {
    return this.deps.term.element?.querySelector('.xterm-screen') as HTMLElement;
  }
}

// Each handle has a row-height stem and a round knob below it.
function newHandle(): HTMLElement {
  const handle = document.createElement('div');
  handle.style.cssText = `position:fixed;display:none;z-index:20;width:${KNOB_PX}px;pointer-events:none`;
  const stem = document.createElement('div');
  stem.style.cssText = `position:absolute;left:${KNOB_PX / 2 - 1}px;top:0;bottom:${KNOB_PX - 2}px;width:2px`;
  const knob = document.createElement('div');
  knob.style.cssText = `position:absolute;left:0;bottom:0;width:${KNOB_PX}px;height:${KNOB_PX}px;border-radius:50%`;
  handle.append(stem, knob);
  document.body.appendChild(handle);
  return handle;
}
