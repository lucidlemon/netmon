export type Metric = {
	ms: number | null;
	tier: string | null;
	color: string | null;
};

export type Adapter = {
	name: string;
	description: string;
	isBest: boolean;
	isOsPreferred: boolean;
	ping: Metric;
	jitter: Metric;
	lossPercent: number;
};

export type Target = {
	id: string;
	label: string;
	host: string;
	adapters: Adapter[];
};

export type Threshold = {
	ms: number;
	label: string;
	color: string;
};

export type NetMonStatus = {
	updatedAtUtc: string;
	targets: Target[];
	latencyThresholds: Threshold[];
	jitterThresholds: Threshold[];
};

const BASE_URL = "http://127.0.0.1:47115";

/**
 * Fetches the current snapshot from NetMonGui's local API. Returns null on any
 * failure (app not running, port closed, timeout) — callers render an "offline"
 * tile rather than throwing, since a Stream Deck key polls this every couple
 * of seconds and NetMon not being open is an expected, non-exceptional state.
 */
export async function fetchStatus(timeoutMs = 1500): Promise<NetMonStatus | null> {
	const controller = new AbortController();
	const timer = setTimeout(() => controller.abort(), timeoutMs);
	try {
		const res = await fetch(`${BASE_URL}/api/status`, { signal: controller.signal });
		if (!res.ok) return null;
		return (await res.json()) as NetMonStatus;
	} catch {
		return null;
	} finally {
		clearTimeout(timer);
	}
}
