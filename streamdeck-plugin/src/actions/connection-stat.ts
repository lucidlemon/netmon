import { action, KeyDownEvent, SingletonAction, WillAppearEvent, WillDisappearEvent, DidReceiveSettingsEvent } from "@elgato/streamdeck";
import { fetchStatus } from "../lib/netmon-api";
import { renderTile } from "../lib/tile";

type Settings = {
	connection?: string;
	metric?: "ping" | "jitter";
};

const POLL_INTERVAL_MS = 1500;

/**
 * Shows live ping or jitter for one NetMonGui-monitored network connection on a key,
 * color-coded using the same gaming-tier thresholds as the app's own charts.
 */
@action({ UUID: "com.danielwinter.netmon.connection-stat" })
export class ConnectionStatAction extends SingletonAction<Settings> {
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

	private async refresh(action: WillAppearEvent<Settings>["action"], settings: Settings): Promise<void> {
		const connection = settings.connection?.trim();
		const metric = settings.metric === "jitter" ? "jitter" : "ping";
		const metricLabel = metric === "ping" ? "PING" : ("JITTER" as const);

		if (!connection) {
			await action.setImage(renderTile({ connectionName: "", metricLabel, valueMs: null, tier: null, color: null, statusMessage: "Pick a connection" }));
			return;
		}

		const status = await fetchStatus();
		if (!status) {
			await action.setImage(renderTile({ connectionName: connection, metricLabel, valueMs: null, tier: null, color: null, statusMessage: "NetMon offline" }));
			return;
		}

		const adapter = status.adapters.find((a) => a.name === connection);
		if (!adapter) {
			await action.setImage(renderTile({ connectionName: connection, metricLabel, valueMs: null, tier: null, color: null, statusMessage: "Not found" }));
			return;
		}

		const value = metric === "ping" ? adapter.ping : adapter.jitter;
		await action.setImage(
			renderTile({
				connectionName: adapter.name,
				metricLabel,
				valueMs: value.ms,
				tier: value.tier,
				color: value.color,
			}),
		);
	}
}
