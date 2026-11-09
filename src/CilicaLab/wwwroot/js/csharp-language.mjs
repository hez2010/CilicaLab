export function registerCSharpLanguageServices(monaco, editor, callback, setDiagnostics) {
    const sourceModel = editor.getModel();
    const pending = new Set();
    const sessions = new Set();
    let disposed = false;
    let diagnosticsTimer;

    function invoke(method, ...args) {
        const promise = callback.invokeMethodAsync(method, ...args);
        pending.add(promise);
        promise.then(() => pending.delete(promise), () => pending.delete(promise));
        return promise;
    }

    function snapshot(model, token, allowTyping = false) {
        if (disposed || model !== sourceModel || token.isCancellationRequested) return null;
        const version = model.getVersionId();
        return {
            source: model.getValue(),
            current: () => !disposed && !model.isDisposed() && (allowTyping || model.getVersionId() === version) && !token.isCancellationRequested
        };
    }

    function range(model, start, length) {
        const from = model.getPositionAt(start), to = model.getPositionAt(start + length);
        return new monaco.Range(from.lineNumber, from.column, to.lineNumber, to.column);
    }

    function release(sessionId) {
        if (!sessions.delete(sessionId)) return;
        return invoke("ReleaseCompletions", sessionId);
    }

    const kinds = monaco.languages.CompletionItemKind;
    const tagKinds = {
        Class: kinds.Class, Structure: kinds.Struct, Interface: kinds.Interface, Enum: kinds.Enum,
        EnumMember: kinds.EnumMember, Delegate: kinds.Function, Method: kinds.Method, ExtensionMethod: kinds.Method,
        Property: kinds.Property, Field: kinds.Field, Event: kinds.Event, Constant: kinds.Constant,
        Local: kinds.Variable, Parameter: kinds.Variable, RangeVariable: kinds.Variable,
        Namespace: kinds.Module, Keyword: kinds.Keyword, Intrinsic: kinds.Keyword,
        TypeParameter: kinds.TypeParameter, Snippet: kinds.Snippet, Operator: kinds.Operator
    };
    const tokenTypes = ["namespace", "class", "struct", "interface", "enum", "type", "typeParameter",
        "parameter", "variable", "field", "constant", "property", "enumMember", "event", "method", "label"];
    const tokenModifiers = ["static", "readonly"];

    const registrations = [
        monaco.languages.registerDocumentSemanticTokensProvider("csharp", {
            getLegend: () => ({ tokenTypes, tokenModifiers }),
            async provideDocumentSemanticTokens(model, lastResultId, token) {
                const state = snapshot(model, token);
                if (!state) return null;
                const tokens = await invoke("GetSemanticTokensAsync", state.source);
                if (!state.current()) return null;
                const data = [];
                let previousLine = 0, previousColumn = 0;
                for (const token of tokens) {
                    const deltaLine = token.line - previousLine;
                    data.push(deltaLine, deltaLine === 0 ? token.column - previousColumn : token.column,
                        token.length, tokenTypes.indexOf(token.type), token.modifiers);
                    previousLine = token.line;
                    previousColumn = token.column;
                }
                return { data: Uint32Array.from(data) };
            },
            releaseDocumentSemanticTokens() {}
        }),
        monaco.languages.registerCompletionItemProvider("csharp", {
            triggerCharacters: [".", ":", ">"],
            async provideCompletionItems(model, position, context, token) {
                const state = snapshot(model, token, true);
                if (!state) return null;
                const offset = model.getOffsetAt(position);
                const result = await invoke("GetCompletionsAsync", state.source, offset);
                if (!result) return null;
                sessions.add(result.sessionId);
                if (!state.current()) { await release(result.sessionId); return null; }
                return {
                    suggestions: result.items.map((item, index) => {
                        const main = item.edits.find(edit => edit.start <= offset && edit.start + edit.length >= offset);
                        const additional = item.edits.filter(edit => edit !== main);
                        let insertText = main.text;
                        let insertTextRules;
                        if (item.newPosition !== null) {
                            const shift = additional.filter(edit => edit.start < main.start)
                                .reduce((sum, edit) => sum + edit.text.length - edit.length, 0);
                            const cursor = item.newPosition - main.start - shift;
                            if (cursor >= 0 && cursor <= insertText.length) {
                                const escape = text => text.replace(/[\\$}]/g, "\\$&");
                                insertText = escape(insertText.slice(0, cursor)) + "$0" + escape(insertText.slice(cursor));
                                insertTextRules = monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet;
                            }
                        }
                        return {
                            label: item.label, filterText: item.filterText, sortText: item.sortText,
                            kind: item.tags.map(tag => tagKinds[tag]).find(kind => kind !== undefined) ?? kinds.Text,
                            insertText, insertTextRules, range: range(model, main.start, main.length),
                            additionalTextEdits: additional.map(edit => ({ range: range(model, edit.start, edit.length), text: edit.text })),
                            sessionId: result.sessionId, index, current: state.current
                        };
                    }),
                    dispose: () => release(result.sessionId)
                };
            },
            async resolveCompletionItem(item, token) {
                if (disposed || token.isCancellationRequested || !item.current()) return item;
                const description = await invoke("DescribeCompletionAsync", item.sessionId, item.index);
                if (!disposed && !token.isCancellationRequested && item.current() && description) item.documentation = description;
                return item;
            }
        }),
        monaco.languages.registerHoverProvider("csharp", {
            async provideHover(model, position, token) {
                const state = snapshot(model, token);
                if (!state) return null;
                const info = await invoke("GetHoverAsync", state.source, model.getOffsetAt(position));
                if (!info || !state.current()) return null;
                return {
                    range: range(model, info.start, info.length),
                    contents: info.sections.map(section => ({ value: section.kind === "Description"
                        ? "```csharp\n" + section.text + "\n```"
                        : section.text.replace(/[\\`*_{}[\]()<>#+.!|~-]/g, "\\$&") }))
                };
            }
        }),
        monaco.languages.registerSignatureHelpProvider("csharp", {
            signatureHelpTriggerCharacters: ["(", ","],
            signatureHelpRetriggerCharacters: [")"],
            async provideSignatureHelp(model, position, token) {
                const state = snapshot(model, token);
                if (!state) return null;
                const info = await invoke("GetSignatureHelpAsync", state.source, model.getOffsetAt(position));
                if (!info || !state.current()) return null;
                return {
                    value: {
                        signatures: info.signatures.map(signature => ({
                            label: signature.label, activeParameter: signature.activeParameter,
                            parameters: signature.parameters.map(parameter => ({ label: [parameter.start, parameter.end] }))
                        })),
                        activeSignature: info.activeSignature,
                        activeParameter: info.signatures[info.activeSignature].activeParameter
                    },
                    dispose() {}
                };
            }
        })
    ];

    async function updateDiagnostics() {
        const version = sourceModel.getVersionId(), source = sourceModel.getValue();
        const diagnostics = await invoke("GetEditorDiagnosticsAsync", source);
        if (!disposed && !sourceModel.isDisposed() && sourceModel.getVersionId() === version) setDiagnostics(diagnostics);
    }

    function scheduleDiagnostics() {
        clearTimeout(diagnosticsTimer);
        setDiagnostics([]);
        diagnosticsTimer = setTimeout(() => {
            updateDiagnostics().catch(error => { if (!disposed) console.error("C# diagnostics failed", error); });
        }, 400);
    }
    const changes = editor.onDidChangeModelContent(scheduleDiagnostics);
    scheduleDiagnostics();

    return async () => {
        disposed = true;
        clearTimeout(diagnosticsTimer);
        changes.dispose();
        for (const registration of registrations) registration.dispose();
        for (const session of sessions) release(session);
        while (pending.size > 0) await Promise.allSettled([...pending]);
    };
}
