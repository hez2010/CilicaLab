import { encodeShareState, decodeShareState, readCompilerOptions, ShareLinkError } from "./share-state.mjs";
import { registerCSharpLanguageServices } from "./csharp-language.mjs";

let sourceEditor, diffEditor, originalModel, modifiedModel, changeListener, overlays;
let settingSource = false;
let graphviz;
let disposeSplitter;
let disposeTraceSplitter;
let disposeDismissal;
let disposeLanguageServices;
let compilerOptions;
const graphs = new Map();
const graphObserver = new ResizeObserver(entries => {
    for (const { target } of entries) {
        if (graphs.get(target)?.fitted && target.clientWidth > 0) fitGraph(target);
    }
});

export async function initialize(sourceElement, diffElement, workspace, splitter, initialSource, initialOptions, pipelineRules, callback) {
    const base = new URL("_content/BlazorMonaco/lib/monaco-editor/min/vs", document.baseURI).href;
    require.config({ paths: { vs: base } });
    await new Promise((resolve, reject) => require(["vs/editor/editor.main"], resolve, reject));

    defineEditorTheme();
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
    compilerOptions = initialOptions;
    try {
        const saved = localStorage.getItem("cilicalab.options");
        if (saved) compilerOptions = readCompilerOptions(JSON.parse(saved), pipelineRules);
    } catch { /* Unavailable storage or an invalid draft uses the initial options. */ }
    let shareError = null;
    try {
        const shared = await decodeShareState(location.hash, pipelineRules);
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
        theme: editorTheme(), automaticLayout: true, fontSize: 13, lineHeight: 22,
        fontFamily: "\"Cascadia Code\", \"Cascadia Mono\", \"JetBrains Mono\", \"SF Mono\", Menlo, Consolas, monospace",
        minimap: { enabled: false }, scrollBeyondLastLine: false, padding: { top: 16, bottom: 16 },
        lineNumbersMinChars: 3, tabSize: 4, insertSpaces: true, renderLineHighlight: "line",
        bracketPairColorization: { enabled: true }, overviewRulerBorder: false,
        fixedOverflowWidgets: true, overflowWidgetsDomNode: overlays
    };
    sourceEditor = monaco.editor.create(sourceElement, {
        ...options, value: source, language: "csharp", ariaLabel: "C# source editor",
        quickSuggestions: { other: true, comments: false, strings: false },
        quickSuggestionsDelay: 150,
        "semanticHighlighting.enabled": true,
        suggestOnTriggerCharacters: true, parameterHints: { enabled: true }, wordBasedSuggestions: "off"
    });
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
    document.addEventListener("cilicalab:themechange", applyEditorTheme);
    return { source, options: compilerOptions, shareError };
}

function editorTheme() {
    return document.documentElement.dataset.theme === "light" ? "cilica-light" : "cilica-dark";
}

function defineEditorTheme() {
    const style = getComputedStyle(document.documentElement);
    const color = name => style.getPropertyValue(name).trim().slice(1);
    const rules = [
        { token: "keyword", foreground: color("--syn-keyword") },
        { token: "number", foreground: color("--syn-number") },
        { token: "string", foreground: color("--syn-string") },
        { token: "comment", foreground: color("--syn-comment"), fontStyle: "italic" },
        ...["type.identifier", "class", "struct", "interface", "enum", "type", "typeParameter"].map(token => ({ token, foreground: color("--syn-teal") })),
        { token: "namespace", foreground: color("--syn-type") },
        { token: "method", foreground: color("--syn-function") },
        ...["parameter", "variable"].map(token => ({ token, foreground: color("--syn-variable") })),
        ...["field", "property", "event", "label"].map(token => ({ token, foreground: color("--code") })),
        ...["constant", "enumMember"].map(token => ({ token, foreground: color("--syn-constant") }))
    ];
    const light = document.documentElement.dataset.theme === "light";
    monaco.editor.defineTheme(editorTheme(), {
        base: light ? "vs" : "vs-dark", inherit: true, rules,
        colors: light ? {
            "editor.background": "#FFFFFF", "editor.foreground": "#1F2A37", "editorGutter.background": "#FFFFFF",
            "editorLineNumber.foreground": "#A3ADB9", "editorLineNumber.activeForeground": "#3C4857",
            "editor.lineHighlightBackground": "#F4F7FA", "editor.lineHighlightBorder": "#00000000",
            "editor.selectionBackground": "#C6E8DF", "editor.inactiveSelectionBackground": "#E0EFEB",
            "editorCursor.foreground": "#087F65", "editorIndentGuide.background1": "#E8ECF1", "editorIndentGuide.activeBackground1": "#C5CED9",
            "editorWidget.background": "#FFFFFF", "editorWidget.border": "#CDD5DF",
            "editorHoverWidget.background": "#FFFFFF", "editorHoverWidget.border": "#CDD5DF",
            "scrollbarSlider.background": "#8B96A533", "scrollbarSlider.hoverBackground": "#8B96A555", "scrollbarSlider.activeBackground": "#8B96A577",
            "diffEditor.insertedTextBackground": "#1A9F5E2A", "diffEditor.removedTextBackground": "#D6455A26",
            "diffEditor.insertedLineBackground": "#1A9F5E12", "diffEditor.removedLineBackground": "#D6455A10",
            "diffEditor.diagonalFill": "#D5DCE4AA"
        } : {
            "editor.background": "#10141A", "editor.foreground": "#D6DEE6", "editorGutter.background": "#10141A",
            "editorLineNumber.foreground": "#4A5562", "editorLineNumber.activeForeground": "#A9B6C3",
            "editor.lineHighlightBackground": "#151A22", "editor.lineHighlightBorder": "#00000000",
            "editor.selectionBackground": "#22504A", "editor.inactiveSelectionBackground": "#1C3A38",
            "editorCursor.foreground": "#52D6B0", "editorIndentGuide.background1": "#1F2630", "editorIndentGuide.activeBackground1": "#334050",
            "editorWidget.background": "#181E27", "editorWidget.border": "#2E3743",
            "editorHoverWidget.background": "#181E27", "editorHoverWidget.border": "#2E3743",
            "scrollbarSlider.background": "#3A455266", "scrollbarSlider.hoverBackground": "#4A566688", "scrollbarSlider.activeBackground": "#5A6878AA",
            "diffEditor.insertedTextBackground": "#3FCF9A26", "diffEditor.removedTextBackground": "#F2788A24",
            "diffEditor.insertedLineBackground": "#2FB98A16", "diffEditor.removedLineBackground": "#F2788A14",
            "diffEditor.diagonalFill": "#2A323D99"
        }
    });
}

function applyEditorTheme() {
    defineEditorTheme();
    monaco.editor.setTheme(editorTheme());
}

function restoreSharedNavigation() {
    location.reload();
}

function initializeDismissal() {
    const optionsMenu = document.querySelector(".settings");
    const dialogs = document.querySelectorAll(".lab-dialog");
    function click(event) {
        if (!optionsMenu.contains(event.target)) optionsMenu.open = false;
        for (const dialog of dialogs) {
            if (dialog.open && event.target === dialog) {
                const bounds = dialog.getBoundingClientRect();
                if (event.clientX < bounds.left || event.clientX > bounds.right ||
                    event.clientY < bounds.top || event.clientY > bounds.bottom) dialog.close();
            }
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

export function saveDraft(options = compilerOptions) {
    compilerOptions = options;
    try {
        localStorage.setItem("cilicalab.source", sourceEditor.getValue());
        localStorage.setItem("cilicalab.options", JSON.stringify(options));
    } catch { /* Storage is optional. */ }
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
    openDialog(dialog);
    input.focus();
    input.select();
}

export function openDialog(dialog) { dialog.showModal(); }
export function closeDialog(dialog) { dialog.close(); }

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

export function initializeTraceSplitter(container, splitter) {
    const panel = splitter.parentElement;
    let percent = 32;
    let dragging = false;
    function resize(value) {
        percent = Math.max(15, Math.min(75, value));
        container.style.setProperty("--trace-height", `${percent}%`);
        splitter.setAttribute("aria-valuenow", Math.round(percent));
        splitter.setAttribute("aria-valuetext", `Trace log ${Math.round(percent)} percent`);
    }
    function start(event) {
        if (event.button !== 0) return;
        dragging = true;
        splitter.setPointerCapture(event.pointerId);
        container.classList.add("trace-resizing");
        event.preventDefault();
    }
    function move(event) {
        if (!dragging) return;
        resize((panel.getBoundingClientRect().bottom - event.clientY) / container.clientHeight * 100);
    }
    function end() { dragging = false; container.classList.remove("trace-resizing"); }
    function key(event) {
        const value = { ArrowUp: percent + 2, ArrowDown: percent - 2, Home: 15, End: 75 }[event.key];
        if (value === undefined) return;
        resize(value);
        event.preventDefault();
    }
    splitter.addEventListener("pointerdown", start);
    splitter.addEventListener("pointermove", move);
    splitter.addEventListener("pointerup", end);
    splitter.addEventListener("pointercancel", end);
    splitter.addEventListener("keydown", key);
    disposeTraceSplitter = () => {
        splitter.removeEventListener("pointerdown", start);
        splitter.removeEventListener("pointermove", move);
        splitter.removeEventListener("pointerup", end);
        splitter.removeEventListener("pointercancel", end);
        splitter.removeEventListener("keydown", key);
    };
}

export function getSource() { return sourceEditor.getValue(); }
export function enableLanguageServices(callback) {
    disposeLanguageServices = registerCSharpLanguageServices(monaco, sourceEditor, callback, setDiagnostics);
}
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
export function setDiagnostics(diagnostics, source = null) {
    if (source !== null && sourceEditor.getValue() !== source) return;
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
export async function dispose() {
    await disposeLanguageServices?.();
    window.removeEventListener("hashchange", restoreSharedNavigation);
    document.removeEventListener("cilicalab:themechange", applyEditorTheme);
    changeListener.dispose();
    sourceEditor.getModel().dispose();
    sourceEditor.dispose();
    diffEditor.dispose();
    originalModel.dispose();
    modifiedModel.dispose();
    overlays.remove();
    disposeSplitter();
    disposeTraceSplitter?.();
    disposeDismissal();
    graphObserver.disconnect();
    for (const [element, state] of graphs) element.removeEventListener("wheel", state.wheel);
    graphs.clear();
}
