import streamDeck, { action, KeyDownEvent, SingletonAction, WillAppearEvent, WillDisappearEvent, DidReceiveSettingsEvent } from "@elgato/streamdeck";
import { fetchStatus, Adapter, AdapterHistory } from "../lib/netmon-api";
import { renderComposite, toImageDataUri, StatOptions, StatMode } from "../lib/tile";

type Metric = "ping" | "jitter";

type Settings = {
	target?: string;
	connection?: string;
	topMetric?: Metric;
	topMode?: StatMode;
	bottomMetric?: Metric;
	bottomMode?: StatMode;
};

const POLL_INTERVAL_MS = 1500;

// No label - just the value/graph, same as the single-stat tile. Top vs. bottom position is
// how the user tells the two metrics apart (they configured which is which), and unitLabel's
// "ms ping"/"ms jitter" is still there in text-showing modes for a reminder.
function statFor(adapter: Adapter | null, metric: Metric, mode: StatMode, statusMessage?: string): StatOptions {
	if (!adapter || statusMessage) {
		return { label: "", valueMs: null, unitLabel: "", tier: null, color: null, mode, history: [], statusMessage: statusMessage ?? "Not found" };
	}
	const value = metric === "ping" ? adapter.ping : adapter.jitter;
	const history: AdapterHistory | undefined = adapter.history;
	return {
		label: "",
		valueMs: value.ms,
		unitLabel: `ms ${metric}`,
		tier: value.tier,
		color: value.color,
		mode,
		history: (metric === "ping" ? history?.ping : history?.jitter) ?? [],
	};
}

/**
 * Shows two metrics (ping and jitter by default) for the same NetMonGui-monitored connection on
 * one key, each independently shown as text, a trend graph, or both.
 */
@action({ UUID: "com.danielwinter.netmon.composite-stat" })
export class CompositeStatAction extends SingletonAction<Settings> {
	private readonly timers = new Map<string, ReturnType<typeof setInterval>>();

	override onWillAppear(ev: WillAppearEvent<Settings>): void {
		this.startPolling(ev.action.id, ev.action, ev.payload.settings);
	}

	override onWillDisappear(ev: WillDisappearEvent<Settings>): void {
		this.stopPolling(ev.action.id);
	}

	override onDidReceiveSettings(ev: DidReceiveSettingsEvent<Settings>): void {
		this.startPolling(ev.action.id, ev.action, ev.payload.settings);
	}

	override async onKeyDown(ev: KeyDownEvent<Settings>): Promise<void> {
		await this.refresh(ev.action, ev.payload.settings);
	}

	private startPolling(id: string, action: WillAppearEvent<Settings>["action"], settings: Settings): void {
		this.stopPolling(id);
		void this.refresh(action, settings);
		this.timers.set(
			id,
			setInterval(() => void this.refresh(action, settings), POLL_INTERVAL_MS),
		);
	}

	private stopPolling(id: string): void {
		const timer = this.timers.get(id);
		if (timer) {
			clearInterval(timer);
			this.timers.delete(id);
		}
	}

	// See ConnectionStatAction.refresh for why this is wrapped in try/catch: an uncaught
	// exception here kills the whole plugin process, not just this key.
	private async refresh(action: WillAppearEvent<Settings>["action"], settings: Settings): Promise<void> {
		const connection = settings.connection?.trim();
		// The PI's <sdpi-select default="..."> only controls what the dropdown *displays* before
		// it's ever touched - it doesn't get written into settings until the user interacts with
		// it, so settings.bottomMetric is genuinely undefined on a freshly-placed key even though
		// the dropdown shows "Jitter". Defaulting both fields to "ping" here (matching the PI's
		// declared defaults would need topMetric ping / bottomMetric jitter) meant a fresh
		// Composite key rendered ping in both slots until you touched the Bottom dropdown once.
		const topMetric = settings.topMetric === "jitter" ? "jitter" : "ping";
		const bottomMetric = settings.bottomMetric === "ping" ? "ping" : "jitter";
		const topMode = settings.topMode ?? "text+graph";
		const bottomMode = settings.bottomMode ?? "text+graph";

		// See ConnectionStatAction.refresh: left to the user's own title if they've set one.
		if (connection) await action.setTitle(connection).catch(() => {});

		try {
			if (!connection) {
				await action.setImage(toImageDataUri(renderComposite(statFor(null, topMetric, topMode, "Pick a connection"), statFor(null, bottomMetric, bottomMode, "Pick a connection"))));
				return;
			}

			const status = await fetchStatus();
			if (!status) {
				await action.setImage(toImageDataUri(renderComposite(statFor(null, topMetric, topMode, "NetMon offline"), statFor(null, bottomMetric, bottomMode, "NetMon offline"))));
				return;
			}

			const targetId = settings.target?.trim() || "default";
			const target = (status.targets ?? []).find((t) => t.id === targetId);
			const adapter = (target?.adapters ?? []).find((a) => a.name === connection) ?? null;
			const statusMessage = !target ? "Target not found" : !adapter ? "Not found" : undefined;

			await action.setImage(
				toImageDataUri(
					renderComposite(
						statFor(adapter, topMetric, topMode, statusMessage),
						statFor(adapter, bottomMetric, bottomMode, statusMessage),
					),
				),
			);
		} catch (err) {
			streamDeck.logger.error("CompositeStatAction.refresh failed", err);
			await action
				.setImage(toImageDataUri(renderComposite(statFor(null, topMetric, topMode, "Error"), statFor(null, bottomMetric, bottomMode, "Error"))))
				.catch(() => {});
		}
	}
}
