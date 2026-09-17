export type StatMode = "text" | "graph" | "text+graph";

export type StatOptions = {
	label: string;
	valueMs: number | null;
	unitLabel: string;
	tier: string | null;
	color: string | null;
	mode: StatMode;
	/** Recent per-sample history, oldest first; null entries are failed pings. Used in graph modes. */
	history: (number | null)[];
	/** Shown instead of the value/graph when NetMon can't be reached or the connection isn't found. */
	statusMessage?: string;
};

const SIZE = 200;
const BG = "#000000";
const OFFLINE_BG = "#2A2A32";
const TEXT_MUTED = "#8A8A92";
const FALLBACK_COLOR = "#6E6E76";
const ERROR_COLOR = "#E05A5A";

function escapeXml(s: string): string {
	return s.replace(/[<>&'"]/g, (c) => {
		switch (c) {
			case "<":
				return "&lt;";
			case ">":
				return "&gt;";
			case "&":
				return "&amp;";
			case "'":
				return "&apos;";
			default:
				return "&quot;";
		}
	});
}

function truncate(s: string, max: number): string {
	return s.length > max ? `${s.slice(0, max - 1)}…` : s;
}

type Point = { x: number; y: number };

// A ceiling on the scale's top edge, not a ceiling on values themselves: normal ranges (ping and
// jitter are usually well under this) still auto-scale to their own min/max for full detail, but
// one spike no longer drags the whole scale up with it and flattens everything else into an
// unreadable line near the bottom - it just clips flat against the top instead.
const GRAPH_MAX_MS = 120;

/**
 * Places history values within [x, y, w, h], auto-scaled to that series' own min/max (with a
 * little headroom, capped at GRAPH_MAX_MS) since ping and jitter live on very different ranges -
 * a shared/fixed scale would flatten whichever one has less range. Points are spaced evenly by
 * index: the API sends ~1 sample/sec without per-point timestamps, so exact elapsed-time
 * positioning isn't available here the way it is in NetMon's own WPF sparkline.
 */
function scalePoints(values: (number | null)[], x: number, y: number, w: number, h: number): Point[] {
	const present = values.filter((v): v is number => v !== null);
	if (present.length === 0) return [];

	let min = Math.min(...present);
	let max = Math.min(Math.max(...present), GRAPH_MAX_MS);
	const range = max - min || Math.max(1, max * 0.1, 1);
	min -= range * 0.1;
	max = Math.min(max + range * 0.2, GRAPH_MAX_MS);

	const n = values.length;
	const pts: Point[] = [];
	for (let i = 0; i < n; i++) {
		const v = values[i];
		if (v === null) continue;
		const clamped = Math.min(v, GRAPH_MAX_MS);
		const px = x + (n <= 1 ? w / 2 : (i / (n - 1)) * w);
		const py = y + h - ((clamped - min) / (max - min)) * h;
		pts.push({ x: px, y: py });
	}
	return pts;
}

function pathFromPoints(pts: Point[]): string {
	return pts.map((p, i) => `${i === 0 ? "M" : "L"}${p.x.toFixed(1)} ${p.y.toFixed(1)}`).join(" ");
}

/** A line with a gradient-filled area beneath it, fading from the series color to transparent. */
function renderSparkline(pts: Point[], x: number, y: number, w: number, h: number, color: string, gradientId: string): string {
	if (pts.length < 2) {
		return `<text x="${x + w / 2}" y="${y + h / 2 + 4}" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="11" fill="${TEXT_MUTED}">not enough data yet</text>`;
	}

	const line = pathFromPoints(pts);
	const area = `${line} L${pts[pts.length - 1]!.x.toFixed(1)} ${(y + h).toFixed(1)} L${pts[0]!.x.toFixed(1)} ${(y + h).toFixed(1)} Z`;

	return `
		<defs>
			<linearGradient id="${gradientId}" x1="0" y1="0" x2="0" y2="1">
				<stop offset="0%" stop-color="${color}" stop-opacity="0.55"/>
				<stop offset="100%" stop-color="${color}" stop-opacity="0"/>
			</linearGradient>
		</defs>
		<path d="${area}" fill="url(#${gradientId})" stroke="none"/>
		<path d="${line}" fill="none" stroke="${color}" stroke-width="2.5" stroke-linejoin="round" stroke-linecap="round"/>
	`;
}

/** Renders one metric (label, value, optional sparkline) into the given rect. */
function renderStatBlock(x: number, y: number, w: number, h: number, opts: StatOptions, idSuffix: string): string {
	const color = opts.color ?? FALLBACK_COLOR;
	const rx = Math.min(16, h * 0.12, w * 0.06);

	if (opts.statusMessage) {
		// A "no connection" glyph (circle-slash) rather than just text - reads faster at a
		// glance, especially at physical key size where small text is hard to make out anyway.
		const cx = x + w / 2;
		const cy = y + h / 2 - h * 0.08;
		const r = Math.max(10, Math.min(w, h) * 0.16);
		const strokeW = Math.max(3, r * 0.22);
		return `
			<rect x="${x}" y="${y}" width="${w}" height="${h}" rx="${rx}" fill="${OFFLINE_BG}"/>
			<circle cx="${cx}" cy="${cy}" r="${r}" fill="none" stroke="${ERROR_COLOR}" stroke-width="${strokeW}"/>
			<line x1="${(cx - r * 0.7).toFixed(1)}" y1="${(cy - r * 0.7).toFixed(1)}" x2="${(cx + r * 0.7).toFixed(1)}" y2="${(cy + r * 0.7).toFixed(1)}" stroke="${ERROR_COLOR}" stroke-width="${strokeW}" stroke-linecap="round"/>
			<text x="${x + w / 2}" y="${cy + r + Math.min(18, h * 0.13)}" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="${Math.min(12, h * 0.1)}" fill="${TEXT_MUTED}">${escapeXml(truncate(opts.statusMessage, 20))}</text>
		`;
	}

	const valueText = opts.valueMs === null || opts.valueMs === undefined ? "--" : opts.valueMs.toFixed(1);
	const bg = `<rect x="${x}" y="${y}" width="${w}" height="${h}" rx="${rx}" fill="${BG}"/>`;

	// The label row is only drawn when opts.label is non-empty. ConnectionStatAction passes ""
	// (a single value has nothing to disambiguate - the user's own Stream Deck title covers it),
	// while CompositeStatAction passes "PING"/"JITTER" since two values share one key and one
	// title, so they still need their own tag to tell them apart.
	const hasLabel = opts.label.trim().length > 0;

	if (opts.mode === "text") {
		const cy = y + h / 2;
		const labelRow = hasLabel
			? `<text x="${x + w / 2}" y="${cy - h * 0.16}" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="${Math.min(15, h * 0.12)}" font-weight="600" fill="${TEXT_MUTED}">${escapeXml(opts.label)}</text>`
			: "";
		const valueY = hasLabel ? cy + h * 0.16 : cy - h * 0.02;
		const valueSize = hasLabel ? Math.min(46, h * 0.36) : Math.min(54, h * 0.42);
		const unitY = hasLabel ? cy + h * 0.36 : cy + h * 0.24;
		return `
			${bg}
			${labelRow}
			<text x="${x + w / 2}" y="${valueY}" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="${valueSize}" font-weight="700" fill="${color}">${escapeXml(valueText)}</text>
			<text x="${x + w / 2}" y="${unitY}" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="${Math.min(13, h * 0.1)}" fill="${TEXT_MUTED}">${escapeXml(opts.unitLabel)}</text>
		`;
	}

	if (opts.mode === "graph") {
		const padTop = hasLabel ? Math.max(18, h * 0.22) : Math.max(14, h * 0.14);
		const pad = 6;
		const pts = scalePoints(opts.history, x + pad, y + padTop, w - pad * 2, h - padTop - pad);
		const labelText = hasLabel
			? `<text x="${x + 8}" y="${y + 15}" font-family="Segoe UI, Arial, sans-serif" font-size="12" font-weight="600" fill="${TEXT_MUTED}">${escapeXml(opts.label)}</text>`
			: "";
		return `
			${bg}
			${labelText}
			<text x="${x + w - 8}" y="${y + (hasLabel ? 15 : 20)}" text-anchor="end" font-family="Segoe UI, Arial, sans-serif" font-size="${hasLabel ? 12 : 15}" font-weight="700" fill="${color}">${escapeXml(valueText)}</text>
			${renderSparkline(pts, x + pad, y + padTop, w - pad * 2, h - padTop - pad, color, `g-${idSuffix}`)}
		`;
	}

	// text+graph
	const textH = hasLabel ? Math.max(46, h * 0.42) : Math.max(34, h * 0.3);
	const pad = 6;
	const pts = scalePoints(opts.history, x + pad, y + textH, w - pad * 2, h - textH - pad);
	const labelRow = hasLabel
		? `<text x="${x + w / 2}" y="${y + textH * 0.38}" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="${Math.min(13, textH * 0.28)}" font-weight="600" fill="${TEXT_MUTED}">${escapeXml(opts.label)}</text>`
		: "";
	const valueY = hasLabel ? y + textH * 0.84 : y + textH * 0.68;
	const valueSize = hasLabel ? Math.min(34, textH * 0.72) : Math.min(40, textH * 0.85);
	return `
		${bg}
		${labelRow}
		<text x="${x + w / 2}" y="${valueY}" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="${valueSize}" font-weight="700" fill="${color}">${escapeXml(valueText)}</text>
		${renderSparkline(pts, x + pad, y + textH, w - pad * 2, h - textH - pad, color, `g-${idSuffix}`)}
	`;
}

function wrapSvg(inner: string): string {
	return `<svg xmlns="http://www.w3.org/2000/svg" width="${SIZE}" height="${SIZE}" viewBox="0 0 ${SIZE} ${SIZE}">${inner}</svg>`;
}

// The manifest has ShowTitle: true, so identifying text (connection name, or whatever the user
// prefers) is Stream Deck's own native title, set via action.setTitle() and fully
// resizable/recolorable/repositionable from the key's own settings - confirmed working on
// freshly-placed keys. (Keys placed while an earlier build shipped ShowTitle: false have their
// title settings cached from that point and won't pick this up without being re-added.)
//
// Stream Deck overlays that title on top of whatever we draw rather than reserving space for
// it, so without this margin the title sits directly on top of our own value/graph content -
// this is blank space only (see above for why nothing is drawn here ourselves).
const TITLE_MARGIN = 48;

/** A single connection-stat key: one metric filling the tile, below the title margin. */
export function renderSingleStat(opts: StatOptions): string {
	return wrapSvg(`
		<rect width="${SIZE}" height="${SIZE}" fill="${BG}"/>
		${renderStatBlock(0, TITLE_MARGIN, SIZE, SIZE - TITLE_MARGIN, opts, "s")}
	`);
}

/** A composite key: two metrics stacked below the title margin, each independently moded. */
export function renderComposite(top: StatOptions, bottom: StatOptions): string {
	const gap = 6;
	const half = (SIZE - TITLE_MARGIN - gap) / 2;
	return wrapSvg(`
		<rect width="${SIZE}" height="${SIZE}" fill="${BG}"/>
		${renderStatBlock(0, TITLE_MARGIN, SIZE, half, top, "a")}
		${renderStatBlock(0, TITLE_MARGIN + half + gap, SIZE, half, bottom, "b")}
	`);
}

/** Wraps a rendered SVG string as a proper data URI - setImage() ignores/drops a bare SVG string
 *  with no "data:" prefix, which is what used to make the tile silently never update. */
export function toImageDataUri(svg: string): string {
	return `data:image/svg+xml,${encodeURIComponent(svg)}`;
}
