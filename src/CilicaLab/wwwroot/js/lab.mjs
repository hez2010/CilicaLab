import { encodeShareState, decodeShareState, ShareLinkError } from "./share-state.mjs";

let sourceEditor, diffEditor, originalModel, modifiedModel, changeListener, overlays;
let settingSource = false;
let graphviz;
let disposeSplitter;
let disposeDismissal;
const graphs = new Map();
const graphObserver = new ResizeObserver(entries => {
    for (const { target } of entries) {
        if (graphs.get(target)?.fitted && target.clientWidth > 0) fitGraph(target);
    }
});

export async function initialize(sourceElement, diffElement, workspace, splitter, initialSource, initialOptions, callback) {
    const base = new URL("_content/BlazorMonaco/lib/monaco-editor/min/vs", document.baseURI).href;
    require.config({ paths: { vs: base } });
    await new Promise((resolve, reject) => require(["vs/editor/editor.main"], resolve, reject));

    monaco.editor.defineTheme("cilica", {
        base: "vs-dark", inherit: true,
        rules: [
            { token: "keyword", foreground: "B5A1EE" },
            { token: "number", foreground: "E4BD7D" },
            { token: "string", foreground: "9DD6AF" },
            { token: "comment", foreground: "71818C" },
            { token: "type.identifier", foreground: "72CFBC" }
        ],
        colors: {
            "editor.background": "#13191F", "editor.foreground": "#CFD8E0",
            "editorLineNumber.foreground": "#54616D", "editorLineNumber.activeForeground": "#A5B7C5",
            "editor.lineHighlightBackground": "#192129", "editor.selectionBackground": "#284B58",
            "editor.inactiveSelectionBackground": "#243C49", "editorIndentGuide.background1": "#242E37",
            "diffEditor.insertedTextBackground": "#30735845", "diffEditor.removedTextBackground": "#93454F40",
            "diffEditor.insertedLineBackground": "#234D342A", "diffEditor.removedLineBackground": "#592D342A"
        }
    });
    for (const language of ["cil", "cilica-ir"]) {
        monaco.languages.register({ id: language });
        monaco.languages.setMonarchTokensProvider(language, { tokenizer: { root: [
            [/\/\/.*$/, "comment"], [/"([^"\\]|\\.)*"/, "string"],
            [/\bIL_[0-9A-Fa-f]+\b/, "type.identifier"], [/#\w+/, "type.identifier"],
            [/\b(?:static|public|private|int|long|float|double|bool|void|ref|ptr|byref)\b/, "keyword"],
            [/\b(?:ret|return|phi|goto|if|br|brtrue|brfalse|call|callvirt|newobj|load|store|select|switch|throw)\b/, "keyword"],
            [/\b\d+(?:\.\d+)?\b/, "number"]
        ] } });
    }

    let source = initialSource;
    try { source = localStorage.getItem("cilicalab.source") ?? source; } catch { /* Storage can be disabled by the browser. */ }
    let compilerOptions = initialOptions;
    let shareError = null;
    try {
        const shared = await decodeShareState(location.hash);
        if (shared) {
            source = shared.source;
            compilerOptions = shared.options;
        }
    } catch {
        shareError = "This share link could not be opened. Your saved code or the default example is shown.";
    }
    overlays = document.createElement("div");
    overlays.className = "editor-overlays monaco-editor";
    document.body.append(overlays);
    const options = {
        theme: "cilica", automaticLayout: true, fontSize: 13, lineHeight: 22,
        fontFamily: "Cascadia Code, Cascadia Mono, Consolas, monospace",
        minimap: { enabled: false }, scrollBeyondLastLine: false, padding: { top: 16, bottom: 16 },
        lineNumbersMinChars: 3, tabSize: 4, insertSpaces: true, renderLineHighlight: "line",
        bracketPairColorization: { enabled: true }, overviewRulerBorder: false,
        fixedOverflowWidgets: true, overflowWidgetsDomNode: overlays
    };
    sourceEditor = monaco.editor.create(sourceElement, { ...options, value: source, language: "csharp", ariaLabel: "C# source editor" });
    originalModel = monaco.editor.createModel("", "cilica-ir");
    modifiedModel = monaco.editor.createModel("", "cilica-ir");
    diffEditor = monaco.editor.createDiffEditor(diffElement, {
        ...options, readOnly: true, originalEditable: false, renderSideBySide: true,
        enableSplitViewResizing: true, renderOverviewRuler: false, diffWordWrap: "off",
        useInlineViewWhenSpaceIsLimited: false,
        originalAriaLabel: "Before transformation", modifiedAriaLabel: "After transformation"
    });
    diffEditor.setModel({ original: originalModel, modified: modifiedModel });
    disposeSplitter = initializeSplitter(workspace, splitter);
    disposeDismissal = initializeDismissal();
    sourceEditor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, () => callback.invokeMethodAsync("CompileAsync"));
    changeListener = sourceEditor.onDidChangeModelContent(() => {
        saveDraft();
        if (!settingSource) callback.invokeMethodAsync("SourceChanged");
    });
    window.addEventListener("hashchange", restoreSharedNavigation);
    return { source, options: compilerOptions, shareError };
}

function restoreSharedNavigation() {
    location.reload();
}

function initializeDismissal() {
    const optionsMenu = document.querySelector(".settings");
    const dialog = document.querySelector(".share-dialog");
    function click(event) {
        if (!optionsMenu.contains(event.target)) optionsMenu.open = false;
        if (dialog.open && event.target === dialog) {
            const bounds = dialog.getBoundingClientRect();
            if (event.clientX < bounds.left || event.clientX > bounds.right ||
                event.clientY < bounds.top || event.clientY > bounds.bottom) dialog.close();
        }
    }
    function keydown(event) {
        if (event.key !== "Escape" || !optionsMenu.open) return;
        if (optionsMenu.contains(document.activeElement)) optionsMenu.querySelector("summary").focus();
        optionsMenu.open = false;
    }
    document.addEventListener("click", click);
    document.addEventListener("keydown", keydown);
    return () => {
        document.removeEventListener("click", click);
        document.removeEventListener("keydown", keydown);
    };
}

export function saveDraft() {
    try { localStorage.setItem("cilicalab.source", sourceEditor.getValue()); } catch { /* Storage is optional. */ }
    if (!location.hash.startsWith("#share=")) return;
    const url = new URL(location.href);
    url.hash = "";
    history.replaceState(history.state, "", url);
}

export async function createShareLink(options) {
    try {
        const url = new URL(location.href);
        url.hash = await encodeShareState({ source: sourceEditor.getValue(), options });
        return { url: url.href, error: null };
    } catch (error) {
        return { url: "", error: error instanceof ShareLinkError ? error.message : "The share link could not be created." };
    }
}

export function openShareDialog(dialog, input) {
    dialog.showModal();
    input.focus();
    input.select();
}

export function closeShareDialog(dialog) { dialog.close(); }

export async function copyShareLink(url, input) {
    try {
        await navigator.clipboard.writeText(url);
        return true;
    } catch {
        input.focus();
        input.select();
        return false;
    }
}

function initializeSplitter(workspace, splitter) {
    let percent = 32;
    let dragging = false;
    function resize(value) {
        percent = Math.max(20, Math.min(65, value));
        workspace.style.setProperty("--source-width", `${percent}%`);
        splitter.setAttribute("aria-valuenow", Math.round(percent));
        splitter.setAttribute("aria-valuetext", `Source ${Math.round(percent)} percent`);
    }
    function start(event) {
        if (event.button !== 0) return;
        dragging = true;
        splitter.setPointerCapture(event.pointerId);
        workspace.classList.add("resizing");
        event.preventDefault();
    }
    function move(event) {
        if (!dragging) return;
        const bounds = workspace.getBoundingClientRect();
        resize((event.clientX - bounds.left) / bounds.width * 100);
    }
    function end() { dragging = false; workspace.classList.remove("resizing"); }
    function key(event) {
        const value = { ArrowLeft: percent - 2, ArrowRight: percent + 2, Home: 20, End: 65 }[event.key];
        if (value === undefined) return;
        resize(value);
        event.preventDefault();
    }
    splitter.addEventListener("pointerdown", start);
    splitter.addEventListener("pointermove", move);
    splitter.addEventListener("pointerup", end);
    splitter.addEventListener("pointercancel", end);
    splitter.addEventListener("keydown", key);
    return () => {
        splitter.removeEventListener("pointerdown", start);
        splitter.removeEventListener("pointermove", move);
        splitter.removeEventListener("pointerup", end);
        splitter.removeEventListener("pointercancel", end);
        splitter.removeEventListener("keydown", key);
    };
}

export function getSource() { return sourceEditor.getValue(); }
export function setSource(source) {
    settingSource = true;
    sourceEditor.setValue(source);
    settingSource = false;
}
export function showDiff(before, after, language, split) {
    monaco.editor.setModelLanguage(originalModel, language);
    monaco.editor.setModelLanguage(modifiedModel, language);
    originalModel.setValue(before);
    modifiedModel.setValue(after);
    diffEditor.updateOptions({ renderSideBySide: split });
    diffEditor.getOriginalEditor().setScrollTop(0);
    diffEditor.getModifiedEditor().setScrollTop(0);
}
export function setDiagnostics(diagnostics) {
    monaco.editor.setModelMarkers(sourceEditor.getModel(), "roslyn", diagnostics.map(diagnostic => ({
        severity: diagnostic.severity === "Error" ? monaco.MarkerSeverity.Error : monaco.MarkerSeverity.Warning,
        message: `${diagnostic.id}: ${diagnostic.message}`,
        startLineNumber: diagnostic.line, startColumn: diagnostic.column,
        endLineNumber: diagnostic.endLine, endColumn: diagnostic.endColumn
    })));
}
export function nextFrame() { return new Promise(resolve => requestAnimationFrame(() => setTimeout(resolve, 0))); }
export async function showGraphs(beforeElement, afterElement, before, after) {
    if (!before && !after) {
        beforeElement.replaceChildren();
        afterElement.replaceChildren();
        return;
    }
    graphviz ??= import("../vendor/viz-js/viz.js").then(module => module.instance());
    const renderer = await graphviz;
    for (const [element, dot] of [[beforeElement, before], [afterElement, after]]) {
        if (!dot) { element.replaceChildren(); continue; }
        const svg = renderer.renderSVGElement(dot);
        svg.setAttribute("role", "img");
        svg.setAttribute("aria-label", element.getAttribute("aria-label"));
        element.replaceChildren(svg);
        let state = graphs.get(element);
        if (!state) {
            const wheel = event => {
                if (!event.ctrlKey && !event.metaKey) return;
                event.preventDefault();
                const bounds = element.getBoundingClientRect();
                zoomGraph(element, Math.exp(-event.deltaY * 0.002), event.clientX - bounds.left, event.clientY - bounds.top);
            };
            state = { wheel };
            graphs.set(element, state);
            element.addEventListener("wheel", wheel, { passive: false });
            graphObserver.observe(element);
        }
        Object.assign(state, { svg, width: svg.viewBox.baseVal.width, height: svg.viewBox.baseVal.height, scale: 1, fitted: true });
        fitGraph(element);
    }
}
function setGraphScale(element, state, scale) {
    state.scale = Math.max(0.02, Math.min(5, scale));
    state.svg.style.width = `${state.width * state.scale}px`;
    state.svg.style.height = `${state.height * state.scale}px`;
    element.parentElement.querySelector("output").textContent = `${Math.round(state.scale * 100)}%`;
}
export function fitGraph(element) {
    const state = graphs.get(element);
    if (!state) return;
    state.fitted = true;
    setGraphScale(element, state, Math.min(1, (element.clientWidth - 32) / state.width, (element.clientHeight - 32) / state.height));
    element.scrollTo(0, 0);
}
export function zoomGraph(element, factor, x = element.clientWidth / 2, y = element.clientHeight / 2) {
    const state = graphs.get(element);
    if (!state) return;
    state.fitted = false;
    const oldScale = state.scale;
    const left = element.scrollLeft, top = element.scrollTop;
    setGraphScale(element, state, oldScale * factor);
    const ratio = state.scale / oldScale;
    element.scrollTo((left + x) * ratio - x, (top + y) * ratio - y);
}
export function download(name, bytes) {
    const url = URL.createObjectURL(new Blob([bytes], { type: "application/octet-stream" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = name;
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}
export function dispose() {
    window.removeEventListener("hashchange", restoreSharedNavigation);
    changeListener.dispose();
    sourceEditor.getModel().dispose();
    sourceEditor.dispose();
    diffEditor.dispose();
    originalModel.dispose();
    modifiedModel.dispose();
    overlays.remove();
    disposeSplitter();
    disposeDismissal();
    graphObserver.disconnect();
    for (const [element, state] of graphs) element.removeEventListener("wheel", state.wheel);
    graphs.clear();
}
