const prefix = "#share=v1.";
const maxStateBytes = 1024 * 1024;

export class ShareLinkError extends Error { }

export async function encodeShareState(state) {
    const bytes = new TextEncoder().encode(JSON.stringify(state));
    if (bytes.length > maxStateBytes) {
        throw new ShareLinkError("This snippet is too large to share. Source and options must fit within 1 MiB.");
    }
    const stream = new Blob([bytes]).stream().pipeThrough(new CompressionStream("gzip"));
    const compressed = new Uint8Array(await new Response(stream).arrayBuffer());
    let binary = "";
    for (let offset = 0; offset < compressed.length; offset += 32768) {
        binary += String.fromCharCode(...compressed.subarray(offset, offset + 32768));
    }
    return prefix + btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "");
}

export async function decodeShareState(hash, pipelineRules, defaultOptions) {
    if (!hash.startsWith("#share=")) return null;
    if (!hash.startsWith(prefix)) throw new ShareLinkError("Unsupported share link version.");
    const encoded = hash.slice(prefix.length);
    if (encoded.length > maxStateBytes * 2 || !/^[A-Za-z0-9_-]+$/.test(encoded)) {
        throw new ShareLinkError("Invalid share link.");
    }
    const bytes = Uint8Array.from(atob(encoded.replaceAll("-", "+").replaceAll("_", "/")), character => character.charCodeAt(0));
    const stream = new Blob([bytes]).stream().pipeThrough(new DecompressionStream("gzip"));
    const reader = stream.getReader();
    const chunks = [];
    let length = 0;
    try {
        while (true) {
            const { value, done } = await reader.read();
            if (done) break;
            length += value.length;
            if (length > maxStateBytes) {
                await reader.cancel();
                throw new ShareLinkError("The shared snippet is too large.");
            }
            chunks.push(value);
        }
    } finally {
        reader.releaseLock();
    }
    const decoded = new Uint8Array(length);
    let offset = 0;
    for (const chunk of chunks) {
        decoded.set(chunk, offset);
        offset += chunk.length;
    }
    const state = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(decoded));
    if (typeof state?.source !== "string") {
        throw new ShareLinkError("Invalid source in share link.");
    }
    return { source: state.source, options: readCompilerOptions(state.options, pipelineRules, defaultOptions) };
}

export function readCompilerOptions(options, pipelineRules, defaultOptions) {
    if (!["release", "enableInlining", "allowCrossAssemblyIPO"].every(option => typeof options?.[option] === "boolean")) {
        throw new ShareLinkError("Invalid compiler options.");
    }
    const result = {};
    for (const [key, fallback] of Object.entries(defaultOptions)) {
        if (key === "pipeline") continue;
        const value = options[key] === undefined ? fallback : options[key];
        if (typeof value !== typeof fallback || typeof value === "number" &&
            (!Number.isInteger(value) || value < 0 || value > 2147483647)) {
            throw new ShareLinkError(`Invalid compiler option: ${key}.`);
        }
        result[key] = value;
    }
    result.pipeline = options.pipeline == null ? null : readPipeline(options.pipeline, pipelineRules);
    return result;
}

function readPipeline(pipeline, rules) {
    if (!Array.isArray(pipeline.stages) || pipeline.stages.length < 1 || pipeline.stages.length > rules.maxStages) {
        throw new ShareLinkError("Invalid optimization pipeline stages.");
    }
    const stages = new Map();
    let stepCount = 0;
    for (const stage of pipeline.stages) {
        if (typeof stage?.id !== "string" || stage.id.length === 0 || stages.has(stage.id) || !Array.isArray(stage.steps) ||
            (stepCount += stage.steps.length) > rules.maxSteps ||
            !Number.isInteger(stage.maxIterations) || stage.maxIterations < 1 || stage.maxIterations > rules.maxIterations ||
            typeof stage.runInSequence !== "boolean" || typeof stage.applyIndependently !== "boolean" ||
            stage.ifChanged != null && typeof stage.ifChanged !== "string") {
            throw new ShareLinkError("Invalid optimization pipeline stage.");
        }
        for (const step of stage.steps) {
            if (typeof step?.pass !== "string" || typeof step.enabled !== "boolean" || (step.defaultPass == null
                ? !rules.passIds.includes(step.pass)
                : !Number.isInteger(step.defaultPass) || step.defaultPass < 0 || rules.defaultPasses[step.defaultPass] !== step.pass)) {
                throw new ShareLinkError("Unknown optimization pass or default configuration.");
            }
        }
        stages.set(stage.id, {
            id: stage.id,
            steps: stage.steps.map(step => ({ pass: step.pass, enabled: step.enabled, defaultPass: step.defaultPass ?? null })),
            maxIterations: stage.maxIterations,
            runInSequence: stage.runInSequence,
            applyIndependently: stage.applyIndependently,
            ifChanged: stage.ifChanged ?? null
        });
    }
    for (const stage of stages.values()) {
        const visited = new Set([stage.id]);
        let next = stage.ifChanged;
        while (next != null) {
            if (visited.has(next) || !stages.has(next)) throw new ShareLinkError("Invalid or cyclic conditional stages.");
            visited.add(next);
            next = stages.get(next).ifChanged;
        }
    }
    return { stages: [...stages.values()] };
}
