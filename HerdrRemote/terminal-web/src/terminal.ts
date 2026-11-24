// Pane terminal hosted in a MAUI HybridWebView (Pages/TerminalView.cs) using xterm.js.
// Render herdr-core's initial 2000-line history and subsequent bytes into local scrollback. Input bypasses xterm.js:
// report program input modes to C#, which encodes keys. Unrendered kitty graphics APC bytes pass through the parser unchanged.
// Each HybridWebView raw message contains one JSON string.
//
// Gesture and rendering rules:
// - Up to 10 px of finger travel remains a tap or long press. A 500 ms one-finger hold selects a word (selection.ts).
// - Double tap selects an editable paragraph: the second touch starts within 300 ms of release and 24 px of the first start.
//   Delay a single tap by those 300 ms.
// - The current sentence highlight pulses from opacity 1 to 0.3 and back (1 s each way) while audio loads, stays steady during
//   playback, and uses half opacity while paused. C# supplies highlight colors.
// - The first movement beyond the threshold sets the gesture axis. A mostly horizontal swipe of at least 60 px changes panes
//   on release (left means next, +1); a vertical gesture scrolls. Two fingers only pinch.
// - Dragging down shows older lines, one line per cell height. Use local scrollback, or wheel steps on the alternate screen
//   when mouse reporting is enabled and no scrollback exists. Page keys use the same routing for a screenful.
// - Keep 5000 scrollback lines, including the initial 2000. Use xterm.js's default font size until C# sends "font".
// - A pinch sends "move" when the displayed font size in pt changes, then "end" once on release. Preserve the viewport using
//   its first 40 non-whitespace characters.
// - Send "dismiss" once per drag when the finger passes the view's bottom edge.
// - "Read from here" (reading.ts) follows terminal sentences as output arrives without scrolling or zooming the user's view.
//   Two hidden cell tags per sentence (anchor.ts) keep highlights attached through zoom, rotation, keyboard changes and repaints.
import { Terminal, type IMarker, type ITheme } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import { Unicode11Addon } from '@xterm/addon-unicode11';
import { Anchors } from './anchor';
import { Reader, type ReadState } from './reading';
import { PLACEHOLDER, bindTerminal, paragraphAt, type Area, type Cell } from './rows';
import { Selection, type MenuAction, type MenuState, type Point } from './selection';

type HostMessage =
  | { type: 'diag' }
  | { type: 'frames'; data: string[] }
  | { type: 'font'; size: number; lineHeight: number }
  | { type: 'theme'; ground: string; ink: string; cursor: string; mark: string; lightMark: string; selection: string; ansi: ITheme;
      work: string; findMark: string; findLightMark: string; missingText: string; reloadText: string }
  | { type: 'reset'; address: string; fresh?: true; lease?: string }
  | { type: 'page'; direction: 'up' | 'down' }
  | { type: 'bottom' }
  | { type: 'image'; key: string; id: number; url: string; width: number; height: number; cols: number; rows: number }
  | { type: 'imagegrid'; key: string; cols: number; rows: number }
  | { type: 'imagedrop'; key: string }
  | { type: 'imagereloadable'; on: boolean }
  | { type: 'imageloading'; id: number; loading: boolean }
  | { type: 'clearmark' }
  | { type: 'reading'; active: boolean; id: number }
  | { type: 'readstate'; id: number; seq: number; state: ReadState }
  | { type: 'selaction'; action: MenuAction; id?: number; lease?: string }
  | { type: 'find'; text: string }
  | { type: 'findstep'; direction: -1 | 1 }
  | { type: 'findclose' };

type ViewMessage =
  | { type: 'diagstate'; buffer: 'normal' | 'alternate'; viewportY: number; baseY: number; resetRun: number; read: boolean; gesture: string }
  | { type: 'hello' }
  | { type: 'ready'; cols: number; rows: number; width: number; height: number }
  | { type: 'credit'; batches: number }
  | { type: 'grid'; cols: number; rows: number; width: number; height: number; font?: true }
  | { type: 'font'; size: number }
  | { type: 'selectstart' }
  | { type: 'copy'; text: string }
  | { type: 'paste' }
  | { type: 'selmenu'; menu: MenuState }
  | { type: 'tap' }
  | { type: 'readstart'; texts: string[] }
  | { type: 'readadd'; id: number; seq: number; texts: string[] }
  | { type: 'readend'; id: number; truncated?: true }
  | { type: 'readlost'; id: number }
  | { type: 'marktap' | 'readstop'; id: number }
  | { type: 'rowclick'; column: number; row: number; cols: number; rows: number; mouse: string; sgr: boolean }
  | { type: 'swipe'; direction: -1 | 1 }
  | { type: 'wheel'; direction: 'up' | 'down'; lines: number; column: number; row: number }
  | { type: 'modes'; cursor: boolean; paste: boolean; mouse: string; sgr: boolean; kitty: number }
  | { type: 'scrolled'; back: boolean }
  | { type: 'cell'; width: number; height: number }
  | { type: 'imagetap'; id: number }
  | { type: 'imagereload' }
  | { type: 'pinch'; phase: 'move' | 'end'; size: number }
  | { type: 'dismiss' }
  | { type: 'found'; text: string; index: number; total: number };

type DocumentMessage = Extract<ViewMessage, { type: 'hello' | 'ready' | 'credit' | 'grid' | 'cell' }>;
type LeaseMessage = Exclude<ViewMessage, DocumentMessage>;

declare global {
  interface Window {
    HybridWebView: { SendRawMessage(message: string): void };
    remoteReceiveFrames(message: Extract<HostMessage, { type: 'frames' }>): void;
  }
  interface WindowEventMap {
    HybridWebViewMessageReceived: CustomEvent<{ message: string }>;
  }
}

// Pinches use whole font sizes down to 1, where xterm.js still produces a consistent grid (about 600 columns on a phone).
// WKWebView supports this on iOS; Android requires removing the WebView's 8 px text floor in TerminalView.
// xterm.js imposes no lower bound itself.
const FONT_SIZE_MIN = 1;
const FONT_SIZE_MAX = 24;
const LONG_PRESS_MS = 500;
const DOUBLE_TAP_MS = 300;
const DOUBLE_TAP_PX = 24;
const TOUCH_SLOP_PX = 10;
const SWIPE_MIN_PX = 60;
const ANCHOR_CHARS = 40;

const container = document.getElementById('terminal') as HTMLElement;
// Hide scrollbar sliders: touch dragging scrolls and the jump-to-bottom button handles returning to live output.
// Use alpha-zero #rrggbbaa, not "transparent": xterm.js 6.0.0 rejects that keyword and its canvas fallback rejects alpha below
// 255, leaving a slider drawn in foreground color at 20% opacity.
const FIXED: ITheme = {
  scrollbarSliderBackground: '#00000000',
  scrollbarSliderHoverBackground: '#00000000',
  scrollbarSliderActiveBackground: '#00000000',
};
const term = new Terminal({
  // term.unicode (needed by the unicode11 addon) is a proposed API in xterm.js 6.0.0.
  allowProposedApi: true,
  cursorStyle: 'bar',
  disableStdin: true,
  // JetBrains Mono supplies symbols absent from Geist Mono (❯ ✓ ✗ ⚠ ◉ ◯). Remote Shell Symbols Mono
  // (terminal-web/symbols-font.py) supplies one-cell glyphs missing from both, including Claude Code's ⏺ ⎿ ✻ ✢ ⏵ … .
  // Without it, iOS uses color emoji and Android shows missing-glyph boxes.
  fontFamily: '"Geist Mono", "JetBrains Mono", "Remote Shell Symbols Mono", "Symbols Nerd Font Mono", monospace',
  // Claude Code's dark theme uses near-white 24-bit text (248,248,242) on the default background, making history invisible on
  // the app's light background. xterm.js raises low-contrast foreground colors to WCAG AA, as VS Code's terminal does.
  minimumContrastRatio: 4.5,
  // Geist Mono's natural line is 1.30 em: 1.154 gives a 1.5 CSS line height. C# sends 1.115 for 1.45 in phone landscape.
  lineHeight: 1.154,
  scrollback: 5000,
  // Reflow the cursor's wrapped line too; xterm.js otherwise truncates streaming paragraphs or long commands on grid changes.
  reflowCursorLine: true,
  theme: FIXED,
});
const fitAddon = new FitAddon();
term.loadAddon(fitAddon);
term.loadAddon(new Unicode11Addon());
term.unicode.activeVersion = '11';
// Bind cell anchors and shared paragraph helpers to this terminal.
const anchors = new Anchors(term);
bindTerminal(term);

// C# sends theme in response to hello and on every theme change: terminal and placeholder backgrounds, selection and reading
// highlights, ink and work colors, and missing-image labels. Wait for the initial theme before sending ready.
let ground = '';
let markColor = '';
let lightMarkColor = '';
// Find uses work-color highlights at the same two strengths as reading.
let findMarkColor = '';
let findLightMarkColor = '';
let inkColor = '';
let workColor = '';
let missingText = '';
let reloadText = '';
// Offer missing-image reload only when C# reports that the program resends images on redraw (imagereloadable).
let imageReloadable = false;
let themeLoaded: () => void = () => {};
const themed = new Promise<void>((resolve) => {
  themeLoaded = resolve;
});
// Opaque decimal view-generation lease: keep it as a string to avoid JavaScript number rounding.
let lease = '';
let resetRun = 0;
// Associate rendered pixels with the last parsed write, not merely the last enqueued write.
let renderLease = '';
let selectionLease = '';
// Listen before opening the terminal so the theme can arrive early. Drop other messages until the terminal opens, before ready.
window.addEventListener('HybridWebViewMessageReceived', (event) => {
  const message = JSON.parse(event.detail.message) as HostMessage;
  if (message.type === 'theme' || term.element !== undefined) {
    receive(message);
  }
});
window.remoteReceiveFrames = (message) => {
  if (message.type !== 'frames') { throw new Error('Expected a frames message.'); }
  if (term.element !== undefined) { receive(message); }
};
sendDocument({ type: 'hello' });

// Track input modes absent from xterm.js: SGR mouse encoding (CSI ? 1006 h/l) and kitty keyboard flags.
// CSI = flags ; how u sets flags (how 2 adds, 3 removes); CSI > flags u pushes; CSI < n u pops n, falling back to 0.
// Keep separate main/alternate flag stacks, clearing the alternate stack on entry so exiting preserves the main stack.
// RIS (ESC c) clears both stacks and SGR mouse. Return false for mode and RIS handlers so xterm.js still applies them;
// consume kitty sequences here because xterm.js does not implement them.
let sgr = false;
let kittyStacks: Record<'normal' | 'alternate', number[]> = { normal: [0], alternate: [0] };
// Reading excludes the input cursor row while CSI ? 25 h/l shows it. Do not inspect synchronized output
// (CSI ? 2026 h .. l) until it closes, bounded by SYNC_MAX_MS.
let cursorShown = true;
let syncSince = 0;
const SYNC_MAX_MS = 1000;
// After an alternate-screen resize, xterm.js retains a cropped or padded old frame until the next program write.
// Treat it as incomplete until that write, bounded by RESIZE_MAX_MS.
let resizeSince = 0;
const RESIZE_MAX_MS = 2000;
term.parser.registerCsiHandler({ prefix: '?', final: 'h' }, (params) => {
  sgr ||= params.includes(1006);
  if (params.includes(25)) { cursorShown = true; }
  if (params.includes(2026)) { syncSince = performance.now(); }
  return false;
});
term.parser.registerCsiHandler({ prefix: '?', final: 'l' }, (params) => {
  sgr &&= !params.includes(1006);
  if (params.includes(25)) { cursorShown = false; }
  if (params.includes(2026)) { syncSince = 0; }
  return false;
});
term.parser.registerCsiHandler({ prefix: '=', final: 'u' }, (params) => {
  const stack = kittyStacks[term.buffer.active.type];
  const flags = typeof params[0] === 'number' ? params[0] : 0;
  const how = typeof params[1] === 'number' && params[1] > 0 ? params[1] : 1;
  const top = stack.length - 1;
  stack[top] = how === 2 ? stack[top] | flags : how === 3 ? stack[top] & ~flags : flags;
  return true;
});
term.parser.registerCsiHandler({ prefix: '>', final: 'u' }, (params) => {
  kittyStacks[term.buffer.active.type].push(typeof params[0] === 'number' ? params[0] : 0);
  return true;
});
term.parser.registerCsiHandler({ prefix: '<', final: 'u' }, (params) => {
  const stack = kittyStacks[term.buffer.active.type];
  const count = typeof params[0] === 'number' && params[0] > 0 ? params[0] : 1;
  kittyStacks[term.buffer.active.type] = stack.length > count ? stack.slice(0, stack.length - count) : [0];
  return true;
});
term.parser.registerEscHandler({ final: 'c' }, () => {
  sgr = false;
  cursorShown = true;
  syncSince = 0;
  kittyStacks = { normal: [0], alternate: [0] };
  return false;
});
term.buffer.onBufferChange((buffer) => {
  tapRevision++;
  if (buffer.type === 'alternate') {
    kittyStacks.alternate = [0];
  }
  bufferChanged();
  reader.touch();
  repaint();
});

// Last reported JSON input modes and scrollback state, used to suppress duplicate bridge messages.
let modesSent = '';
let scrolledBack = false;

function sendModes(origin: string): void {
  if (origin !== lease) { return; }
  const modes: ViewMessage = {
    type: 'modes',
    cursor: term.modes.applicationCursorKeysMode,
    paste: term.modes.bracketedPasteMode,
    mouse: term.modes.mouseTrackingMode,
    sgr,
    kitty: kittyStacks[term.buffer.active.type][kittyStacks[term.buffer.active.type].length - 1],
  };
  const shown = JSON.stringify(modes);
  if (shown !== modesSent) {
    tapRevision++;
    modesSent = shown;
    send(modes, origin);
  }
}

function sendScrolled(origin: string): void {
  if (origin !== lease) { return; }
  const back = term.buffer.active.viewportY < term.buffer.active.baseY;
  if (back !== scrolledBack) {
    scrolledBack = back;
    send({ type: 'scrolled', back }, origin);
  }
}

// C# parses kitty graphics before forwarding bytes to xterm.js. Render images through two placement modes:
// - Direct placement uses OSC 5113 "p;id;cols;rows;x;y;w;h" at the image's top-left cursor cell. x/y/w/h specify a source
//   rectangle in image pixels; zero w/h use the remainder. C# also advances the cursor. "d;id" deletes an image; id 0 deletes all.
//   Track the first row with a marker, follow scrollback, and draw on the original buffer after writes and scrolls
//   (showPlacements). Do not use xterm.js decorations: they disappear on the alternate screen used by full-screen programs like pi.
// - Unicode placeholders, used by Claude Code, are U+10EEEE cells whose foreground is the image ID (palette below 256, otherwise
//   24-bit RGB). Kitty-table diacritics encode image row and column; omitted values follow the cell to the left.
//   After writes and scrolls, cover visible placeholders with the corresponding image slice on the terminal background
//   so missing-glyph boxes remain hidden (showPlaceholders).
// An "image" message carries a thumbnail data URL, pixel size and placeholder grid, before or after its cells arrive.
// Transfer once per page lifetime per "{address}#{id}" key; convert once to a Blob URL shared by all elements. imagegrid updates
// the grid; imagedrop revokes the URL when C#'s LRU evicts it. reset only switches lookup addresses, retaining earlier panes' images.
// An image tap requests the full image via imagetap. Missing placeholders show loading while bytes arrive or reload is pending,
// otherwise a broken-image box. If imagereloadable is true, tapping a missing image requests redraw via imagereload.
type Picture = { url: string; width: number; height: number; cols: number; rows: number };
const pictures = new Map<string, Picture>();
// Resolve image IDs within the stream address supplied by C#'s reset message.
let currentAddress = '';

function pictureOf(id: number): Picture | undefined {
  return pictures.get(`${currentAddress}#${id}`);
}

// Convert a PNG data URL into a shared Blob URL.
function blobUrlOf(url: string): string {
  const text = atob(url.slice(url.indexOf(',') + 1));
  const bytes = new Uint8Array(text.length);
  for (let i = 0; i < text.length; i++) {
    bytes[i] = text.charCodeAt(i);
  }
  return URL.createObjectURL(new Blob([bytes], { type: 'image/png' }));
}

// screenStamp changes on writes, scrolling, resize/reflow, buffer switches, reset, theme and font changes.
// bufferStamp changes only when lines change (write, reflow, buffer switch, reset), so cached find hits survive scrolling.
// Each overlay records the stamps it last drew and skips unchanged frames.
let screenStamp = 0;
let bufferStamp = 0;
let picturesStamp = 0;

function bufferChanged(): void {
  bufferStamp++;
  screenStamp++;
}

// Reuse overlay elements by key. Change cssText only when styles differ and rebuild children only when content differs.
// Remove obsolete elements; disambiguate duplicate keys by their order in the pass.
type Keyed = { key: string; css: string; content?: string; fill?: (element: HTMLElement) => void };

function placeKeyed(pool: Map<string, HTMLElement>, wanted: Keyed[], parent: HTMLElement): void {
  const kept = new Set<string>();
  for (const item of wanted) {
    const key = uniqueKey(kept, item.key);
    let element = pool.get(key);
    if (element === undefined) {
      element = document.createElement('div');
      pool.set(key, element);
    }
    if (element.dataset.css !== item.css) {
      element.dataset.css = item.css;
      element.style.cssText = item.css;
    }
    const content = item.content ?? '';
    if (element.dataset.content !== content) {
      element.dataset.content = content;
      element.replaceChildren();
      item.fill?.(element);
    }
    if (element.parentElement !== parent) {
      parent.appendChild(element);
    }
  }
  for (const [key, element] of pool) {
    if (!kept.has(key)) {
      element.remove();
      pool.delete(key);
    }
  }
}

// Reserve a unique key for this pass, appending a sequence number to duplicates.
function uniqueKey(kept: Set<string>, wanted: string): string {
  let key = wanted;
  for (let n = 1; kept.has(key); n++) {
    key = `${wanted}#${n}`;
  }
  kept.add(key);
  return key;
}

function clearKeyed(pool: Map<string, HTMLElement>): void {
  for (const element of pool.values()) {
    element.remove();
  }
  pool.clear();
}
// Track images arriving via imageloading and tap-requested reloads until receipt or RELOAD_QUIET_MS without redraw output.
const loadingImages = new Set<number>();
const reloading = new Set<number>();
let reloadTimer: number | undefined;
const RELOAD_QUIET_MS = 1500;
// Use the same loading spinner as herdr-app and the native agent list.
const spin = document.createElement('style');
spin.textContent = '@keyframes image-spin { to { transform: rotate(360deg); } }';
document.head.appendChild(spin);
const SPIN_MS = 900;
// Detect placeholder row/column combining marks.
const COMBINING = /\p{M}/u;
// Direct placements retain image ID, first-row marker, first column, cell dimensions, buffer, and source pixel rectangle.
let placements: { id: number; marker: IMarker; x: number; cols: number; rows: number; alternate: boolean;
  source: { x: number; y: number; w: number; h: number } }[] = [];
let placementMarks: { id: number; element: HTMLElement }[] = [];
// Cache rendered placements by image ID, marker row and first column, plus their last drawn stamps.
const placementPool = new Map<string, HTMLElement>();
let placementsStamp = 0;
let placementsDrawn = '';
// kitty's row/column diacritics (rowcolumn-diacritics.txt): a diacritic's index in this table is the row or column number.
const DIACRITICS = new Map([
  0x305, 0x30d, 0x30e, 0x310, 0x312, 0x33d, 0x33e, 0x33f, 0x346, 0x34a, 0x34b, 0x34c, 0x350, 0x351, 0x352, 0x357, 0x35b, 0x363,
  0x364, 0x365, 0x366, 0x367, 0x368, 0x369, 0x36a, 0x36b, 0x36c, 0x36d, 0x36e, 0x36f, 0x483, 0x484, 0x485, 0x486, 0x487, 0x592,
  0x593, 0x594, 0x595, 0x597, 0x598, 0x599, 0x59c, 0x59d, 0x59e, 0x59f, 0x5a0, 0x5a1, 0x5a8, 0x5a9, 0x5ab, 0x5ac, 0x5af, 0x5c4,
  0x610, 0x611, 0x612, 0x613, 0x614, 0x615, 0x616, 0x617, 0x657, 0x658, 0x659, 0x65a, 0x65b, 0x65d, 0x65e, 0x6d6, 0x6d7, 0x6d8,
  0x6d9, 0x6da, 0x6db, 0x6dc, 0x6df, 0x6e0, 0x6e1, 0x6e2, 0x6e4, 0x6e7, 0x6e8, 0x6eb, 0x6ec, 0x730, 0x732, 0x733, 0x735, 0x736,
  0x73a, 0x73d, 0x73f, 0x740, 0x741, 0x743, 0x745, 0x747, 0x749, 0x74a, 0x7eb, 0x7ec, 0x7ed, 0x7ee, 0x7ef, 0x7f0, 0x7f1, 0x7f3,
  0x816, 0x817, 0x818, 0x819, 0x81b, 0x81c, 0x81d, 0x81e, 0x81f, 0x820, 0x821, 0x822, 0x823, 0x825, 0x826, 0x827, 0x829, 0x82a,
  0x82b, 0x82c, 0x82d, 0x951, 0x953, 0x954, 0xf82, 0xf83, 0xf86, 0xf87, 0x135d, 0x135e, 0x135f, 0x17dd, 0x193a, 0x1a17, 0x1a75,
  0x1a76, 0x1a77, 0x1a78, 0x1a79, 0x1a7a, 0x1a7b, 0x1a7c, 0x1b6b, 0x1b6d, 0x1b6e, 0x1b6f, 0x1b70, 0x1b71, 0x1b72, 0x1b73, 0x1cd0,
  0x1cd1, 0x1cd2, 0x1cda, 0x1cdb, 0x1ce0, 0x1dc0, 0x1dc1, 0x1dc3, 0x1dc4, 0x1dc5, 0x1dc6, 0x1dc7, 0x1dc8, 0x1dc9, 0x1dcb, 0x1dcc,
  0x1dd1, 0x1dd2, 0x1dd3, 0x1dd4, 0x1dd5, 0x1dd6, 0x1dd7, 0x1dd8, 0x1dd9, 0x1dda, 0x1ddb, 0x1ddc, 0x1ddd, 0x1dde, 0x1ddf, 0x1de0,
  0x1de1, 0x1de2, 0x1de3, 0x1de4, 0x1de5, 0x1de6, 0x1dfe, 0x20d0, 0x20d1, 0x20d4, 0x20d5, 0x20d6, 0x20d7, 0x20db, 0x20dc, 0x20e1,
  0x20e7, 0x20e9, 0x20f0, 0x2cef, 0x2cf0, 0x2cf1, 0x2de0, 0x2de1, 0x2de2, 0x2de3, 0x2de4, 0x2de5, 0x2de6, 0x2de7, 0x2de8, 0x2de9,
  0x2dea, 0x2deb, 0x2dec, 0x2ded, 0x2dee, 0x2def, 0x2df0, 0x2df1, 0x2df2, 0x2df3, 0x2df4, 0x2df5, 0x2df6, 0x2df7, 0x2df8, 0x2df9,
  0x2dfa, 0x2dfb, 0x2dfc, 0x2dfd, 0x2dfe, 0x2dff, 0xa66f, 0xa67c, 0xa67d, 0xa6f0, 0xa6f1, 0xa8e0, 0xa8e1, 0xa8e2, 0xa8e3, 0xa8e4,
  0xa8e5, 0xa8e6, 0xa8e7, 0xa8e8, 0xa8e9, 0xa8ea, 0xa8eb, 0xa8ec, 0xa8ed, 0xa8ee, 0xa8ef, 0xa8f0, 0xa8f1, 0xaab0, 0xaab2, 0xaab3,
  0xaab7, 0xaab8, 0xaabe, 0xaabf, 0xaac1, 0xfe20, 0xfe21, 0xfe22, 0xfe23, 0xfe24, 0xfe25, 0xfe26, 0x10a0f, 0x10a38, 0x1d185,
  0x1d186, 0x1d187, 0x1d188, 0x1d189, 0x1d1aa, 0x1d1ab, 0x1d1ac, 0x1d1ad, 0x1d242, 0x1d243, 0x1d244,
].map((code, index) => [code, index]));
// Cache placeholder elements by image ID, first buffer row and first column. Keep cell rectangles separately for hit-testing.
const placeholderPool = new Map<string, HTMLElement>();
let placeholderRuns: { id: number; top: number; bottom: number; left: number; right: number }[] = [];
let placeholdersDrawn = '';

// Merge horizontal placeholder runs with matching image, consecutive image rows and identical columns into rectangles.
// One element per rectangle avoids internal seams at fractional zoom. Fit the image into its full grid, centered as kitty does,
// then offset to this rectangle and paint the terminal background beneath it.
function showPlaceholders(): void {
  const screen = container.querySelector('.xterm-screen') as HTMLElement | null;
  if (screen === null) {
    return;
  }
  // Loading and reloading state changes also advance picturesStamp.
  const drawn = `${screenStamp}:${picturesStamp}`;
  if (drawn === placeholdersDrawn) {
    return;
  }
  placeholdersDrawn = drawn;
  placeholderRuns = [];
  const buffer = term.buffer.active;
  const cellWidth = screen.clientWidth / term.cols;
  const cellHeight = screen.clientHeight / term.rows;
  // Build rectangles from image coordinates, cell bounds and trailing blank coverage.
  const rectangles: Rectangle[] = [];
  for (let row = 0; row < term.rows; row++) {
    const line = buffer.getLine(buffer.viewportY + row);
    if (line === undefined || !line.translateToString().includes(PLACEHOLDER)) {
      continue;
    }
    let run: { id: number; imageRow: number; firstColumn: number; left: number; right: number } | null = null;
    let previous: { id: number; imageRow: number; column: number } | null = null;
    for (let x = 0; x <= term.cols; x++) {
      const cell = x < term.cols ? line.getCell(x) : undefined;
      const chars = cell?.getChars() ?? '';
      const id = cell === undefined || !chars.startsWith(PLACEHOLDER) ? -1
        : cell.isFgRGB() || cell.isFgPalette() ? cell.getFgColor() : 0;
      const marks = [...chars].slice(1).map((mark) => DIACRITICS.get(mark.codePointAt(0) ?? 0));
      const imageRow = marks[0] ?? (previous?.id === id ? previous.imageRow : 0);
      const column = marks[1] ?? (previous?.id === id && previous.imageRow === imageRow ? previous.column + 1 : 0);
      if (run !== null && (id !== run.id || imageRow !== run.imageRow || column !== run.firstColumn + (x - run.left))) {
        // U+10EEEE has no glyph here; its fallback box overflows a cell. Cover trailing blank cells up to the next visible character
        // with the background too, hiding that overflow.
        let covered = run.right;
        while (covered < term.cols && (line.getCell(covered)?.getChars() ?? '').trim() === '') {
          covered++;
        }
        const ended = run;
        const above = rectangles.find((rectangle) => rectangle.id === ended.id && rectangle.bottom === row - 1 && rectangle.left === ended.left
          && rectangle.right === ended.right && rectangle.firstColumn === ended.firstColumn
          && rectangle.imageRow + (row - rectangle.top) === ended.imageRow);
        if (above !== undefined) {
          above.bottom = row;
          above.covered = Math.min(above.covered, covered);
        } else {
          rectangles.push({ ...ended, top: row, bottom: row, covered });
        }
        run = null;
      }
      if (id >= 0) {
        run ??= { id, imageRow, firstColumn: column, left: x, right: x };
        run.right = x + 1;
        previous = { id, imageRow, column };
      } else {
        previous = null;
      }
    }
  }
  // Most frames contain no placeholders; clear old elements without sizing or drawing new ones.
  if (rectangles.length === 0) {
    placeKeyed(placeholderPool, [], screen);
    return;
  }
  placeKeyed(placeholderPool, placeholderItems(rectangles, cellWidth, cellHeight), screen);
}

// xterm's DOM renderer applies per-cell letter spacing to a placeholder run, but WebKit spaces every code point
// (U+10EEEE plus two diacritics). On iOS this collapses the span to zero width and shifts its fallback boxes and later spans left.
// Set the actual cell width using non-combining code points and hide the glyphs. Leave already-correct spans unchanged.
function sizePlaceholderSpans(cellWidth: number, rows: Iterable<Element>): void {
  for (const rowElement of rows) {
    for (const span of rowElement.children) {
      const text = span.textContent ?? '';
      if (!text.includes(PLACEHOLDER)) {
        continue;
      }
      const cells = [...text].filter((ch) => !COMBINING.test(ch)).length;
      const width = `${cells * cellWidth}px`;
      if ((span as HTMLElement).style.visibility === 'hidden' && (span as HTMLElement).style.width === width) {
        continue;
      }
      (span as HTMLElement).style.cssText += `;display:inline-block;width:${width};letter-spacing:0;overflow:hidden;`
        + 'visibility:hidden;white-space:pre;vertical-align:top';
    }
  }
}

type Rectangle = { id: number; imageRow: number; firstColumn: number; top: number; bottom: number; left: number; right: number; covered: number };

// Build keyed rectangle overlays and retain their hit-test bounds in placeholderRuns.
function placeholderItems(rectangles: Rectangle[], cellWidth: number, cellHeight: number): Keyed[] {
  const buffer = term.buffer.active;
  const wanted: Keyed[] = [];
  // If a floating element splits an image, show loading or missing status only in its tallest rectangle; cover the others.
  const tallest = new Map<number, Rectangle>();
  for (const rectangle of rectangles) {
    const shown = tallest.get(rectangle.id);
    if (shown === undefined || rectangle.bottom - rectangle.top > shown.bottom - shown.top) {
      tallest.set(rectangle.id, rectangle);
    }
  }
  for (const rectangle of rectangles) {
    const picture = pictureOf(rectangle.id);
    const item: Keyed = {
      key: `${rectangle.id}:${buffer.viewportY + rectangle.top}:${rectangle.left}`,
      css: `position:absolute;pointer-events:none;z-index:5;background-color:${ground};left:${rectangle.left * cellWidth}px;`
        + `top:${rectangle.top * cellHeight}px;width:${(rectangle.covered - rectangle.left) * cellWidth}px;`
        + `height:${(rectangle.bottom - rectangle.top + 1) * cellHeight}px;box-sizing:border-box;overflow:hidden`,
    };
    if (picture === undefined && (tallest.get(rectangle.id) !== rectangle || rectangle.bottom - rectangle.top < 2)) {
      // Cover only: status belongs to another piece, or fewer than three visible rows would clip its label.
    } else if (picture === undefined && (loadingImages.has(rectangle.id) || reloading.has(rectangle.id))) {
      // Center the spinner on the background. Retained elements keep spinning; new ones use a clock-based negative delay
      // so moving an image between rectangles does not restart the animation.
      item.css += ';display:flex;align-items:center;justify-content:center';
      item.content = `loading:${workColor}`;
      item.fill = (element) => element.appendChild(spinnerOf());
    } else if (picture === undefined) {
      // Show a dashed broken-image box with the reason and reload instructions only when redraw can resend the image.
      const text = withAlpha(inkColor, 0.6);
      item.css += `;border:1px dashed ${withAlpha(inkColor, 0.3)};display:flex;flex-direction:column;align-items:center;`
        + `justify-content:center;gap:2px;padding:2px 4px;color:${text};font:12px -apple-system, system-ui, sans-serif;`
        + 'line-height:1.25;text-align:center;white-space:normal;overflow-wrap:anywhere';
      const lines = imageReloadable ? [missingText, reloadText] : [missingText];
      item.content = `missing:${text}:${lines.join('\n')}`;
      item.fill = (element) => fillMissing(element, text, lines);
    } else if (picture.cols > 0 && picture.rows > 0) {
      const scale = Math.min(picture.cols * cellWidth / picture.width, picture.rows * cellHeight / picture.height);
      const drawnWidth = picture.width * scale;
      const drawnHeight = picture.height * scale;
      const offsetX = (picture.cols * cellWidth - drawnWidth) / 2 - rectangle.firstColumn * cellWidth;
      const offsetY = (picture.rows * cellHeight - drawnHeight) / 2 - rectangle.imageRow * cellHeight;
      item.css += `;background-image:url("${picture.url}");background-repeat:no-repeat;`
        + `background-size:${drawnWidth}px ${drawnHeight}px;background-position:${offsetX}px ${offsetY}px`;
    }
    wanted.push(item);
    // Hit-test the full visible rectangle, including covered trailing blanks.
    placeholderRuns.push({ id: rectangle.id, top: rectangle.top, bottom: rectangle.bottom, left: rectangle.left, right: rectangle.covered });
  }
  return wanted;
}

// Match herdr-app's spinner with a clock-aligned animation phase.
function spinnerOf(): HTMLElement {
  const spinner = document.createElement('div');
  spinner.style.cssText = `flex:none;width:16px;height:16px;box-sizing:border-box;border-radius:50%;`
    + `border:2px solid ${withAlpha(workColor, 0.24)};border-top-color:${withAlpha(workColor, 1)};`
    + `animation:image-spin ${SPIN_MS}ms linear -${performance.now() % SPIN_MS}ms infinite`;
  return spinner;
}

function fillMissing(element: HTMLElement, text: string, lines: string[]): void {
  element.innerHTML = `<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="${text}" stroke-width="1.5" `
    + 'stroke-linecap="round" stroke-linejoin="round" style="flex:none"><path d="M12 4H5a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h5"/>'
    + '<path d="M15 4h4a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-6"/><path d="M14 4l-3 6 3 3-2 7"/><path d="M3 16l4-4 3 3"/></svg>';
  for (const line of lines) {
    const label = document.createElement('div');
    label.textContent = line;
    element.appendChild(label);
  }
}

// Apply opacity to theme colors received as #rrggbb or #rrggbbaa.
function withAlpha(hex: string, alpha: number): string {
  const value = parseInt(hex.slice(1, 7), 16);
  return `rgba(${(value >> 16) & 255},${(value >> 8) & 255},${value & 255},${alpha})`;
}

// After RELOAD_QUIET_MS without redraw output, mark still-missing requested images as unavailable again.
function waitForReload(): void {
  const origin = lease;
  window.clearTimeout(reloadTimer);
  reloadTimer = window.setTimeout(() => {
    if (origin !== lease) { return; }
    reloadTimer = undefined;
    reloading.clear();
    picturesStamp++;
    showPlaceholders();
  }, RELOAD_QUIET_MS);
}

term.parser.registerOscHandler(5113, (data) => {
  const [kind, id, cols, rows, x, y, w, h] = data.split(';');
  if (kind === 'd') {
    placements = placements.filter((placement) => {
      const gone = id === '0' || placement.id === Number(id);
      if (gone) {
        placement.marker.dispose();
      }
      return !gone;
    });
    placementsStamp++;
    showPlacements();
    return true;
  }
  const marker = term.registerMarker(0);
  if (marker !== undefined) {
    placements.push({ id: Number(id), marker, x: term.buffer.active.cursorX, cols: Math.max(1, Number(cols)), rows: Math.max(1, Number(rows)),
      alternate: term.buffer.active.type === 'alternate', source: { x: Number(x) || 0, y: Number(y) || 0, w: Number(w) || 0, h: Number(h) || 0 } });
    placementsStamp++;
    showPlacements();
  }
  return true;
});

// Draw visible direct placements on their original buffer, stretching the source rectangle over their cells (kitty c=/r=)
// and clipping to screen bounds. Skip images not yet received. Drop placements whose markers were disposed after scrollback
// trimming or leaving the alternate screen, which clears that buffer.
function showPlacements(): void {
  const screen = container.querySelector('.xterm-screen') as HTMLElement | null;
  const drawn = `${screenStamp}:${picturesStamp}:${placementsStamp}`;
  if (screen === null || drawn === placementsDrawn) {
    return;
  }
  placementsDrawn = drawn;
  placements = placements.filter((placement) => !placement.marker.isDisposed);
  const wanted: (Keyed & { id: number })[] = [];
  const buffer = term.buffer.active;
  const alternate = buffer.type === 'alternate';
  const cellWidth = screen.clientWidth / term.cols;
  const cellHeight = screen.clientHeight / term.rows;
  for (const placement of placements) {
    const picture = pictureOf(placement.id);
    const row = placement.marker.line - buffer.viewportY;
    const top = Math.max(row, 0);
    const bottom = Math.min(row + placement.rows, term.rows);
    const width = Math.min(placement.cols, term.cols - placement.x);
    if (picture === undefined || placement.alternate !== alternate || top >= bottom || width <= 0) {
      continue;
    }
    const sx = placement.source.x;
    const sy = placement.source.y;
    const sw = placement.source.w || picture.width - sx;
    const sh = placement.source.h || picture.height - sy;
    if (sw <= 0 || sh <= 0) {
      continue;
    }
    const scaleX = placement.cols * cellWidth / sw;
    const scaleY = placement.rows * cellHeight / sh;
    wanted.push({
      id: placement.id,
      key: `${placement.id}:${placement.marker.line}:${placement.x}`,
      css: `position:absolute;pointer-events:none;z-index:5;left:${placement.x * cellWidth}px;top:${top * cellHeight}px;`
        + `width:${width * cellWidth}px;height:${(bottom - top) * cellHeight}px;background-image:url("${picture.url}");`
        + `background-repeat:no-repeat;background-size:${picture.width * scaleX}px ${picture.height * scaleY}px;`
        + `background-position:${-sx * scaleX}px ${-sy * scaleY + (row - top) * cellHeight}px`,
    });
  }
  placeKeyed(placementPool, wanted, screen);
  // Map each drawn element back to the image ID in its key for tap handling.
  placementMarks = [...placementPool].map(([key, element]) => ({ id: Number(key.split(':')[0]), element }));
}

// Report changed cell dimensions in device pixels; C# uses them to size images without explicit dimensions.
let cellSent = '';
function sendCell(): void {
  const screen = container.querySelector('.xterm-screen') as HTMLElement | null;
  if (screen === null || term.cols === 0 || term.rows === 0) {
    return;
  }
  const cell: ViewMessage = {
    type: 'cell',
    width: Math.max(1, Math.round(screen.clientWidth / term.cols * window.devicePixelRatio)),
    height: Math.max(1, Math.round(screen.clientHeight / term.rows * window.devicePixelRatio)),
  };
  const shown = JSON.stringify(cell);
  if (shown !== cellSent) {
    cellSent = shown;
    sendDocument(cell);
  }
}

// Scroll local history (negative means older), or send wheel steps at the target cell on an alternate screen with mouse reporting.
// The alternate screen has no local scrollback.
function scrollLines(lines: number, steps: number, column: number, row: number, origin: string): void {
  if (origin !== lease) { return; }
  renderLease = origin;
  if (term.buffer.active.type === 'alternate' && term.modes.mouseTrackingMode !== 'none') {
    queueWheel(lines < 0 ? 'up' : 'down', steps, column, row, origin);
  } else {
    term.scrollLines(lines);
  }
}

// Accumulate wheel steps by direction and send at most once per animation frame at the last cell.
// Flush accumulated steps before a direction change.
let pendingWheel: { direction: 'up' | 'down'; lines: number; column: number; row: number; origin: string } | null = null;
let wheelFrame = 0;

function queueWheel(direction: 'up' | 'down', steps: number, column: number, row: number, origin: string): void {
  if (pendingWheel !== null && (pendingWheel.direction !== direction || pendingWheel.origin !== origin)) {
    flushWheel();
  }
  pendingWheel = { direction, lines: (pendingWheel?.lines ?? 0) + steps, column, row, origin };
  if (wheelFrame === 0) {
    wheelFrame = window.requestAnimationFrame(() => { if (origin === lease) { flushWheel(); } });
  }
}

function flushWheel(): void {
  window.cancelAnimationFrame(wheelFrame);
  wheelFrame = 0;
  const wheel = pendingWheel;
  pendingWheel = null;
  if (wheel !== null) {
    const { origin, ...fields } = wheel;
    send({ type: 'wheel', ...fields }, origin);
  }
}

// Last ready/grid report: terminal columns and rows, plus #terminal's border box including padding in whole CSS pixels.
let grid = { cols: 0, rows: 0, width: 0, height: 0 };

function gridNow(): { cols: number; rows: number; width: number; height: number } {
  const box = container.getBoundingClientRect();
  return { cols: term.cols, rows: term.rows, width: Math.round(box.width), height: Math.round(box.height) };
}
let gesture = {
  lease: '',
  readingId: 0,
  revision: 0,
  startTime: 0,
  startX: 0,
  startY: 0,
  lastY: 0,
  moved: false,
  twoFingers: false,
  startMidX: 0,
  startMidY: 0,
  lastMidX: 0,
  lastMidY: 0,
  startDistance: 0,
  startFontSize: 0,
  scrollRemainder: 0,
  // Send dismiss at most once after this drag crosses the bottom edge.
  dismissed: false,
  // The first movement past the threshold fixes the axis: x changes panes, y scrolls. Track the last x position too.
  axis: '' as '' | 'x' | 'y',
  lastX: 0,
  // Route the touch to selection (initial hold or handle), or track a touch starting outside it.
  // An outside tap clears selection; scrolling or pinching preserves it.
  owner: '' as '' | 'selection' | 'outside',
  // Last displayed pinch size, initially the starting size, and whether a move message was sent.
  pinchShown: 0,
  pinchSent: false,
  // Current preview scale, initially 1.
  pinchScale: 1,
};
// Retain a short one-finger tap's release time and start point until a double tap consumes it or another gesture ends.
let lastTap: { time: number; x: number; y: number; revision: number; deliver: () => void } | null = null;
let tapRevision = 0;
// Single taps wait for double-tap arbitration; a distant second touch delivers the first instead of losing it.
let tapTimer: number | undefined;
// Start selection after a stationary LONG_PRESS_MS hold; movement, a second finger or early release cancels the timer.
let pressTimer: number | undefined;
const selection = new Selection({
  term,
  anchors,
  revision: () => bufferStamp,
  cellAt,
  paragraphAt,
  copy: (text) => send({ type: 'copy', text }, selectionLease),
  paste: () => send({ type: 'paste' }, selectionLease),
  // Use the selection's proven head and tail spans as the exact reading bounds.
  read: (head, tail) => { reader.readSelection(head, tail); },
  readFrom: (head) => { reader.readFrom(head); },
  context: (point) => {
    const row = point.row - term.buffer.active.viewportY;
    const box = container.querySelector('.xterm-screen')!.getBoundingClientRect();
    const image = imageAt(box.left + (point.col + 0.5) * box.width / term.cols, box.top + (row + 0.5) * box.height / term.rows);
    return { readable: paragraphAt(point) !== null,
      image: image === undefined ? 'none' : imageAction(image), id: image };
  },
  action: (action, _point, image) => {
    if (image !== undefined) { actOnImage(image, selectionLease); }
  },
  started: () => send({ type: 'selectstart' }, selectionLease),
  menu: (menu) => send({ type: 'selmenu', menu }, selectionLease),
});
// Hold a partial ?2026 frame or alternate-screen resize until the next write or its timeout.
const syncLeft = (): number => {
  const now = performance.now();
  return Math.max(syncSince !== 0 ? SYNC_MAX_MS - (now - syncSince) : 0, resizeSince !== 0 ? RESIZE_MAX_MS - (now - resizeSince) : 0, 0);
};
const syncOpen = (): boolean => syncLeft() > 0;
anchors.held = syncOpen;
// Connect terminal sentence intake, C# speech messages and reading highlights.
const reader = new Reader({
  term,
  anchors,
  post: (message) => send(message as unknown as LeaseMessage, lease),
  lastWriteAt: () => lastWriteAt,
  syncLeft,
  cursorVisible: () => cursorShown,
  // Changes requested during overlay drawing belong to that frame; do not schedule another.
  redraw: () => { readStamp++; if (!drawingOverlays) { repaint(); } },
});
// Hide the selection menu while a scroll or pinch moves the screen.
let screenMoving = false;

function moveScreen(moving: boolean): void {
  if (screenMoving !== moving) {
    screenMoving = moving;
    selection.setMoving(moving);
  }
}

function sendDocument(message: DocumentMessage): void {
  window.HybridWebView.SendRawMessage(JSON.stringify(message));
}

function send(message: LeaseMessage, origin: string): void {
  if (origin.length !== 0) { window.HybridWebView.SendRawMessage(JSON.stringify({ ...message, lease: origin })); }
}

// Fit and report grid or viewport size changes. Always answer C#'s font request with font:true, even when unchanged:
// C# waits for a grid at that font before attaching the stream.
function fitGrid(fontAnswer = false): void {
  fitAddon.fit();
  const now = gridNow();
  if (fontAnswer || now.cols !== grid.cols || now.rows !== grid.rows || now.width !== grid.width || now.height !== grid.height) {
    tapRevision++;
    grid = now;
    sendDocument(fontAnswer ? { type: 'grid', ...grid, font: true } : { type: 'grid', ...grid });
  }
  sendCell();
}

// The native pinch badge displays font size in pt.
function pinchValue(): number {
  return term.options.fontSize as number;
}

// Track whether C# reports an active read and its ID.
let reading = { active: false, id: 0 };
// Timestamp of the last parsed output; sentence intake waits for text to settle.
let lastWriteAt = 0;
// Defer C#'s reset until the new stream's first bytes arrive.
let pendingReset: (() => void) | null = null;
// Pending shared overlay frame; 0 means none.
let overlayFrame = 0;
let overlayLease = '';
// Normal-buffer pinch anchor: top buffer line, its next ANCHOR_CHARS non-whitespace characters, and whether live output is visible.
let pinchAnchor: { line: number; key: string; bottom: boolean } | null = null;
// Current-sentence highlight elements keyed by buffer row and first column. readStamp changes with reading state.
const readPool = new Map<string, HTMLElement>();
// Draw pending-sentence highlights below the current sentence; only the stronger current highlight handles speech taps.
const readLightPool = new Map<string, HTMLElement>();
let readStamp = 0;
let readDrawn = '';
const pulse = document.createElement('style');
pulse.textContent = '@keyframes mark-pulse { from { opacity: 1; } to { opacity: 0.3; } }';
document.head.appendChild(pulse);

// Map page coordinates to viewport-relative cells; return null outside the grid.
function cellAt(clientX: number, clientY: number): { row: number; col: number } | null {
  const screen = container.querySelector('.xterm-screen') as HTMLElement;
  const box = screen.getBoundingClientRect();
  const row = Math.floor((clientY - box.top) / (box.height / term.rows));
  const col = Math.floor((clientX - box.left) / (box.width / term.cols));
  return row < 0 || row >= term.rows || col < 0 || col >= term.cols ? null : { row, col };
}

// Share image hit-testing between the native selection menu and ordinary taps.
function imageAt(x: number, y: number): number | undefined {
  const cell = cellAt(x, y);
  if (cell === null) { return undefined; }
  return placementMarks.find(mark => {
    const rect = mark.element.getBoundingClientRect();
    return x >= rect.left && x < rect.right && y >= rect.top && y < rect.bottom;
  })?.id ?? placeholderRuns.find(run => cell.row >= run.top && cell.row <= run.bottom && cell.col >= run.left && cell.col < run.right)?.id;
}

// Open received images, or reload missing ones only if redraw resends images (imageReloadable).
// Offer no action while loading or when recovery is unsupported.
function imageAction(image: number): 'open' | 'reload' | 'none' {
  if (pictureOf(image) !== undefined) { return 'open'; }
  return imageReloadable && !loadingImages.has(image) && !reloading.has(image) ? 'reload' : 'none';
}

function actOnImage(image: number, origin: string): void {
  const action = imageAction(image);
  if (action === 'open') {
    send({ type: 'imagetap', id: image }, origin);
  } else if (action === 'reload') {
    for (const run of placeholderRuns) {
      if (pictureOf(run.id) === undefined && !loadingImages.has(run.id)) { reloading.add(run.id); }
    }
    picturesStamp++;
    send({ type: 'imagereload' }, origin);
    waitForReload();
    showPlaceholders();
  }
}

// Remove whitespace for block and paragraph comparisons.
function keyOf(text: string): string {
  return text.replace(/\s+/g, '');
}

function markCss(area: Area, color: string, layer = 10): string {
  const screen = container.querySelector('.xterm-screen') as HTMLElement;
  const cellWidth = screen.clientWidth / term.cols;
  const cellHeight = screen.clientHeight / term.rows;
  return `position:absolute;background:${color};border-radius:4px;pointer-events:none;z-index:${layer};left:${area.left * cellWidth}px;`
    + `top:${area.top * cellHeight}px;width:${(area.right - area.left) * cellWidth}px;height:${(area.bottom - area.top + 1) * cellHeight}px`;
}

// One read mark: a row of the sentence being spoken; it pulses while its audio loads, is steady while it plays and dimmer while paused.
function addReadMark(wanted: Keyed[], area: Area, state: ReadState): void {
  let css = markCss(area, markColor);
  css += `;opacity:${state === 'paused' ? '0.5' : '1'}${state === 'waiting' ? ';animation:mark-pulse 1s ease-in-out infinite alternate' : ''}`;
  wanted.push({ key: `${term.buffer.active.viewportY + area.top}:${area.left}`, css });
}

// Draw pending sentences lightly and the current sentence strongly on top, using reader.pending and reader.mark.
// Draw only proven tagged cells, never already-read text. Tapping the strong highlight pauses or resumes speech.
function showRead(): void {
  const drawn = `${screenStamp}:${readStamp}`;
  if (drawn === readDrawn) {
    return;
  }
  readDrawn = drawn;
  const screen = container.querySelector('.xterm-screen') as HTMLElement | null;
  const mark = screen === null ? null : reader.mark();
  const light = screen === null ? [] : reader.pending();
  if (screen === null) {
    clearKeyed(readPool);
    clearKeyed(readLightPool);
    return;
  }
  const top = term.buffer.active.viewportY;
  placeKeyed(readLightPool, light.map((area) => ({ key: `${top + area.top}:${area.left}`, css: markCss(area, lightMarkColor, 9) })), screen);
  const wanted: Keyed[] = [];
  for (const area of mark?.areas ?? []) {
    addReadMark(wanted, area, mark!.state);
  }
  placeKeyed(readPool, wanted, screen);
}

// One shared repaint keeps images, reading/find highlights, selection handles/menu and scrollback state aligned with text.
// xterm.js render events draw overlays immediately in the same frame as text: writes, scrolls, grid/reflow, buffer, theme and font
// changes. Other events request repaint: output, scroll, resize, buffer/theme/font changes, font loading, visibility and reset.
// Coalesce requests into one drawing per frame, and update reading highlights only while reading or clearing existing marks.
function repaint(): void {
  if (overlayFrame !== 0 && overlayLease !== renderLease) {
    window.cancelAnimationFrame(overlayFrame);
    overlayFrame = 0;
  }
  if (overlayFrame === 0) {
    const origin = overlayLease = renderLease;
    overlayFrame = window.requestAnimationFrame(() => drawOverlays(origin));
  }
}
let drawingOverlays = false;
function drawOverlays(origin: string): void {
  if (origin !== lease) { return; }
  window.cancelAnimationFrame(overlayFrame);
  overlayFrame = 0;
  if (term.element === undefined) {
    return;
  }
  showPlacements();
  showPlaceholders();
  // Resolve dirty reading spans against this frame before drawing highlights; do not inspect partial frames.
  drawingOverlays = true;
  try { reader.frame(); } finally { drawingOverlays = false; }
  if (reader.active || readPool.size > 0 || readLightPool.size > 0) {
    showRead();
  }
  if (findKey !== '' || findPool.size > 0) {
    if (findKey !== '') { findFollow(); }
    showFind();
  }
  selection.follow();
  sendScrolled(origin);
}

// Capture the normal-buffer viewport when the pinch starts: its top line and following text, or its live-output position.
function takeAnchor(): void {
  const buffer = term.buffer.active;
  if (buffer.type !== 'normal') {
    pinchAnchor = null;
    return;
  }
  let key = '';
  for (let y = buffer.viewportY; y < buffer.length && key.length < ANCHOR_CHARS; y++) {
    key += keyOf(buffer.getLine(y)?.translateToString(true) ?? '');
  }
  pinchAnchor = { line: buffer.viewportY, key: key.slice(0, ANCHOR_CHARS), bottom: buffer.viewportY >= buffer.baseY };
}

// After pinch reflow, restore live output or the line containing the anchor text nearest the old line.
// The anchor may begin within a row after reflow.
function keepPlace(): void {
  const anchor = pinchAnchor;
  const buffer = term.buffer.active;
  if (anchor === null || buffer.type !== 'normal') {
    return;
  }
  if (anchor.bottom) {
    renderLease = gesture.lease;
    term.scrollToBottom();
    return;
  }
  if (anchor.key === '') {
    return;
  }
  // Record the whitespace-free buffer text and each line's starting offset.
  const starts: number[] = [];
  let full = '';
  for (let y = 0; y < buffer.length; y++) {
    starts.push(full.length);
    full += keyOf(buffer.getLine(y)?.translateToString(true) ?? '');
  }
  let best: number | null = null;
  let line = 0;
  for (let at = full.indexOf(anchor.key); at >= 0; at = full.indexOf(anchor.key, at + 1)) {
    // Find the last line whose starting offset does not exceed this character.
    while (line + 1 < starts.length && starts[line + 1] <= at) {
      line++;
    }
    if (best === null || Math.abs(line - anchor.line) < Math.abs(best - anchor.line)) {
      best = line;
    }
  }
  if (best !== null) {
    anchor.line = best;
    renderLease = gesture.lease;
    term.scrollToLine(best);
  }
}

// Case-insensitive literal find covers the active screen and scrollback, joining wrapped rows into logical lines.
// Start at the newest match; previous/next move older/newer with wraparound and scroll only when the match is not fully visible.
// A marker at the logical line's first row plus a text offset retains the match through output, scrolling and reflow.
// Render visible hits separately in drawOverlays using markCss: current strong, others light, without changing reading or selection.
// Count non-overlapping matches by starting each search after the preceding match.
type FindHit = { line: number; offset: number; areas: Area[] };
// Search text and normalized key (empty when closed), current match, and visible highlight state.
let findText = '';
let findKey = '';
let findCurrent: { marker: IMarker; offset: number } | null = null;
// Debounce text changes by FIND_WAIT_MS for one scan per typing burst. Cache hits at bufferStamp for reuse by navigation
// and highlights until lines change. Key highlight elements by line, offset and row; findStamp follows the current match.
const FIND_WAIT_MS = 150;
let findPending: string | null = null;
let findTimer: number | undefined;
let findKept: { key: string; stamp: number; hits: FindHit[] } | null = null;
const findPool = new Map<string, HTMLElement>();
let findStamp = 0;
let findDrawn = '';
let findSent = '';
let findEpoch = 0;

// When a wide glyph wraps, xterm leaves an empty one-cell gap at the end of the preceding row; that gap is not logical text.
// Use the same rule in findLine and the fast precheck in findHits.
function findGap(y: number): boolean {
  const buffer = term.buffer.active;
  const next = buffer.getLine(y + 1);
  if (next === undefined || !next.isWrapped || next.getCell(0)?.getWidth() !== 2) {
    return false;
  }
  const last = buffer.getLine(y)?.getCell(term.cols - 1);
  return last !== undefined && last.getWidth() === 1 && last.getChars() === '';
}

// Lowercase the key, precheck and cell text by code point, without context. Whole-string lowercasing treats a final sigma
// differently from the same character lowercased in an individual cell.
function foldFind(text: string): string {
  let folded = '';
  for (const point of text) { folded += point.toLowerCase(); }
  return folded;
}

// Read a logical line and its wrapped continuations as lowercase text. Map every UTF-16 code unit to its source cell
// and return the first row after the logical line.

function findLine(start: number): { text: string; cells: Cell[]; next: number } {
  const buffer = term.buffer.active;
  let text = '';
  const cells: Cell[] = [];
  let y = start;
  do {
    const line = buffer.getLine(y);
    const last = findGap(y) ? term.cols - 1 : term.cols;
    for (let x = 0; line !== undefined && x < last; x++) {
      const cell = line.getCell(x);
      if (cell === undefined || cell.getWidth() === 0) {
        continue;
      }
      const chars = cell.getChars();
      // Treat empty and image-placeholder cells as spaces, matching screenRows.
      const shown = foldFind(chars === '' || chars.startsWith(PLACEHOLDER) ? ' ' : chars);
      for (let i = 0; i < shown.length; i++) {
        cells.push({ row: y, col: x, width: Math.max(1, cell.getWidth()) });
      }
      text += shown;
    }
    y++;
  } while (y < buffer.length && buffer.getLine(y)?.isWrapped === true);
  return { text, cells, next: y };
}

// Find non-overlapping matches in logical lines intersecting [from, to), oldest first.
function findHits(from: number, to: number): FindHit[] {
  const buffer = term.buffer.active;
  const hits: FindHit[] = [];
  if (findKey === '') {
    return hits;
  }
  let y = from;
  while (y > 0 && buffer.getLine(y)?.isWrapped === true) {
    y--;
  }
  while (y < Math.min(to, buffer.length)) {
    // Skip cell-by-cell mapping for lines whose plain text cannot contain the key.
    let plain = buffer.getLine(y)?.translateToString(false, 0, findGap(y) ? term.cols - 1 : undefined) ?? '';
    let end = y + 1;
    while (end < buffer.length && buffer.getLine(end)?.isWrapped === true) {
      plain += buffer.getLine(end)?.translateToString(false, 0, findGap(end) ? term.cols - 1 : undefined) ?? '';
      end++;
    }
    if (foldFind(plain).includes(findKey)) {
      const line = findLine(y);
      for (let at = line.text.indexOf(findKey); at >= 0; at = line.text.indexOf(findKey, at + findKey.length)) {
        const byRow = new Map<number, Area>();
        for (const cell of line.cells.slice(at, at + findKey.length)) {
          const area = byRow.get(cell.row);
          byRow.set(cell.row, {
            top: cell.row,
            bottom: cell.row,
            left: Math.min(area?.left ?? cell.col, cell.col),
            right: Math.max(area?.right ?? 0, cell.col + cell.width),
          });
        }
        hits.push({ line: y, offset: at, areas: [...byRow.values()] });
      }
    }
    y = end;
  }
  return hits;
}

function isCurrent(hit: FindHit): boolean {
  return findCurrent !== null && !findCurrent.marker.isDisposed && findCurrent.marker.line === hit.line && findCurrent.offset === hit.offset;
}

// Select the indexed match and report its 1-based index and total to C#; report index 0 if none exists.
function findSelect(hits: FindHit[], index: number, origin: string): void {
  if (origin !== lease) { return; }
  findCurrent?.marker.dispose();
  findCurrent = null;
  const hit = hits[index];
  if (hit !== undefined) {
    const buffer = term.buffer.active;
    const marker = term.registerMarker(hit.line - (buffer.baseY + buffer.cursorY));
    findCurrent = marker === undefined ? null : { marker, offset: hit.offset };
    const top = hit.areas[0].top;
    const bottom = hit.areas[hit.areas.length - 1].bottom;
    if (top < buffer.viewportY || bottom >= buffer.viewportY + term.rows) {
      renderLease = origin;
      term.scrollToLine(Math.max(0, top - Math.floor(term.rows / 3)));
    }
  }
  findSent = `${hit === undefined ? 0 : index + 1}/${hits.length}`;
  send({ type: 'found', text: findText, index: hit === undefined ? 0 : index + 1, total: hits.length }, origin);
  findStamp++;
  repaint();
}

// Reuse whole-buffer hits while the key and buffer are unchanged; otherwise rescan.
function allHits(): FindHit[] {
  if (findKept !== null && findKept.key === findKey && findKept.stamp === bufferStamp) {
    return findKept.hits;
  }
  findKept = { key: findKey, stamp: bufferStamp, hits: findHits(0, term.buffer.active.length) };
  return findKept.hits;
}

function find(text: string): void {
  const origin = lease;
  findPending = text;
  window.clearTimeout(findTimer);
  findTimer = window.setTimeout(() => runFind(origin), FIND_WAIT_MS);
}

// Run a pending debounced search now and select the newest match; navigation does not wait for the timer.
function runFind(origin: string): void {
  if (origin !== lease) { return; }
  window.clearTimeout(findTimer);
  findTimer = undefined;
  const text = findPending;
  findPending = null;
  if (text !== null) {
    findText = text;
    findKey = foldFind(text);
    const hits = allHits();
    findSelect(hits, hits.length - 1, origin);
  }
}

// Move older (-1) or newer (+1), wrapping; select the newest match if the previous one disappeared.
function findStep(direction: -1 | 1): void {
  const origin = lease;
  runFind(origin);
  const hits = allHits();
  const at = hits.findIndex(isCurrent);
  findSelect(hits, at < 0 ? hits.length - 1 : (at + direction + hits.length) % hits.length, origin);
}

function findClose(): void {
  window.clearTimeout(findTimer);
  findTimer = undefined;
  findPending = null;
  findKept = null;
  findText = '';
  findKey = '';
  findSent = '';
  findCount?.anchor?.dispose();
  findCount = null;
  findCurrent?.marker.dispose();
  findCurrent = null;
  findStamp++;
  repaint();
}

// Keep find counts current without rescanning settled normal-buffer scrollback. Add newly settled lines and drop trimmed ones;
// rescan live rows once per changed drawing. All alternate-screen rows are live because that buffer has no scrollback.
// Store settled hit rows in epoch coordinates (row + trimmed count); a marker at the first live line measures trimming.
type FindCount = {
  key: string; epoch: number; cols: number; type: string; stamp: number;
  anchor: IMarker | null; endEpoch: number; hits: { line: number; offset: number }[]; drop: number; live: FindHit[];
};
let findCount: FindCount | null = null;
let findLastLine = 0;

function findFollow(): void {
  const buffer = term.buffer.active;
  let c = findCount;
  if (c !== null && c.key === findKey && c.epoch === findEpoch && c.cols === term.cols && c.type === buffer.type && c.stamp === bufferStamp) {
    return;
  }
  const seed = findKept !== null && findKept.key === findKey && findKept.stamp === bufferStamp ? findKept.hits : null;
  if (c === null || c.key !== findKey || c.epoch !== findEpoch || c.cols !== term.cols || c.type !== buffer.type) {
    c?.anchor?.dispose();
    if (c !== null && c.type !== buffer.type) {
      findCurrent?.marker.dispose();
      findCurrent = null;
    }
    c = findCount = { key: findKey, epoch: findEpoch, cols: term.cols, type: buffer.type, stamp: -1, anchor: null, endEpoch: 0, hits: [], drop: 0, live: [] };
  }
  // Start live scanning at the logical line containing the first screen row; the alternate screen has no settled rows.
  let settledEnd = buffer.type === 'normal' ? buffer.baseY : 0;
  while (settledEnd > 0 && buffer.getLine(settledEnd)?.isWrapped === true) {
    settledEnd--;
  }
  let cut = c.anchor === null ? 0 : c.endEpoch - c.anchor.line;
  const settledNow = c.anchor === null ? 0 : c.anchor.line;
  if (c.anchor !== null && (c.anchor.isDisposed || settledEnd < settledNow || (cut > 0 && buffer.getLine(0)?.isWrapped === true))) {
    // Recount if settled history is gone, scrollback shrank, or trimming left only part of the first logical line.
    c.anchor.dispose();
    c.anchor = null;
    c.hits = [];
    c.drop = 0;
    cut = 0;
  }
  let from = c.anchor === null ? 0 : c.anchor.line;
  if (c.anchor !== null && cut > 0) {
    while (c.drop < c.hits.length && c.hits[c.drop].line < cut) {
      c.drop++;
    }
    if (c.drop > 1024 && c.drop * 2 > c.hits.length) {
      c.hits.splice(0, c.drop);
      c.drop = 0;
    }
  }
  if (seed !== null && c.stamp === -1 && c.anchor === null) {
    for (const hit of seed) {
      if (hit.line < settledEnd) {
        c.hits.push({ line: hit.line, offset: hit.offset });
      }
    }
    c.live = seed.filter(hit => hit.line >= settledEnd);
    from = settledEnd;
  } else {
    if (settledEnd > from) {
      for (const hit of findHits(from, settledEnd)) {
        c.hits.push({ line: hit.line + cut, offset: hit.offset });
      }
    }
    c.live = findHits(settledEnd, buffer.length);
  }
  if (settledEnd !== from || c.anchor === null) {
    c.anchor?.dispose();
    c.anchor = settledEnd > 0 ? term.registerMarker(settledEnd - (buffer.baseY + buffer.cursorY)) ?? null : null;
    if (c.anchor === null) {
      c.hits = [];
      c.drop = 0;
      cut = 0;
    }
    c.endEpoch = settledEnd + cut;
  }
  c.stamp = bufferStamp;

  const settled = c.hits.length - c.drop;
  const total = settled + c.live.length;
  const at = (n: number): { line: number; offset: number } => n < settled ? { line: c.hits[c.drop + n].line - cut, offset: c.hits[c.drop + n].offset } : c.live[n - settled];
  // Find the current hit or the first one after its former location.
  let index = 0;
  if (total > 0) {
    if (findCurrent === null) {
      index = total - 1;
    } else if (findCurrent.marker.isDisposed && buffer.type === 'normal') {
      index = 0;
    } else {
      // An alternate-screen redraw can dispose the marker; use its last recorded row.
      const line = findCurrent.marker.isDisposed ? findLastLine : findCurrent.marker.line;
      let low = 0;
      let high = total;
      while (low < high) {
        const middle = (low + high) >> 1;
        const hit = at(middle);
        if (hit.line < line || (hit.line === line && hit.offset < findCurrent.offset)) {
          low = middle + 1;
        } else {
          high = middle;
        }
      }
      index = Math.min(low, total - 1);
    }
    if (findCurrent !== null && !findCurrent.marker.isDisposed) {
      findLastLine = findCurrent.marker.line;
    }
    const hit = at(index);
    if (findCurrent === null || findCurrent.marker.isDisposed || findCurrent.marker.line !== hit.line || findCurrent.offset !== hit.offset) {
      // Retain the nearest surviving match without scrolling when the previous match disappears.
      findCurrent?.marker.dispose();
      const marker = term.registerMarker(hit.line - (buffer.baseY + buffer.cursorY));
      findCurrent = marker === undefined ? null : { marker, offset: hit.offset };
      findLastLine = hit.line;
      findStamp++;
    }
  } else if (findCurrent !== null) {
    findCurrent.marker.dispose();
    findCurrent = null;
    findStamp++;
  }
  const shown = `${total > 0 ? index + 1 : 0}/${total}`;
  if (shown !== findSent) {
    findSent = shown;
    send({ type: 'found', text: findText, index: total > 0 ? index + 1 : 0, total }, lease);
  }
}

// Return hits intersecting [from, to) from a valid cache, otherwise scan only that row range.
function visibleHits(from: number, to: number): FindHit[] {
  if (findKept === null || findKept.key !== findKey || findKept.stamp !== bufferStamp) {
    return findHits(from, to);
  }
  const hits = findKept.hits;
  // Binary-search buffer-ordered hits for the first whose last row reaches from.
  let low = 0;
  let high = hits.length;
  while (low < high) {
    const middle = (low + high) >> 1;
    if (hits[middle].areas[hits[middle].areas.length - 1].bottom < from) {
      low = middle + 1;
    } else {
      high = middle;
    }
  }
  let end = low;
  while (end < hits.length && hits[end].areas[0].top < to) {
    end++;
  }
  return hits.slice(low, end);
}

// Draw visible find highlights. Add a 1.5 px inset work-color border to the current match without changing its size,
// so shape as well as intensity distinguishes it.
function showFind(): void {
  const screen = container.querySelector('.xterm-screen') as HTMLElement | null;
  const drawn = `${screenStamp}:${findStamp}`;
  if (screen === null || drawn === findDrawn) {
    return;
  }
  findDrawn = drawn;
  const top = term.buffer.active.viewportY;
  const wanted: Keyed[] = [];
  for (const hit of visibleHits(top, top + term.rows)) {
    const current = isCurrent(hit);
    for (const area of hit.areas) {
      if (area.top >= top && area.top < top + term.rows) {
        const css = markCss({ ...area, top: area.top - top, bottom: area.bottom - top }, current ? findMarkColor : findLightMarkColor);
        wanted.push({ key: `${hit.line}:${hit.offset}:${area.top}`, css: current ? `${css};box-shadow:inset 0 0 0 1.5px ${workColor}` : css });
      }
    }
  }
  placeKeyed(findPool, wanted, screen);
}

// Coalesce parsed-batch credits within a task using setTimeout(0), not animation frames, so hidden pages keep returning credits.
// C# allows at most two batches in flight, so each message credits one or two.
let pendingCredits = 0;

function creditBatch(): void {
  if (pendingCredits++ === 0) {
    window.setTimeout(() => {
      const batches = pendingCredits;
      pendingCredits = 0;
      sendDocument({ type: 'credit', batches });
    }, 0);
  }
}

function receive(message: HostMessage): void {
  switch (message.type) {
    case 'diag':
      send({ type: 'diagstate', buffer: term.buffer.active.type, viewportY: term.buffer.active.viewportY,
        baseY: term.buffer.active.baseY, resetRun, read: reader.active, gesture: gesture.owner }, lease);
      break;
    case 'frames': {
      const origin = lease;
      // atob returns one character (0-255) per byte; concatenate decoded frames into one ordered byte array.
      const strings = message.data.map(atob);
      let length = 0;
      for (const s of strings) {
        length += s.length;
      }
      const bytes = new Uint8Array(length);
      let at = 0;
      for (const s of strings) {
        for (let i = 0; i < s.length; i++) {
          bytes[at++] = s.charCodeAt(i);
        }
      }
      if (pendingReset !== null) {
        pendingReset();
        pendingReset = null;
      }
      term.write(
        bytes,
        () => {
          renderLease = origin;
          lastWriteAt = performance.now();
          creditBatch();
          if (origin !== lease) { return; }
          sendModes(origin);
          sendScrolled(origin);
          // Restart the reload quiet timer while the program continues redrawing.
          if (reloading.size > 0) {
            waitForReload();
          }
          repaint();
        },
      );
      break;
    }
    case 'font':
      term.options.fontSize = message.size;
      term.options.lineHeight = message.lineHeight;
      fitGrid(true);
      screenStamp++;
      repaint();
      break;
    case 'theme':
      term.options.theme = { ...FIXED, background: message.ground, foreground: message.ink, cursor: message.cursor, selectionBackground: message.selection, ...message.ansi };
      // Match the page and container-padding background to the terminal.
      document.documentElement.style.background = message.ground;
      document.body.style.background = message.ground;
      ground = message.ground;
      markColor = message.mark;
      lightMarkColor = message.lightMark;
      findMarkColor = message.findMark;
      findLightMarkColor = message.findLightMark;
      inkColor = message.ink;
      workColor = message.work;
      missingText = message.missingText;
      reloadText = message.reloadText;
      selection.setTheme(message.cursor);
      themeLoaded();
      screenStamp++;
      repaint();
      break;
    case 'reset': {
      tapRevision++;
      if (message.fresh) {
        if (typeof message.lease !== 'string' || !/^[1-9][0-9]*$/.test(message.lease)) {
          throw new Error('A fresh terminal reset requires an owner lease.');
        }
        selection.clear();
        // Detach any active read when switching panes; keep it until its pane returns.
        reading = { active: false, id: 0 };
        findClose();
        reader.detach();
        readStamp++;
        showRead();
        window.clearTimeout(pressTimer);
        window.clearTimeout(tapTimer);
        pressTimer = tapTimer = undefined;
        lastTap = null;
        window.cancelAnimationFrame(wheelFrame);
        wheelFrame = 0;
        pendingWheel = null;
        gesture.owner = '';
        gesture.lease = '';
        gesture.moved = true;
        gesture.twoFingers = gesture.pinchSent = false;
        gesture.scrollRemainder = 0;
        moveScreen(false);
        pinchAnchor = null;
        const screen = container.querySelector('.xterm-screen') as HTMLElement | null;
        if (screen !== null) { screen.style.transform = screen.style.transformOrigin = ''; }
        lease = message.lease;
      }
      const run = ++resetRun;
      const origin = lease;
      // The new stream's initial paint sets all modes again. C# resets its copies, so resend defaults too.
      // Retain image blobs by key. Switch their lookup address only when clearing the screen, so the old frame keeps its own images.
      // Clear loading state now: C# posts missing images after reset, and image/imageloading messages take effect immediately.
      const address = message.address;
      picturesStamp++;
      loadingImages.clear();
      reloading.clear();
      window.clearTimeout(reloadTimer);
      reloadTimer = undefined;
      // Reset in xterm write order, after old queued bytes and before new-stream bytes posted by C# after reset.
      // Wait for those new bytes before clearing: replace the old frame and write the new initial paint in the same turn,
      // avoiding a blank frame on reconnect or return from the background.
      pendingReset = () => term.write('', () => {
        if (run !== resetRun) { return; }
        renderLease = origin;
        term.reset();
        findEpoch++;
        currentAddress = address;
        picturesStamp++;
        for (const placement of placements) {
          placement.marker.dispose();
        }
        placements = [];
        bufferChanged();
        repaint();
        sgr = false;
        cursorShown = true;
        syncSince = 0;
        // Reset removes tags, markers and frame continuity; reading can recover only by aligning its retained row text.
        reader.reset();
        kittyStacks = { normal: [0], alternate: [0] };
        modesSent = '';
        scrolledBack = false;
        sendModes(origin);
        // A touch scroll between the C# reset and this queued reset may have reported scrollback; clear that state explicitly.
        send({ type: 'scrolled', back: false }, origin);
      });
      break;
    }
    case 'page':
      scrollLines(message.direction === 'up' ? -term.rows : term.rows, Math.ceil(term.rows / 3), Math.floor(term.cols / 2), Math.floor(term.rows / 2), lease);
      break;
    case 'bottom':
      renderLease = lease;
      term.scrollToBottom();
      break;
    case 'image': {
      // Revoke the old Blob URL when the program reuses an image key.
      const older = pictures.get(message.key);
      if (older !== undefined) {
        URL.revokeObjectURL(older.url);
      }
      pictures.set(message.key, { url: blobUrlOf(message.url), width: message.width, height: message.height, cols: message.cols, rows: message.rows });
      loadingImages.delete(message.id);
      reloading.delete(message.id);
      picturesStamp++;
      repaint();
      break;
    }
    case 'imagegrid': {
      const picture = pictures.get(message.key);
      if (picture !== undefined) {
        picture.cols = message.cols;
        picture.rows = message.rows;
        picturesStamp++;
        repaint();
      }
      break;
    }
    case 'imagereloadable':
      if (imageReloadable !== message.on) {
        imageReloadable = message.on;
        picturesStamp++;
        repaint();
      }
      break;
    case 'imagedrop': {
      const picture = pictures.get(message.key);
      if (picture !== undefined) {
        URL.revokeObjectURL(picture.url);
        pictures.delete(message.key);
        picturesStamp++;
        repaint();
      }
      break;
    }
    case 'imageloading':
      if (message.loading) {
        loadingImages.add(message.id);
      } else {
        loadingImages.delete(message.id);
      }
      picturesStamp++;
      repaint();
      break;
    case 'clearmark':
      // Release reading sentences, tags and markers when C# stops or finishes the read.
      reader.clear();
      readStamp++;
      showRead();
      break;
    case 'reading': {
      if (message.active && (!Number.isSafeInteger(message.id) || message.id <= 0)) { break; }
      tapRevision++;
      reading = { active: message.active, id: message.id };
      // Active attaches a page-started read or restores this pane's read. Inactive preserves sentences while the pane is hidden
      // or the app is backgrounded; do not accept or highlight text until reattached.
      if (message.active) { reader.attach(message.id); } else { reader.detach(); }
      readStamp++;
      showRead();
      window.clearTimeout(tapTimer);
      lastTap = null;
      break;
    }
    case 'selaction':
      if ((message.action === 'clear' || message.id !== undefined) && message.lease !== lease) { break; }
      selection.act(message.action, message.id);
      break;
    case 'find':
      find(message.text);
      break;
    case 'findstep':
      findStep(message.direction);
      break;
    case 'findclose':
      findClose();
      break;
    case 'readstate':
      if (reading.active && message.id === reading.id) {
        reader.setState(message.seq, message.state);
      }
      break;
  }
}

function onTouchStart(event: TouchEvent): void {
  const origin = lease;
  event.preventDefault();
  window.clearTimeout(tapTimer);
  const first = event.touches[0];
  if (lastTap !== null && (event.touches.length !== 1 || gesture.owner === 'selection'
    || event.timeStamp - lastTap.time >= DOUBLE_TAP_MS
    || Math.hypot(first.clientX - lastTap.x, first.clientY - lastTap.y) > DOUBLE_TAP_PX)) {
    if (event.touches.length === 1) { lastTap.deliver(); }
    lastTap = null;
  }
  if (event.touches.length === 1) {
    gesture = {
      ...gesture,
      lease: origin,
      readingId: reading.active ? reading.id : 0,
      revision: tapRevision,
      startTime: event.timeStamp,
      startX: first.clientX,
      startY: first.clientY,
      lastY: first.clientY,
      lastX: first.clientX,
      moved: false,
      twoFingers: false,
      scrollRemainder: 0,
      dismissed: false,
      axis: '',
      owner: '',
    };
    const hit = selection.active ? selection.touchStart(first.clientX, first.clientY) : '';
    gesture.owner = hit === 'handle' ? 'selection' : hit;
    // A long press outside the selection replaces it with a new selection.
    if (hit === '' || hit === 'outside') {
      const press = gesture;
      pressTimer = window.setTimeout(() => {
        if (origin !== lease || gesture !== press) { return; }
        if (!gesture.moved && !gesture.twoFingers) {
          window.clearTimeout(tapTimer);
          lastTap = null;
          gesture.owner = 'selection';
          selectionLease = origin;
          selection.begin(gesture.startX, gesture.startY);
        }
      }, LONG_PRESS_MS);
    }
  } else if (event.touches.length === 2) {
    gesture.lease = origin;
    window.clearTimeout(pressTimer);
    // A second finger always starts a pinch. Release any dragged handle; the final lift ends the pinch
    // (badge and font), rather than acting on selection or its menu.
    if (gesture.owner === 'selection') {
      selection.release();
    }
    gesture.owner = '';
    const second = event.touches[1];
    const midX = (first.clientX + second.clientX) / 2;
    const midY = (first.clientY + second.clientY) / 2;
    const distance = Math.hypot(first.clientX - second.clientX, first.clientY - second.clientY);
    if (gesture.twoFingers) {
      // If a finger returns during a pinch, continue from the displayed scale with the same origin, starting size, badge and anchor.
      // Still fit the grid only once at the final lift.
      gesture = { ...gesture, lastMidX: midX, lastMidY: midY, startDistance: distance / gesture.pinchScale };
      return;
    }
    gesture = {
      ...gesture,
      twoFingers: true,
      startMidX: midX,
      startMidY: midY,
      lastMidX: midX,
      lastMidY: midY,
      startDistance: distance,
      startFontSize: term.options.fontSize as number,
      pinchShown: pinchValue(),
      pinchSent: false,
      pinchScale: 1,
    };
    takeAnchor();
  }
}

function onTouchMove(event: TouchEvent): void {
  event.preventDefault();
  const origin = gesture.lease;
  if (origin !== lease) { return; }
  const first = event.touches[0];
  if (event.touches.length === 1 && !gesture.twoFingers) {
    if (gesture.owner === 'selection') {
      selection.move(first.clientX, first.clientY);
      return;
    }
    const deltaY = first.clientY - gesture.lastY;
    gesture.moved ||= Math.hypot(first.clientX - gesture.startX, first.clientY - gesture.startY) > TOUCH_SLOP_PX;
    if (!gesture.moved) {
      return;
    }
    // The first movement past the threshold fixes the gesture: mostly horizontal swipes panes on release; otherwise scroll.
    if (gesture.axis === '') {
      window.clearTimeout(pressTimer);
      gesture.axis = Math.abs(first.clientX - gesture.startX) > Math.abs(first.clientY - gesture.startY) ? 'x' : 'y';
    }
    if (gesture.axis === 'x') {
      gesture.lastX = first.clientX;
      gesture.lastY = first.clientY;
      return;
    }
    moveScreen(true);
    // A drag of one cell height scrolls one line; dragging down shows older lines (scrollLines).
    const cellHeight = (container.querySelector('.xterm-screen') as HTMLElement).clientHeight / term.rows;
    gesture.scrollRemainder += deltaY;
    const lines = Math.trunc(gesture.scrollRemainder / cellHeight);
    if (lines !== 0) {
      const cell = cellAt(first.clientX, first.clientY) ?? { row: 0, col: 0 };
      scrollLines(-lines, Math.abs(lines), cell.col, cell.row, origin);
      gesture.scrollRemainder -= lines * cellHeight;
    }
    // Dragging beyond the terminal's bottom edge dismisses the keyboard.
    if (!gesture.dismissed && first.clientY > document.documentElement.clientHeight) {
      gesture.dismissed = true;
      send({ type: 'dismiss' }, origin);
    }
    gesture.lastY = first.clientY;
  } else if (event.touches.length === 2) {
    moveScreen(true);
    const second = event.touches[1];
    const midX = (first.clientX + second.clientX) / 2;
    const midY = (first.clientY + second.clientY) / 2;
    const distance = Math.hypot(first.clientX - second.clientX, first.clientY - second.clientY);
    // Preview pinch zoom with a transform around the initial finger midpoint; fit the grid only on release.
    // Overlays are inside .xterm-screen and scale with it.
    const start = gesture.startFontSize;
    const scale = Math.min(FONT_SIZE_MAX / start, Math.max(FONT_SIZE_MIN / start, distance / gesture.startDistance));
    const screen = container.querySelector('.xterm-screen') as HTMLElement;
    if (screen.style.transform === '') {
      const box = screen.getBoundingClientRect();
      screen.style.transformOrigin = `${gesture.startMidX - box.left}px ${gesture.startMidY - box.top}px`;
    }
    screen.style.transform = `scale(${scale})`;
    gesture.pinchScale = scale;
    const shown = Math.round(start * scale);
    if (shown !== gesture.pinchShown) {
      gesture.pinchShown = shown;
      gesture.pinchSent = true;
      send({ type: 'pinch', phase: 'move', size: gesture.pinchShown }, origin);
    }
    gesture.lastMidX = midX;
    gesture.lastMidY = midY;
  }
}

// Shared by touchend and touchcancel; a cancelled touch reports a changed font but triggers no action.
function onTouchEnd(event: TouchEvent): void {
  event.preventDefault();
  const origin = gesture.lease;
  if (origin !== lease) { return; }
  if (event.touches.length > 0) {
    return;
  }
  const ended = event.type === 'touchend';
  const previousTap = lastTap;
  lastTap = null;
  window.clearTimeout(pressTimer);
  moveScreen(false);
  // Release the selected handle or long press and show the menu.
  if (gesture.owner === 'selection') {
    selection.release();
    return;
  }
  // An outside tap clears selection without another action; an outside scroll or pinch preserves it.
  if (gesture.owner === 'outside' && !reading.active && !gesture.moved && !gesture.twoFingers) {
    if (ended) {
      selection.clear();
    }
  }
  if (gesture.twoFingers) {
    // Commit pinch zoom: remove the preview transform, fit the new font size once, and restore the anchor once.
    const screen = container.querySelector('.xterm-screen') as HTMLElement;
    screen.style.transform = '';
    screen.style.transformOrigin = '';
    if (gesture.pinchShown !== term.options.fontSize) {
      term.options.fontSize = gesture.pinchShown;
      fitGrid();
      keepPlace();
    }
    pinchAnchor = null;
    if (gesture.pinchSent) {
      send({ type: 'pinch', phase: 'end', size: gesture.pinchShown }, origin);
    }
    if (term.options.fontSize !== gesture.startFontSize) {
      send({ type: 'font', size: term.options.fontSize as number }, origin);
    }
  } else if (gesture.axis === 'x') {
    // A horizontal one-finger swipe moves to the next pane when leftward, otherwise the previous pane.
    const travelX = gesture.lastX - gesture.startX;
    if (ended && Math.abs(travelX) >= SWIPE_MIN_PX && Math.abs(travelX) > Math.abs(gesture.lastY - gesture.startY)) {
      send({ type: 'swipe', direction: travelX < 0 ? 1 : -1 }, origin);
    }
  } else if (!gesture.moved) {
    if (ended && gesture.revision === tapRevision) {
      if (previousTap !== null && gesture.startTime - previousTap.time <= DOUBLE_TAP_MS
        && previousTap.revision === tapRevision
        && Math.hypot(gesture.startX - previousTap.x, gesture.startY - previousTap.y) <= DOUBLE_TAP_PX) {
        selectionLease = origin;
        selection.beginParagraph(gesture.startX, gesture.startY);
        return;
      }
      const x = gesture.startX, y = gesture.startY;
      const cell = cellAt(gesture.startX, gesture.startY);
      const id = gesture.readingId, active = id !== 0;
      const outside = gesture.owner === 'outside';
      const revision = gesture.revision, mouse = term.modes.mouseTrackingMode, encoding = sgr;
      const screen = container.querySelector('.xterm-screen')!.getBoundingClientRect();
      let marked = false;
      if (active && x >= screen.left && x < screen.right && y >= screen.top && y < screen.bottom) {
        for (const element of readPool.values()) {
          if (!element.isConnected) { continue; }
          const box = element.getBoundingClientRect();
          if (x >= box.left && x < box.right && y >= box.top && y < box.bottom) { marked = true; break; }
        }
      }
      const buffer = term.buffer.active, viewport = buffer.viewportY, cols = term.cols, rows = term.rows, font = term.options.fontSize;
      const image = imageAt(x, y);
      const tap = { time: event.timeStamp, x, y, revision, deliver: () => {
        if (lastTap === tap) { lastTap = null; }
        const now = container.querySelector('.xterm-screen')!.getBoundingClientRect();
        if (revision !== tapRevision || origin !== lease || reading.active !== active || reading.id !== id || buffer !== term.buffer.active
          || mouse !== term.modes.mouseTrackingMode || encoding !== sgr
          || viewport !== buffer.viewportY || cols !== term.cols || rows !== term.rows || font !== term.options.fontSize
          || screen.left !== now.left || screen.top !== now.top || screen.width !== now.width || screen.height !== now.height || cell === null) { return; }
        if (active) { send({ type: marked ? 'marktap' : 'readstop', id }, origin); return; }
        // Handle openable/reloadable image taps before program mouse tracking, which would expand the underlying Claude Code message.
        // Leave loading or unrecoverable missing images to the program's mouse handling.
        if (!outside && image !== undefined && imageAt(x, y) === image && imageAction(image) !== 'none') { actOnImage(image, origin); return; }
        if (mouse !== 'none') { send({ type: 'rowclick', column: cell.col, row: cell.row, cols, rows, mouse, sgr: encoding }, origin); return; }
        if (outside) { return; }
        if (image !== undefined) { if (imageAt(x, y) === image) { actOnImage(image, origin); } }
        else { send({ type: 'tap' }, origin); }
      } };
      lastTap = tap;
      tapTimer = window.setTimeout(tap.deliver, DOUBLE_TAP_MS);
    }
  }
}

// Fit after fonts load so the initial grid uses actual cell metrics. Send ready only after the theme arrives too,
// preventing a first frame with the wrong colors.
const fonts = Promise.all(
  ['400 16px "Geist Mono"', '700 16px "Geist Mono"', '400 16px "JetBrains Mono"', '700 16px "JetBrains Mono"', '16px "Remote Shell Symbols Mono"',
    '16px "Symbols Nerd Font Mono"']
    .map((font) => document.fonts.load(font)),
);
Promise.all([fonts, themed]).then(() => {
  term.open(container);
  // Selection and redraw-only renders replace spans without public onRender. Size just the replaced rows before paint.
  const rows = container.querySelector('.xterm-rows') as HTMLElement;
  new MutationObserver((records) => {
    const changed = new Set<Element>();
    for (const record of records) {
      if (record.target instanceof Element && record.target.parentElement === rows) {
        changed.add(record.target);
      } else if (record.target === rows) {
        for (const node of record.addedNodes) {
          if (node instanceof Element) { changed.add(node); }
        }
      }
    }
    const screen = container.querySelector('.xterm-screen') as HTMLElement;
    sizePlaceholderSpans(screen.clientWidth / term.cols, changed);
  }).observe(rows, { childList: true, subtree: true });
  // disableStdin already makes the textarea readonly (xterm.js 6.0.0); no soft keyboard either.
  (term.textarea as HTMLTextAreaElement).inputMode = 'none';
  fitAddon.fit();
  grid = gridNow();
  sendDocument({ type: 'ready', ...grid });
  sendCell();
  term.onWriteParsed(() => { resizeSince = 0; bufferChanged(); reader.touch(); });
  term.onScroll(() => {
    tapRevision++;
    screenStamp++;
    sendScrolled(renderLease);
    repaint();
  });
  // Every reported viewport render draws the overlays in the same frame (drawOverlays).
  term.onRender(() => drawOverlays(renderLease));
  // Grid changes from pinch, keyboard or rotation reflow text and cell tags; reading must resolve them again.
  term.onResize(() => {
    if (term.buffer.active.type === 'alternate') { resizeSince = performance.now(); }
    tapRevision++;
    findEpoch++;
    bufferChanged();
    repaint();
    reader.touch();
  });
  // Redraw after page visibility returns (backgrounded WebViews may skip frames) or a font finishes loading.
  document.addEventListener('visibilitychange', () => {
    if (!document.hidden) {
      repaint();
    }
  });
  document.fonts.addEventListener('loadingdone', () => {
    screenStamp++;
    repaint();
  });
  document.addEventListener('touchstart', onTouchStart, { passive: false });
  document.addEventListener('touchmove', onTouchMove, { passive: false });
  document.addEventListener('touchend', onTouchEnd, { passive: false });
  document.addEventListener('touchcancel', onTouchEnd, { passive: false });
  // Refit after the WebView receives its resized page, including keyboard, rotation and strip-collapse changes.
  // MAUI SizeChanged precedes this resize, so a C# fit request there would read stale dimensions.
  new ResizeObserver(() => {
    fitGrid();
    repaint();
  }).observe(container);
});
