import { build, context } from "esbuild";

const watch = process.argv.includes("--watch");

const options = {
	entryPoints: ["src/plugin.ts"],
	outfile: "com.danielwinter.netmon.sdPlugin/bin/plugin.js",
	bundle: true,
	platform: "node",
	format: "cjs",
	target: "node20",
	sourcemap: watch ? "inline" : false,
	minify: !watch,
	logLevel: "info",
};

if (watch) {
	const ctx = await context(options);
	await ctx.watch();
	console.log("Watching for changes…");
} else {
	await build(options);
}
