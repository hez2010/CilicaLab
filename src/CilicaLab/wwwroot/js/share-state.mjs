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

export async function decodeShareState(hash) {
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
    if (typeof state?.source !== "string" ||
        !["release", "enableInlining", "allowCrossAssemblyIPO"].every(option => typeof state.options?.[option] === "boolean")) {
        throw new ShareLinkError("Invalid source or options in share link.");
    }
    return {
        source: state.source,
        options: {
            release: state.options.release,
            enableInlining: state.options.enableInlining,
            allowCrossAssemblyIPO: state.options.allowCrossAssemblyIPO
        }
    };
}
