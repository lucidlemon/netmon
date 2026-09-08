export type TileOptions = {
	connectionName: string;
	metricLabel: "PING" | "JITTER";
	valueMs: number | null;
	tier: string | null;
	color: string | null;
	/** Shown instead of the metric when NetMon can't be reached or the connection isn't found. */
	statusMessage?: string;
};

const SIZE = 200;
const OFFLINE_BG = "#2A2A32";
const TEXT_DARK = "#0B0B0E";
const TEXT_LIGHT = "#C8C8CE";

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

/** Renders a self-contained key tile as an SVG string, colored to match the app's own threshold tiers. */
export function renderTile(opts: TileOptions): string {
	if (opts.statusMessage) {
		return `<svg xmlns="http://www.w3.org/2000/svg" width="${SIZE}" height="${SIZE}" viewBox="0 0 ${SIZE} ${SIZE}">
  <rect width="${SIZE}" height="${SIZE}" rx="24" fill="${OFFLINE_BG}"/>
  <text x="50%" y="94" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="17" font-weight="600" fill="${TEXT_LIGHT}">${escapeXml(opts.metricLabel)}</text>
  <text x="50%" y="122" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="15" fill="${TEXT_LIGHT}">${escapeXml(truncate(opts.statusMessage, 22))}</text>
</svg>`;
	}

	const bg = opts.color ?? "#6E6E76";
	const valueText = opts.valueMs === null || opts.valueMs === undefined ? "--" : opts.valueMs.toFixed(1);
	const nameText = truncate(opts.connectionName, 16);
	const tierText = opts.tier ? truncate(opts.tier, 18) : "";

	return `<svg xmlns="http://www.w3.org/2000/svg" width="${SIZE}" height="${SIZE}" viewBox="0 0 ${SIZE} ${SIZE}">
  <rect width="${SIZE}" height="${SIZE}" rx="24" fill="${bg}"/>
  <text x="50%" y="34" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="15" font-weight="600" fill="${TEXT_DARK}" opacity="0.8">${escapeXml(nameText)}</text>
  <text x="50%" y="120" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="58" font-weight="700" fill="${TEXT_DARK}">${escapeXml(valueText)}</text>
  <text x="50%" y="148" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="17" fill="${TEXT_DARK}" opacity="0.75">ms ${opts.metricLabel.toLowerCase()}</text>
  <text x="50%" y="180" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="15" fill="${TEXT_DARK}" opacity="0.7">${escapeXml(tierText)}</text>
</svg>`;
}
